/// <summary>
/// Parser for F4SE Address Library format V0 databases
/// (version-x-y-z-0.bin): uint64 count + count * { uint64 id, uint64 rva },
/// sorted by id.
/// </summary>
public sealed class AddressLibrary
{
    private readonly ulong[] _ids;
    private readonly ulong[] _rvas;

    private AddressLibrary(ulong[] ids, ulong[] rvas)
    {
        _ids = ids;
        _rvas = rvas;
    }

    public int Count => _ids.Length;

    public static AddressLibrary Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var count = checked((int)reader.ReadUInt64());

        var expectedSize = 8L + (long)count * 16;
        if (reader.BaseStream.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"'{path}' does not look like a V0 address library: expected {expectedSize} bytes for {count} entries, file is {reader.BaseStream.Length}");
        }

        var ids = new ulong[count];
        var rvas = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = reader.ReadUInt64();
            rvas[i] = reader.ReadUInt64();
        }
        return new AddressLibrary(ids, rvas);
    }

    public bool TryResolve(ulong id, out ulong rva)
    {
        var index = Array.BinarySearch(_ids, id);
        if (index < 0)
        {
            rva = 0;
            return false;
        }
        rva = _rvas[index];
        return true;
    }

    /// <summary>All IDs that map to the given RVA (usually exactly one).</summary>
    public IReadOnlyList<ulong> ReverseLookup(ulong rva)
    {
        var matches = new List<ulong>();
        for (var i = 0; i < _ids.Length; i++)
        {
            if (_rvas[i] == rva)
            {
                matches.Add(_ids[i]);
            }
        }
        return matches;
    }
}
