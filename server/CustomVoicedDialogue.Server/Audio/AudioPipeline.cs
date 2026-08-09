using NAudio.Wave;

namespace CustomVoicedDialogue.Server.Audio;

/// <summary>
/// Normalizes whatever container a TTS service returns into the format the
/// game engine is known to accept for voice lines — RIFF PCM, 16-bit,
/// mono, 48 kHz — with any dead air the provider left at the front of the
/// line trimmed off (see <see cref="TrimLeadingSilence"/>) and a clean
/// 150 ms silence lead-in added in its place (the engine clips the first
/// moments of playback; the pad was established by HerikaServer).
/// Mono and 16-bit are engine requirements (voice lines are 3D-positioned);
/// 48 kHz is the quality ceiling of the cloud providers, so nothing is
/// downsampled on the way in.
/// </summary>
public static class AudioPipeline
{
    static AudioPipeline()
    {
        // MediaFoundation must be initialized once per process before the
        // resampler/reader are used.
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
    }

    public const int TargetSampleRate = 48000;
    public const int TargetBits = 16;
    public const int TargetChannels = 1;
    public static readonly TimeSpan LeadInPad = TimeSpan.FromMilliseconds(150);

    /// <summary>Loudness every line is levelled to, as RMS over speech (not
    /// counting pauses).  -20 dBFS is the usual band for game dialogue and
    /// leaves ample headroom for the peak ceiling below.</summary>
    private const double TargetRmsDbfs = -20.0;

    /// <summary>No sample may exceed this after gain, so levelling can never
    /// introduce clipping.</summary>
    private const double PeakCeilingDbfs = -1.0;

    /// <summary>Bounds on the correction.  Turning a hot line down is always
    /// safe, so the floor is generous; the ceiling is what needs restraint,
    /// since boosting also boosts a provider's noise floor.</summary>
    private const double MinGain = 0.05;
    private const double MaxGain = 6.0;

    public static byte[] NormalizeToGameWav(byte[] sourceAudio)
    {
        using var sourceStream = new MemoryStream(sourceAudio);
        using var reader = CreateReader(sourceStream);

        var targetFormat = new WaveFormat(TargetSampleRate, TargetBits, TargetChannels);
        using var resampler = new MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };

        // Buffer the converted PCM so the whole line's loudness is known
        // before any of it is written.  This costs one extra pass over a few
        // hundred KB — microseconds, against a synthesis call measured in
        // seconds — so levelling adds no perceptible latency.
        using var pcm = new MemoryStream();
        var readBuffer = new byte[targetFormat.AverageBytesPerSecond];
        int read;
        while ((read = resampler.Read(readBuffer, 0, readBuffer.Length)) > 0)
        {
            pcm.Write(readBuffer, 0, read);
        }
        var samples = TrimLeadingSilence(pcm.ToArray(), targetFormat.AverageBytesPerSecond);
        ApplyGain(samples, ComputeLevellingGain(samples));

        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, targetFormat))
        {
            var padLength = (int)(targetFormat.AverageBytesPerSecond * LeadInPad.TotalSeconds);
            padLength -= padLength % targetFormat.BlockAlign;
            writer.Write(new byte[padLength], 0, padLength);
            writer.Write(samples, 0, samples.Length);
        }
        return output.ToArray();
    }

    /// <summary>Cuts dead air from the front of the line.  Measured live:
    /// Inworld's steering brackets ("[firm, commanding tone] ...") reliably
    /// leave a beat of silence before the spoken line actually starts (4 of
    /// 4 identical calls with a bracket vs. 0 of 4 without one) — this
    /// happens whether or not the game needs it, so it is removed here
    /// rather than chased in the prompt.  The 150 ms engine-required pad
    /// above is added back afterward, so the game still gets a clean,
    /// controlled lead-in regardless of how much dead air the provider
    /// returned.</summary>
    internal static byte[] TrimLeadingSilence(byte[] pcm16, int bytesPerSecond)
    {
        const double threshold = 0.01;  // matches ComputeLevellingGain's noise floor
        var searchLimit = Math.Min(pcm16.Length, bytesPerSecond * 3);
        var offset = 0;
        for (; offset + 1 < searchLimit; offset += 2)
        {
            var sample = (short)(pcm16[offset] | (pcm16[offset + 1] << 8)) / 32768.0;
            if (Math.Abs(sample) >= threshold)
            {
                return offset == 0 ? pcm16 : pcm16[offset..];
            }
        }
        // No sound within the search window (or the whole clip is
        // silent) — leave it as-is; the validator flags silence
        // explicitly downstream rather than this guessing at it.
        return pcm16;
    }

    /// <summary>Gain that brings speech to <see cref="TargetRmsDbfs"/>
    /// without pushing any peak past <see cref="PeakCeilingDbfs"/>.  RMS is
    /// measured over speech only — samples above a noise-floor threshold —
    /// so a line padded with pauses is not over-boosted to compensate.</summary>
    internal static double ComputeLevellingGain(ReadOnlySpan<byte> pcm16)
    {
        const double activeThreshold = 0.01;  // ~-40 dBFS: below this is pause/noise
        double sumSquares = 0;
        long activeCount = 0;
        double peak = 0;

        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var sample = (short)(pcm16[i] | (pcm16[i + 1] << 8)) / 32768.0;
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
            if (magnitude >= activeThreshold)
            {
                sumSquares += sample * sample;
                activeCount++;
            }
        }

        // Too little speech to measure (or pure silence): leave it alone.
        if (activeCount < TargetSampleRate / 20 || peak <= 0)
        {
            return 1.0;
        }

        var rms = Math.Sqrt(sumSquares / activeCount);
        if (rms <= 0)
        {
            return 1.0;
        }

        var gain = Math.Pow(10, TargetRmsDbfs / 20.0) / rms;
        var peakLimit = Math.Pow(10, PeakCeilingDbfs / 20.0) / peak;
        return Math.Clamp(Math.Min(gain, peakLimit), MinGain, MaxGain);
    }

    private static void ApplyGain(Span<byte> pcm16, double gain)
    {
        if (Math.Abs(gain - 1.0) < 0.01)
        {
            return;
        }
        for (var i = 0; i + 1 < pcm16.Length; i += 2)
        {
            var scaled = (short)(pcm16[i] | (pcm16[i + 1] << 8)) * gain;
            var clamped = (short)Math.Clamp(Math.Round(scaled), short.MinValue, short.MaxValue);
            pcm16[i] = (byte)(clamped & 0xFF);
            pcm16[i + 1] = (byte)((clamped >> 8) & 0xFF);
        }
    }

    private static WaveStream CreateReader(Stream source)
    {
        // Sniff the container instead of trusting extensions: services lie.
        Span<byte> header = stackalloc byte[4];
        var read = source.Read(header);
        source.Position = 0;
        if (read == 4 && header.SequenceEqual("RIFF"u8))
        {
            return new WaveFileReader(source);
        }
        // MediaFoundation handles mp3/aac/wma and most raw formats.
        return new StreamMediaFoundationReader(source);
    }
}
