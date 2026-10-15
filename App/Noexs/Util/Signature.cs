using System.Globalization;
using System.Text;

namespace Noexs.Util;

/// <summary>A byte pattern with wildcards, written like "1F 20 03 D5 ?? ?? ?? 94".</summary>
public sealed class Signature
{
    public Signature(byte[] values, bool[] exact)
    {
        Values = values;
        Exact = exact;
    }

    public byte[] Values { get; }

    /// <summary>false = wildcard byte.</summary>
    public bool[] Exact { get; }

    public int Length => Values.Length;

    public int WildcardCount => Exact.Count(e => !e);

    public override string ToString()
    {
        var sb = new StringBuilder(Length * 3);
        for (int i = 0; i < Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(Exact[i] ? Values[i].ToString("X2") : "??");
        }
        return sb.ToString();
    }

    /// <summary>Parses "AA BB ?? CC" (also accepts "?" and no spaces, e.g. "AABB??CC").</summary>
    public static Signature Parse(string text)
    {
        string s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace("?", "??").Replace("????", "??");
        if (s.Length == 0 || s.Length % 2 != 0)
        {
            throw new FormatException("A signature is a list of hex bytes and ?? wildcards, e.g. \"1F 20 03 D5 ?? ?? ?? 94\"");
        }
        var values = new byte[s.Length / 2];
        var exact = new bool[s.Length / 2];
        for (int i = 0; i < values.Length; i++)
        {
            string b = s.Substring(i * 2, 2);
            if (b == "??")
            {
                continue;
            }
            if (!byte.TryParse(b, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out values[i]))
            {
                throw new FormatException($"\"{b}\" is not a hex byte");
            }
            exact[i] = true;
        }
        return new Signature(values, exact);
    }

