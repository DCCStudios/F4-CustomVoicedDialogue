using NAudio.Wave;

namespace CustomVoicedDialogue.Server.Audio;

public sealed record ValidationResult(bool Ok, string? Failure, TimeSpan Duration, bool ClippingWarning)
{
    public static ValidationResult Fail(string reason) => new(false, reason, TimeSpan.Zero, false);
}

/// <summary>
/// Gatekeeper between synthesis and delivery: audio only reaches the game
/// when it decodes back cleanly, is in the exact game format, actually
/// contains sound, and has a plausible duration for its text.  Catches
/// providers that return HTTP 200 with empty/garbage audio, truncated
/// downloads, and runaway synthesis.
/// </summary>
public static class AudioValidator
{
    private const double MinimumRms = 0.003;         // body below this = silent/garbage
    private const double ClippingSampleRatio = 0.005;
    private const double MinWordsPerSecond = 0.25;   // duration sanity band
    private const double MaxSecondsPerWord = 4.0;

    public static ValidationResult Validate(byte[] wav, string sourceText)
    {
        try
        {
            using var stream = new MemoryStream(wav);
            using var reader = new WaveFileReader(stream);

            var format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm ||
                format.SampleRate != AudioPipeline.TargetSampleRate ||
                format.BitsPerSample != AudioPipeline.TargetBits ||
                format.Channels != AudioPipeline.TargetChannels)
            {
                return ValidationResult.Fail(
                    $"wrong format: {format.Encoding} {format.SampleRate}Hz {format.BitsPerSample}bit {format.Channels}ch");
            }

            // Truncation check: the data chunk the header declares must fit
            // inside the actual file (NAudio tolerates short reads; a
            // half-downloaded wav must not reach the game).
            if (reader.Length > wav.Length)
            {
                return ValidationResult.Fail($"file is truncated: header declares {reader.Length} data bytes but the file has {wav.Length} total");
            }

            var duration = reader.TotalTime;
            if (duration <= AudioPipeline.LeadInPad)
            {
                return ValidationResult.Fail("audio contains no data beyond the lead-in pad");
            }

            // RMS + clipping over the body (excluding the silent pad).
            var padBytes = (long)(format.AverageBytesPerSecond * AudioPipeline.LeadInPad.TotalSeconds);
            padBytes -= padBytes % format.BlockAlign;
            reader.Position = padBytes;

            double sumSquares = 0;
            long sampleCount = 0;
            long clippedCount = 0;
            var buffer = new byte[format.AverageBytesPerSecond];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i + 1 < read; i += 2)
                {
                    var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                    sumSquares += sample * sample;
                    sampleCount++;
                    if (Math.Abs(sample) >= 0.999)
                    {
                        clippedCount++;
                    }
                }
            }

            if (sampleCount == 0)
            {
                return ValidationResult.Fail("no samples after the lead-in pad");
            }

            var rms = Math.Sqrt(sumSquares / sampleCount);
            if (rms < MinimumRms)
            {
                return ValidationResult.Fail($"audio is effectively silent (RMS {rms:F5})");
            }

            // Duration sanity: a line of N words should take neither a blink
            // nor minutes.  Wide bands — this only catches gross failures.
            var words = Math.Max(1, sourceText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            var bodySeconds = (duration - AudioPipeline.LeadInPad).TotalSeconds;
            if (bodySeconds < words * MinWordsPerSecond / 2 && bodySeconds < 0.35)
            {
                return ValidationResult.Fail($"duration {bodySeconds:F2}s is implausibly short for {words} word(s)");
            }
            if (bodySeconds > words * MaxSecondsPerWord + 10)
            {
                return ValidationResult.Fail($"duration {bodySeconds:F2}s is implausibly long for {words} word(s)");
            }

            var clipping = sampleCount > 0 && (double)clippedCount / sampleCount > ClippingSampleRatio;
            return new ValidationResult(true, null, duration, clipping);
        }
        catch (Exception exception)
        {
            return ValidationResult.Fail($"decode-back failed: {exception.Message}");
        }
    }
}
