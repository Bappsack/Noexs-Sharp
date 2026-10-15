using System.Globalization;
using Noexs.UI.Models;
using Noexs.Util;

namespace Noexs.UI.Services;

/// <summary>A dump file created by the Search tab, offered to Pointer Search and Dump Parser.</summary>
public sealed record DumpEntry(string Path, string Label, DateTime Created, bool Full, long? SingleResult);

/// <summary>Dumps created by searches in this session, newest first, plus older ones in tmp\.</summary>
public static class DumpHistory
{
    private static readonly List<DumpEntry> Session = new();
    private static int searchNumber;

    /// <summary>Raised on the UI thread after a search produced a new dump.</summary>
    public static event Action<DumpEntry>? Added;

    public static void Add(SearchResult result, bool refined, string condition)
    {
        searchNumber++;
        long size = 0;
        try
        {
            size = new FileInfo(result.Location).Length;
        }
        catch (IOException)
        {
        }
        int count = result.Size;
        string label = $"#{searchNumber} {condition} · {result.DataType} · {count} result{(count == 1 ? "" : "s")} · " +
                       $"{NetUtils.FormatSize(size)} · {DateTime.Now:HH:mm:ss}" + (refined ? " (refine: partial dump)" : "");
        var entry = new DumpEntry(result.Location, label, DateTime.Now, !refined,
            count == 1 ? result.Addresses[0] : null);
        Session.Insert(0, entry);
        Added?.Invoke(entry);
    }

    /// <summary>
    /// This session's search dumps (newest first), followed by up to <paramref name="olderMax"/>
    /// older .dmp files from the tmp folder.
    /// </summary>
    public static List<DumpEntry> List(int olderMax = 10)
    {
        var list = Session.Where(e => File.Exists(e.Path)).ToList();
        var known = new HashSet<string>(list.Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        try
        {
            var older = new DirectoryInfo(NoexesFiles.GetTempDir())
                .EnumerateFiles("*.dmp")
                .Where(f => !known.Contains(f.FullName) && f.Length > 0x40)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(olderMax);
            foreach (FileInfo f in older)
            {
                list.Add(new DumpEntry(f.FullName,
                    $"older: {f.Name} · {NetUtils.FormatSize(f.Length)} · {f.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)}",
                    f.LastWriteTime, true, null));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error(e, "listing tmp dumps");
        }
        return list;
    }
}
