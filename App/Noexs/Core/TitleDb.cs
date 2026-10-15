using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Noexs.Util;

namespace Noexs.Core;

/// <summary>
/// Title ID → name lookup. Game names reported by the console itself (noexs 2.0.0+, exact and in
/// the console's language) are remembered in %LOCALAPPDATA%\Noexs\titles\console-names.txt and
/// win; otherwise switchbrew's "Title list" and "Title list/Games" pages are used (a snapshot is
/// embedded; <see cref="UpdateFromSwitchbrewAsync"/> refreshes it).
/// </summary>
public static partial class TitleDb
{
    private const string GamesUrl = "https://switchbrew.org/w/index.php?title=Title_list/Games&action=raw";
    private const string TitlesUrl = "https://switchbrew.org/w/index.php?title=Title_list&action=raw";

    private static readonly string CacheDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noexs", "titles");

    private static readonly object Lock = new();
    private static Dictionary<ulong, string>? names;
    private static Dictionary<ulong, string>? consoleNames;

    private static string ConsoleNamesFile => System.IO.Path.Combine(CacheDir, "console-names.txt");

    private static Dictionary<ulong, string> ConsoleNames
    {
        get
        {
            lock (Lock)
            {
                return consoleNames ??= LoadConsoleNames();
            }
        }
    }

    /// <summary>Program IDs of installed games (system titles are 0100000000000xxx).</summary>
    public static bool IsApplicationId(long titleId)
    {
        ulong id = (ulong)titleId;
        return id >> 56 == 0x01 && id >= 0x0100000000010000UL;
    }

    public static bool HasConsoleName(long titleId)
    {
        return ConsoleNames.ContainsKey((ulong)titleId);
    }

