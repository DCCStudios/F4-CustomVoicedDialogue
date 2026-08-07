using System.IO;
using NAudio.Wave;

namespace CustomVoicedDialogue.App;

/// <summary>Plays a wav file or byte buffer for GUI previews.</summary>
public sealed class AudioPreview : IDisposable
{
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private MemoryStream? _stream;

    public void Play(byte[] wavBytes)
    {
        Stop();
        _stream = new MemoryStream(wavBytes);
        _reader = new WaveFileReader(_stream);
        _output = new WaveOutEvent();
        _output.Init(_reader);
        _output.Play();
    }

    public void PlayFile(string path) => Play(File.ReadAllBytes(path));

    public void Stop()
    {
        _output?.Stop();
        _output?.Dispose();
        _reader?.Dispose();
        _stream?.Dispose();
        _output = null;
        _reader = null;
        _stream = null;
    }

    public void Dispose() => Stop();
}
