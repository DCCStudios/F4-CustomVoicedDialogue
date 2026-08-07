/// <summary>
/// Minimal PE reader: maps RVAs to file offsets and runs IDA-style
/// signature scans ("E8 ? ? ? ? 48 8B F0") over the whole image.
/// </summary>
public sealed class PeImage
{
    private readonly byte[] _file;
    private readonly List<(uint Rva, uint RawOffset, uint RawSize, uint VirtualSize)> _sections = [];

    private PeImage(byte[] file)
    {
        _file = file;
        var peOffset = BitConverter.ToInt32(file, 0x3C);
        if (BitConverter.ToUInt32(file, peOffset) != 0x00004550)  // "PE\0\0"
        {
            throw new InvalidDataException("not a PE file");
        }

        var sectionCount = BitConverter.ToUInt16(file, peOffset + 6);
        var optionalHeaderSize = BitConverter.ToUInt16(file, peOffset + 20);
        var sectionTable = peOffset + 24 + optionalHeaderSize;
        for (var i = 0; i < sectionCount; i++)
        {
            var entry = sectionTable + i * 40;
            var virtualSize = BitConverter.ToUInt32(file, entry + 8);
            var rva = BitConverter.ToUInt32(file, entry + 12);
            var rawSize = BitConverter.ToUInt32(file, entry + 16);
            var rawOffset = BitConverter.ToUInt32(file, entry + 20);
            _sections.Add((rva, rawOffset, rawSize, virtualSize));
        }
    }

    public static PeImage Load(string path) => new(File.ReadAllBytes(path));

    public byte[] ReadRva(ulong rva, int length)
    {
        var offset = RvaToFileOffset(rva);
        var bytes = new byte[length];
        Array.Copy(_file, offset, bytes, 0, length);
        return bytes;
    }

    public long RvaToFileOffset(ulong rva)
    {
        foreach (var section in _sections)
        {
            if (rva >= section.Rva && rva < section.Rva + Math.Max(section.RawSize, section.VirtualSize))
            {
                return (long)(rva - section.Rva) + section.RawOffset;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(rva), $"RVA 0x{rva:X} is not inside any section");
    }

    public ulong FileOffsetToRva(long offset)
    {
        foreach (var section in _sections)
        {
            if (offset >= section.RawOffset && offset < section.RawOffset + section.RawSize)
            {
                return (ulong)(offset - section.RawOffset) + section.Rva;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(offset));
    }

    /// <summary>
    /// Finds positions whose trailing rel32 resolves to the target RVA.
    /// Reported as the RVA of the 4-byte displacement minus the typical
    /// opcode length, labeled by the byte(s) preceding the displacement.
    /// </summary>
    public IReadOnlyList<(ulong Rva, string Kind)> FindRipRelativeReferences(ulong target)
    {
        var matches = new List<(ulong, string)>();
        foreach (var section in _sections)
        {
            var end = section.RawOffset + Math.Min(section.RawSize, section.VirtualSize);
            for (long offset = section.RawOffset; offset + 4 <= end; offset++)
            {
                var displacement = BitConverter.ToInt32(_file, (int)offset);
                var siteRva = (ulong)(offset - section.RawOffset) + section.Rva;
                // rip-relative: target = rva-after-displacement + disp
                if ((long)siteRva + 4 + displacement != (long)target)
                {
                    continue;
                }

                var prev = offset >= 1 ? _file[offset - 1] : (byte)0;
                var prev2 = offset >= 2 ? _file[offset - 2] : (byte)0;
                var kind = prev switch
                {
                    0xE8 => "call",
                    0xE9 => "jmp",
                    _ when prev2 == 0x8D => $"lea (modrm {prev:X2})",
                    _ when prev2 == 0x8B => $"mov (modrm {prev:X2})",
                    _ => $"bytes {prev2:X2} {prev:X2}",
                };
                matches.Add((siteRva - 1, kind));
            }
        }
        return matches;
    }

    public ulong FindFunctionStart(ulong rva)
    {
        var offset = RvaToFileOffset(rva);
        // Scan backwards for int3/nop padding, then step past it.
        while (offset > 0 && _file[offset - 1] != 0xCC)
        {
            offset--;
        }
        return FileOffsetToRva(offset);
    }

    public IReadOnlyList<ulong> SignatureScan(string signature)
    {
        var parts = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pattern = new int[parts.Length];  // -1 = wildcard
        for (var i = 0; i < parts.Length; i++)
        {
            pattern[i] = parts[i] is "?" or "??" ? -1 : Convert.ToInt32(parts[i], 16);
        }

        var matches = new List<ulong>();
        var limit = _file.Length - pattern.Length;
        for (var offset = 0; offset <= limit; offset++)
        {
            var hit = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] >= 0 && _file[offset + i] != pattern[i])
                {
                    hit = false;
                    break;
                }
            }
            if (!hit)
            {
                continue;
            }
            try
            {
                matches.Add(FileOffsetToRva(offset));
            }
            catch (ArgumentOutOfRangeException)
            {
                // match in headers/overlay — not a code match
            }
        }
        return matches;
    }
}
