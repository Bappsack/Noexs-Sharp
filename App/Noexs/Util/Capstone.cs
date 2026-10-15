using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Noexs.Util;

/// <summary>
/// Minimal binding to the Capstone disassembler (native library from the Gee.External.Capstone
/// package, embedded into the single-file build). Only ARM64 and only mnemonic/operand text are
/// needed, so the instruction record layout is calibrated at runtime instead of being hard-coded
/// for one Capstone version.
/// </summary>
public static partial class Capstone
{
    private const string Lib = "capstone";
    private const int CS_ARCH_ARM64 = 1;
    private const int CS_MODE_LITTLE_ENDIAN = 0;
    private const int CS_OPT_SKIPDATA = 5;
    private const nuint CS_OPT_ON = 3;

    [LibraryImport(Lib, EntryPoint = "cs_open")]
    private static partial int Open(int arch, int mode, out nuint handle);

    [LibraryImport(Lib, EntryPoint = "cs_option")]
    private static partial int Option(nuint handle, int type, nuint value);

    [LibraryImport(Lib, EntryPoint = "cs_disasm")]
    private static unsafe partial nuint Disasm(nuint handle, byte* code, nuint size, ulong address, nuint count, out IntPtr insn);

    [LibraryImport(Lib, EntryPoint = "cs_free")]
    private static partial void Free(IntPtr insn, nuint count);

    [LibraryImport(Lib, EntryPoint = "cs_close")]
    private static partial int Close(ref nuint handle);

    private sealed record Layout(int BytesOffset, int MnemonicOffset, int OpStrOffset, int Stride);

    private static readonly object Lock = new();
    private static nuint handle;
    private static Layout? layout;

    /// <summary>True when the native library could be loaded and initialised.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                Ensure();
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public readonly record struct Instruction(long Address, byte[] Bytes, string Mnemonic, string Operands)
    {
        public string Text => Operands.Length > 0 ? Mnemonic + " " + Operands : Mnemonic;
    }

    private static void Ensure()
    {
        lock (Lock)
        {
            if (layout != null)
            {
                return;
            }
            int rc = Open(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN, out handle);
            if (rc != 0)
            {
                throw new InvalidOperationException("cs_open failed (" + rc + ")");
            }
            // keep going over data that isn't a valid instruction (shown as .byte)
            Option(handle, CS_OPT_SKIPDATA, CS_OPT_ON);
            layout = Calibrate();
        }
    }

    /// <summary>Finds field offsets and the record size by disassembling a known "nop; ret".</summary>
    private static unsafe Layout Calibrate()
    {
        byte[] code = [0x1F, 0x20, 0x03, 0xD5, 0xC0, 0x03, 0x5F, 0xD6];
        IntPtr insn;
        nuint n;
        fixed (byte* p = code)
        {
            n = Disasm(handle, p, (nuint)code.Length, 0x1000, 0, out insn);
        }
        if (n != 2)
        {
            throw new InvalidOperationException("capstone calibration failed");
        }
        try
        {
            // two records are at least 2 * ~240 bytes; stay well inside that allocation
            var mem = new ReadOnlySpan<byte>((void*)insn, 480);
            int nop = mem.IndexOf("nop\0"u8);
            int ret = mem.IndexOf("ret\0"u8);
            if (nop < 18 || ret <= nop)
            {
                throw new InvalidOperationException("capstone calibration failed (layout)");
            }
            const int bytesOffset = 18; // id(4) pad(4) address(8) size(2)
            return new Layout(bytesOffset, nop, nop + 32, ret - nop);
        }
        finally
        {
            Free(insn, n);
        }
    }

    /// <summary>Disassembles ARM64 code located at <paramref name="address"/>.</summary>
    public static unsafe List<Instruction> Disassemble(byte[] code, long address)
    {
        Ensure();
        var result = new List<Instruction>();
        lock (Lock)
        {
            Layout l = layout!;
            IntPtr insn;
            nuint n;
            fixed (byte* p = code)
            {
                n = Disasm(handle, p, (nuint)code.Length, (ulong)address, 0, out insn);
            }
            try
            {
                for (nuint i = 0; i < n; i++)
                {
                    byte* rec = (byte*)insn + (long)i * l.Stride;
                    long addr = (long)BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(rec + 8, 8));
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(rec + 16, 2));
                    byte[] bytes = new ReadOnlySpan<byte>(rec + l.BytesOffset, Math.Min(size, l.MnemonicOffset - l.BytesOffset)).ToArray();
                    result.Add(new Instruction(addr, bytes, CString(rec + l.MnemonicOffset, 32), CString(rec + l.OpStrOffset, 160)));
                }
            }
            finally
            {
                if (n > 0)
                {
                    Free(insn, n);
                }
            }
        }
        return result;
    }

    private static unsafe string CString(byte* p, int max)
    {
        var span = new ReadOnlySpan<byte>(p, max);
        int len = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(len < 0 ? span : span[..len]);
    }
}
