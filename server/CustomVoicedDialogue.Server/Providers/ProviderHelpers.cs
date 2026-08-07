using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CustomVoicedDialogue.Server.Providers;

internal static class ProviderHelpers
{
    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static string NormalizeEndpoint(string endpoint) => endpoint.TrimEnd('/');

    /// <summary>POSTs JSON and returns the raw response body, throwing a
    /// descriptive error (including any service message) on failure.</summary>
    public static async Task<byte[]> PostForAudioAsync(
        HttpClient client, string url, object body, CancellationToken cancellationToken,
        Action<HttpRequestMessage>? configure = null, string accept = "audio/wav")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Json(body) };
        request.Headers.Accept.ParseAdd(accept);
        configure?.Invoke(request);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = TrimForError(bytes);
            throw new HttpRequestException($"{url} returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
        }
        if (bytes.Length == 0)
        {
            throw new HttpRequestException($"{url} returned an empty body");
        }
        return bytes;
    }

    public static string TrimForError(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 500));
        return text.Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>Generic connection test: times a request factory and turns
    /// exceptions into readable failures.</summary>
    public static async Task<ConnectionTestResult> TimeAsync(Func<Task<string>> probe)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var message = await probe();
            return new ConnectionTestResult(true, message, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            return new ConnectionTestResult(false, exception.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>Wraps headerless PCM into a RIFF wav container so the audio
    /// pipeline can decode it (used by services returning raw LINEAR16).</summary>
    public static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int channels = 1, int bitsPerSample = 16)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        var blockAlign = channels * bitsPerSample / 8;
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return output.ToArray();
    }

    public static bool LooksLikeRiff(byte[] bytes) =>
        bytes.Length > 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F';

    /// <summary>Shared "endpoint is alive" probe for local services.</summary>
    public static async Task<ConnectionTestResult> ProbeEndpointAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        return await TimeAsync(async () =>
        {
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return $"service answered HTTP {(int)response.StatusCode} at {endpoint}";
        });
    }
}
