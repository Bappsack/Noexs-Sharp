using System.Globalization;
using System.Numerics;

namespace Noexs.Util;

/// <summary>
/// A small ARM64 assembler for the instructions typically patched in cheats, in the same syntax
/// the disassembler prints (so an instruction can be edited and re-assembled in place).
/// Branch targets are absolute addresses ("b #0x7100001234"), as printed by Capstone.
/// </summary>
public static class Arm64Assembler
{
    public const string Supported =
        "nop, ret/br/blr, b/bl/b.cond, cbz/cbnz, tbz/tbnz, mov/movz/movn/movk, add/sub/adds/subs, cmp/cmn, " +
        "and/orr/eor/ands/tst, neg, ldr/str/ldrb/strb/ldrh/strh/ldrsb/ldrsh/ldrsw (+ldur/stur), ldp/stp, " +
        ".word 0x... or raw hex bytes";

    public sealed class AsmException(string message) : Exception(message);

    private readonly record struct Reg(int Num, bool Is64, bool IsSp);

    /// <summary>Assembles one instruction at <paramref name="pc"/> and returns its 4 little-endian bytes.</summary>
    public static byte[] Assemble(string text, long pc)
    {
        uint word = Encode(text.Trim(), pc);
        return BitConverter.GetBytes(word);
    }

    private static uint Encode(string text, long pc)
    {
        if (text.Length == 0)
        {
            throw new AsmException("empty instruction");
        }
        string lower = text.ToLowerInvariant();

        // raw data: ".word 0xd503201f" or 8 hex digits of instruction bytes ("1F2003D5")
        if (lower.StartsWith(".word", StringComparison.Ordinal) || lower.StartsWith(".inst", StringComparison.Ordinal))
        {
            return (uint)ParseNumber(lower[5..].Trim().TrimStart('#'));
        }
        string compact = lower.Replace(" ", "");
        if (compact.Length == 8 && compact.All(Uri.IsHexDigit))
        {
            return BitConverter.ToUInt32(Convert.FromHexString(compact));
        }

        int space = lower.IndexOfAny([' ', '\t']);
        string mn = space < 0 ? lower : lower[..space];
        string[] ops = space < 0 ? [] : SplitOperands(lower[(space + 1)..]);

        switch (mn)
        {
            case "nop":
                return 0xD503201F;
            case "ret":
                return 0xD65F0000 | (uint)(ops.Length > 0 ? GpReg(ops[0]).Num : 30) << 5;
            case "br":
                return 0xD61F0000 | (uint)GpReg(ops[0]).Num << 5;
            case "blr":
                return 0xD63F0000 | (uint)GpReg(ops[0]).Num << 5;
            case "b" or "bl":
            {
                Need(ops, 1, mn);
                long off = BranchOffset(ops[0], pc, 26, mn);
                return (mn == "b" ? 0x14000000u : 0x94000000u) | ((uint)(off >> 2) & 0x3FFFFFF);
            }
            case "cbz" or "cbnz":
            {
                Need(ops, 2, mn);
                Reg rt = GpReg(ops[0]);
                long off = BranchOffset(ops[1], pc, 19, mn);
                return Sf(rt) | (mn == "cbz" ? 0x34000000u : 0x35000000u) | ((uint)(off >> 2) & 0x7FFFF) << 5 | (uint)rt.Num;
            }
            case "tbz" or "tbnz":
            {
                Need(ops, 3, mn);
                Reg rt = GpReg(ops[0]);
                int bit = (int)ParseImm(ops[1]);
                if (bit < 0 || bit > (rt.Is64 ? 63 : 31))
                {
                    throw new AsmException("bit number out of range");
                }
                long off = BranchOffset(ops[2], pc, 14, mn);
                return (uint)(bit >> 5) << 31 | (mn == "tbz" ? 0x36000000u : 0x37000000u) | (uint)(bit & 0x1F) << 19 |
                       ((uint)(off >> 2) & 0x3FFF) << 5 | (uint)rt.Num;
            }
            case "movz" or "movn" or "movk":
                return MoveWide(mn, ops);
            case "mov":
                return Mov(ops);
            case "add" or "sub" or "adds" or "subs":
                return AddSub(mn, ops);
            case "cmp" or "cmn":
            {
                Need(ops, 2, mn);
                Reg rn = GpReg(ops[0], allowSp: true);
                string[] full = [rn.Is64 ? "xzr" : "wzr", .. ops];
                return AddSub(mn == "cmp" ? "subs" : "adds", full);
            }
            case "neg":
            {
                Need(ops, 2, mn);
                Reg rd = GpReg(ops[0]);
                return AddSub("sub", [ops[0], rd.Is64 ? "xzr" : "wzr", .. ops[1..]]);
            }
            case "and" or "orr" or "eor" or "ands":
                return Logical(mn, ops);
            case "tst":
            {
                Need(ops, 2, mn);
                Reg rn = GpReg(ops[0]);
                return Logical("ands", [rn.Is64 ? "xzr" : "wzr", .. ops]);
            }
            case "ldr" or "str" or "ldrb" or "strb" or "ldrh" or "strh" or "ldrsb" or "ldrsh" or "ldrsw"
                or "ldur" or "stur" or "ldurb" or "sturb" or "ldurh" or "sturh" or "ldursb" or "ldursh" or "ldursw":
                return LoadStore(mn, ops);
            case "ldp" or "stp":
                return Pair(mn, ops);
        }
        if (mn.StartsWith("b.", StringComparison.Ordinal))
        {
            Need(ops, 1, mn);
            int cond = Cond(mn[2..]);
            long off = BranchOffset(ops[0], pc, 19, mn);
            return 0x54000000u | ((uint)(off >> 2) & 0x7FFFF) << 5 | (uint)cond;
        }
        throw new AsmException($"unsupported instruction \"{mn}\".\nSupported: {Supported}");
    }

