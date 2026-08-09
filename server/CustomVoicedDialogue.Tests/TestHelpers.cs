using System.Net;
using System.Text;
using CustomVoicedDialogue.Server.Providers;

namespace CustomVoicedDialogue.Tests;

/// <summary>Records every request and answers from a scripted queue.</summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    public sealed record CapturedRequest(HttpMethod Method, string Url, string? Body, HttpRequestHeaders Headers);

    public sealed record HttpRequestHeaders(Dictionary<string, string> Values)
    {
        public string? Get(string name) => Values.TryGetValue(name, out var value) ? value : null;
    }

    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public MockHttpHandler Respond(HttpStatusCode status, byte[] body, string contentType = "application/octet-stream")
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        });
        return this;
    }

    public MockHttpHandler RespondJson(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Respond(status, Encoding.UTF8.GetBytes(json), "application/json");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!.ToString(), body, new HttpRequestHeaders(headers)));

        return _responses.Count > 0
            ? _responses.Dequeue()(request)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestAudio.ValidSourceWav()) };
    }
}

public static class TestAudio
{
    /// <summary>A one-second 440 Hz tone as 22.05 kHz mono 16-bit wav —
    /// a realistic provider response.</summary>
    public static byte[] ValidSourceWav(int sampleRate = 22050, double seconds = 1.0, double amplitude = 0.5)
    {
        var sampleCount = (int)(sampleRate * seconds);
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * amplitude * short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return WrapWav(pcm, sampleRate);
    }

    public static byte[] SilentSourceWav(int sampleRate = 22050, double seconds = 1.0) =>
        WrapWav(new byte[(int)(sampleRate * seconds) * 2], sampleRate);

    /// <summary>A provider response with dead air before the line starts —
    /// the shape Inworld returns after a bracketed steering instruction.</summary>
    public static byte[] SourceWavWithLeadingSilence(double silenceSeconds, int sampleRate = 22050, double toneSeconds = 1.0, double amplitude = 0.5)
    {
        var silenceCount = (int)(sampleRate * silenceSeconds);
        var toneCount = (int)(sampleRate * toneSeconds);
        var pcm = new byte[(silenceCount + toneCount) * 2];
        for (var i = 0; i < toneCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * amplitude * short.MaxValue);
            var offset = (silenceCount + i) * 2;
            pcm[offset] = (byte)(sample & 0xFF);
            pcm[offset + 1] = (byte)(sample >> 8);
        }
        return WrapWav(pcm, sampleRate);
    }

    public static byte[] WrapWav(byte[] pcm, int sampleRate)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return output.ToArray();
    }
}

/// <summary>An in-memory provider for orchestration tests.</summary>
public sealed class FakeProvider : ITtsProvider
{
    public int SynthesizeCalls;
    public Func<string, byte[]>? OnSynthesize;

    public string Id => "fake";
    public string DisplayName => "Fake";
    public bool IsCloud => false;
    public int? DefaultLocalPort => null;
    public string? HelpUrl => null;

    public IReadOnlyList<ProviderOption> Options { get; } =
    [
        new("voice", "Voice", OptionKind.Text, "default-voice"),
    ];

    public Task<byte[]> SynthesizeAsync(string text, string voice, ProviderSettings settings, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref SynthesizeCalls);
        return Task.FromResult(OnSynthesize?.Invoke(text) ?? TestAudio.ValidSourceWav());
    }

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TtsVoice>>(
        [
            new TtsVoice("male-1", "Male 1", VoiceGender.Male),
            new TtsVoice("female-1", "Female 1", VoiceGender.Female),
            new TtsVoice("female-2", "Female 2", VoiceGender.Female),
        ]);

    public Task<ConnectionTestResult> TestConnectionAsync(ProviderSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult(new ConnectionTestResult(true, "ok", TimeSpan.Zero));
}
