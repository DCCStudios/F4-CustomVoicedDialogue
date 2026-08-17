using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomVoicedDialogue.Server.Config;

/// <summary>
/// Persistent application configuration.  Stored as JSON next to the exe
/// when that directory is writable (portable installs), otherwise under
/// %APPDATA%\CustomVoicedDialogue.  API keys are DPAPI-protected per user
/// before they touch disk.
/// </summary>
public sealed class AppConfig
{
    public int Port { get; set; } = 47600;

    /// <summary>Active provider id (e.g. "elevenlabs").</summary>
    public string Provider { get; set; } = "";

    /// <summary>Per-provider option values, keyed by provider id.  Secret
    /// options are stored DPAPI-encrypted with a "dpapi:" prefix.</summary>
    public Dictionary<string, Dictionary<string, string>> ProviderSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Voice used for the player's own lines.</summary>
    public string PlayerVoice { get; set; } = "";

    /// <summary>Voice NPC lines fall back to per voice type; empty entries
    /// use deterministic auto-assignment.</summary>
    public Dictionary<string, string> NpcVoiceOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Accent id the player's lines are performed in ("american"
    /// is the neutral default and adds no direction at all).</summary>
    public string PlayerAccent { get; set; } = VoiceMapping.Accents.Default;

    /// <summary>Accent id per NPC voice type; unlisted voice types are
    /// neutral.</summary>
    public Dictionary<string, string> NpcAccentOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How often an accent slips (0–100).  Real speakers are not
    /// perfectly consistent, so a little wobble sounds more human than a
    /// flawless one; 0 performs every line identically.</summary>
    public int AccentImperfection { get; set; } = 15;

    /// <summary>Whether a line shouted in combat at someone hostile is
    /// performed at full volume.  Deliberately an app setting rather than a
    /// provider option: provider options feed the voice fingerprint, so
    /// adding one there would delete and regenerate every existing line.
    /// Changing this shapes newly generated lines only.</summary>
    public bool ShoutInCombat { get; set; } = true;

    public bool StartServerOnLaunch { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    /// <summary>Where generated/cached wavs live; defaults beside config.</summary>
    public string? CacheDirectory { get; set; }

    /// <summary>The game folder the plugin writes voice files into, reported
    /// by the plugin when it checks in.  Persisted so the line catalogue can
    /// still tell whether generated audio survives on disk before the game
    /// has been launched this session.</summary>
    public string? GameRoot { get; set; }

    [JsonIgnore]
    public string ConfigPath { get; private set; } = "";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultConfigPath()
    {
        var exeDirectory = AppContext.BaseDirectory;
        var portablePath = Path.Combine(exeDirectory, "CustomVoicedDialogue.config.json");
        try
        {
            // Probe writability: portable installs keep everything together.
            var probe = Path.Combine(exeDirectory, ".write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portablePath;
        }
        catch (Exception)
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CustomVoicedDialogue");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "CustomVoicedDialogue.config.json");
        }
    }

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath();
        AppConfig config;
        if (File.Exists(path))
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), SerializerOptions) ?? new AppConfig();
        }
        else
        {
            config = new AppConfig();
        }
        config.ConfigPath = path;
        return config;
    }

    public void Save()
    {
        var path = string.IsNullOrEmpty(ConfigPath) ? DefaultConfigPath() : ConfigPath;
        ConfigPath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    public string ResolveCacheDirectory()
    {
        if (!string.IsNullOrEmpty(CacheDirectory))
        {
            return CacheDirectory;
        }
        var configDirectory = Path.GetDirectoryName(string.IsNullOrEmpty(ConfigPath) ? DefaultConfigPath() : ConfigPath)!;
        return Path.Combine(configDirectory, "soundcache");
    }

    /// <summary>The generated-line catalogue: a plain text file beside the
    /// config, readable and searchable outside the app.</summary>
    public string ResolveLineLogPath()
    {
        var configDirectory = Path.GetDirectoryName(string.IsNullOrEmpty(ConfigPath) ? DefaultConfigPath() : ConfigPath)!;
        return Path.Combine(configDirectory, "CustomVoicedDialogue.lines.txt");
    }

    // ---- secret protection ------------------------------------------------

    private const string DpapiPrefix = "dpapi:";

    public static string ProtectSecret(string plainText)
    {
        if (string.IsNullOrEmpty(plainText) || plainText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return plainText;
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), null, DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(protectedBytes);
    }

    public static string UnprotectSecret(string storedValue)
    {
        if (string.IsNullOrEmpty(storedValue) || !storedValue.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            return storedValue;
        }
        try
        {
            var protectedBytes = Convert.FromBase64String(storedValue[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Settings for a provider with secrets decrypted, ready for a
    /// provider call.</summary>
    public Providers.ProviderSettings SettingsFor(Providers.ITtsProvider provider)
    {
        var stored = ProviderSettings.TryGetValue(provider.Id, out var values)
            ? new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in provider.Options)
        {
            if (option.Kind == Providers.OptionKind.Secret && stored.TryGetValue(option.Key, out var secret))
            {
                stored[option.Key] = UnprotectSecret(secret);
            }
        }
        return new Providers.ProviderSettings(stored).WithDefaults(provider);
    }

    /// <summary>Stores option values for a provider, encrypting secrets.</summary>
    public void StoreSettings(Providers.ITtsProvider provider, IReadOnlyDictionary<string, string> values)
    {
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var option = provider.Options.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
            stored[key] = option?.Kind == Providers.OptionKind.Secret ? ProtectSecret(value) : value;
        }
        ProviderSettings[provider.Id] = stored;
    }
}
