using System.Net.Http;
using System.Text.Json;

namespace CustomVoicedDialogue.App;

public sealed record AvailableUpdate(string Version, string Url);

/// <summary>
/// Checks the project's GitHub releases so fixes can be pushed by simply
/// publishing a new release.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "DCCStudios/F4-CustomVoicedDialogue";

    public static string CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static async Task<AvailableUpdate?> CheckAsync()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CustomVoicedDialogue");
        var json = await client.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest");
        using var document = JsonDocument.Parse(json);
        var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "";
        var url = document.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases";

        var latest = ParseVersion(tag);
        var current = ParseVersion(CurrentVersion);
        return latest > current ? new AvailableUpdate(tag.TrimStart('v', 'V'), url) : null;
    }

    internal static Version ParseVersion(string value)
    {
        var cleaned = value.TrimStart('v', 'V');
        var dash = cleaned.IndexOf('-');
        if (dash >= 0)
        {
            cleaned = cleaned[..dash];
        }
        return Version.TryParse(cleaned, out var version) ? version : new Version(0, 0, 0);
    }
}
