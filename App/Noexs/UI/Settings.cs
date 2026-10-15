using Noexs.Core;
using Noexs.Util;
using System.Text.Json;
using System.Text.Json.Serialization;
using Noexs.IO.Net;
using Noexs.UI.Models;

namespace Noexs.UI;

/// <summary>
/// Persistent user settings (java.util.prefs in the original), stored as JSON in the
/// per-user application data folder.
/// </summary>
public static class Settings
{
    private static readonly object Lock = new();
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Noexs", "settings.json");

    private static readonly SettingsData Prefs = Load();

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), NoexsJsonContext.Default.SettingsData)
                       ?? new SettingsData();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error(e);
        }
        return new SettingsData();
    }

    private static void Save()
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Prefs, NoexsJsonContext.Default.SettingsData));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error(e);
            }
        }
    }

    public static string GetChooserDir()
    {
        string? f = Prefs.ChooserFile;
        if (string.IsNullOrEmpty(f) || !Directory.Exists(f))
        {
            f = Directory.GetCurrentDirectory();
        }
        return f;
    }

    public static void SetChooserFile(string f)
    {
        Prefs.ChooserFile = Path.GetFullPath(f);
        Save();
    }

    public static string GetConnectionHost()
    {
        return Prefs.Host ?? NetworkConstants.DEFAULT_HOST;
    }

    public static void SetConnectionHost(string host)
    {
        Prefs.Host = host;
        Save();
    }

    private const int MaxHostHistory = 15;

    /// <summary>Addresses that were connected to successfully, most recent first.</summary>
    public static List<string> GetHostHistory()
    {
        if (Prefs.HostHistory is { } list)
        {
            return list.ToList();
        }
        // first start with this version: the last used address seeds the history
        string? host = Prefs.Host?.Trim();
        return string.IsNullOrEmpty(host) || host == NetworkConstants.DEFAULT_HOST ? new List<string>() : new List<string> { host };
    }

    public static void AddHostHistory(string host)
    {
        host = host.Trim();
        if (host.Length == 0)
        {
            return;
        }
        List<string> list = GetHostHistory();
        list.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, host);
        Prefs.HostHistory = list.Take(MaxHostHistory).ToList();
        Save();
    }

    public static void RemoveHostHistory(string host)
    {
        List<string> list = GetHostHistory();
        list.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        Prefs.HostHistory = list;
        Save();
    }

    // Pointer searcher options (defaults from PointerSearcher by Takumi4685)
    public static long GetPointerOffset()
    {
        return Prefs.PointerOffsetRange ?? 0x800;
    }

    public static void SetPointerOffset(long value)
    {
        Prefs.PointerOffsetRange = value;
        Save();
    }

    public static int GetPointerDepth()
    {
        return Prefs.PointerMaxDepth ?? 4;
    }

    public static void SetPointerDepth(int depth)
    {
        Prefs.PointerMaxDepth = depth;
        Save();
    }

    public static int GetPointerOffsetNum()
    {
        return Prefs.PointerOffsetNum ?? 1;
    }

    public static void SetPointerOffsetNum(int num)
    {
        Prefs.PointerOffsetNum = num;
        Save();
    }

    public static bool ShouldSwapEndian()
    {
        return Prefs.SwapEndian ?? false;
    }

    public static void SetSwapEndian(bool swapEndian)
    {
        Prefs.SwapEndian = swapEndian;
        Save();
    }

    /// <summary>How the sysmodule compresses dumps (less data on the link, more work on the Switch).</summary>
    public static DumpCompression GetDumpCompression()
    {
        return DumpCompressionExtensions.Parse(Prefs.DumpCompression) ?? DumpCompression.Lz4;
    }

    public static void SetDumpCompression(DumpCompression compression)
    {
        Prefs.DumpCompression = compression.Key();
        Save();
    }

    /// <summary>The sysmodule sends one buffer while it reads and compresses the next.</summary>
    public static bool GetDoubleBuffering()
    {
        return Prefs.DoubleBuffering ?? true;
    }

    public static void SetDoubleBuffering(bool on)
    {
        Prefs.DoubleBuffering = on;
        Save();
    }

    /// <summary>null = follow the operating system theme.</summary>
    public static bool? GetDarkMode()
    {
        return Prefs.DarkMode;
    }

    public static void SetDarkMode(bool dark)
    {
        Prefs.DarkMode = dark;
        Save();
    }
}

public sealed class SettingsData
{
    [JsonPropertyName("HOST")] public string? Host { get; set; }
    [JsonPropertyName("HOST_HISTORY")] public List<string>? HostHistory { get; set; }
    [JsonPropertyName("PS_MAX_DEPTH")] public int? PointerMaxDepth { get; set; }
    [JsonPropertyName("PS_OFFSET_RANGE")] public long? PointerOffsetRange { get; set; }
    [JsonPropertyName("PS_OFFSET_NUM")] public int? PointerOffsetNum { get; set; }
    [JsonPropertyName("MEM_ENDIAN")] public bool? SwapEndian { get; set; }
    [JsonPropertyName("CHOOSER_FILE")] public string? ChooserFile { get; set; }
    [JsonPropertyName("DARK_MODE")] public bool? DarkMode { get; set; }
    [JsonPropertyName("DUMP_CODEC")] public string? DumpCompression { get; set; }
    [JsonPropertyName("DOUBLE_BUFFERING")] public bool? DoubleBuffering { get; set; }
}

/// <summary>Gson-compatible watchlist entry (field names and enum names match the Java files).</summary>
public sealed class SerializedWatchlistItem
{
    [JsonPropertyName("update")] public bool Update { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
    [JsonPropertyName("addr")] public string? Addr { get; set; }
    [JsonPropertyName("desc")] public string? Desc { get; set; }
    [JsonPropertyName("type")] public DataType? Type { get; set; }
    [JsonPropertyName("value")] public long Value { get; set; }
}

public sealed class DataTypeJsonConverter : JsonConverter<DataType>
{
    public override DataType? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType == JsonTokenType.Null ? null : DataType.FromName(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, DataType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Name);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(DataTypeJsonConverter)])]
[JsonSerializable(typeof(SettingsData))]
[JsonSerializable(typeof(List<SerializedWatchlistItem>))]
internal partial class NoexsJsonContext : JsonSerializerContext;

public static class NoexesFiles
{
    // tmp\ next to the executable like the Java app; falls back to the user profile when that
    // folder isn't writable (e.g. the exe lives in Program Files).
    private static readonly string Tmp = ResolveTmp();

    private static string ResolveTmp()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "tmp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noexs", "tmp"),
            Path.Combine(Path.GetTempPath(), "Noexs", "tmp"),
        ];
        foreach (string dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error(e, "tmp folder " + dir + " not usable");
            }
        }
        return Path.GetTempPath();
    }

    public static string CreateTempFile(string ext)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (true)
        {
            string res = Path.Combine(Tmp, now + "." + ext);
            try
            {
                using (new FileStream(res, FileMode.CreateNew))
                {
                }
                return res;
            }
            catch (IOException) when (File.Exists(res))
            {
                now++;
            }
        }
    }

    public static string CreateTempDir()
    {
        string res = Path.Combine(Tmp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        Directory.CreateDirectory(res);
        return res;
    }

    public static string GetTempDir()
    {
        return Tmp;
    }
}
