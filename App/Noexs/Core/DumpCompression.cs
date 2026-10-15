namespace Noexs.Core;

/// <summary>How the sysmodule compresses memory it sends (transfer option, command 0x17).</summary>
public enum DumpCompression
{
    Off,
    Rle,
    Lz4,
}

public static class DumpCompressionExtensions
{
    public static string Label(this DumpCompression c) => c switch
    {
        DumpCompression.Rle => "RLE",
        DumpCompression.Lz4 => "LZ4",
        _ => "off",
    };

    /// <summary>Name in settings.json.</summary>
    public static string Key(this DumpCompression c) => c.Label().ToLowerInvariant();

    public static DumpCompression? Parse(string? key) => key?.ToLowerInvariant() switch
    {
        "off" => DumpCompression.Off,
        "rle" => DumpCompression.Rle,
        "lz4" => DumpCompression.Lz4,
        _ => null,
    };
}
