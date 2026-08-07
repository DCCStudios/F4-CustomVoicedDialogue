using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

/// <summary>
/// xVASynth — the local app with native Fallout 4 voice models ("f4_*").
/// Ported from tts-xvasynth.php with two deliberate fixes:
///  - the loaded model is cached per voice instead of re-POSTing
///    /loadModel on every call (HerikaServer's main latency bug);
///  - audio is written to a local temp outfile and read back (no WSL UNC
///    path hack — this app runs on the same machine as xVASynth);
///  - no "sk_" prefix mangling, so f4_ model names pass through intact.
/// </summary>
public sealed class XvaSynthProvider : ITtsProvider
{
    private readonly HttpClient _http;
    private string? _loadedModel;

    public XvaSynthProvider(HttpClient http) => _http = http;

    public string Id => "xvasynth";
    public string DisplayName => "xVASynth (Fallout 4 voices)";
    public bool IsCloud => false;
    public int? DefaultLocalPort => 8008;
    public string? HelpUrl => "https://www.nexusmods.com/skyrimspecialedition/mods/44184";

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("url", "Endpoint", OptionKind.Text, "http://127.0.0.1:8008", "xVASynth must be running with its server active."),
        new("game", "Game folder", OptionKind.Text, "fallout4", "Model folder under resources/app/models."),
        new("model", "Default model", OptionKind.Text, "", "Fallback voice model, e.g. f4_ma_boone. NPC auto-assignment matches models to voice types."),
        new("modelType", "Model type", OptionKind.Choice, "xVAPitch", Choices: ["xVAPitch", "FastPitch1.1", "FastPitch"]),
        new("version", "Model version", OptionKind.Text, "3.0"),
        new("base_lang", "Language", OptionKind.Text, "en"),
        new("pace", "Pace", OptionKind.Number, "1.0", "Speaking pace.", 0.5, 2),
        new("vocoder", "Vocoder", OptionKind.Text, "n/a"),
        new("waveglowPath", "WaveGlow path", OptionKind.Text, "resources/app/models/waveglow_256channels_universal_v4.pt"),
    ];

    public async Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var endpoint = ProviderHelpers.NormalizeEndpoint(settings.Get("url", "http://127.0.0.1:8008"));
        var game = settings.Get("game", "fallout4");
        var voiceModel = string.IsNullOrEmpty(voice) ? settings.Get("model") : voice;
        if (string.IsNullOrEmpty(voiceModel))
        {
            throw new InvalidOperationException("no xVASynth voice model configured");
        }
        var modelPath = voiceModel.Contains('/') ? voiceModel : $"resources/app/models/{game}/{voiceModel}";

        if (_loadedModel != modelPath)
        {
            var loadBody = new Dictionary<string, object>
            {
                ["outputs"] = "",
                ["model"] = modelPath,
                ["modelType"] = settings.Get("modelType", "xVAPitch"),
                ["version"] = settings.Get("version", "3.0"),
                ["base_lang"] = settings.Get("base_lang", "en"),
                ["pluginsContext"] = "{}",
            };
            using var loadResponse = await _http.PostAsync(
                $"{endpoint}/loadModel",
                new StringContent(JsonSerializer.Serialize(loadBody), System.Text.Encoding.UTF8, "text/plain"),
                cancellationToken);
            loadResponse.EnsureSuccessStatusCode();
            _loadedModel = modelPath;
        }

        var outFile = Path.Combine(Path.GetTempPath(), $"cvd_xvas_{Guid.NewGuid():N}.wav");
        try
        {
            var synthesisBody = new Dictionary<string, object>
            {
                ["sequence"] = text,
                ["editorStyles"] = new Dictionary<string, object>(),
                ["pace"] = settings.GetDouble("pace", 1.0),
                ["base_lang"] = settings.Get("base_lang", "en"),
                ["base_emb"] = Array.Empty<object>(),
                ["modelType"] = settings.Get("modelType", "xVAPitch"),
                ["useSR"] = false,
                ["useCleanup"] = false,
                ["outfile"] = outFile,
                ["pluginsContext"] = "{}",
                ["vocoder"] = settings.Get("vocoder", "n/a"),
                ["waveglowPath"] = settings.Get("waveglowPath"),
                ["model"] = modelPath,
            };
            using var response = await _http.PostAsync(
                $"{endpoint}/synthesize",
                new StringContent(JsonSerializer.Serialize(synthesisBody), System.Text.Encoding.UTF8, "text/plain"),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            // xVASynth writes the wav to the outfile; the HTTP body carries
            // no audio.  Give slow disks a moment before declaring failure.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (File.Exists(outFile) && new FileInfo(outFile).Length > 44)
                {
                    return await File.ReadAllBytesAsync(outFile, cancellationToken);
                }
                await Task.Delay(250, cancellationToken);
            }
            throw new TimeoutException("xVASynth accepted the request but never produced the output file");
        }
        finally
        {
            try
            {
                File.Delete(outFile);
            }
            catch (IOException)
            {
            }
        }
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        // xVASynth's HTTP server has no model-list endpoint; enumerate the
        // installed model folder when it is on this machine.  Voice ids are
        // folder-relative model names like "f4_ma_boone".
        var voices = new List<TtsVoice>();
        var game = settings.Get("game", "fallout4");
        foreach (var root in CandidateModelRoots())
        {
            var gameDirectory = Path.Combine(root, game);
            if (!Directory.Exists(gameDirectory))
            {
                continue;
            }
            foreach (var file in Directory.EnumerateFiles(gameDirectory, "*.json"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var gender = id.Contains("female", StringComparison.OrdinalIgnoreCase) ? VoiceGender.Female
                    : id.StartsWith("f4_m", StringComparison.OrdinalIgnoreCase) ? VoiceGender.Male
                    : VoiceGender.Unknown;
                voices.Add(new TtsVoice(id, id, gender));
            }
            if (voices.Count > 0)
            {
                break;
            }
        }
        return Task.FromResult<IReadOnlyList<TtsVoice>>(voices);
    }

    private static IEnumerable<string> CandidateModelRoots()
    {
        foreach (var drive in new[] { "C:", "D:", "E:", "F:" })
        {
            yield return $@"{drive}\Program Files (x86)\Steam\steamapps\common\xVASynth\resources\app\models";
            yield return $@"{drive}\Games\xVASynth\resources\app\models";
        }
    }

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        ProviderHelpers.ProbeEndpointAsync(_http, ProviderHelpers.NormalizeEndpoint(settings.Get("url", "http://127.0.0.1:8008")), cancellationToken);
}