    /// <summary>Remembers a name the console reported (kept across sessions, also for offline dumps).</summary>
    public static void RememberConsoleName(long titleId, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            return;
        }
        lock (Lock)
        {
            Dictionary<ulong, string> db = consoleNames ??= LoadConsoleNames();
            if (db.TryGetValue((ulong)titleId, out string? old) && old == name)
            {
                return;
            }
            db[(ulong)titleId] = name;
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(ConsoleNamesFile,
                    db.OrderBy(kv => kv.Key).Select(kv => kv.Key.ToString("X16") + "\t" + kv.Value.Replace('\t', ' ')),
                    Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log.Error(e, "saving console game names");
            }
        }
    }

    private static Dictionary<ulong, string> LoadConsoleNames()
    {
        var db = new Dictionary<ulong, string>();
        try
        {
            if (File.Exists(ConsoleNamesFile))
            {
                foreach (string line in File.ReadAllLines(ConsoleNamesFile, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab == 16 && ulong.TryParse(line.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong id))
                    {
                        db[id] = line[(tab + 1)..];
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "loading console game names");
        }
        return db;
    }

    public static int Count => Names.Count;

    private static Dictionary<ulong, string> Names
    {
        get
        {
            lock (Lock)
            {
                return names ??= Load();
            }
        }
    }

    /// <summary>The name for a program ID (game updates and add-on IDs map to their base game), or null.</summary>
    public static string? Lookup(long titleId)
    {
        if (titleId == 0 || titleId == -1)
        {
            return null;
        }
        ulong id = (ulong)titleId;
        // updates (+0x800) and further programs of a multi-program game (+index) belong to the game
        // whose ID has the low 12 bits clear. (Subtracting 0x1000 for add-ons, as before, turned a
        // game missing from the list into the name of a neighbouring, unrelated title.)
        ulong baseId = id & ~0xFFFUL;
        foreach (Dictionary<ulong, string> db in new[] { ConsoleNames, Names })
        {
            if (db.TryGetValue(id, out string? name) || (baseId != id && db.TryGetValue(baseId, out name)))
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>"Name (pid)" with the hex title ID as fallback, e.g. "Pokémon Legends: Z-A (137)".</summary>
    public static string Describe(long titleId, long pid)
    {
        string? name = Lookup(titleId);
        string who = name ?? (titleId == 0 || titleId == -1 ? "unknown" : HexUtils.FormatTitleId(titleId));
        return who + " (" + pid + ")";
    }

    private static Dictionary<ulong, string> Load()
    {
        var db = new Dictionary<ulong, string>();
        foreach (string page in new[] { "titles", "games" })
        {
            try
            {
                Parse(ReadPage(page), db);
            }
            catch (Exception e)
            {
                Log.Error(e, "loading title list " + page);
            }
        }
        return db;
    }

    private static string ReadPage(string page)
    {
        string cached = System.IO.Path.Combine(CacheDir, page + ".wiki");
        if (File.Exists(cached))
        {
            return File.ReadAllText(cached, Encoding.UTF8);
        }
        using Stream? s = typeof(TitleDb).Assembly.GetManifestResourceStream("titles/" + page + ".wiki.gz");
        if (s == null)
        {
            return "";
        }
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Downloads both pages from switchbrew, checks them and replaces the cached copies.</summary>
    public static async Task<int> UpdateFromSwitchbrewAsync(CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noexs/1.1 (+title list update)");
        var pages = new Dictionary<string, string>
        {
            ["titles"] = await http.GetStringAsync(TitlesUrl, token).ConfigureAwait(false),
            ["games"] = await http.GetStringAsync(GamesUrl, token).ConfigureAwait(false),
        };
        var db = new Dictionary<ulong, string>();
        foreach (string text in pages.Values)
        {
            Parse(text, db);
        }
        if (db.Count < 100)
        {
            throw new InvalidDataException("switchbrew returned an unexpected page (only " + db.Count + " titles found)");
        }
        Directory.CreateDirectory(CacheDir);
        foreach (var (page, text) in pages)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(CacheDir, page + ".wiki"), text, Encoding.UTF8, token)
                .ConfigureAwait(false);
        }
        lock (Lock)
        {
            names = db;
        }
        return db.Count;
    }

    // ---- wikitext parsing ----

    [GeneratedRegex(@"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]")]
    private static partial Regex WikiLink();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\{\{[^}]*\}\}")]
    private static partial Regex Template();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Reads every wikitable whose header has "ProgramId" and "Description" columns.</summary>
    public static void Parse(string wikitext, Dictionary<ulong, string> into)
    {
        int idCol = -1, nameCol = -1;
        foreach (string raw in wikitext.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("{|", StringComparison.Ordinal))
            {
                idCol = nameCol = -1;
            }
            else if (line.StartsWith('!'))
            {
                string[] headers = line.TrimStart('!').Split(new[] { "!!", "||" }, StringSplitOptions.None)
                    .Select(h => h.Trim()).ToArray();
                idCol = Array.FindIndex(headers, h => h.StartsWith("ProgramId", StringComparison.OrdinalIgnoreCase)
                                                      || h.StartsWith("Title ID", StringComparison.OrdinalIgnoreCase)
                                                      || h.StartsWith("TitleId", StringComparison.OrdinalIgnoreCase));
                nameCol = Array.FindIndex(headers, h => h.StartsWith("Description", StringComparison.OrdinalIgnoreCase)
                                                        || h.StartsWith("Name", StringComparison.OrdinalIgnoreCase));
            }
            else if (line.StartsWith("| ", StringComparison.Ordinal) && idCol >= 0 && nameCol >= 0)
            {
                string[] cells = line.Substring(1).Split("||");
                if (cells.Length <= Math.Max(idCol, nameCol))
                {
                    continue;
                }
                if (!ulong.TryParse(cells[idCol].Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong id))
                {
                    continue;
                }
                string name = Clean(cells[nameCol]);
                if (name.Length > 0)
                {
                    into.TryAdd(id, name);
                }
            }
        }
    }

    private static string Clean(string cell)
    {
        string s = WikiLink().Replace(cell, "$1");
        s = Template().Replace(s, "");
        s = HtmlTag().Replace(s, " ");
        s = s.Replace("'''", "").Replace("''", "");
        s = WebUtility.HtmlDecode(s);
        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        return Spaces().Replace(s, " ").Trim();
    }
}
