namespace Noexs.Util;

/// <summary>
/// Decoders for the memory chunks the sysmodule sends. Both check every length against the input
/// and the output, so corrupt data is reported (false) instead of overrunning a buffer.
/// </summary>
public static class ChunkCodecs
{
    /// <summary>RLE: (value, count) byte pairs. True if they fill dst exactly.</summary>
    public static bool DecodeRle(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if ((src.Length & 1) != 0)
        {
            return false;
        }
        int pos = 0;
        for (int i = 0; i < src.Length; i += 2)
        {
            int count = src[i + 1];
            if (count > dst.Length - pos)
            {
                return false;
            }
            dst.Slice(pos, count).Fill(src[i]);
            pos += count;
        }
        return pos == dst.Length;
    }

    /// <summary>
    /// One LZ4 block (https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md).
    /// True if it decodes to exactly dst.Length bytes.
    /// </summary>
    public static bool DecodeLz4(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int ip = 0;
        int op = 0;
        while (true)
        {
            if (ip >= src.Length)
            {
                return false;
            }
            int token = src[ip++];

            int literals = token >> 4;
            if (literals == 15 && !ReadLength(src, ref ip, ref literals))
            {
                return false;
            }
            if (literals > src.Length - ip || literals > dst.Length - op)
            {
                return false;
            }
            src.Slice(ip, literals).CopyTo(dst.Slice(op));
            ip += literals;
            op += literals;
            if (ip == src.Length)
            {
                return op == dst.Length; // the last sequence has only literals
            }

            if (src.Length - ip < 2)
            {
                return false;
            }
            int offset = src[ip] | (src[ip + 1] << 8);
            ip += 2;
            if (offset == 0 || offset > op)
            {
                return false;
            }
            int matchLength = token & 15;
            if (matchLength == 15 && !ReadLength(src, ref ip, ref matchLength))
            {
                return false;
            }
            matchLength += 4;
            if (matchLength > dst.Length - op)
            {
                return false;
            }
            CopyMatch(dst, op, offset, matchLength);
            op += matchLength;
        }
    }

    private static bool ReadLength(ReadOnlySpan<byte> src, ref int ip, ref int length)
    {
        int b;
        do
        {
            if (ip >= src.Length)
            {
                return false;
            }
            b = src[ip++];
            length += b;
        } while (b == 255);
        return true;
    }

    /// <summary>Copies length bytes from op - offset to op; the regions overlap when offset &lt; length.</summary>
    private static void CopyMatch(Span<byte> dst, int op, int offset, int length)
    {
        if (offset == 1)
        {
            dst.Slice(op, length).Fill(dst[op - 1]);
            return;
        }
        // the pattern of `offset` bytes repeats: copy it in pieces that never overlap their source,
        // doubling the piece size as the copied part grows
        int start = op - offset;
        int done = 0;
        while (done < length)
        {
            int piece = Math.Min(offset + done, length - done);
            dst.Slice(start, piece).CopyTo(dst.Slice(op + done));
            done += piece;
        }
    }
}
