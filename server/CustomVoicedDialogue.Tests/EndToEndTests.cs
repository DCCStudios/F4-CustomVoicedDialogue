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
        var synthesis = new SynthesisService(_config, registry);
        _host = new ServerHost(_config, synthesis);
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
