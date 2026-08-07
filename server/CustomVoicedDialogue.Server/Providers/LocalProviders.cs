using System.Text;
using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

// Local, free TTS services.  Request shapes are 1:1 ports of the matching
// HerikaServer tts-*.php files, minus its Skyrim-specific voice mapping
// (voices pass through unchanged here — an unmapped voice id must reach
// the service, not become null).

internal static class LocalProviderHelpers
{
    /// <summary>Speaker list for XTTS-style servers ({endpoint}/speakers_list
    /// returning a JSON array of names).</summary>
    public static async Task<IReadOnlyList<TtsVoice>> GetSpeakersListAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{endpoint}/speakers_list", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var voices = new List<TtsVoice>();
        if (json.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in json.RootElement.EnumerateArray())
            {
                var id = element.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    voices.Add(new TtsVoice(id, id, VoiceMappingGenderGuess(id)));
                }
            }
        }
        return voices;
    }

    public static VoiceGender VoiceMappingGenderGuess(string id) =>
        id.Contains("female", StringComparison.OrdinalIgnoreCase) ? VoiceGender.Female
        : id.Contains("male", StringComparison.OrdinalIgnoreCase) ? VoiceGender.Male
        : VoiceGender.Unknown;
}

/// <summary>Piper: POST {endpoint} root with text/voice/length_scale.</summary>
public sealed class PiperProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public PiperProvider(HttpClient http) => _http = http;

    public string Id => "piper";
    public string DisplayName => "Piper";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 5000;
    public string? HelpUrl => "https://github.com/OHF-Voice/piper1-gpl";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "http://127.0.0.1:5000"),
        new("voiceid", "Voice", OptionKind.Text, "en_US-amy-low"),
        new("length_scale", "Length scale", OptionKind.Number, "1.0", "Speaking speed; higher is slower.", 0.2, 4),
        new("noise_scale", "Noise scale", OptionKind.Number, "0", "Speaking variability.", 0, 1),
        new("noise_w_scale", "Noise W scale", OptionKind.Number, "0", "Phoneme width variability.", 0, 1),
        new("speaker", "Speaker name", OptionKind.Text, "", "For multi-speaker voices."),
        new("speaker_id", "Speaker id", OptionKind.Number, "0", "Overrides speaker name when > 0.", 0, 999),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = text,
            ["voice"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid", "en_US-amy-low") : voice,
            ["length_scale"] = Math.Clamp(settings.GetDouble("length_scale", 1.0), 0.2, 4.0),
        };
        var noiseScale = settings.GetDouble("noise_scale", 0);
        if (noiseScale > 0.001)
        {
            body["noise_scale"] = Math.Clamp(noiseScale, 0, 1);
        }
        var noiseW = settings.GetDouble("noise_w_scale", 0);
        if (noiseW > 0.001)
        {
            body["noise_w_scale"] = Math.Clamp(noiseW, 0, 1);
        }
        var speaker = settings.Get("speaker");
        if (!string.IsNullOrEmpty(speaker))
        {
            body["speaker"] = speaker;
        }
        var speakerId = settings.GetInt("speaker_id", 0);
        if (speakerId > 0)
        {
            body["speaker_id"] = speakerId;
        }
        return await ProviderHelpers.PostForAudioAsync(
            _http, ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:5000")), body, cancellationToken);
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TtsVoice>>([]);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.ProbeEndpointAsync(_http, ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:5000")), cancellationToken);
}

/// <summary>Kokoro: OpenAI-compatible POST {endpoint}/v1/audio/speech.</summary>
public sealed class KokoroProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public KokoroProvider(HttpClient http) => _http = http;

    public string Id => "kokoro";
    public string DisplayName => "Kokoro";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 8880;
    public string? HelpUrl => "https://github.com/remsky/Kokoro-FastAPI";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "http://127.0.0.1:8880"),
        new("voiceid", "Voice", OptionKind.Text, "af_bella", "Kokoro voice id; combine with + for blends (af_bella+af_sky)."),
        new("speed", "Speed", OptionKind.Number, "1.0", "Speech speed.", 0.5, 2),
        new("lang_code", "Language code", OptionKind.Text, "a", "Kokoro language group code."),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = "kokoro",
            ["input"] = text,
            // Deliberate departure from HerikaServer: the voice id passes
            // through untouched instead of a Skyrim-only lookup table that
            // nulled unmapped voices.
            ["voice"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid", "af_bella") : voice,
            ["response_format"] = "wav",
            ["speed"] = settings.GetDouble("speed", 1.0),
            ["lang_code"] = settings.Get("lang_code", "a"),
        };
        return await ProviderHelpers.PostForAudioAsync(
            _http,
            $"{ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:8880"))}/v1/audio/speech",
            body,
            cancellationToken);
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // Kokoro-FastAPI exposes /v1/audio/voices -> {"voices": [...]}.
        try
        {
            var endpoint = ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:8880"));
            using var response = await _http.GetAsync($"{endpoint}/v1/audio/voices", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var voices = new List<TtsVoice>();
            foreach (var element in json.RootElement.GetProperty("voices").EnumerateArray())
            {
                var id = element.GetString();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                // Kokoro ids encode gender in the prefix: ?f_ female, ?m_ male.
                var gender = id.Length > 2 && id[1] == 'f' ? VoiceGender.Female
                    : id.Length > 2 && id[1] == 'm' ? VoiceGender.Male
                    : VoiceGender.Unknown;
                voices.Add(new TtsVoice(id, id, gender));
            }
            return voices;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var voices = await ListVoicesAsync(settings, cancellationToken);
            return voices.Count > 0 ? $"service is up; {voices.Count} voice(s)" : "service answered but returned no voices";
        });
}

