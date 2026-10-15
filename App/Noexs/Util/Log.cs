namespace Noexs.Util;

/// <summary>
/// Minimal file logger (%LOCALAPPDATA%\Noexs\noexs.log). The Java app printed stack traces to the
/// console; a WinExe has none, so they go here instead. Never throws.
/// </summary>
public static class Log
{
    private const long MaxSize = 2 * 1024 * 1024;
    private static readonly object Lock = new();

    public static string Directory { get; } = ResolveDirectory();

    public static string FilePath => Path.Combine(Directory, "noexs.log");

    private static string ResolveDirectory()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noexs");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return Path.GetTempPath();
        }
    }

    public static void Error(Exception e, string? context = null)
    {
        Write("ERROR", (context != null ? context + ": " : "") + e);
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
