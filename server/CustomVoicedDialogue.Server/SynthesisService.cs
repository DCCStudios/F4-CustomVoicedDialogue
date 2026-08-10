using System.Collections.Concurrent;
using CustomVoicedDialogue.Server.Audio;
using CustomVoicedDialogue.Server.Cache;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;
using CustomVoicedDialogue.Server.VoiceMapping;

namespace CustomVoicedDialogue.Server;

public enum JobState
{
    Synthesizing,
    Done,
    Failed,
}

public sealed record JobStatus(JobState State, string? WavPath, string? Failure);

public sealed record SynthHistoryEntry(
    DateTimeOffset Timestamp,
    string Text,
    string VoicePath,
    string Voice,
    string Provider,
    TimeSpan Elapsed,
    bool Success,
    string? Failure,
    bool ClippingWarning,
    string? WavPath,
    string? EnrichedText = null);

/// <summary>
/// Orchestrates one dialogue line from request to validated wav:
/// job identity is the engine voice path (dedupes prefetch vs hook-time
/// requests); audio identity is the content-hash cache key (dedupes the
/// same text+voice across different lines and sessions).
/// </summary>
public sealed class SynthesisService
{
    private readonly AppConfig _config;
    private readonly ProviderRegistry _providers;
    private readonly SoundCache _cache;
    private readonly VoiceMapper _voiceMapper;
    private readonly ConcurrentDictionary<string, Lazy<Task<JobStatus>>> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<SynthHistoryEntry> _history = new();
    // Concurrent provider calls: high enough that a full dialogue wheel
    // (up to ~9 lines) generates in two waves, low enough to stay polite
    // to provider rate limits.
    private readonly SemaphoreSlim _providerGate = new(4);
    private IReadOnlyList<TtsVoice>? _voiceListCache;
    private string? _voiceListCacheKey;
    private readonly object _generationLock = new();
    private string? _lastPlayerFingerprint;
    private string? _lastNpcFingerprint;

    public SynthesisService(AppConfig config, ProviderRegistry providers)
    {
        _config = config;
        _providers = providers;
        _cache = new SoundCache(config.ResolveCacheDirectory());
        _voiceMapper = new VoiceMapper(config);
    }

    public SoundCache Cache => _cache;

    public ProviderRegistry Providers => _providers;

    /// <summary>Digest of everything that determines a generated player
    /// line's audio: provider, player voice, and provider settings.  The
    /// plugin stores it alongside a manifest of the files it wrote and
    /// deletes its stale player files when the fingerprint changes, so a
    /// voice change in this app regenerates them automatically.  Empty
    /// while no provider is configured.</summary>
    public string PlayerVoiceFingerprint()
    {
        var provider = string.IsNullOrEmpty(_config.Provider) ? null : _providers.Get(_config.Provider);
        if (provider is null)
        {
            return "";
        }
        var settings = _config.SettingsFor(provider);
        // Mirrors VoiceMapper: an empty PlayerVoice falls through to the
        // provider's own configured default voice.
        var voice = string.IsNullOrEmpty(_config.PlayerVoice)
            ? settings.Get("voice", settings.Get("voiceid", ""))
            : _config.PlayerVoice;
        // The accent only joins the identity once one is actually in use, so
        // adding the feature does not invalidate everybody's existing audio.
        var accent = Accents.Get(_config.PlayerAccent);
        // The mechanism version rides in the marker: "ipa" was the
        // hand-lexicon-only stage, "ipa2" added rule-derived pronunciations, "ipa3" the accent-true r symbols, "ipa4" the Australian vowel overhaul, "ipa5" its KT-Speech corrections, "ipa7" its weakened-glide PRICE, "ipa8" merged IPA spans, "ipa9" the Grimes yeah stretch, "ipa10" its general vowel-holding, "ipa11" stress-tripled emphasis (reverted, caused pauses), "ipa12" restored walk/talk l, "ipa13" unheld before l, "ipa14" hold only the stressed vowel, "ipa15" bridged spans, "ipa16" holds only on standalone FLEECE/GOOSE, "ipa17" clause-lead absorption, "ipa18" THOUGHT holds and onset-cluster fix.
        // Bumping it regenerates accented audio after a mechanism change.
        var accentPart = accent.IsNeutral ? "" : $"|ipa18:{accent.Id}|{_config.AccentImperfection}";
        return Fingerprint(
            $"player|{provider.Id.ToLowerInvariant()}|{voice}|{settings.CanonicalHash()}{accentPart}");
    }

    /// <summary>Same idea for NPC lines: provider, per-voice-type override
    /// map, and provider settings (auto-assignment is deterministic from
    /// these plus the provider's voice list).</summary>
    public string NpcVoiceFingerprint()
    {
        var provider = string.IsNullOrEmpty(_config.Provider) ? null : _providers.Get(_config.Provider);
        if (provider is null)
        {
            return "";
        }
        var settings = _config.SettingsFor(provider);
        var overrides = string.Join(
            "\n",
            _config.NpcVoiceOverrides
                .Where(pair => !string.IsNullOrEmpty(pair.Value))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value}"));
        var accents = string.Join(
            "\n",
            _config.NpcAccentOverrides
                .Where(pair => !string.IsNullOrEmpty(pair.Value) && !Accents.Get(pair.Value).IsNeutral)
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key.ToLowerInvariant()}={pair.Value}"));
        // As above: no accents configured, no change to existing audio.
        var accentPart = accents.Length == 0 ? "" : $"|ipa18:{accents}|{_config.AccentImperfection}";
        return Fingerprint(
            $"npc|{provider.Id.ToLowerInvariant()}|{overrides}|{settings.CanonicalHash()}{accentPart}");
    }

