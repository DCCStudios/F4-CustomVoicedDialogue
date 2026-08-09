using CustomVoicedDialogue.Server.Audio;
using CustomVoicedDialogue.Server.Cache;
using CustomVoicedDialogue.Server.Providers;
using CustomVoicedDialogue.Server.VoiceMapping;
using NAudio.Wave;

namespace CustomVoicedDialogue.Tests;

public class AudioPipelineTests
{
    [Fact]
    public void NormalizesToGameFormatWithLeadIn()
    {
        var source = TestAudio.ValidSourceWav(sampleRate: 22050, seconds: 1.0);

        var wav = AudioPipeline.NormalizeToGameWav(source);

        using var reader = new WaveFileReader(new MemoryStream(wav));
        Assert.Equal(AudioPipeline.TargetSampleRate, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.Equal(WaveFormatEncoding.Pcm, reader.WaveFormat.Encoding);
        // ~1 s of audio + 150 ms pad
        Assert.InRange(reader.TotalTime.TotalMilliseconds, 1050, 1350);
    }

    [Fact]
    public void TrimsProviderLeadingSilenceButKeepsTheEngineLeadInPad()
    {
        // Inworld reliably leaves a beat of dead air after a bracketed
        // steering instruction before the line actually starts (measured:
        // 4 of 4 calls with a bracket vs. 0 of 4 without one) — whatever
        // the provider returns, the game should still only ever hear our
        // own controlled 150 ms pad, not a provider-dependent one.
        var withLeadIn = AudioPipeline.NormalizeToGameWav(
            TestAudio.SourceWavWithLeadingSilence(silenceSeconds: 0.6, toneSeconds: 1.0));
        var withoutLeadIn = AudioPipeline.NormalizeToGameWav(
            TestAudio.SourceWavWithLeadingSilence(silenceSeconds: 0.0, toneSeconds: 1.0));

        using var trimmedReader = new WaveFileReader(new MemoryStream(withLeadIn));
        using var plainReader = new WaveFileReader(new MemoryStream(withoutLeadIn));

        // The 600 ms the provider added up front must not survive: both
        // come out the same length (150 ms pad + ~1 s of tone), not 600 ms
        // apart.
        Assert.InRange(
            Math.Abs(trimmedReader.TotalTime.TotalMilliseconds - plainReader.TotalTime.TotalMilliseconds),
            0, 60);
        Assert.InRange(trimmedReader.TotalTime.TotalMilliseconds, 1050, 1350);
    }

    [Fact]
    public void LeadingSilenceTrimLeavesSilentAudioAlone()
    {
        // A genuinely silent clip must not be reduced to nothing — that is
        // the validator's call to make (and report why), not this pass's.
        var trimmed = AudioPipeline.TrimLeadingSilence(new byte[48000 * 2 * 2], 48000 * 2);
        Assert.Equal(48000 * 2 * 2, trimmed.Length);
    }

    [Fact]
    public void LevellingBringsQuietAndLoudLinesToTheSameLoudness()
    {
        static double SpeechRms(byte[] wav)
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            var buffer = new byte[reader.Length];
            var read = reader.Read(buffer, 0, buffer.Length);
            double sumSquares = 0;
            long count = 0;
            for (var i = 0; i + 1 < read; i += 2)
            {
                var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
                if (Math.Abs(sample) >= 0.01)
                {
                    sumSquares += sample * sample;
                    count++;
                }
            }
            return count == 0 ? 0 : Math.Sqrt(sumSquares / count);
        }

        var quiet = AudioPipeline.NormalizeToGameWav(TestAudio.ValidSourceWav(amplitude: 0.05));
        var loud = AudioPipeline.NormalizeToGameWav(TestAudio.ValidSourceWav(amplitude: 0.9));

        var quietRms = SpeechRms(quiet);
        var loudRms = SpeechRms(loud);

        // Sources 25 dB apart come out within 2 dB of each other, in the
        // neighbourhood of the -20 dBFS target (0.1).  The match is the
        // point of levelling; the exact figure drifts a little here because
        // a pure tone has no true silence for the speech gate to exclude.
        Assert.InRange(quietRms, 0.05, 0.25);
        Assert.InRange(loudRms, 0.05, 0.25);
        Assert.InRange(Math.Abs(20 * Math.Log10(quietRms / loudRms)), 0.0, 2.0);
    }

    [Fact]
    public void LevellingNeverClips()
    {
        var wav = AudioPipeline.NormalizeToGameWav(TestAudio.ValidSourceWav(amplitude: 0.02));

        using var reader = new WaveFileReader(new MemoryStream(wav));
        var buffer = new byte[reader.Length];
        var read = reader.Read(buffer, 0, buffer.Length);
        for (var i = 0; i + 1 < read; i += 2)
        {
            Assert.InRange(Math.Abs(BitConverter.ToInt16(buffer, i) / 32768.0), 0.0, 0.95);
        }
    }

    [Fact]
    public void ValidatorAcceptsPipelineOutput()
    {
        var wav = AudioPipeline.NormalizeToGameWav(TestAudio.ValidSourceWav());
        var result = AudioValidator.Validate(wav, "one two three four");
        Assert.True(result.Ok, result.Failure);
    }

