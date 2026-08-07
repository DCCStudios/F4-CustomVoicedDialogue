using System.Security.Cryptography;
using System.Text;

namespace CustomVoicedDialogue.Server.Cache;

/// <summary>
/// Content-addressed wav cache.  The key covers everything that shapes the
/// audio — provider, voice, canonical option hash, and the exact text — so
/// changing any setting can never serve stale audio (HerikaServer keyed on
/// md5(text) alone and had to disable its cache to survive that).
/// Files are written atomically (tmp + rename) so a concurrent reader can
/// never observe a partial wav.
/// </summary>
public sealed class SoundCache
{
    private readonly string _directory;

    public SoundCache(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string CacheDirectory => _directory;

    public static string ComputeKey(string providerId, string voice, string optionsHash, string text)
    {
        var canonical = $"{providerId.ToLowerInvariant()}|{voice}|{optionsHash}|{text.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public string PathFor(string key) => Path.Combine(_directory, key + ".wav");

    public bool TryGet(string key, out string path)
    {
        path = PathFor(key);
        return File.Exists(path);
    }

    public string Store(string key, byte[] wavBytes)
    {
        var path = PathFor(key);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, wavBytes);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>Deletes cached audio older than the given age; returns the
    /// number of files removed.  Surfaced as a GUI maintenance action.</summary>
    public int Prune(TimeSpan maxAge)
    {
        var removed = 0;
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.wav"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
                // In use; skip.
            }
        }
        return removed;
    }

    public (int Files, long Bytes) Stats()
    {
        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.wav"))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }
        return (files, bytes);
    }
}
