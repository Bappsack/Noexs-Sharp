using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Noexs.UI.Views;
using Noexs.Util;

namespace Noexs.UI;

public partial class NoexsApplication : Application
{
    public const int VERSION_MAJOR = 2;
    public const int VERSION_MINOR = 0;
    public const int VERSION_PATCH = 1;

    public const string APP_NAME = "Noexs";
    public const string APP_VERSION = "2.0.2";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // JavaFX only logs exceptions thrown from event handlers; never let one take the app down.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Error(e.Exception, "UI");
            MainWindow.Instance?.OnUnhandledException(e.Exception);
        };

        ApplyTheme(Settings.GetDarkMode());
    }

    /// <summary>null follows the operating system.</summary>
    public static void ApplyTheme(bool? dark)
    {
        if (Current != null)
        {
            Current.RequestedThemeVariant = dark switch
            {
                true => ThemeVariant.Dark,
                false => ThemeVariant.Light,
                null => ThemeVariant.Default,
            };
        }
    }

    public static bool IsDark => Current?.ActualThemeVariant == ThemeVariant.Dark;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