    public bool MatchesAt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + Length > data.Length)
        {
            return false;
        }
        for (int i = 0; i < Length; i++)
        {
            if (Exact[i] && data[offset + i] != Values[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>All match offsets in <paramref name="data"/> (instructions are 4-byte aligned, so step defaults to 4).</summary>
    public List<int> Scan(ReadOnlySpan<byte> data, int step = 4, int max = 1000)
    {
        var result = new List<int>();
        // anchor on the first exact byte for a fast pre-check
        int anchor = Array.IndexOf(Exact, true);
        for (int off = 0; off + Length <= data.Length; off += step)
        {
            if (anchor >= 0 && data[off + anchor] != Values[anchor])
            {
                continue;
            }
            if (MatchesAt(data, off))
            {
                result.Add(off);
                if (result.Count >= max)
                {
                    break;
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Builds signatures from ARM64 code: bits that the linker changes between game builds are
/// wildcarded (branch/call targets, adrp pages, and the page offsets used with them), while
/// opcodes, registers and struct offsets stay exact.
/// </summary>
public static class SignatureMaker
{
    public sealed record Result(Signature Signature, long Start, int TargetOffset, int Matches, bool Unique, int Instructions);

    /// <summary>Bits of <paramref name="insn"/> that are build-specific. Updates the set of registers holding an adrp page.</summary>
    public static uint VariableBits(uint insn, HashSet<int> pageRegs)
    {
        int rd = (int)(insn & 0x1F);
        int rn = (int)((insn >> 5) & 0x1F);

        if ((insn & 0x7C000000) == 0x14000000) return 0x03FFFFFF;        // b / bl
        if ((insn & 0xFF000010) == 0x54000000) return 0x00FFFFE0;        // b.cond
        if ((insn & 0x7E000000) == 0x34000000) return 0x00FFFFE0;        // cbz / cbnz
        if ((insn & 0x7E000000) == 0x36000000) return 0x0007FFE0;        // tbz / tbnz
        if (IsLiteralLoad(insn))                                         // ldr (literal)
        {
            pageRegs.Remove(rd);
            return 0x00FFFFE0;
        }
        if (IsAdrp(insn) || IsAdr(insn))                                // adr / adrp
        {
            if (IsAdrp(insn))
            {
                pageRegs.Add(rd);
            }
            else
            {
                pageRegs.Remove(rd);
            }
            return 0x60FFFFE0;
        }
        if (IsAddImm(insn) && pageRegs.Contains(rn))                     // add rd, page, #pageoff
        {
            pageRegs.Remove(rd);
            return 0x003FFC00;
        }
        if (IsLdStUnsigned(insn) && pageRegs.Contains(rn))               // ldr/str rt, [page, #pageoff]
        {
            if (IsLoad(insn))
            {
                pageRegs.Remove(rd);
            }
            return 0x003FFC00;
        }
        // same register rules as the reference scan
        uint clobbered = Clobbers(insn);
        if (clobbered != 0)
        {
            pageRegs.RemoveWhere(r => (clobbered & (1u << r)) != 0);
        }
        return 0;
    }

    /// <summary>
    /// Registers (bit mask) whose page meaning an instruction ends, for instructions other than
    /// adrp/adr/add/ldr-str-with-page: loads overwrite rt (ldp also rt2), data processing writes
    /// rd, a call (bl/blr) clobbers x0-x18, and after b/br/ret the next instruction starts a
    /// different path, so nothing is known any more.
    /// </summary>
    internal static uint Clobbers(uint insn)
    {
        int rd = (int)(insn & 0x1F);
        if ((insn & 0x0A000000) == 0x08000000) // loads and stores
        {
            if (((insn >> 22) & 1) == 0)
            {
                return 0; // store
            }
            uint mask = 1u << rd;
            if ((insn & 0x3A000000) == 0x28000000) // load pair: rt2 too
            {
                mask |= 1u << (int)((insn >> 10) & 0x1F);
            }
            return mask;
        }
        if ((insn & 0x1C000000) == 0x14000000) // branches, exceptions, system
        {
            if ((insn & 0x7C000000) == 0x14000000) // b / bl
            {
                return (insn & 0x80000000) != 0 ? 0x7FFFFu : 0xFFFFFFFFu;
            }
            if ((insn & 0xFE000000) == 0xD6000000) // br / blr / ret
            {
                return ((insn >> 21) & 0xF) == 1 ? 0x7FFFFu : 0xFFFFFFFFu;
            }
            return 0; // conditional branches, cbz/tbz, nop, ...
        }
        return 1u << rd; // data processing
    }

    /// <summary>Load (or prefetch) vs. store for the ldr/str (unsigned offset) class.</summary>
    private static bool IsLoad(uint insn)
    {
        int opc = (int)((insn >> 22) & 3);
        bool simd = ((insn >> 26) & 1) != 0;
        return simd ? (opc & 1) != 0 : opc != 0;
    }

    /// <summary>Instructions before a signature that are checked for an adrp feeding it.</summary>
    public const int LookBack = 16;

    /// <summary>Signature bytes for <paramref name="count"/> instructions starting at <paramref name="offset"/> in code.</summary>
    public static Signature Build(ReadOnlySpan<byte> code, int offset, int count)
    {
        var values = new byte[count * 4];
        var exact = new bool[count * 4];
        var pageRegs = new HashSet<int>();
        // an adrp just before the start still makes the page offsets in the signature build-specific
        for (int p = Math.Max(0, offset - LookBack * 4); p < offset; p += 4)
        {
            VariableBits(BitConverter.ToUInt32(code.Slice(p, 4)), pageRegs);
        }
        for (int i = 0; i < count; i++)
        {
            uint insn = BitConverter.ToUInt32(code.Slice(offset + i * 4, 4));
            uint variable = VariableBits(insn, pageRegs);
            for (int b = 0; b < 4; b++)
            {
                values[i * 4 + b] = (byte)(insn >> (b * 8));
                exact[i * 4 + b] = ((variable >> (b * 8)) & 0xFF) == 0;
            }
        }
        return new Signature(values, exact);
    }

    /// <summary>
    /// Shortest unique signature for the instruction at <paramref name="target"/> inside
    /// <paramref name="code"/> (which starts at <paramref name="codeBase"/>). Tries starting up to
    /// <paramref name="maxBack"/> instructions earlier when the code at the target alone isn't unique.
    /// </summary>
    /// <param name="minBack">start at least this many instructions before the target (e.g. to include its adrp)</param>
    public static Result Create(byte[] code, long codeBase, long target, int minInstructions = 4, int maxInstructions = 32, int maxBack = 8,
        int minBack = 0)
    {
        target &= ~3L;
        int targetOff = (int)(target - codeBase);
        if (targetOff < 0 || targetOff + 4 > code.Length)
        {
            throw new ArgumentException("the address is not inside the code region");
        }

        Result? best = null;
        for (int back = minBack; back <= minBack + maxBack; back++)
        {
            int start = targetOff - back * 4;
            if (start < 0)
            {
                break;
            }
            int maxCount = Math.Min(maxInstructions, (code.Length - start) / 4);
            int minCount = Math.Max(minInstructions, back + 1);
            if (minCount > maxCount)
            {
                continue;
            }
            // candidates for the shortest pattern, then narrowed as the pattern grows
            Signature sig = Build(code, start, minCount);
            List<int> candidates = sig.Scan(code, 4, int.MaxValue);
            int count = minCount;
            while (candidates.Count > 1 && count < maxCount)
            {
                count++;
                sig = Build(code, start, count);
                Signature s = sig;
                candidates = candidates.Where(c => s.MatchesAt(code, c)).ToList();
            }
            var result = new Result(sig, codeBase + start, back * 4, candidates.Count, candidates.Count == 1, count);
            if (result.Unique && (best == null || !best.Unique || result.Signature.Length < best.Signature.Length))
            {
                best = result;
            }
            else if (best == null)
            {
                best = result;
            }
            if (best.Unique && best.Instructions <= Math.Max(minInstructions, back + 2))
            {
                break; // starting further back can't give a shorter one
            }
        }
        return best!;
    }

    /// <summary>
    /// A global address computed by code (adrp + add/ldr/str, or a literal load).
    /// <paramref name="PageInstruction"/> is the adrp it depends on (null for adr / literal loads).
    /// </summary>
    public sealed record Reference(long InstructionAddress, long Target, string Kind, long? PageInstruction = null);

    private static long AdrImm(uint insn)
    {
        long imm = ((insn >> 29) & 3) | (((insn >> 5) & 0x7FFFF) << 2);
        return (imm << 43) >> 43; // sign-extend 21 bits
    }

    private static bool IsAdrp(uint insn) => (insn & 0x9F000000) == 0x90000000;

    private static bool IsAdr(uint insn) => (insn & 0x9F000000) == 0x10000000;

    private static bool IsLiteralLoad(uint insn) => (insn & 0x3B000000) == 0x18000000;

    private static bool IsAddImm(uint insn) => (insn & 0x7F800000) == 0x11000000;

    private static bool IsLdStUnsigned(uint insn) => (insn & 0x3B000000) == 0x39000000;

    private static long LiteralImm(uint insn) => (((long)((insn >> 5) & 0x7FFFF)) << 45) >> 45 << 2;

    private static long AddImm(uint insn)
    {
        long imm = (insn >> 10) & 0xFFF;
        return ((insn >> 22) & 1) != 0 ? imm << 12 : imm;
    }

    private static long LdStImm(uint insn)
    {
        int size = (int)(insn >> 30);
        bool simd = ((insn >> 26) & 1) != 0;
        int opc = (int)((insn >> 22) & 3);
        int scale = simd && size == 0 && opc >= 2 ? 4 : size; // 128-bit q register
        return ((insn >> 10) & 0xFFF) << scale;
    }

    /// <summary>
    /// Resolves adrp-based and literal references in <paramref name="count"/> instructions at <paramref name="pc"/>.
    /// With <paramref name="lookBack"/> an adrp just before the code counts too; without it only
    /// references computed entirely inside the code are returned (what a signature match can rely on).
    /// </summary>
    public static List<Reference> References(ReadOnlySpan<byte> code, int offset, long pc, int count, bool lookBack = true)
    {
        // start a little earlier so an adrp right before the code is known (those refs are dropped)
        int back = lookBack ? Math.Min(LookBack, Math.Max(0, offset) / 4) : 0;
        List<Reference> refs = Scan(code, offset - back * 4, count + back, pc - back * 4, long.MinValue, long.MaxValue, int.MaxValue);
        refs.RemoveAll(r => r.InstructionAddress < pc);
        return refs;
    }

    /// <summary>
    /// Every instruction in <paramref name="code"/> that computes an address in [lo, hi]
    /// (e.g. all code using a global variable), up to <paramref name="max"/>.
    /// </summary>
    public static List<Reference> FindReferences(ReadOnlySpan<byte> code, long codeBase, long lo, long hi, int max = 256)
    {
        return Scan(code, 0, code.Length / 4, codeBase, lo, hi, max);
    }

    private static List<Reference> Scan(ReadOnlySpan<byte> code, int offset, int count, long pc, long lo, long hi, int max)
    {
        var refs = new List<Reference>();
        var page = new long[32];
        var setAt = new int[32]; // instruction index of the adrp, or -1
        Array.Fill(setAt, -1);
        bool Live(int reg, int i) => setAt[reg] >= 0 && i - setAt[reg] <= LookBack;
        void Add(long at, long target, string kind, long? pageAt = null)
        {
            if (target >= lo && target <= hi && refs.Count < max)
            {
                refs.Add(new Reference(at, target, kind, pageAt));
            }
        }

        for (int i = 0; i < count && offset + i * 4 + 4 <= code.Length && refs.Count < max; i++)
        {
            uint insn = BitConverter.ToUInt32(code.Slice(offset + i * 4, 4));
            long at = pc + i * 4L;
            int rd = (int)(insn & 0x1F);
            int rn = (int)((insn >> 5) & 0x1F);
            if (IsAdrp(insn))
            {
                page[rd] = (at & ~0xFFFL) + (AdrImm(insn) << 12);
                setAt[rd] = i;
            }
            else if (IsAdr(insn))
            {
                Add(at, at + AdrImm(insn), "address");
                setAt[rd] = -1;
            }
            else if (IsLiteralLoad(insn))
            {
                Add(at, at + LiteralImm(insn), "literal load");
                setAt[rd] = -1;
            }
            else if (IsAddImm(insn) && Live(rn, i))
            {
                Add(at, page[rn] + AddImm(insn), "address", pc + setAt[rn] * 4L);
                setAt[rd] = -1;
            }
            else if (IsLdStUnsigned(insn) && Live(rn, i))
            {
                bool load = IsLoad(insn);
                Add(at, page[rn] + LdStImm(insn), load ? "load" : "store", pc + setAt[rn] * 4L);
                if (load)
                {
                    setAt[rd] = -1;
                }
            }
            else
            {
                uint clobbered = Clobbers(insn);
                for (int r = 0; clobbered != 0 && r < 32; r++)
                {
                    if ((clobbered & (1u << r)) != 0)
                    {
                        setAt[r] = -1;
                    }
                }
            }
        }
        return refs;
    }
}
