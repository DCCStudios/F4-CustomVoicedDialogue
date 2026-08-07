using System.Net.Http.Headers;

namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// OpenAI speech API (and any OpenAI-compatible endpoint).  Ported from
/// HerikaServer's tts-openai.php: POST {endpoint} with bearer auth,
/// {input, model, voice}, mp3 response; gpt-4o-mini-tts adds instructions.
/// </summary>
public sealed class OpenAiProvider : ITtsProvider
{
    private static readonly string[] KnownVoices =
        ["alloy", "ash", "ballad", "coral", "echo", "fable", "onyx", "nova", "sage", "shimmer", "verse"];

    private readonly HttpClient _http;

    public OpenAiProvider(HttpClient http) => _http = http;

    public string Id => "openai";
    public string DisplayName => "OpenAI";
    public bool IsCloud => true;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => "https://platform.openai.com/api-keys";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "https://api.openai.com/v1/audio/speech", "Change for OpenAI-compatible services."),
        new("API_KEY", "API key", OptionKind.Secret, ""),
        new("voice", "Voice", OptionKind.Choice, "nova", Choices: KnownVoices),
        new("model_id", "Model", OptionKind.Choice, "tts-1", "gpt-4o-mini-tts supports style instructions.",
            Choices: ["tts-1", "tts-1-hd", "gpt-4o-mini-tts"]),
        new("instructions", "Style instructions", OptionKind.Text, "", "Only used by gpt-4o-mini-tts, e.g. \"speak like a weary wasteland survivor\"."),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["input"] = text,
            ["model"] = settings.Get("model_id", "tts-1"),
            ["voice"] = string.IsNullOrEmpty(voice) ? settings.Get("voice", "nova") : voice,
        };
        var instructions = settings.Get("instructions");
        if (!string.IsNullOrEmpty(instructions) &&
            settings.Get("model_id").Equals("gpt-4o-mini-tts", StringComparison.OrdinalIgnoreCase))
        {
            body["instructions"] = instructions;
        }

        return await ProviderHelpers.PostForAudioAsync(
            _http, settings.Get("endpoint", "https://api.openai.com/v1/audio/speech"), body, cancellationToken,
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Get("API_KEY")),
            accept: "audio/mpeg");
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // The speech API has a fixed voice roster; genders per OpenAI docs.
        IReadOnlyList<TtsVoice> voices =
        [
            new TtsVoice("alloy", "Alloy"),
            new TtsVoice("ash", "Ash", VoiceGender.Male),
            new TtsVoice("ballad", "Ballad", VoiceGender.Male),
            new TtsVoice("coral", "Coral", VoiceGender.Female),
            new TtsVoice("echo", "Echo", VoiceGender.Male),
            new TtsVoice("fable", "Fable"),
            new TtsVoice("onyx", "Onyx", VoiceGender.Male),
            new TtsVoice("nova", "Nova", VoiceGender.Female),
            new TtsVoice("sage", "Sage", VoiceGender.Female),
            new TtsVoice("shimmer", "Shimmer", VoiceGender.Female),
            new TtsVoice("verse", "Verse", VoiceGender.Male),
        ];
        return Task.FromResult(voices);
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            // Cheapest authenticated call: a one-word synthesis.
            var bytes = await SynthesizeAsync("Test.", settings.Get("voice", "nova"), settings, cancellationToken);
            return $"authenticated; test synthesis returned {bytes.Length} bytes";
        });
}
