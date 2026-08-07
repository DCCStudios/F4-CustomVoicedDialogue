using System.Text.Json;

/// <summary>
/// Verifies every hook site the plugin patches, per runtime, without
/// launching the game: resolves each site's Address Library ID against the
/// matching database, reads the executable's bytes at base+delta, and
/// compares them with the expected opcodes.  Run before every release and
/// whenever a new game patch drops.
///
/// Manifest shape (paths relative to the manifest file):
/// {
///   "runtimes": {
///     "OG":  { "exe": "...", "bin": "..." },
///     "NG":  { "exe": "...", "bin": "..." }
///   },
///   "sites": [
///     { "name": "...", "id": { "OG": 123, "NG": 456 },
///       "delta": { "OG": "0x102", "NG": "0x102" },
///       "expected": { "OG": "E8", "NG": "E8" },
///       "callTargetId": { "NG": 2268671 } }   // optional rel32 destination check
///   ]
/// }
/// </summary>
public static class GuardCheck
{
    public static int Run(string[] args)
    {
        var manifestPath = Path.GetFullPath(args[1]);
        var root = Path.GetDirectoryName(manifestPath)!;
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var runtimes = new Dictionary<string, (PeImage Exe, AddressLibrary Db)>();
        foreach (var runtime in manifest.RootElement.GetProperty("runtimes").EnumerateObject())
        {
            var exePath = Path.Combine(root, runtime.Value.GetProperty("exe").GetString()!);
            var binPath = Path.Combine(root, runtime.Value.GetProperty("bin").GetString()!);
            runtimes[runtime.Name] = (PeImage.Load(exePath), AddressLibrary.Load(binPath));
        }

        var failures = 0;
        var checks = 0;
        foreach (var site in manifest.RootElement.GetProperty("sites").EnumerateArray())
        {
            var name = site.GetProperty("name").GetString()!;
            foreach (var (runtimeName, (exe, db)) in runtimes)
            {
                if (!site.GetProperty("id").TryGetProperty(runtimeName, out var idElement))
                {
                    Console.WriteLine($"SKIP  [{runtimeName}] {name}: no ID for this runtime");
                    continue;
                }

                checks++;
                var id = idElement.GetUInt64();
                if (!db.TryResolve(id, out var baseRva))
                {
                    Console.WriteLine($"FAIL  [{runtimeName}] {name}: ID {id} missing from the address library");
                    failures++;
                    continue;
                }

                var delta = ParseHex(site.GetProperty("delta").GetProperty(runtimeName).GetString()!);
                var expected = Convert.FromHexString(site.GetProperty("expected").GetProperty(runtimeName).GetString()!.Replace(" ", ""));
                var actual = exe.ReadRva(baseRva + delta, expected.Length);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    Console.WriteLine(
                        $"FAIL  [{runtimeName}] {name}: at 0x{baseRva + delta:X} expected {Convert.ToHexString(expected)}, found {Convert.ToHexString(actual)}");
                    failures++;
                    continue;
                }

                // Optional: the rel32 call at the site must land on another
                // known Address Library ID (e.g. BSFixedString::Set).
                if (site.TryGetProperty("callTargetId", out var callTargets) &&
                    callTargets.TryGetProperty(runtimeName, out var callTargetElement))
                {
                    var siteBytes = exe.ReadRva(baseRva + delta, 5);
                    var rel = BitConverter.ToInt32(siteBytes, 1);
                    var destination = (ulong)((long)(baseRva + delta) + 5 + rel);
                    if (!db.TryResolve(callTargetElement.GetUInt64(), out var expectedDestination))
                    {
                        Console.WriteLine($"FAIL  [{runtimeName}] {name}: call target ID missing from the address library");
                        failures++;
                        continue;
                    }
                    if (destination != expectedDestination)
                    {
                        Console.WriteLine(
                            $"FAIL  [{runtimeName}] {name}: call resolves to 0x{destination:X}, expected 0x{expectedDestination:X}");
                        failures++;
                        continue;
                    }
                }

                Console.WriteLine($"OK    [{runtimeName}] {name}: 0x{baseRva + delta:X} = {Convert.ToHexString(expected)}");
            }
        }

        Console.WriteLine($"\n{checks - failures}/{checks} checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static ulong ParseHex(string value) =>
        Convert.ToUInt64(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, 16);
}
