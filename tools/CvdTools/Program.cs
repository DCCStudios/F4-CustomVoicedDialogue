using System.Globalization;

// Offline verification tooling for the CustomVoicedDialogue F4SE plugin.
//
// Commands:
//   resolve <bin> <id...>              Address Library ID -> RVA
//   reverse <bin> <rvaHex...>          RVA -> Address Library ID(s)
//   scan <exe> <sig>                   IDA-style signature scan, prints RVAs
//   bytes <exe> <rvaHex> <len>         hex dump at an RVA
//   calltarget <exe> <rvaHex>          resolve the rel32 call/jmp at an RVA
//   guardcheck <manifest.json>         verify every hook site against its exe
//
// The F4SE "version-x-y-z-0.bin" files are Address Library format V0:
// a uint64 entry count followed by count * { uint64 id, uint64 rva } pairs
// sorted by id (see CommonLib REL::IDDB::load_v0).

return args.Length == 0 ? Usage() : args[0] switch
{
    "resolve" => Resolve(args),
    "reverse" => Reverse(args),
    "scan" => Scan(args),
    "bytes" => Bytes(args),
    "calltarget" => CallTarget(args),
    "xref" => Xref(args),
    "funcstart" => FuncStart(args),
    "guardcheck" => GuardCheck.Run(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: CvdTools resolve|reverse|scan|bytes|calltarget|guardcheck ...");
    return 2;
}

static int Resolve(string[] args)
{
    var db = AddressLibrary.Load(args[1]);
    for (var i = 2; i < args.Length; i++)
    {
        var id = ulong.Parse(args[i], CultureInfo.InvariantCulture);
        Console.WriteLine(db.TryResolve(id, out var rva)
            ? $"ID {id} -> 0x{rva:X}"
            : $"ID {id} -> MISSING");
    }
    return 0;
}

static int Reverse(string[] args)
{
    var db = AddressLibrary.Load(args[1]);
    for (var i = 2; i < args.Length; i++)
    {
        var rva = ParseHex(args[i]);
        var ids = db.ReverseLookup(rva);
        Console.WriteLine(ids.Count > 0
            ? $"RVA 0x{rva:X} -> IDs: {string.Join(", ", ids)}"
            : $"RVA 0x{rva:X} -> NO ID");
    }
    return 0;
}

static int Scan(string[] args)
{
    var pe = PeImage.Load(args[1]);
    var matches = pe.SignatureScan(args[2]);
    if (matches.Count == 0)
    {
        Console.WriteLine("no matches");
        return 1;
    }
    foreach (var rva in matches)
    {
        Console.WriteLine($"match at RVA 0x{rva:X}");
    }
    return 0;
}

static int Bytes(string[] args)
{
    var pe = PeImage.Load(args[1]);
    var rva = ParseHex(args[2]);
    var length = int.Parse(args[3], CultureInfo.InvariantCulture);
    var bytes = pe.ReadRva(rva, length);
    Console.WriteLine($"0x{rva:X}: {Convert.ToHexString(bytes)}");
    return 0;
}

static int CallTarget(string[] args)
{
    var pe = PeImage.Load(args[1]);
    var rva = ParseHex(args[2]);
    var bytes = pe.ReadRva(rva, 5);
    if (bytes[0] != 0xE8 && bytes[0] != 0xE9)
    {
        Console.WriteLine($"0x{rva:X}: not a rel32 call/jmp (first byte 0x{bytes[0]:X2})");
        return 1;
    }
    var rel = BitConverter.ToInt32(bytes, 1);
    var target = (ulong)((long)rva + 5 + rel);
    Console.WriteLine($"0x{rva:X}: {(bytes[0] == 0xE8 ? "call" : "jmp")} -> 0x{target:X}");
    return 0;
}

static int Xref(string[] args)
{
    // Finds rip-relative references to a target RVA: any position whose
    // trailing rel32 (ending the instruction) resolves to the target.
    // Covers call/jmp rel32 and lea/mov [rip+disp] operands.
    var pe = PeImage.Load(args[1]);
    var target = ParseHex(args[2]);
    var matches = pe.FindRipRelativeReferences(target);
    if (matches.Count == 0)
    {
        Console.WriteLine("no references");
        return 1;
    }
    foreach (var (rva, kind) in matches)
    {
        Console.WriteLine($"ref at RVA 0x{rva:X} ({kind})");
    }
    return 0;
}

static int FuncStart(string[] args)
{
    // Walks backwards from an RVA to the first instruction after 0xCC/0x90
    // padding — a good-enough function start heuristic for MSVC builds.
    var pe = PeImage.Load(args[1]);
    var rva = ParseHex(args[2]);
    Console.WriteLine($"function containing 0x{rva:X} starts at 0x{pe.FindFunctionStart(rva):X}");
    return 0;
}

static ulong ParseHex(string value) =>
    ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
