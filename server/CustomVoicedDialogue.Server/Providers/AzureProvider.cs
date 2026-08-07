using System.Security;
using System.Text;
using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// Azure Cognitive Services TTS.  Ported from tts-azure.php: fetch a
/// bearer token from the region's issueToken endpoint (cached ~8 min),
/// then POST SSML (prosody + optional mstts:express-as style) for RIFF
/// 24 kHz 16-bit mono PCM.
/// </summary>
public sealed class AzureProvider : ITtsProvider
{
    private readonly HttpClient _http;
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry;

    public AzureProvider(HttpClient http) => _http = http;

    public string Id => "azure";
    public string DisplayName => "Azure Speech";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("API_KEY", "API key", OptionKind.Secret, ""),
        new("region", "Region", OptionKind.Text, "eastus", "The Azure region of your Speech resource, e.g. eastus, westeurope."),
        new("voice", "Voice", OptionKind.Text, "en-US-NancyNeural"),
        new("rate", "Rate", OptionKind.Number, "1.15", "Speech rate multiplier.", 0.5, 2),
        new("volume", "Volume", OptionKind.Number, "20", "Relative volume boost in percent.", -50, 50),
        new("contour", "Pitch contour", OptionKind.Text, "", "Optional SSML pitch contour, e.g. (11%, +15%) (60%, -23%)."),
        new("style", "Style", OptionKind.Text, "", "Optional mstts:express-as style, e.g. cheerful, whispering."),
    ];

    private async Task<string> GetTokenAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiry)
        {
            return _cachedToken;
        }
        var region = settings.Get("region", "eastus");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken");
        request.Headers.Add("Ocp-Apim-Subscription-Key", settings.Get("API_KEY"));
        request.Content = new ByteArrayContent([]);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        _cachedToken = await response.Content.ReadAsStringAsync(cancellationToken);
        _tokenExpiry = DateTimeOffset.UtcNow.AddMinutes(8);
        return _cachedToken;
    }

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(settings, cancellationToken);
        var region = settings.Get("region", "eastus");
        var voiceName = string.IsNullOrEmpty(voice) ? settings.Get("voice", "en-US-NancyNeural") : voice;

        var ssml = BuildSsml(
            text,
            voiceName,
            settings.GetDouble("rate", 1.15),
            settings.GetDouble("volume", 20),
            settings.Get("contour"),
            settings.Get("style"));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
        request.Headers.Add("User-Agent", "CustomVoicedDialogue");
        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        using var response = await _http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Azure TTS returned {(int)response.StatusCode}: {ProviderHelpers.TrimForError(bytes)}");
        }
        return bytes;
    }

    internal static string BuildSsml(string text, string voiceName, double rate, double volume, string contour, string style)
    {
        var prosodyAttributes = new StringBuilder($"rate=\"{rate.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" volume=\"+{volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}%\"");
        if (!string.IsNullOrEmpty(contour))
        {
            prosodyAttributes.Append($" contour=\"{SecurityElement.Escape(contour)}\"");
        }

        var body = $"<prosody {prosodyAttributes}>{SecurityElement.Escape(text)}</prosody>";
        if (!string.IsNullOrEmpty(style))
        {
            body = $"<mstts:express-as style=\"{SecurityElement.Escape(style)}\" styledegree=\"2\">{body}</mstts:express-as>";
        }

        return
            "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" " +
            "xmlns:mstts=\"https://www.w3.org/2001/mstts\" xml:lang=\"en-US\">" +
            $"<voice name=\"{SecurityElement.Escape(voiceName)}\">{body}</voice></speak>";
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(settings, cancellationToken);
        var region = settings.Get("region", "eastus");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var voices = new List<TtsVoice>();
        foreach (var voice in json.RootElement.EnumerateArray())
        {
            var shortName = voice.GetProperty("ShortName").GetString() ?? "";
            var gender = voice.TryGetProperty("Gender", out var genderElement)
                ? genderElement.GetString() switch
                {
                    "Female" => VoiceGender.Female,
                    "Male" => VoiceGender.Male,
                    _ => VoiceGender.Unknown,
                }
                : VoiceGender.Unknown;
            voices.Add(new TtsVoice(shortName, shortName, gender));
        }
        return voices;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            await GetTokenAsync(settings, cancellationToken);
            return $"token issued for region {settings.Get("region", "eastus")}";
        });
}
