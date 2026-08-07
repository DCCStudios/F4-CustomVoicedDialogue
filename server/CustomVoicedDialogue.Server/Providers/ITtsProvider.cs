namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// One text-to-speech backend.  Providers are stateless: everything they
/// need arrives in the <see cref="ProviderSettings"/> for the call, so the
/// GUI can test alternative settings without touching saved config.
/// </summary>
public interface ITtsProvider
{
    /// <summary>Stable identifier used in config files ("elevenlabs").</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>True when the service is a hosted API needing an API key;
    /// false for services running on the user's machine.</summary>
    bool IsCloud { get; }

    /// <summary>Default port probed by local-service auto-detection.</summary>
    int? DefaultLocalPort { get; }

    /// <summary>Link to the provider's setup / API key page.</summary>
    string? HelpUrl { get; }

    /// <summary>Optional provider-specific usage notes shown on the
    /// settings page when this provider is selected (capabilities the
    /// option schema cannot express, e.g. inline markup syntax).</summary>
    string? UsageNotes => null;

    /// <summary>The full option schema; the GUI renders settings panels
    /// from this, so adding a provider never means new UI code.</summary>
    IReadOnlyList<ProviderOption> Options { get; }

    /// <summary>Synthesizes one line and returns the raw audio bytes in
    /// whatever container the service produces (wav/mp3/...).  The audio
    /// pipeline normalizes afterwards.</summary>
    Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken);

    /// <summary>Voices this provider can currently offer, for pickers and
    /// NPC auto-assignment.  May be a static list for cloud services with
    /// fixed voices, or a live query for local model servers.</summary>
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken);

    /// <summary>Cheapest possible liveness/auth check, surfaced by the GUI
    /// "Test connection" button and the first-run wizard gate.</summary>
    Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken);
}

public enum OptionKind
{
    Text,
    Secret,
    Number,
    Toggle,
    Choice,
}

/// <summary>One configurable field of a provider, rich enough for the GUI
/// to render a labeled, validated control.</summary>
public sealed record ProviderOption(
    string Key,
    string Label,
    OptionKind Kind,
    string DefaultValue,
    string? Description = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null);

public enum VoiceGender
{
    Unknown,
    Male,
    Female,
}

public sealed record TtsVoice(string Id, string DisplayName, VoiceGender Gender = VoiceGender.Unknown);

public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan Elapsed);

/// <summary>Resolved option values for one provider invocation.</summary>
public sealed class ProviderSettings
{
    private readonly Dictionary<string, string> _values;

    public ProviderSettings(IReadOnlyDictionary<string, string>? values = null)
    {
        _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values.ToDictionary(p => p.Key, p => p.Value), StringComparer.OrdinalIgnoreCase);
    }

    public static ProviderSettings Defaults(ITtsProvider provider) =>
        new(provider.Options.ToDictionary(o => o.Key, o => o.DefaultValue));

    /// <summary>Missing keys fall back to the provider's schema default.</summary>
    public ProviderSettings WithDefaults(ITtsProvider provider)
    {
        var merged = provider.Options.ToDictionary(o => o.Key, o => o.DefaultValue, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                merged[key] = value;
            }
        }
        return new ProviderSettings(merged);
    }

    public string Get(string key) => _values.TryGetValue(key, out var value) ? value : string.Empty;

    public string Get(string key, string fallback)
    {
        var value = Get(key);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback)
    {
        var value = Get(key);
        return value.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Canonical, order-independent digest of the option values
    /// that affect audio output — part of the audio cache key so changed
    /// settings can never serve stale audio (a HerikaServer bug this
    /// project deliberately fixes).</summary>
    public string CanonicalHash()
    {
        var canonical = string.Join(
            "\n",
            _values.Where(p => !string.IsNullOrEmpty(p.Value))
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => $"{p.Key.ToLowerInvariant()}={p.Value}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)));
    }
}
