using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomVoicedDialogue.Server.Lines;

/// <summary>Where a generated line's audio currently stands on disk.</summary>
public enum LineHealth
{
    /// <summary>Both the cached wav and the game's voice file are present.</summary>
    Ok,

    /// <summary>The cached wav is present but the game's voice file is gone
    /// (deleted by hand, or the mod folder was cleaned) — the line will be
    /// written again the next time the game asks for it.</summary>
    MissingInGame,

    /// <summary>The cached wav is gone, so the line has to be synthesized
    /// again from scratch.</summary>
    MissingInCache,

    /// <summary>Neither copy survives.</summary>
    Missing,

    /// <summary>The game folder is not known yet (the plugin has not checked
    /// in this session), so only the cache side could be checked.</summary>
    Unverified,
}

/// <summary>One generated line: what was asked for, what was actually spoken
/// after tagging, and which voice file in the game's Sound folder carries
/// it.  This is the record the log persists.</summary>
public sealed record LineRecord
{
    /// <summary>Data-relative engine voice path, e.g.
    /// "Sound\Voice\Fallout4.esm\PlayerVoiceMale01\0001ABC2_1.wav".  This is
    /// the real dialogue file — never one of the silence carriers.</summary>
    [JsonPropertyName("file")]
    public string VoicePath { get; init; } = "";

    /// <summary>The line as the game wrote it.</summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    /// <summary>What was actually sent to the synthesizer: steering
    /// instruction, non-verbal tags, emphasis and accent pronunciation
    /// included.  Equal to <see cref="Text"/> when nothing was added.</summary>
    [JsonPropertyName("tagged")]
    public string TaggedText { get; init; } = "";

    [JsonPropertyName("voice")]
    public string Voice { get; init; } = "";

    [JsonPropertyName("voiceType")]
    public string VoiceType { get; init; } = "";

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("accent")]
    public string Accent { get; init; } = "";

    /// <summary>The scene the line was generated in, as the game reported it
    /// (in combat, sneaking, a hostile listener).  Empty for an ordinary
    /// conversation.  Recorded because it explains why a take sounds the way
    /// it does when auditioning one against another.</summary>
    [JsonPropertyName("scene")]
    public string Scene { get; init; } = "";

    /// <summary>Direction the user typed for this specific line when they
    /// last regenerated it, if any.  Kept so the Lines tab can show what was
    /// asked for and offer it again as the starting point.</summary>
    [JsonPropertyName("prompt")]
    public string CustomPrompt { get; init; } = "";

    [JsonPropertyName("isPlayer")]
    public bool IsPlayer { get; init; }

    /// <summary>Which take this is.  0 is the line's natural first
    /// generation; each "regenerate" in the app bumps it, which shifts the
    /// tagging and gives a genuinely different reading.</summary>
    [JsonPropertyName("variant")]
    public int Variant { get; init; }

    /// <summary>Content-addressed key of the audio in the server cache.</summary>
    [JsonPropertyName("cacheKey")]
    public string CacheKey { get; init; } = "";

    [JsonPropertyName("generated")]
    public DateTimeOffset Generated { get; init; }

    [JsonIgnore]
    public LineHealth Health { get; set; } = LineHealth.Unverified;
}

/// <summary>
/// The catalogue of every line this app has generated: its text, the tagging
/// that shaped the performance, and the voice file it was written to.
///
/// It is a plain text file (one JSON object per line) so it can be read,
/// searched and diffed outside the app.  Beyond the record, it is what lets
/// the app notice when generated audio disappears from disk — a hand-deleted
/// wav, a cleared Overwrite folder — and offer to make it again.
/// </summary>
public sealed class LineLog
{
    private readonly string _path;
    private readonly ConcurrentDictionary<string, LineRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    // Every take of every line, in append order (the active one is last).
    // Kept so the Lines tab can offer earlier takes to audition and restore;
    // their audio lives in the sound cache, keyed by each take's cacheKey.
    private readonly ConcurrentDictionary<string, List<LineRecord>> _takes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeLock = new();
    // Records appended since the file was last written whole.  A generated
    // line costs one appended line rather than a rewrite of the catalogue,
    // which otherwise grew with everything ever generated (measured 0.7ms at
    // 10 lines, 12ms at 5000).  The file is rewritten only when the slack
    // gets large enough to be worth reclaiming.
    private int _appendedSinceRewrite;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public LineLog(string path)
    {
        _path = path;
        Load();
    }

    public string Path => _path;

    /// <summary>Raised after a validation pass changed any line's health, so
    /// an open view can refresh without polling every record itself.</summary>
    public event Action? Changed;

    public IReadOnlyList<LineRecord> Records =>
        _records.Values.OrderByDescending(r => r.Generated).ToList();

    public LineRecord? Find(string voicePath) =>
        _records.TryGetValue(voicePath, out var record) ? record : null;

    public int Count => _records.Count;