    // ---- operand helpers ----

    /// <summary>Splits on commas outside of [...] brackets.</summary>
    private static string[] SplitOperands(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']') depth--;
            else if (s[i] == ',' && depth == 0)
            {
                parts.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(s[start..].Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }

    private static void Need(string[] ops, int n, string mn)
    {
        if (ops.Length < n)
        {
            throw new AsmException($"{mn} needs {n} operand(s)");
        }
    }

    private static Reg GpReg(string s, bool allowSp = false)
    {
        s = s.Trim();
        switch (s)
        {
            case "xzr": return new Reg(31, true, false);
            case "wzr": return new Reg(31, false, false);
            case "lr": return new Reg(30, true, false);
            case "fp": return new Reg(29, true, false);
            case "sp" when allowSp: return new Reg(31, true, true);
            case "wsp" when allowSp: return new Reg(31, false, true);
            case "sp" or "wsp": throw new AsmException("sp is not allowed here");
        }
        if (s.Length >= 2 && (s[0] == 'x' || s[0] == 'w')
            && int.TryParse(s[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n <= 30)
        {
            return new Reg(n, s[0] == 'x', false);
        }
        throw new AsmException($"\"{s}\" is not a general purpose register");
    }

    private static bool IsReg(string s)
    {
        try
        {
            GpReg(s, allowSp: true);
            return true;
        }
        catch (AsmException)
        {
            return false;
        }
    }

    private static uint Sf(Reg r) => r.Is64 ? 0x80000000u : 0u;

    private static long ParseNumber(string s)
    {
        s = s.Trim();
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        ulong v = s.StartsWith("0x", StringComparison.Ordinal)
            ? ulong.Parse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : ulong.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
        return neg ? -(long)v : (long)v;
    }

    private static long ParseImm(string s)
    {
        s = s.Trim();
        if (s.StartsWith('#')) s = s[1..];
        try
        {
            return ParseNumber(s);
        }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new AsmException($"\"{s}\" is not a number");
        }
    }

    /// <summary>"lsl #12" etc. Returns (kind, amount).</summary>
    private static (string Kind, int Amount) Shift(string s)
    {
        string[] p = s.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return (p[0], p.Length > 1 ? (int)ParseImm(p[1]) : 0);
    }

    private static long BranchOffset(string target, long pc, int bits, string mn)
    {
        long dest = ParseImm(target);
        long off = dest - pc;
        if ((off & 3) != 0)
        {
            throw new AsmException("branch target must be 4-byte aligned");
        }
        long range = 1L << (bits + 1); // +-(2^bits * 4) / 2
        if (off < -range || off >= range)
        {
            throw new AsmException($"{mn} target out of range (max +-0x{range:X})");
        }
        return off;
    }

    private static int Cond(string c)
    {
        string[] names = ["eq", "ne", "hs", "lo", "mi", "pl", "vs", "vc", "hi", "ls", "ge", "lt", "gt", "le", "al", "nv"];
        c = c switch { "cs" => "hs", "cc" => "lo", _ => c };
        int i = Array.IndexOf(names, c);
        return i >= 0 ? i : throw new AsmException($"unknown condition \"{c}\"");
    }

    // ---- instruction classes ----

    private static uint MoveWide(string mn, string[] ops)
    {
        Need(ops, 2, mn);
        Reg rd = GpReg(ops[0]);
        long imm = ParseImm(ops[1]);
        int shift = ops.Length > 2 ? Shift(ops[2]).Amount : 0;
        if (shift % 16 != 0 || shift > (rd.Is64 ? 48 : 16) || imm < 0 || imm > 0xFFFF)
        {
            throw new AsmException($"{mn}: immediate must be 0..0xffff with lsl #0/16{(rd.Is64 ? "/32/48" : "")}");
        }
        uint opc = mn switch { "movn" => 0x12800000u, "movz" => 0x52800000u, _ => 0x72800000u };
        return Sf(rd) | opc | (uint)(shift / 16) << 21 | (uint)imm << 5 | (uint)rd.Num;
    }

    private static uint Mov(string[] ops)
    {
        Need(ops, 2, "mov");
        Reg rd = GpReg(ops[0], allowSp: true);
        if (IsReg(ops[1]))
        {
            Reg rm = GpReg(ops[1], allowSp: true);
            if (rd.IsSp || rm.IsSp)
            {
                // mov to/from sp is "add rd, rn, #0"
                return Sf(rd) | 0x11000000u | (uint)rm.Num << 5 | (uint)rd.Num;
            }
            return Sf(rd) | 0x2A0003E0u | (uint)rm.Num << 16 | (uint)rd.Num;
        }

        ulong mask = rd.Is64 ? ulong.MaxValue : 0xFFFFFFFFul;
        ulong value = (ulong)ParseImm(ops[1]) & mask;
        int chunks = rd.Is64 ? 4 : 2;
        // movz: a single non-zero 16-bit chunk
        for (int hw = 0; hw < chunks; hw++)
        {
            if ((value & ~(0xFFFFul << (hw * 16))) == 0)
            {
                return Sf(rd) | 0x52800000u | (uint)hw << 21 | (uint)((value >> (hw * 16)) & 0xFFFF) << 5 | (uint)rd.Num;
            }
        }
        // movn: the inverse is a single chunk
        ulong inv = ~value & mask;
        for (int hw = 0; hw < chunks; hw++)
        {
            if ((inv & ~(0xFFFFul << (hw * 16))) == 0)
            {
                return Sf(rd) | 0x12800000u | (uint)hw << 21 | (uint)((inv >> (hw * 16)) & 0xFFFF) << 5 | (uint)rd.Num;
            }
        }
        // orr rd, zr, #bitmask
        if (!rd.IsSp && EncodeBitmask(value, rd.Is64 ? 64 : 32, out uint n, out uint immr, out uint imms))
        {
            return Sf(rd) | 0x320003E0u | n << 22 | immr << 16 | imms << 10 | (uint)rd.Num;
        }
        throw new AsmException("this value needs several instructions (movz + movk)");
    }

    private static uint AddSub(string mn, string[] ops)
    {
        Need(ops, 3, mn);
        bool sub = mn.StartsWith("sub", StringComparison.Ordinal);
        bool setFlags = mn.EndsWith('s');
        Reg rd = GpReg(ops[0], allowSp: !setFlags);
        Reg rn = GpReg(ops[1], allowSp: true);
        if (!IsReg(ops[2]))
        {
            long imm = ParseImm(ops[2]);
            if (imm < 0)
            {
                imm = -imm;
                sub = !sub;
            }
            uint sh = 0;
            if (ops.Length > 3)
            {
                (string kind, int amount) = Shift(ops[3]);
                if (kind != "lsl" || (amount != 0 && amount != 12)) throw new AsmException("only lsl #0/#12 allowed");
                sh = amount == 12 ? 1u : 0u;
            }
            else if (imm > 0xFFF && (imm & 0xFFF) == 0 && imm >> 12 <= 0xFFF)
            {
                imm >>= 12;
                sh = 1;
            }
            if (imm > 0xFFF)
            {
                throw new AsmException("immediate must be 0..0xfff (optionally lsl #12)");
            }
            return Sf(rd) | (sub ? 1u : 0u) << 30 | (setFlags ? 1u : 0u) << 29 | 0x11000000u | sh << 22 |
                   (uint)imm << 10 | (uint)rn.Num << 5 | (uint)rd.Num;
        }
        Reg rm = GpReg(ops[2]);
        uint shiftType = 0, amountBits = 0;
        if (ops.Length > 3)
        {
            (string kind, int amount) = Shift(ops[3]);
            shiftType = kind switch { "lsl" => 0u, "lsr" => 1u, "asr" => 2u, _ => throw new AsmException("unsupported shift " + kind) };
            amountBits = (uint)amount;
        }
        return Sf(rd) | (sub ? 1u : 0u) << 30 | (setFlags ? 1u : 0u) << 29 | 0x0B000000u | shiftType << 22 |
               (uint)rm.Num << 16 | amountBits << 10 | (uint)rn.Num << 5 | (uint)rd.Num;
    }

    private static uint Logical(string mn, string[] ops)
    {
        Need(ops, 3, mn);
        Reg rd = GpReg(ops[0], allowSp: mn != "ands");
        Reg rn = GpReg(ops[1]);
        uint opc = mn switch { "and" => 0u, "orr" => 1u, "eor" => 2u, _ => 3u };
        if (!IsReg(ops[2]))
        {
            ulong value = (ulong)ParseImm(ops[2]) & (rd.Is64 ? ulong.MaxValue : 0xFFFFFFFFul);
            if (!EncodeBitmask(value, rd.Is64 ? 64 : 32, out uint n, out uint immr, out uint imms))
            {
                throw new AsmException("immediate is not encodable as a bitmask immediate");
            }
            return Sf(rd) | opc << 29 | 0x12000000u | n << 22 | immr << 16 | imms << 10 | (uint)rn.Num << 5 | (uint)rd.Num;
        }
        Reg rm = GpReg(ops[2]);
        uint shiftType = 0, amountBits = 0;
        if (ops.Length > 3)
        {
            (string kind, int amount) = Shift(ops[3]);
            shiftType = kind switch { "lsl" => 0u, "lsr" => 1u, "asr" => 2u, "ror" => 3u, _ => throw new AsmException("unsupported shift " + kind) };
            amountBits = (uint)amount;
        }
        return Sf(rd) | opc << 29 | 0x0A000000u | shiftType << 22 | (uint)rm.Num << 16 | amountBits << 10 |
               (uint)rn.Num << 5 | (uint)rd.Num;
    }

    /// <summary>Logical (bitmask) immediate encoding; false if the value has no encoding.</summary>
    public static bool EncodeBitmask(ulong imm, int regSize, out uint n, out uint immr, out uint imms)
    {
        n = immr = imms = 0;
        if (regSize == 32)
        {
            imm &= 0xFFFFFFFF;
            imm |= imm << 32;
        }
        if (imm == 0 || imm == ulong.MaxValue)
        {
            return false;
        }
        int size = 64;
        while (size > 2)
        {
            int half = size / 2;
            ulong m = (1UL << half) - 1;
            if ((imm & m) != ((imm >> half) & m))
            {
                break;
            }
            size = half;
        }
        ulong mask = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
        ulong elt = imm & mask;
        int ones = BitOperations.PopCount(elt);
        ulong pattern = ones == 64 ? ulong.MaxValue : (1UL << ones) - 1;
        for (int r = 0; r < size; r++)
        {
            // rotate the pattern right by r within the element
            ulong rotated = r == 0 ? pattern : ((pattern >> r) | (pattern << (size - r))) & mask;
            if (rotated == elt)
            {
                immr = (uint)r;
                imms = (uint)((~(size - 1) << 1 | (ones - 1)) & 0x3F);
                n = size == 64 ? 1u : 0u;
                return true;
            }
        }
        return false;
    }

    private static uint LoadStore(string mn, string[] ops)
    {
        Need(ops, 2, mn);
        bool unscaledOnly = mn.Contains("ur", StringComparison.Ordinal);
        string baseMn = unscaledOnly ? mn.Replace("ur", "r") : mn;
        Reg rt = GpReg(ops[0]);
        (uint size, uint opc) = baseMn switch
        {
            "strb" => (0u, 0u),
            "ldrb" => (0u, 1u),
            "ldrsb" => (0u, rt.Is64 ? 2u : 3u),
            "strh" => (1u, 0u),
            "ldrh" => (1u, 1u),
            "ldrsh" => (1u, rt.Is64 ? 2u : 3u),
            "ldrsw" => (2u, 2u),
            "str" => (rt.Is64 ? 3u : 2u, 0u),
            "ldr" => (rt.Is64 ? 3u : 2u, 1u),
            _ => throw new AsmException("unsupported load/store " + mn),
        };
        if (baseMn == "ldrsw" && !rt.Is64)
        {
            throw new AsmException("ldrsw needs an x register");
        }

        // [xn], [xn, #imm], [xn, #imm]!, [xn], #imm
        string addr = ops[1];
        bool pre = addr.EndsWith('!');
        if (pre) addr = addr[..^1].Trim();
        if (!addr.StartsWith('[') || !addr.EndsWith(']'))
        {
            throw new AsmException("address must look like [xN], [xN, #off], [xN, #off]! or [xN], #off");
        }
        string[] inner = SplitOperands(addr[1..^1]);
        Reg rn = GpReg(inner[0], allowSp: true);
        long imm = inner.Length > 1 ? ParseImm(inner[1]) : 0;
        bool post = ops.Length > 2;
        if (post)
        {
            imm = ParseImm(ops[2]);
        }

        if (pre || post)
        {
            if (imm < -256 || imm > 255) throw new AsmException("pre/post-index offset must be -256..255");
            return size << 30 | 0x38000000u | opc << 22 | ((uint)imm & 0x1FF) << 12 | (pre ? 3u : 1u) << 10 |
                   (uint)rn.Num << 5 | (uint)rt.Num;
        }
        long scale = 1L << (int)size;
        if (!unscaledOnly && imm >= 0 && imm % scale == 0 && imm / scale <= 0xFFF)
        {
            return size << 30 | 0x39000000u | opc << 22 | (uint)(imm / scale) << 10 | (uint)rn.Num << 5 | (uint)rt.Num;
        }
        if (imm < -256 || imm > 255)
        {
            throw new AsmException($"offset out of range (unsigned multiple of {scale} up to 0x{0xFFF * scale:X}, or -256..255)");
        }
        // ldur/stur: unscaled 9-bit offset
        return size << 30 | 0x38000000u | opc << 22 | ((uint)imm & 0x1FF) << 12 | (uint)rn.Num << 5 | (uint)rt.Num;
    }

    private static uint Pair(string mn, string[] ops)
    {
        Need(ops, 3, mn);
        Reg rt = GpReg(ops[0]);
        Reg rt2 = GpReg(ops[1]);
        if (rt.Is64 != rt2.Is64) throw new AsmException("ldp/stp registers must have the same size");
        string addr = ops[2];
        bool pre = addr.EndsWith('!');
        if (pre) addr = addr[..^1].Trim();
        string[] inner = SplitOperands(addr.Trim('[', ']'));
        Reg rn = GpReg(inner[0], allowSp: true);
        long imm = inner.Length > 1 ? ParseImm(inner[1]) : 0;
        bool post = ops.Length > 3;
        if (post) imm = ParseImm(ops[3]);
        int scale = rt.Is64 ? 8 : 4;
        if (imm % scale != 0 || imm / scale < -64 || imm / scale > 63)
        {
            throw new AsmException($"ldp/stp offset must be a multiple of {scale} in -{64 * scale}..{63 * scale}");
        }
        uint type = post ? 1u : pre ? 3u : 2u;
        return (rt.Is64 ? 2u : 0u) << 30 | 0x28000000u | type << 23 | (mn == "ldp" ? 1u : 0u) << 22 |
               ((uint)(imm / scale) & 0x7F) << 15 | (uint)rt2.Num << 10 | (uint)rn.Num << 5 | (uint)rt.Num;
    }
}
