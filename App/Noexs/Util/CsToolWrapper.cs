using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Noexs.Util;

/// <summary>
/// ARM64 disassembly/assembly for the Disassembler tab. Uses the built-in Capstone binding and
/// <see cref="Arm64Assembler"/>; capstone's cstool / keystone's kstool on PATH are only fallbacks.
/// </summary>
public static partial class CsToolWrapper
{
    public static List<Insn> Disassemble(byte[] code, long addr)
    {
        if (Capstone.Available)
        {
            return Capstone.Disassemble(code, addr)
                .Select(i => new Insn(i.Address, Convert.ToHexString(i.Bytes).Chunk(2).Aggregate("", (s, c) => s + new string(c) + " ").Trim(),
                    i.Text))
                .ToList();
        }
        return Disasm(code, addr);
    }

    /// <summary>Built-in assembler first; kstool only for instructions it doesn't support.</summary>
    public static byte[]? AssembleInstruction(string str, long addr)
    {
        try
        {
            return Arm64Assembler.Assemble(str, addr);
        }
        catch (Arm64Assembler.AsmException e) when (e.Message.StartsWith("unsupported instruction", StringComparison.Ordinal))
        {
            // no kstool (or it failed too): report what the built-in assembler supports
            byte[]? viaKstool = TryKstool(str, addr);
            if (viaKstool == null)
            {
                throw;
            }
            return viaKstool;
        }
    }

    private static byte[]? TryKstool(string str, long addr)
    {
        try
        {
            return Assemble(str, addr);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    // ECMAScript mode makes \W ASCII-only, matching java.util.regex.
    [GeneratedRegex(@"^\W*([0-9a-fA-F]*)\W{2}([0-9a-fA-F\W]*)\W{2}(.*)$", RegexOptions.ECMAScript)]
    private static partial Regex Pattern();

    private static Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(args[0])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }
        return Process.Start(psi) ?? throw new IOException("Unable to start " + args[0]);
    }

    private static void Destroy(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        p.Dispose();
    }

    public static byte[]? Assemble(string str, long addr)
    {
        Process p = Start("kstool", "arm64", str, HexUtils.FormatAddress(addr));
        try
        {
            string? line = p.StandardOutput.ReadLine();
            if (line == null || line.StartsWith("ERROR", StringComparison.Ordinal))
            {
                return null;
            }
            int idx1 = line.LastIndexOf('[');
            int idx2 = line.LastIndexOf(']');
            string split = line.Substring(idx1 + 1, idx2 - idx1 - 1);
            var res = new List<byte>();
            foreach (string data in split.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                res.Add((byte)int.Parse(data, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }
            return res.ToArray();
        }
        finally
        {
            Destroy(p);
        }
    }

    public static List<Insn> Disasm(byte[] code, long addr)
    {
        Process p = Start("cstool", "arm64", Convert.ToHexString(code), HexUtils.FormatAddress(addr));
        var insns = new List<Insn>();
        try
        {
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                Match m = Pattern().Match(line);
                if (m.Success && HexUtils.TryParseHex(m.Groups[1].Value, out long address))
                {
                    string mem = m.Groups[2].Value.Trim();
                    string op = m.Groups[3].Value.Replace('\t', ' ').Trim();
                    insns.Add(new Insn(address, mem, op));
                }
                //else: we had an error parsing
            }
        }
        finally
        {
            Destroy(p);
        }
        return insns;
    }

    public sealed class Insn
    {
        internal Insn(long addr, string mnemonic, string opstr)
        {
            Addr = addr;
            Mnemonic = mnemonic;
            OpStr = opstr;
        }

        public long Addr { get; }

        public string Mnemonic { get; }

        public string OpStr { get; }

        public override string ToString()
        {
            return $"{HexUtils.FormatAddress(Addr)}: {HexUtils.Pad('0', 8, Mnemonic).ToUpperInvariant()} {OpStr}";
        }
    }
}