    /// <summary>Records a generated line, replacing any earlier take of the
    /// same voice file (the newest generation is the one on disk).</summary>
    public void Record(LineRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.VoicePath))
        {
            return;
        }
        _records[record.VoicePath] = record;
        RememberTake(record);
        Append(record);
        Changed?.Invoke();
    }

    /// <summary>Adds a take to the per-line history, replacing any earlier
    /// occurrence of the same audio (cacheKey) so a re-selected take moves to
    /// the end (becomes active) rather than duplicating.</summary>
    private void RememberTake(LineRecord record)
    {
        var takes = _takes.GetOrAdd(record.VoicePath, _ => new List<LineRecord>());
        lock (takes)
        {
            takes.RemoveAll(t => string.Equals(t.CacheKey, record.CacheKey, StringComparison.OrdinalIgnoreCase));
            takes.Add(record);
        }
    }

    /// <summary>Every distinct take of a line, oldest first — the active take
    /// is last.  Their audio is in the sound cache, keyed by CacheKey.</summary>
    public IReadOnlyList<LineRecord> TakesFor(string voicePath)
    {
        if (!_takes.TryGetValue(voicePath, out var takes))
        {
            return Array.Empty<LineRecord>();
        }
        lock (takes)
        {
            return takes.ToList();
        }
    }

    /// <summary>Prunes a line's take history down to the one take with the
    /// given cacheKey (its active take), dropping every earlier take.  The
    /// line and its active take stay in place.</summary>
    public void KeepOnlyTake(string voicePath, string cacheKey)
    {
        if (_takes.TryGetValue(voicePath, out var takes))
        {
            lock (takes)
            {
                var keep = takes.FindLast(t => string.Equals(t.CacheKey, cacheKey, StringComparison.OrdinalIgnoreCase));
                takes.Clear();
                if (keep is not null)
                {
                    takes.Add(keep);
                }
            }
        }
        Rewrite();
        Changed?.Invoke();
    }

    /// <summary>Forgets a line entirely — every take (used when its audio is
    /// gone and the user clears it out rather than regenerating).</summary>
    public bool Forget(string voicePath)
    {
        var removed = _records.TryRemove(voicePath, out _);
        _takes.TryRemove(voicePath, out _);
        if (!removed)
        {
            return false;
        }
        // A removal cannot be expressed by appending, so this one rewrites.
        Rewrite();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The take number to use for the next regeneration of a line:
    /// one past the highest take ever made for it, so a new take never
    /// collides with an earlier one the user might still restore.</summary>
    public int NextVariant(string voicePath) =>
        _takes.TryGetValue(voicePath, out var takes) && takes.Count > 0
            ? takes.Max(t => t.Variant) + 1
            : 1;

    /// <summary>Re-checks every recorded line against the disk: the cached
    /// wav on this side, and the game's own voice file when the game folder
    /// is known.  Returns how many records changed health.</summary>
    public int Validate(Func<string, string> cachePathFor, string? gameDataRoot)
    {
        var changed = 0;
        foreach (var record in _records.Values)
        {
            var inCache = !string.IsNullOrEmpty(record.CacheKey) && File.Exists(cachePathFor(record.CacheKey));
            bool? inGame = null;
            if (!string.IsNullOrEmpty(gameDataRoot) && !string.IsNullOrEmpty(record.VoicePath))
            {
                inGame = File.Exists(System.IO.Path.Combine(gameDataRoot, "Data", record.VoicePath));
            }

            var health = (inCache, inGame) switch
            {
                (true, null) => LineHealth.Unverified,
                (true, true) => LineHealth.Ok,
                (true, false) => LineHealth.MissingInGame,
                (false, true) => LineHealth.MissingInCache,
                (false, false) => LineHealth.Missing,
                (false, null) => LineHealth.MissingInCache,
            };

            if (record.Health != health)
            {
                record.Health = health;
                changed++;
            }
        }
        if (changed > 0)
        {
            Changed?.Invoke();
        }
        return changed;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }
            try
            {
                var record = JsonSerializer.Deserialize<LineRecord>(line, SerializerOptions);
                if (record is not null && !string.IsNullOrWhiteSpace(record.VoicePath))
                {
                    // Last-wins for the active take; every take is retained for
                    // the history (the file is append-only, newest last).
                    _records[record.VoicePath] = record;
                    RememberTake(record);
                }
            }
            catch (JsonException)
            {
                // A truncated last line (power loss mid-write) must not cost
                // the whole catalogue.
            }
        }
    }

    private const string Header =
        "# CustomVoicedDialogue generated lines. One JSON record per line.\n" +
        "# \"file\" is the voice file in the game's Data folder; \"tagged\" is what was actually spoken.\n" +
        "# Later records win, so a line's newest take is the last one listed for its file.\n";

    /// <summary>Appends one record.  Loading applies last-wins, so a newer
    /// take of a line simply follows the older one instead of forcing the
    /// whole catalogue to be rewritten on every generated line.</summary>
    private void Append(LineRecord record)
    {
        lock (_writeLock)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                if (!File.Exists(_path))
                {
                    File.WriteAllText(_path, Header);
                }
                File.AppendAllText(_path, JsonSerializer.Serialize(record, SerializerOptions) + Environment.NewLine);
                _appendedSinceRewrite++;
            }
            catch (IOException)
            {
                // Losing a catalogue write must never take the app down.
                return;
            }
        }

        // Superseded takes accumulate as dead lines.  Reclaim them once they
        // could plausibly outnumber the live records, which for a catalogue
        // that only ever grows is effectively never.
        if (_appendedSinceRewrite > Math.Max(256, _records.Count))
        {
            Rewrite();
        }
    }

    /// <summary>Writes the whole catalogue: every retained take, grouped by
    /// line with the active take last, so a re-selected or earlier take
    /// survives a compaction instead of being dropped.</summary>
    private void Rewrite()
    {
        lock (_writeLock)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                var builder = new StringBuilder(Header.Replace("\n", Environment.NewLine));
                foreach (var voicePath in _takes.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var record in TakesFor(voicePath))
                    {
                        builder.AppendLine(JsonSerializer.Serialize(record, SerializerOptions));
                    }
                }
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, builder.ToString());
                File.Move(temporary, _path, overwrite: true);
                _appendedSinceRewrite = 0;
            }
            catch (IOException)
            {
                // Left as-is; the next rewrite will try again.
            }
        }
    }
}