    private static string Fingerprint(string canonical) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))[..32];

    /// <summary>Drops finished jobs when the voice configuration changed.
    /// Job identity is the engine voice path, so a completed job would keep
    /// serving audio in the PREVIOUS voice for that line — the reason a
    /// mid-session voice change used to keep speaking the old voice.  The
    /// content-addressed audio cache is left intact: switching back is still
    /// an instant hit.  Cheap and idempotent, so it can guard every entry
    /// point.</summary>
    public void SyncVoiceGeneration()
    {
        var player = PlayerVoiceFingerprint();
        var npc = NpcVoiceFingerprint();
        lock (_generationLock)
        {
            var first = _lastPlayerFingerprint is null && _lastNpcFingerprint is null;
            if (player == _lastPlayerFingerprint && npc == _lastNpcFingerprint)
            {
                return;
            }
            _lastPlayerFingerprint = player;
            _lastNpcFingerprint = npc;
            if (!first)
            {
                _jobs.Clear();
            }
        }
    }

    public int QueueDepth => _jobs.Count(j => j.Value.IsValueCreated && !j.Value.Value.IsCompleted);

    public IReadOnlyList<SynthHistoryEntry> History => _history.Reverse().Take(100).ToList();

    public event Action<SynthHistoryEntry>? Synthesized;

    /// <summary>Requests synthesis for a line.  Returns the finished status
    /// when the audio is already available, otherwise starts (or joins) the
    /// job and reports it as in flight.</summary>
    public async Task<JobStatus> RequestAsync(string text, string voicePath, string voiceType, bool isPlayer, CancellationToken cancellationToken, bool inlineWait = true)
    {
        // A voice change since the last request retires every finished job.
        SyncVoiceGeneration();

        // Up to one retry: a completed job whose audio file has since been
        // deleted (cache clear, voice-change invalidation) must be dropped
        // and synthesized fresh — returning the stale WavPath made the
        // endpoint throw on every request for that line until restart.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var job = _jobs.GetOrAdd(
                voicePath,
                _ => new Lazy<Task<JobStatus>>(() => RunJobAsync(text, voicePath, voiceType, isPlayer)));

            var task = job.Value;
            if (task.IsCompleted)
            {
                var status = await task;
                if (status.State == JobState.Failed)
                {
                    // Allow a later retry (e.g. server settings were fixed).
                    _jobs.TryRemove(voicePath, out _);
                    return status;
                }
                if (status.State == JobState.Done && !File.Exists(status.WavPath))
                {
                    _jobs.TryRemove(voicePath, out _);
                    continue;
                }
                return status;
            }

            // Give fast providers a moment to finish inline so a cache-warm
            // /api/synth can answer 200 immediately instead of bouncing 202.
            // Prefetch batches skip the wait — it would serialize submissions.
            if (inlineWait)
            {
                var winner = await Task.WhenAny(task, Task.Delay(150, cancellationToken));
                if (winner == task)
                {
                    return await task;
                }
            }
            return new JobStatus(JobState.Synthesizing, null, null);
        }
        return new JobStatus(JobState.Synthesizing, null, null);
    }

    /// <summary>Queries a job; with a positive <paramref name="waitMs"/> the
    /// call long-polls, holding until the job finishes or the window closes,
    /// so a hot line's audio arrives with near-zero extra latency.</summary>
    public async Task<JobStatus?> QueryAsync(string voicePath, int waitMs = 0)
    {
        if (!_jobs.TryGetValue(voicePath, out var job))
        {
            return null;
        }
        if (!job.Value.IsCompleted && waitMs > 0)
        {
            await Task.WhenAny(job.Value, Task.Delay(Math.Min(waitMs, 3000)));
        }
        if (!job.Value.IsCompleted)
        {
            return new JobStatus(JobState.Synthesizing, null, null);
        }
        var status = await job.Value;
        if (status.State == JobState.Failed)
        {
            _jobs.TryRemove(voicePath, out _);
        }
        else if (status.State == JobState.Done && !File.Exists(status.WavPath))
        {
            // Stale entry — the audio was deleted after the job finished.
            // Drop it and report unknown so the client re-submits the line.
            _jobs.TryRemove(voicePath, out _);
            return null;
        }
        return status;
    }

    /// <summary>Fetches the provider voice list in the background so the
    /// session's first synthesis does not pay the (multi-second) fetch.</summary>
    public void WarmVoiceCache()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var provider = string.IsNullOrEmpty(_config.Provider) ? null : _providers.Get(_config.Provider);
                if (provider is not null)
                {
                    await GetVoicesCachedAsync(provider, _config.SettingsFor(provider));
                }
            }
            catch (Exception)
            {
            }
        });
    }

    /// <summary>The accent a line is performed in: the player's own for
    /// player lines, otherwise the override set for that NPC voice type
    /// (unset voice types stay neutral).</summary>
    public Accent ResolveAccent(bool isPlayer, string voiceType)
    {
        if (isPlayer)
        {
            return Accents.Get(_config.PlayerAccent);
        }
        return !string.IsNullOrEmpty(voiceType) && _config.NpcAccentOverrides.TryGetValue(voiceType, out var accentId)
            ? Accents.Get(accentId)
            : Accents.Get(Accents.Default);
    }

    private async Task<JobStatus> RunJobAsync(string text, string voicePath, string voiceType, bool isPlayer)
    {
        var startedAt = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var providerId = _config.Provider;
        var voice = "";
        try
        {
            var provider = _providers.Get(providerId)
                ?? throw new InvalidOperationException($"no TTS provider is configured (set one up in the CustomVoicedDialogue app)");
            var settings = _config.SettingsFor(provider);

            voice = _voiceMapper.ResolveVoice(isPlayer, voiceType, await GetVoicesCachedAsync(provider, settings));
            if (string.IsNullOrEmpty(voice))
            {
                voice = settings.Get("voice", settings.Get("voiceid", ""));
            }

            // Optional emotion auto-tagging (Inworld TTS-2 steering).  The
            // enriched text is the audio's identity: it keys the cache and
            // is what the provider actually speaks.
            var textForSynthesis = text;
            if (provider is InworldProvider inworldProvider)
            {
                textForSynthesis = await inworldProvider.AutoTagAsync(
                    text, voiceType, isPlayer, settings, CancellationToken.None, voicePath,
                    ResolveAccent(isPlayer, voiceType), _config.AccentImperfection);
            }
            var enriched = string.Equals(textForSynthesis, text, StringComparison.Ordinal) ? null : textForSynthesis;

            var cacheKey = SoundCache.ComputeKey(provider.Id, voice, settings.CanonicalHash(), textForSynthesis);
            if (_cache.TryGet(cacheKey, out var cachedPath))
            {
                Record(startedAt, text, voicePath, voice, provider.Id, stopwatch.Elapsed, true, null, false, cachedPath, enriched);
                return new JobStatus(JobState.Done, cachedPath, null);
            }

            byte[] wav;
            ValidationResult validation;
            var attempt = 0;
            while (true)
            {
                attempt++;
                await _providerGate.WaitAsync();
                byte[] raw;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    raw = await provider.SynthesizeAsync(textForSynthesis, voice, settings, timeout.Token);
                }
                finally
                {
                    _providerGate.Release();
                }

                wav = AudioPipeline.NormalizeToGameWav(raw);
                validation = AudioValidator.Validate(wav, text);
                if (validation.Ok || attempt >= 2)
                {
                    break;
                }
            }

            if (!validation.Ok)
            {
                Record(startedAt, text, voicePath, voice, provider.Id, stopwatch.Elapsed, false, validation.Failure, false, null, enriched);
                return new JobStatus(JobState.Failed, null, validation.Failure);
            }

            var path = _cache.Store(cacheKey, wav);
            Record(startedAt, text, voicePath, voice, provider.Id, stopwatch.Elapsed, true, null, validation.ClippingWarning, path, enriched);
            return new JobStatus(JobState.Done, path, null);
        }
        catch (Exception exception)
        {
            Record(startedAt, text, voicePath, voice, providerId, stopwatch.Elapsed, false, exception.Message, false, null);
            return new JobStatus(JobState.Failed, null, exception.Message);
        }
    }

    private async Task<IReadOnlyList<TtsVoice>> GetVoicesCachedAsync(ITtsProvider provider, ProviderSettings settings)
    {
        var key = provider.Id + "|" + settings.CanonicalHash();
        if (_voiceListCacheKey == key && _voiceListCache is not null)
        {
            return _voiceListCache;
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var voices = await provider.ListVoicesAsync(settings, timeout.Token);
            _voiceListCache = voices;
            _voiceListCacheKey = key;
            return voices;
        }
        catch (Exception)
        {
            return _voiceListCache ?? Array.Empty<TtsVoice>();
        }
    }

    /// <summary>Drops the memoized voice list (call when settings change).</summary>
    public void InvalidateVoiceCache()
    {
        _voiceListCache = null;
        _voiceListCacheKey = null;
    }

    private void Record(DateTimeOffset timestamp, string text, string voicePath, string voice, string provider, TimeSpan elapsed, bool success, string? failure, bool clipping, string? wavPath, string? enrichedText = null)
    {
        var entry = new SynthHistoryEntry(timestamp, text, voicePath, voice, provider, elapsed, success, failure, clipping, wavPath, enrichedText);
        _history.Enqueue(entry);
        while (_history.Count > 200 && _history.TryDequeue(out _))
        {
        }
        Synthesized?.Invoke(entry);
    }
}