/// <summary>Base for XTTS-style speaker_wav services (XTTS-fastapi,
/// Chatterbox, PocketTTS, OmniVoice): POST {endpoint}/tts_to_audio with
/// {text, speaker_wav, language}; voices come from /speakers_list.</summary>
public abstract class SpeakerWavProviderBase : ITtsProvider
{
    protected readonly HttpClient Http;

    protected SpeakerWavProviderBase(HttpClient http) => Http = http;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public bool IsCloud => false;
    public abstract int? DefaultLocalPort { get; }
    public abstract string? HelpUrl { get; }
    protected abstract string DefaultEndpoint { get; }

    public virtual IReadOnlyList<ProviderOption> Options =>
    [
        new("endpoint", "Endpoint", OptionKind.Text, DefaultEndpoint),
        new("voiceid", "Voice", OptionKind.Text, "TheNarrator", "Name of a cloned/sample voice known to the service."),
        new("language", "Language", OptionKind.Text, "en"),
    ];

    protected string Endpoint(ProviderSettings settings) =>
        ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", DefaultEndpoint));

    public virtual async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = text,
            ["speaker_wav"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid", "TheNarrator") : voice,
            ["language"] = settings.Get("language", "en"),
        };
        return await ProviderHelpers.PostForAudioAsync(Http, $"{Endpoint(settings)}/tts_to_audio", body, cancellationToken);
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        LocalProviderHelpers.GetSpeakersListAsync(Http, Endpoint(settings), cancellationToken);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.TimeAsync(async () =>
        {
            var voices = await ListVoicesAsync(settings, cancellationToken);
            return $"service is up; {voices.Count} voice(s) available";
        });
}

public sealed class XttsFastApiProvider : SpeakerWavProviderBase
{
    public XttsFastApiProvider(HttpClient http) : base(http)
    {
    }

    public override string Id => "xtts";
    public override string DisplayName => "XTTS (voice cloning)";
    public override int? DefaultLocalPort => 8020;
    public override string? HelpUrl => "https://github.com/daswer123/xtts-api-server";
    protected override string DefaultEndpoint => "http://127.0.0.1:8020";
}

public sealed class ChatterboxProvider : SpeakerWavProviderBase
{
    public ChatterboxProvider(HttpClient http) : base(http)
    {
    }

    public override string Id => "chatterbox";
    public override string DisplayName => "Chatterbox";
    public override int? DefaultLocalPort => 8023;
    public override string? HelpUrl => "https://github.com/resemble-ai/chatterbox";
    protected override string DefaultEndpoint => "http://127.0.0.1:8023";
}

public sealed class OmniVoiceProvider : SpeakerWavProviderBase
{
    public OmniVoiceProvider(HttpClient http) : base(http)
    {
    }

    public override string Id => "omnivoice";
    public override string DisplayName => "OmniVoice";
    public override int? DefaultLocalPort => 8021;
    public override string? HelpUrl => "https://www.nexusmods.com/skyrimspecialedition/mods/126330";
    protected override string DefaultEndpoint => "http://127.0.0.1:8021";
}

/// <summary>PocketTTS: XTTS-shaped Python API on 8086, or the audio.cpp
/// OpenAI-shaped API when the endpoint targets it.</summary>
public sealed class PocketTtsProvider : SpeakerWavProviderBase
{
    public PocketTtsProvider(HttpClient http) : base(http)
    {
    }

    public override string Id => "pockettts";
    public override string DisplayName => "PocketTTS";
    public override int? DefaultLocalPort => 8086;
    public override string? HelpUrl => "https://www.nexusmods.com/skyrimspecialedition/mods/126330";
    protected override string DefaultEndpoint => "http://127.0.0.1:8086";

    public override IReadOnlyList<ProviderOption> Options =>
    [
        new("endpoint", "Endpoint", OptionKind.Text, DefaultEndpoint),
        new("voiceid", "Voice", OptionKind.Text, "TheNarrator"),
        new("language", "Language", OptionKind.Text, "en"),
        new("model", "audio.cpp model", OptionKind.Text, "pocket-tts", "Only used when the endpoint is an audio.cpp server."),
        new("audio_cpp", "audio.cpp API", OptionKind.Toggle, "false", "Enable when pointing at audio.cpp instead of the Python server."),
    ];

