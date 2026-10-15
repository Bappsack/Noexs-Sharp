using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Noexs.Util;

namespace Noexs;

/// <summary>
/// Makes the native libraries that were zipped into the executable at publish time (see the
/// NoexsEmbedNativeLibraries target in Noexs.csproj) available before anything touches them.
/// Without the embedded resource (normal builds / dotnet run) this does nothing and the libraries
/// are loaded from next to the executable as usual.
/// </summary>
internal static partial class NativeBootstrap
{
    private static string? nativeDir;

    public static void Initialize()
    {
        byte[] zip;
        using (Stream? s = typeof(NativeBootstrap).Assembly.GetManifestResourceStream("natives.zip"))
        {
            if (s == null)
            {
                return;
            }
            zip = new byte[s.Length];
            s.ReadExactly(zip);
        }

        string hash = Convert.ToHexString(SHA256.HashData(zip))[..16];
        string dir = Extract(zip, hash, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Noexs", "native"))
                     ?? Extract(zip, hash, Path.Combine(Path.GetTempPath(), "Noexs", "native"))
                     ?? throw new IOException("Unable to extract the embedded native libraries");
        nativeDir = dir;

        if (OperatingSystem.IsWindows())
        {
            SetDllDirectoryW(dir);
        }

        // Preload everything by full path; later loads by plain name bind to these modules.
        foreach (string lib in Directory.GetFiles(dir).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                                                 || f.EndsWith(".so", StringComparison.Ordinal)
                                                                 || f.EndsWith(".dylib", StringComparison.Ordinal)))
        {
            try
            {
                NativeLibrary.Load(lib);
            }
            catch (Exception e)
            {
                Log.Error(e, "preloading " + lib);
            }
        }

        foreach (Assembly asm in new[]
                 {
                     typeof(SkiaSharp.SKImageInfo).Assembly,
                     typeof(HarfBuzzSharp.Blob).Assembly,
                     typeof(NativeBootstrap).Assembly,
                 })
        {
            try
            {
                NativeLibrary.SetDllImportResolver(asm, Resolve);
            }
            catch (InvalidOperationException)
            {
                // a resolver is already registered
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (nativeDir != null)
        {
            foreach (string candidate in Candidates(libraryName))
            {
                string path = Path.Combine(nativeDir, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    return handle;
                }
            }
        }
        return IntPtr.Zero; // fall back to the default probing
    }

    private static IEnumerable<string> Candidates(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows())
        {
            yield return name + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return name + ".dylib";
            yield return "lib" + name + ".dylib";
        }
        else
        {
            yield return name + ".so";
            yield return "lib" + name + ".so";
        }
    }

    private static string? Extract(byte[] zip, string hash, string root)
    {
        try
        {
            string dir = Path.Combine(root, hash);
            string marker = Path.Combine(dir, ".complete");
            if (File.Exists(marker))
            {
                return dir;
            }

            Directory.CreateDirectory(root);
            string tmp = Path.Combine(root, hash + ".tmp-" + Environment.ProcessId);
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, true);
            }
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(tmp);
            }
            File.WriteAllText(Path.Combine(tmp, ".complete"), hash);

            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true); // incomplete leftover
                }
                Directory.Move(tmp, dir);
            }
            catch (IOException) when (File.Exists(marker))
            {
                // another instance finished first
                Directory.Delete(tmp, true);
            }

            CleanupOldVersions(root, hash);
            return dir;
        }
        catch (Exception e)
        {
            Log.Error(e, "extracting native libraries to " + root);
            return null;
        }
    }

    private static void CleanupOldVersions(string root, string keep)
    {
        foreach (string old in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(old) == keep)
            {
                continue;
            }
            try
            {
                Directory.Delete(old, true);
            }
            catch
            {
                // still in use by an older running instance
            }
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDllDirectoryW(string path);
}