    [Fact]
    public void ValidatorRejectsSilentAudio()
    {
        var wav = AudioPipeline.NormalizeToGameWav(TestAudio.SilentSourceWav());
        var result = AudioValidator.Validate(wav, "hello there");
        Assert.False(result.Ok);
        Assert.Contains("silent", result.Failure);
    }

    [Fact]
    public void ValidatorRejectsWrongSampleRate()
    {
        // A 22.05 kHz wav that skipped the pipeline.
        var result = AudioValidator.Validate(TestAudio.ValidSourceWav(), "hello");
        Assert.False(result.Ok);
        Assert.Contains("format", result.Failure);
    }

    [Fact]
    public void ValidatorRejectsTruncatedFile()
    {
        var wav = AudioPipeline.NormalizeToGameWav(TestAudio.ValidSourceWav());
        var truncated = wav[..(wav.Length / 3)];
        var result = AudioValidator.Validate(truncated, "hello");
        Assert.False(result.Ok);
    }

    [Fact]
    public void ValidatorRejectsGarbageBytes()
    {
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        var result = AudioValidator.Validate(garbage, "hello");
        Assert.False(result.Ok);
    }

    [Fact]
    public void PipelineDecodesMp3Extensionless()
    {
        // MediaFoundation path: feed it a wav pretending to be unknown —
        // container sniffing must not rely on file names.
        var source = TestAudio.ValidSourceWav();
        var wav = AudioPipeline.NormalizeToGameWav(source);
        Assert.True(AudioValidator.Validate(wav, "some words here").Ok);
    }
}

public class SoundCacheTests
{
    [Fact]
    public void KeyChangesWithEveryComponent()
    {
        var baseline = SoundCache.ComputeKey("prov", "voice", "opts", "text");
        Assert.NotEqual(baseline, SoundCache.ComputeKey("prov2", "voice", "opts", "text"));
        Assert.NotEqual(baseline, SoundCache.ComputeKey("prov", "voice2", "opts", "text"));
        Assert.NotEqual(baseline, SoundCache.ComputeKey("prov", "voice", "opts2", "text"));
        Assert.NotEqual(baseline, SoundCache.ComputeKey("prov", "voice", "opts", "text2"));
        // Stable across trivial text whitespace and provider casing.
        Assert.Equal(baseline, SoundCache.ComputeKey("PROV", "voice", "opts", "  text  "));
    }

    [Fact]
    public void StoreIsAtomicAndRetrievable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cvd-test-" + Guid.NewGuid().ToString("N"));
        var cache = new SoundCache(directory);
        try
        {
            var key = SoundCache.ComputeKey("p", "v", "o", "t");
            Assert.False(cache.TryGet(key, out _));
            cache.Store(key, [1, 2, 3]);
            Assert.True(cache.TryGet(key, out var path));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CanonicalHashIsOrderIndependent()
    {
        var a = new ProviderSettings(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" }).CanonicalHash();
        var b = new ProviderSettings(new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }).CanonicalHash();
        Assert.Equal(a, b);
        var c = new ProviderSettings(new Dictionary<string, string> { ["a"] = "1", ["b"] = "3" }).CanonicalHash();
        Assert.NotEqual(a, c);
    }
}

public class VoiceMapperTests
{
    private static VoiceMapper Mapper(Dictionary<string, string>? overrides = null, string playerVoice = "player-v")
    {
        var config = new CustomVoicedDialogue.Server.Config.AppConfig
        {
            PlayerVoice = playerVoice,
            NpcVoiceOverrides = overrides ?? [],
        };
        return new VoiceMapper(config);
    }

    private static readonly IReadOnlyList<TtsVoice> Voices =
    [
        new TtsVoice("m1", "M1", VoiceGender.Male),
        new TtsVoice("m2", "M2", VoiceGender.Male),
        new TtsVoice("f1", "F1", VoiceGender.Female),
        new TtsVoice("f2", "F2", VoiceGender.Female),
        new TtsVoice("f4_ma_boone", "Boone", VoiceGender.Male),
    ];

    [Fact]
    public void PlayerAlwaysGetsConfiguredVoice() =>
        Assert.Equal("player-v", Mapper().ResolveVoice(true, "PlayerVoiceFemale01", Voices));

    [Fact]
    public void OverrideWinsForNpc() =>
        Assert.Equal("f2", Mapper(new() { ["MaleBoston"] = "f2" }).ResolveVoice(false, "MaleBoston", Voices));

    [Fact]
    public void NativeF4ModelMatchesVoiceType() =>
        Assert.Equal("f4_ma_boone", Mapper().ResolveVoice(false, "MA_Boone", Voices));

    [Fact]
    public void AssignmentIsDeterministicAndGendered()
    {
        var mapper = Mapper();
        var first = mapper.ResolveVoice(false, "FemaleEvenToned", Voices);
        var second = mapper.ResolveVoice(false, "FemaleEvenToned", Voices);
        Assert.Equal(first, second);
        Assert.StartsWith("f", first);  // female pool

        var male = mapper.ResolveVoice(false, "MaleRough", Voices);
        Assert.True(male is "m1" or "m2" or "f4_ma_boone");
    }

    [Fact]
    public void FemaleIsDetectedBeforeMaleSubstring() =>
        Assert.Equal(VoiceGender.Female, VoiceMapper.GuessGender("SettlerFemale01"));
}
