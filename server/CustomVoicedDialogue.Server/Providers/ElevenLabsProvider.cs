using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// ElevenLabs hosted TTS.  Request shape ported 1:1 from HerikaServer's
/// tts-11labs.php: POST /v1/text-to-speech/{voice} with xi-api-key,
/// voice_settings block, optional normalization flags, mp3 response.
/// </summary>
public sealed class ElevenLabsProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public ElevenLabsProvider(HttpClient http) => _http = http;

    public string Id => "elevenlabs";
    public string DisplayName => "ElevenLabs";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://elevenlabs.io/app/settings/api-keys";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("API_KEY", "API key", OptionKind.Secret, ""),
        new("voice_id", "Voice ID", OptionKind.Text, "EXAVITQu4vr4xnSDxMaL", "Default voice; NPC auto-assignment picks from your voice library."),
        new("model_id", "Model", OptionKind.Choice, "eleven_multilingual_v2", "eleven_v3 enables audio tags.",
            Choices: ["eleven_monolingual_v1", "eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5", "eleven_v3"]),
        new("stability", "Stability", OptionKind.Number, "0.75", "Higher values sound steadier and less varied.", 0, 1),
        new("similarity_boost", "Similarity boost", OptionKind.Number, "0.75", "Higher values cling more closely to the selected voice.", 0, 1),
        new("style", "Style", OptionKind.Number, "0.0", "Extra style exaggeration; can increase latency.", 0, 1),
        new("speed", "Speed", OptionKind.Number, "1.0", "Speaking rate.", 0.7, 1.2),
        new("use_speaker_boost", "Speaker boost", OptionKind.Toggle, "true", "Boosts resemblance to the original voice (ignored by eleven_v3)."),
        new("apply_text_normalization", "Text normalization", OptionKind.Choice, "auto", "Rewrites numbers, dates, abbreviations before speech.", Choices: ["auto", "on", "off"]),
        new("apply_language_text_normalization", "Language normalization", OptionKind.Toggle, "false", "Extra language-specific cleanup."),
        new("language_code", "Language code", OptionKind.Text, "", "Optional ISO code for multilingual models."),
        new("v3_audio_tags", "V3 audio tags", OptionKind.Text, "", "Optional prompt tags prefixed to the text (eleven_v3 only), e.g. [whispers]."),
        new("optimize_streaming_latency", "Latency optimization", OptionKind.Number, "0", "0 keeps the default quality/latency balance.", 0, 4),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var voiceId = string.IsNullOrEmpty(voice) ? settings.Get("voice_id") : voice;
        var modelId = settings.Get("model_id", "eleven_multilingual_v2");
        var isV3 = modelId.Equals("eleven_v3", StringComparison.OrdinalIgnoreCase);

        var requestText = text;
        var tags = settings.Get("v3_audio_tags");
        if (isV3 && !string.IsNullOrEmpty(tags))
        {
            requestText = $"{tags} {text}".Trim();
        }

        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}";
        var latency = settings.GetInt("optimize_streaming_latency", 0);
        if (latency > 0)
        {
            url += $"?optimize_streaming_latency={latency}";
        }

        var voiceSettings = new Dictionary<string, object>
        {
            ["stability"] = settings.GetDouble("stability", 0.75),
            ["similarity_boost"] = settings.GetDouble("similarity_boost", 0.75),
            ["style"] = settings.GetDouble("style", 0.0),
            ["speed"] = settings.GetDouble("speed", 1.0),
        };
        if (!isV3)
        {
            voiceSettings["use_speaker_boost"] = settings.GetBool("use_speaker_boost", true);
        }

        var body = new Dictionary<string, object>
        {
            ["text"] = requestText,
            ["model_id"] = modelId,
            ["voice_settings"] = voiceSettings,
            ["apply_language_text_normalization"] = settings.GetBool("apply_language_text_normalization", false),
        };
        var normalization = settings.Get("apply_text_normalization").ToLowerInvariant();
        if (normalization is "auto" or "on" or "off")
        {
            body["apply_text_normalization"] = normalization;
        }
        var languageCode = settings.Get("language_code");
        if (!string.IsNullOrEmpty(languageCode))
        {
            body["language_code"] = languageCode;
        }

        return await ProviderHelpers.PostForAudioAsync(
            _http, url, body, cancellationToken,
            request => request.Headers.Add("xi-api-key", settings.Get("API_KEY")),
            accept: "audio/mpeg");
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
        request.Headers.Add("xi-api-key", settings.Get("API_KEY"));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var voices = new List<TtsVoice>();
        foreach (var voice in json.RootElement.GetProperty("voices").EnumerateArray())
        {
            var id = voice.GetProperty("voice_id").GetString() ?? "";
            var name = voice.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? id : id;
            var gender = VoiceGender.Unknown;
            if (voice.TryGetProperty("labels", out var labels) &&
                labels.ValueKind == JsonValueKind.Object &&
                labels.TryGetProperty("gender", out var genderElement))
            {
                gender = genderElement.GetString()?.ToLowerInvariant() switch
                {
                    "female" => VoiceGender.Female,
                    "male" => VoiceGender.Male,
                    _ => VoiceGender.Unknown,
                };
            }
            voices.Add(new TtsVoice(id, name, gender));
        }
        return voices;
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var voices = await ListVoicesAsync(settings, cancellationToken);
            return $"authenticated; {voices.Count} voice(s) in your library";
        });
}
