using System.Net;
using System.Text;
using System.Text.Json;
using CustomVoicedDialogue.Server;
using CustomVoicedDialogue.Server.Api;
using CustomVoicedDialogue.Server.Config;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.Tests;

/// <summary>
/// The exact HTTP sequence the F4SE plugin performs, against a real
/// Kestrel instance with a fake provider — the automated version of the
/// GUI's "Simulate game request" diagnostic.
/// </summary>
public class EndToEndTests : IAsyncLifetime
{
    private sealed class FakeRegistryClient : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no outbound HTTP expected in this test");
    }

    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "cvd-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _fakeProvider = new();
    private ServerHost? _host;
    private SynthesisService? _synthesis;
    private AppConfig? _config;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        _port = 47990 + Random.Shared.Next(0, 400);
        _config = AppConfig.Load(Path.Combine(_tempDirectory, "config.json"));
        _config.Port = _port;
        _config.Provider = "fake";
        _config.CacheDirectory = Path.Combine(_tempDirectory, "cache");
        _config.PlayerVoice = "female-1";

        var registry = new ProviderRegistry(new HttpClient(new FakeRegistryClient()));
        registry.Register(_fakeProvider);
        _synthesis = new SynthesisService(_config, registry);
        _host = new ServerHost(_config, _synthesis);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Regenerate_PreservesTheLastDirection()
    {
        var synthesis = _synthesis!;
        const string voicePath = @"Sound\Voice\TestMod.esp\PlayerVoiceFemale01\Direct_00099999_1.wav";

        // Generate the line so it lands in the catalogue.
        await synthesis.RequestAsync("Attack now.", voicePath, "PlayerVoiceFemale01", true, CancellationToken.None);

        // Direct it with custom text.
        await synthesis.RegenerateAsync(voicePath, "[loud, urgent]");
        Assert.Equal("[loud, urgent]", synthesis.Lines.Find(voicePath)!.CustomPrompt);

        // A plain Regenerate (no new direction) must NOT wipe the directed
        // text — it stays on record for the next time Direct is opened.
        await synthesis.RegenerateAsync(voicePath, direction: null);
        Assert.Equal("[loud, urgent]", synthesis.Lines.Find(voicePath)!.CustomPrompt);
    }

    [Fact]
    public async Task Takes_SelectRestoresAnEarlierTake_DeleteClearsThemAll()
    {
        var synthesis = _synthesis!;
        const string voicePath = @"Sound\Voice\TestMod.esp\PlayerVoiceFemale01\Takes_00088888_1.wav";

        // Generate the line (take 0), then regenerate twice (takes 1, 2).
        await synthesis.RequestAsync("Hold the line.", voicePath, "PlayerVoiceFemale01", true, CancellationToken.None);
        await synthesis.RegenerateAsync(voicePath);
        await synthesis.RegenerateAsync(voicePath);

        var takes = synthesis.TakesFor(voicePath);
        Assert.Equal(3, takes.Count);
        Assert.True(takes[^1].IsActive);                       // newest is active
        Assert.All(takes, t => Assert.True(t.Available));      // all wavs present

        // Restore take 0.
        var takeZero = takes[0];
        Assert.True(synthesis.SelectTake(voicePath, takeZero.CacheKey));
        var afterSelect = synthesis.TakesFor(voicePath);
        Assert.True(afterSelect.Single(t => t.Variant == 0).IsActive);
        Assert.Equal(3, afterSelect.Count);                    // still three, no dupes

        // Delete everything for the line.
        synthesis.DeleteAllTakes(voicePath);
        Assert.Empty(synthesis.TakesFor(voicePath));
        Assert.Null(synthesis.Lines.Find(voicePath));
        Assert.False(File.Exists(takeZero.WavPath));           // cached audio gone
    }

    [Fact]
    public async Task Takes_DeleteOthers_KeepsTheActiveTakeAndItsAudio()
    {
        var synthesis = _synthesis!;
        const string voicePath = @"Sound\Voice\TestMod.esp\PlayerVoiceFemale01\Others_00077777_1.wav";

        await synthesis.RequestAsync("Fall back!", voicePath, "PlayerVoiceFemale01", true, CancellationToken.None);
        await synthesis.RegenerateAsync(voicePath);
        await synthesis.RegenerateAsync(voicePath);   // three takes, newest active

        var before = synthesis.TakesFor(voicePath);
        var active = before.Single(t => t.IsActive);
        var others = before.Where(t => !t.IsActive).ToList();

        synthesis.DeleteOtherTakes(voicePath);

        // Only the active take survives, still playable; the line is intact.
        var after = synthesis.TakesFor(voicePath);
        Assert.Single(after);
        Assert.True(after[0].IsActive);
        Assert.True(File.Exists(active.WavPath));
        Assert.NotNull(synthesis.Lines.Find(voicePath));
        Assert.All(others, o => Assert.False(File.Exists(o.WavPath)));   // their audio freed
    }

    [Fact]
    public async Task SynthLifecycle_202Then200WithValidWav()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        // Status ping, as the plugin does at kGameDataReady.
        var status = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        // First request: either inline 200 (fast fake provider) or 202 + poll.
        var body = JsonSerializer.Serialize(new
        {
            text = "I've been looking for this vault for weeks.",
            voicePath = @"Sound\Voice\TestMod.esp\PlayerVoiceFemale01\Quest_Topic_00012345_1.wav",
            voiceType = "PlayerVoiceFemale01",
            isPlayer = true,
        });
        var synth = await client.PostAsync("/api/synth", new StringContent(body, Encoding.UTF8, "application/json"));

        byte[]? wav = null;
        if (synth.StatusCode == HttpStatusCode.OK)
        {
            wav = await synth.Content.ReadAsByteArrayAsync();
        }
        else
        {
            Assert.Equal(HttpStatusCode.Accepted, synth.StatusCode);
            for (var attempt = 0; attempt < 40 && wav is null; attempt++)
            {
                await Task.Delay(250);
                var result = await client.GetAsync(
                    "/api/result?voicePath=" + Uri.EscapeDataString(@"Sound\Voice\TestMod.esp\PlayerVoiceFemale01\Quest_Topic_00012345_1.wav"));
                if (result.StatusCode == HttpStatusCode.OK)
                {
                    wav = await result.Content.ReadAsByteArrayAsync();
                }
                else
                {
                    Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
                }
            }
        }

        Assert.NotNull(wav);
        var validation = CustomVoicedDialogue.Server.Audio.AudioValidator.Validate(wav!, "I've been looking for this vault for weeks.");
        Assert.True(validation.Ok, validation.Failure);

        // Same line again: instant 200 from cache, no second synthesis.
        var callsBefore = _fakeProvider.SynthesizeCalls;
        var repeat = await client.PostAsync("/api/synth", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(callsBefore, _fakeProvider.SynthesizeCalls);
    }

    [Fact]
    public async Task Status_VoiceFingerprint_TracksPlayerVoiceOnly()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

        var first = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement;
        var playerBefore = first.GetProperty("voiceFingerprint").GetString();
        var npcBefore = first.GetProperty("npcVoiceFingerprint").GetString();
        Assert.False(string.IsNullOrEmpty(playerBefore));
        Assert.False(string.IsNullOrEmpty(npcBefore));

        // Stable across identical config.
        var again = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement;
        Assert.Equal(playerBefore, again.GetProperty("voiceFingerprint").GetString());

        // Changing the player voice must change the player fingerprint and
        // leave the NPC fingerprint alone.
        _config!.PlayerVoice = "male-2";
        var changed = JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement;
        Assert.NotEqual(playerBefore, changed.GetProperty("voiceFingerprint").GetString());
        Assert.Equal(npcBefore, changed.GetProperty("npcVoiceFingerprint").GetString());
        _config.PlayerVoice = "female-1";
    }

    [Fact]
    public async Task Prefetch_QueuesBatch()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        var body = JsonSerializer.Serialize(new
        {
            lines = new[]
            {
                new { text = "Yes.", voicePath = @"Sound\Voice\T.esp\PlayerVoiceMale01\A_1.wav", voiceType = "PlayerVoiceMale01", isPlayer = true },
                new { text = "No.", voicePath = @"Sound\Voice\T.esp\PlayerVoiceMale01\B_1.wav", voiceType = "PlayerVoiceMale01", isPlayer = true },
            },
        });
        var response = await client.PostAsync("/api/prefetch", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, payload.GetProperty("queued").GetInt32() + payload.GetProperty("cached").GetInt32());
    }
}
