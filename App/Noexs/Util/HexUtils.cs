using System.Globalization;
using System.Text;

namespace Noexs.Util;

public static class HexUtils
{
    public static string FormatAddress(long addr)
    {
        return Pad('0', 10, ((ulong)addr).ToString("X"));
    }

    public static string FormatSize(long size)
    {
        return "0x" + Pad('0', 10, ((ulong)size).ToString("X"));
    }

    public static string Pad(char with, int len, string str)
    {
        return str.Length >= len ? str : str.PadLeft(len, with);
    }

    public static string FormatInt(int value)
    {
        return Pad('0', 8, ((uint)value).ToString("X"));
    }

    public static string FormatTitleId(long titleId)
    {
        return Pad('0', 16, ((ulong)titleId).ToString("X"));
    }

    public static string FormatAccess(int access)
    {
        var sb = new StringBuilder(3);
        sb.Append((access & 1) != 0 ? 'R' : '-');
        sb.Append((access & 2) != 0 ? 'W' : '-');
        sb.Append((access & 4) != 0 ? 'X' : '-');
        return sb.ToString();
    }

    /// <summary>Hex number as typed or pasted: "4741FA0", "0x4741FA0", "47 41 FA0" or "4741FA0h".</summary>
    public static bool TryParseHex(string? s, out long value)
    {
        string t = new string((s ?? "").Where(c => !char.IsWhiteSpace(c) && c != '_').ToArray());
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }
        if (t.EndsWith('h') || t.EndsWith('H'))
        {
            t = t[..^1];
        }
        bool ok = ulong.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong u);
        value = (long)u;
        return ok;
    }

    /// <summary>Math.abs(long) with Java overflow semantics (no exception for long.MinValue).</summary>
    public static long Abs(long v)
    {
        return v < 0 ? unchecked(-v) : v;
    }
}

public static class NetUtils
{
    private static readonly string[] BYTE_SUFFEXES = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatSize(double size)
    {
        int i = 0;
        while (i < BYTE_SUFFEXES.Length - 1 && size > 1024)
        {
            size /= 1024;
            i++;
        }
        size *= 100;
        size = Math.Floor(size);
        size /= 100;
        return JavaDouble(size) + BYTE_SUFFEXES[i];
    }

    // Java's Double.toString always shows a fractional part ("1.0").
    private static string JavaDouble(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') ? s : s + ".0";
    }
}

public static class TimeUtils
{
    private const long SECOND_IN_MS = 1000;
    private const long MINUTE_IN_MS = SECOND_IN_MS * 60;
    private const long HOUR_IN_MS = MINUTE_IN_MS * 60;

    public static string FormatTime(long timeInMs)
    {
        var sb = new StringBuilder();
        long tmp = timeInMs / HOUR_IN_MS;
        if (tmp > 0)
        {
            if (tmp < 10)
            {
                sb.Append('0');
            }
            sb.Append(tmp).Append(':');
            timeInMs -= HOUR_IN_MS * tmp;
        }
        tmp = timeInMs / MINUTE_IN_MS;
        if (tmp < 10)
        {
            sb.Append('0');
        }
        sb.Append(tmp).Append(':');
        timeInMs -= MINUTE_IN_MS * tmp;

        tmp = timeInMs / SECOND_IN_MS;
        if (tmp < 10)
        {
            sb.Append('0');
        }
        sb.Append(tmp);
        return sb.ToString();
    }
}

public sealed class Rolling
{
    private readonly int size;
    private double total;
    private int index;
    private readonly double[] samples;

    public Rolling(int size)
    {
        this.size = size;
        samples = new double[size];
    }

    public void Add(double x)
    {
        total -= samples[index];
        samples[index] = x;
        total += x;
        if (++index == size) index = 0; // cheaper than modulus
    }

    public double Average => total / size;
}
