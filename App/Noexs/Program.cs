using System.Runtime.InteropServices;
using Avalonia;
using Noexs.UI;
using Noexs.Util;

namespace Noexs;

internal static partial class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), "unhandled (fatal)");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "unobserved task");
            e.SetObserved();
        };

        try
        {
            NativeBootstrap.Initialize();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e)
        {
            Log.Error(e, "fatal");
            ShowFatal(e);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<NoexsApplication>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static void ShowFatal(Exception e)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(e);
            return;
        }
        try
        {
            MessageBoxW(IntPtr.Zero,
                "Noexs failed to start:\n\n" + e.Message + "\n\nDetails were written to:\n" + Log.FilePath,
                NoexsApplication.APP_NAME, 0x10 /* MB_ICONERROR */);
        }
        catch
        {
            // nothing left to report with
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