    public override async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.GetBool("audio_cpp", false))
        {
            return await base.SynthesizeAsync(text, voice, settings, cancellationToken);
        }
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Get("model", "pocket-tts"),
            ["input"] = text,
            ["language"] = settings.Get("language", "en"),
            ["voice"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid", "TheNarrator") : voice,
        };
        return await ProviderHelpers.PostForAudioAsync(Http, $"{Endpoint(settings)}/v1/audio/speech", body, cancellationToken);
    }
}

/// <summary>MeloTTS: POST {endpoint}/tts with speaker/text/language/speed.</summary>
public sealed class MeloTtsProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public MeloTtsProvider(HttpClient http) => _http = http;

    public string Id => "melotts";
    public string DisplayName => "MeloTTS";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 8084;
    public string? HelpUrl => "https://github.com/myshell-ai/MeloTTS";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "http://127.0.0.1:8084"),
        new("voiceid", "Speaker", OptionKind.Text, "EN-US"),
        new("language", "Language", OptionKind.Text, "EN"),
        new("speed", "Speed", OptionKind.Number, "1.0", "Speech speed.", 0.5, 2),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["speaker"] = string.IsNullOrEmpty(voice) ? settings.Get("voiceid", "EN-US") : voice,
            ["text"] = text,
            ["language"] = settings.Get("language", "EN"),
            ["speed"] = settings.GetDouble("speed", 1.0),
        };
        return await ProviderHelpers.PostForAudioAsync(
            _http, $"{ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:8084"))}/tts", body, cancellationToken);
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TtsVoice>>([]);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.ProbeEndpointAsync(_http, ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:8084")), cancellationToken);
}

/// <summary>Mimic3: POST SSML to {endpoint}/api/tts.</summary>
public sealed class Mimic3Provider : ITtsProvider
{
    private readonly HttpClient _http;

    public Mimic3Provider(HttpClient http) => _http = http;

    public string Id => "mimic3";
    public string DisplayName => "Mimic 3";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 59125;
    public string? HelpUrl => "https://github.com/MycroftAI/mimic3";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "http://127.0.0.1:59125"),
        new("voice", "Voice", OptionKind.Text, "en_UK/apope_low#default"),
        new("rate", "Rate", OptionKind.Number, "1", "Speech rate.", 0.5, 2),
        new("volume", "Volume", OptionKind.Number, "60", "Speech volume.", 0, 100),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var voiceName = string.IsNullOrEmpty(voice) ? settings.Get("voice", "en_UK/apope_low#default") : voice;
        var ssml =
            "<speak version=\"1.1\" xml:lang=\"en\">" +
            $"<voice name=\"{System.Security.SecurityElement.Escape(voiceName)}\">" +
            $"<prosody rate=\"{settings.GetDouble("rate", 1)}\" volume=\"{settings.GetDouble("volume", 60)}\">" +
            $"<s>{System.Security.SecurityElement.Escape(text)}</s>" +
            "</prosody></voice></speak>";

        var endpoint = ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:59125"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/api/tts")
        {
            Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml"),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Mimic3 returned {(int)response.StatusCode}: {ProviderHelpers.TrimForError(bytes)}");
        }
        return bytes;
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:59125"));
            using var response = await _http.GetAsync($"{endpoint}/api/voices", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var voices = new List<TtsVoice>();
            foreach (var voice in json.RootElement.EnumerateArray())
            {
                var key = voice.GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(key))
                {
                    voices.Add(new TtsVoice(key, key));
                }
            }
            return voices;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.ProbeEndpointAsync(_http, ProviderHelpers.NormalizeEndpoint(settings.Get("endpoint", "http://127.0.0.1:59125")), cancellationToken);
}

/// <summary>KoboldCpp: POST {endpoint} (default /api/extra/tts) with
/// {input, voice}.</summary>
public sealed class KoboldCppProvider : ITtsProvider
{
    private readonly HttpClient _http;

    public KoboldCppProvider(HttpClient http) => _http = http;

    public string Id => "koboldcpp";
    public string DisplayName => "KoboldCpp";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 5001;
    public string? HelpUrl => "https://github.com/LostRuins/koboldcpp";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("endpoint", "Endpoint", OptionKind.Text, "http://127.0.0.1:5001/api/extra/tts"),
        new("voice", "Voice", OptionKind.Text, "kobo"),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["input"] = text,
            ["voice"] = string.IsNullOrEmpty(voice) ? settings.Get("voice", "kobo") : voice,
        };
        return await ProviderHelpers.PostForAudioAsync(
            _http, settings.Get("endpoint", "http://127.0.0.1:5001/api/extra/tts"), body, cancellationToken);
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TtsVoice>>([]);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.ProbeEndpointAsync(_http, settings.Get("endpoint", "http://127.0.0.1:5001/api/extra/tts"), cancellationToken);
}
