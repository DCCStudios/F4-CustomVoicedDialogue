using System.Collections.Concurrent;
using CustomVoicedDialogue.Server.Audio;
using CustomVoicedDialogue.Server.Cache;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Lines;
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
    private readonly LineLog _lineLog;
    private readonly VoiceMapper _voiceMapper;
    private readonly System.Threading.Timer _validationTimer;
    // Extra takes requested from the app, by voice path.  A line's natural
    // generation is take 0; "regenerate" bumps this, which shifts the
    // tagging and the cache key so a genuinely different reading comes back
    // instead of the cached one.
    private readonly ConcurrentDictionary<string, int> _variants = new(StringComparer.OrdinalIgnoreCase);
    // Voice files the game still holds an old take of.  The plugin collects
    // these when it checks in and deletes its copies, so the next encounter
    // with the line picks up the take chosen here.
    private readonly ConcurrentDictionary<string, byte> _invalidated = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<JobStatus>>> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<SynthHistoryEntry> _history = new();
    // Concurrent provider calls.  A dialogue wheel is up to ~9 lines and
    // synthesis dominates a line's cost (measured ~1.2s against ~0.4s for
    // tagging), so the gate is what decides how long a wheel takes: at 4 it
    // took 2430ms end to end, at 9 it took 994ms with no errors.  Eight
    // clears a normal wheel in one wave while keeping a little headroom —
    // providers do enforce rate limits (a 429 is observable under heavier
    // parallel load), and a rejected line falls back to silence.
    private readonly SemaphoreSlim _providerGate = new(8);
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
        _lineLog = new LineLog(config.ResolveLineLogPath());
        // A take chosen in the app has to outlive the app.  Without this the
        // next session would generate the line at take 0 again and quietly
        // undo the user's choice the first time the game asked for it.
        foreach (var record in _lineLog.Records.Where(record => record.Variant > 0))
        {
            _variants[record.VoicePath] = record.Variant;
        }
        _voiceMapper = new VoiceMapper(config);
        // Generated audio can disappear between sessions (a cleared MO2
        // Overwrite, a hand-deleted wav), so the catalogue re-checks itself
        // on a slow heartbeat rather than only when someone opens the tab.
        _validationTimer = new System.Threading.Timer(
            _ => ValidateLines(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20));
    }

    public SoundCache Cache => _cache;

    /// <summary>The catalogue of generated lines, their tagging, and the
    /// voice files they were written to.</summary>
    public LineLog Lines => _lineLog;

    /// <summary>Re-checks every catalogued line against the disk.</summary>
    public int ValidateLines() => _lineLog.Validate(_cache.PathFor, _config.GameRoot);

    /// <summary>Hands the plugin the voice files whose game-side copy is now
    /// out of date, and clears them.  Deleting is idempotent on the plugin
    /// side, so a delivery lost to a crash costs nothing worse than the old
    /// take surviving until the line is regenerated again.</summary>
    public IReadOnlyList<string> TakePendingInvalidations()
    {
        if (_invalidated.IsEmpty)
        {
            return Array.Empty<string>();
        }
        var paths = _invalidated.Keys.ToList();
        foreach (var path in paths)
        {
            _invalidated.TryRemove(path, out _);
        }
        return paths;
    }

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
        // hand-lexicon-only stage, "ipa2" added rule-derived pronunciations, "ipa3" the accent-true r symbols, "ipa4" the Australian vowel overhaul, "ipa5" its KT-Speech corrections, "ipa7" its weakened-glide PRICE, "ipa8" merged IPA spans, "ipa9" the Grimes yeah stretch, "ipa10" its general vowel-holding, "ipa11" stress-tripled emphasis (reverted, caused pauses), "ipa12" restored walk/talk l, "ipa13" unheld before l, "ipa14" hold only the stressed vowel, "ipa15" bridged spans, "ipa16" holds only on standalone FLEECE/GOOSE, "ipa17" clause-lead absorption, "ipa18" THOUGHT holds and onset-cluster fix, "ipa19" guaranteed voice texture (vocal fry), "ipa20" stable single-length "dog"/"dogs" override (the doubled /ɔːː/ drifted to "darg"), "ipa21" dropped "breath catching in the throat" from the Grimes voice texture.
        // Bumping it regenerates accented audio after a mechanism change.
        // Note the fingerprint keys on accent.Id, not the VoiceTexture
        // string, so a texture change only reaches existing lines through
        // this marker.
        var accentPart = accent.IsNeutral ? "" : $"|ipa21:{accent.Id}|{_config.AccentImperfection}";
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
        var accentPart = accents.Length == 0 ? "" : $"|ipa19:{accents}|{_config.AccentImperfection}";
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
    public async Task<JobStatus> RequestAsync(string text, string voicePath, string voiceType, bool isPlayer, CancellationToken cancellationToken, bool inlineWait = true, string scene = "", string direction = "")
    {
        // A voice change since the last request retires every finished job.
        SyncVoiceGeneration();

        // Up to one retry: a completed job whose audio file has since been
        // deleted (cache clear, voice-change invalidation) must be dropped
        // and synthesized fresh — returning the stale WavPath made the
        // endpoint throw on every request for that line until restart.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var variant = _variants.TryGetValue(voicePath, out var requested) ? requested : 0;
            var job = _jobs.GetOrAdd(
                voicePath,
                _ => new Lazy<Task<JobStatus>>(() => RunJobAsync(text, voicePath, voiceType, isPlayer, variant, scene, direction)));

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

    /// <summary>One take of a line for the Lines tab's take picker: what was
    /// spoken, its cached wav, whether that audio still exists, and whether
    /// it is the take currently in force.</summary>
    public sealed record TakeInfo(
        int Variant, string TaggedText, string Scene, string CustomPrompt,
        string CacheKey, string WavPath, bool Available, bool IsActive, DateTimeOffset Generated);

    /// <summary>Every take of a line, oldest first, each with its cached wav
    /// and whether that audio is still on disk.</summary>
    public IReadOnlyList<TakeInfo> TakesFor(string voicePath)
    {
        var active = _lineLog.Find(voicePath);
        return _lineLog.TakesFor(voicePath)
            .Select(take =>
            {
                var wav = string.IsNullOrEmpty(take.CacheKey) ? "" : _cache.PathFor(take.CacheKey);
                var available = wav.Length > 0 && File.Exists(wav);
                var isActive = active is not null &&
                    string.Equals(active.CacheKey, take.CacheKey, StringComparison.OrdinalIgnoreCase);
                return new TakeInfo(take.Variant, take.TaggedText, take.Scene, take.CustomPrompt,
                    take.CacheKey, wav, available, isActive, take.Generated);
            })
            .OrderBy(take => take.Variant)
            .ToList();
    }

    /// <summary>Makes a previously generated take the active one for a line:
    /// its cached audio is served for the line and the plugin swaps the game
    /// file, so the game plays it on its next encounter.  Returns false when
    /// that take's audio is no longer in the cache.</summary>
    public bool SelectTake(string voicePath, string cacheKey)
    {
        var take = _lineLog.TakesFor(voicePath)
            .FirstOrDefault(t => string.Equals(t.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase));
        if (take is null || string.IsNullOrEmpty(take.CacheKey))
        {
            return false;
        }
        var wav = _cache.PathFor(take.CacheKey);
        if (!File.Exists(wav))
        {
            return false;
        }

        // Re-record the take so it becomes the active one (last-wins), serve
        // this take's cached wav for the line, and have the plugin swap the
        // game file on its next check-in.  A later Regenerate still advances
        // past every take, since NextVariant reads the whole history.
        _lineLog.Record(take with { Generated = DateTimeOffset.Now, Health = LineHealth.Unverified });
        _jobs[voicePath] = new Lazy<Task<JobStatus>>(Task.FromResult(new JobStatus(JobState.Done, wav, null)));
        _invalidated[voicePath] = 0;
        ValidateLines();
        return true;
    }

    /// <summary>Deletes every take of a line EXCEPT the active one: frees the
    /// earlier takes' cached audio and prunes the history, but leaves the
    /// line playing its current take (no game-file change).</summary>
    public void DeleteOtherTakes(string voicePath)
    {
        var active = _lineLog.Find(voicePath);
        if (active is null)
        {
            return;
        }
        foreach (var take in _lineLog.TakesFor(voicePath))
        {
            if (!string.IsNullOrEmpty(take.CacheKey) &&
                !string.Equals(take.CacheKey, active.CacheKey, StringComparison.OrdinalIgnoreCase))
            {
                _cache.Delete(take.CacheKey);
            }
        }
        _lineLog.KeepOnlyTake(voicePath, active.CacheKey);
    }

    /// <summary>Deletes every take of a line — its cached audio and its
    /// catalogue entry — and tells the plugin to remove the game file.</summary>
    public void DeleteAllTakes(string voicePath)
    {
        foreach (var take in _lineLog.TakesFor(voicePath))
        {
            if (!string.IsNullOrEmpty(take.CacheKey))
            {
                _cache.Delete(take.CacheKey);
            }
        }
        _jobs.TryRemove(voicePath, out _);
        _variants.TryRemove(voicePath, out _);
        _lineLog.Forget(voicePath);
        _invalidated[voicePath] = 0;
    }

    /// <summary>Files a generated line in the catalogue: its text, the
    /// tagging that shaped the performance, and the voice file it belongs
    /// to.  Silence carriers never reach here — this only ever sees the
    /// engine's real dialogue path.</summary>
    private void Catalogue(
        string text, string taggedText, string voicePath, string voiceType,
        string voice, string providerId, Accent accent, bool isPlayer, int variant, string cacheKey, string scene, string direction)
    {
        // Only an actual Direct (a non-empty direction) changes the stored
        // direction.  A normal generation or a plain Regenerate keeps
        // whatever the line was last directed with, so the text is never lost
        // and is still there when Direct is next opened.
        var customPrompt = string.IsNullOrEmpty(direction)
            ? _lineLog.Find(voicePath)?.CustomPrompt ?? ""
            : direction;
        var record = new LineRecord
        {
            VoicePath = voicePath,
            Text = text,
            TaggedText = taggedText,
            Voice = voice,
            VoiceType = voiceType,
            Provider = providerId,
            Accent = accent.IsNeutral ? "" : accent.Id,
            Scene = scene,
            CustomPrompt = customPrompt,
            IsPlayer = isPlayer,
            Variant = variant,
            CacheKey = cacheKey,
            Generated = DateTimeOffset.Now,
            Health = LineHealth.Unverified,
        };
        _lineLog.Record(record);
    }

    /// <summary>Generates a fresh take of an already-generated line: a new
    /// take number, so the tagging (and therefore the performance) differs
    /// from the one on record.  Used by the Lines tab to re-roll a reading
    /// until it sounds right.  Returns the finished status, or null when the
    /// line is not in the catalogue.</summary>
    /// <param name="direction">Optional direction from the user for this one
    /// line ("sound exhausted, almost out of breath").  It steers the take
    /// instead of leaving the reading entirely to chance.</param>
    public async Task<JobStatus?> RegenerateAsync(string voicePath, string? direction = null, CancellationToken cancellationToken = default)
    {
        var record = _lineLog.Find(voicePath);
        if (record is null)
        {
            return null;
        }

        _variants[voicePath] = _lineLog.NextVariant(voicePath);
        // Drop the finished job so the next request actually re-runs rather
        // than handing back the completed take.
        _jobs.TryRemove(voicePath, out _);

        // A plain Regenerate (direction == null) re-rolls with the NORMAL
        // rules and tagging — it does not re-apply the directed text.  The
        // directed text is not lost, though: it stays on record (see
        // Catalogue) so it is still there next time Direct is opened.  A
        // fresh Direct passes its own text to apply it again.
        //
        // The retake keeps the scene the line was first generated in, so
        // re-rolling a line spoken under gunfire does not quietly reroll it
        // as a calm one.
        var status = await RequestAsync(
            record.Text, voicePath, record.VoiceType, record.IsPlayer, cancellationToken,
            scene: record.Scene, direction: direction ?? "");

        // Regeneration is a deliberate, foreground action whose whole point
        // is hearing the result, so it waits for the audio rather than
        // returning the moment the inline wait lapses.
        for (var i = 0; i < 60 && status.State == JobState.Synthesizing; i++)
        {
            var polled = await QueryAsync(voicePath, 2000);
            if (polled is null)
            {
                break;
            }
            status = polled;
        }

        if (status.State == JobState.Done)
        {
            // The game still has the previous take written at this path and
            // would keep playing it; queue it for the plugin to remove.
            _invalidated[voicePath] = 0;
        }
        ValidateLines();
        return status;
    }

    private async Task<JobStatus> RunJobAsync(string text, string voicePath, string voiceType, bool isPlayer, int variant = 0, string scene = "", string direction = "")
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
            var accent = ResolveAccent(isPlayer, voiceType);
            // The line key drives the deterministic take number (and the
            // accent's slip pattern).  Folding the variant in is what makes
            // "regenerate" produce a different reading instead of repeating
            // the cached one; variant 0 keeps the original key exactly, so
            // adding this feature invalidates nobody's existing audio.
            var lineKey = variant > 0 ? $"{voicePath}#{variant}" : voicePath;

            // A take already chosen in the app is reproduced from its record,
            // not derived again: retake tagging samples loosely on purpose, so
            // re-deriving it would hand back a different reading than the one
            // that was picked (and miss the cache doing it).  Regeneration
            // asks for the NEXT take, so it never matches here and tags fresh.
            var chosen = variant > 0 ? _lineLog.Find(voicePath) : null;
            if (chosen is not null && chosen.Variant == variant &&
                !string.IsNullOrEmpty(chosen.TaggedText) &&
                string.Equals(chosen.Text, text, StringComparison.Ordinal))
            {
                textForSynthesis = chosen.TaggedText;
            }
            else if (provider is InworldProvider inworldProvider)
            {
                textForSynthesis = await inworldProvider.AutoTagAsync(
                    text, voiceType, isPlayer, settings, CancellationToken.None, lineKey,
                    accent, _config.AccentImperfection, variant, scene, _config.ShoutInCombat, direction);
            }
            var enriched = string.Equals(textForSynthesis, text, StringComparison.Ordinal) ? null : textForSynthesis;

            // The variant also joins the audio identity: a re-take whose
            // tagging happens to land identically must still not serve the
            // previous take's wav straight back from the cache.
            var optionsHash = variant > 0 ? $"{settings.CanonicalHash()}|take{variant}" : settings.CanonicalHash();
            var cacheKey = SoundCache.ComputeKey(provider.Id, voice, optionsHash, textForSynthesis);
            if (_cache.TryGet(cacheKey, out var cachedPath))
            {
                Record(startedAt, text, voicePath, voice, provider.Id, stopwatch.Elapsed, true, null, false, cachedPath, enriched);
                Catalogue(text, textForSynthesis, voicePath, voiceType, voice, provider.Id, accent, isPlayer, variant, cacheKey, scene, direction);
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
            Catalogue(text, textForSynthesis, voicePath, voiceType, voice, provider.Id, accent, isPlayer, variant, cacheKey, scene, direction);
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
