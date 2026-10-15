using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noexs.Core;
using Noexs.Util;
using Noexs.Misc;
using Noexs.UI.Views;

namespace Noexs.UI;

public enum AlertType
{
    NONE,
    INFORMATION,
    WARNING,
    CONFIRMATION,
    ERROR,
}

/// <summary>Modal alert (javafx.scene.control.Alert replacement).</summary>
public sealed class MessageDialog : Window
{
    private MessageDialog(string title, string? header, string content, string? details)
    {
        Title = title;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 320;
        MaxWidth = 640;

        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
        if (!string.IsNullOrEmpty(header))
        {
            panel.Children.Add(new TextBlock { Text = header, FontWeight = FontWeight.Bold, FontSize = 13 });
        }
        panel.Children.Add(new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap });
        if (details != null)
        {
            panel.Children.Add(new Expander
            {
                Header = "The exception stacktrace was:",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBox
                {
                    Text = details,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 300,
                    Width = 600,
                },
            });
        }
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 70, IsDefault = true };
        ok.Click += (_, _) => Close();
        panel.Children.Add(ok);
        Content = panel;
    }

    public static void ShowMessage(string infoMessage, Result rc, AlertType alertType)
    {
        ResultDecoder.Module? module = ResultDecoder.Lookup(rc);
        string sb = "\n\nReason:\n\tModule:" + (module == null ? rc.Module.ToString() : module.Name) +
                    "\n\tDesc:" + (module == null ? rc.Desc.ToString() : module.GetMessage(rc.Desc));
        ShowMessage(infoMessage + sb, alertType);
    }

    public static void ShowMessage(string infoMessage, AlertType alertType)
    {
        string? header = alertType switch
        {
            AlertType.NONE => "",
            AlertType.INFORMATION => "Info",
            AlertType.WARNING => "Warning",
            AlertType.CONFIRMATION => "Confirmation",
            AlertType.ERROR => "Error",
            _ => null,
        };
        Show(NoexsApplication.APP_NAME, header, infoMessage, null);
    }

    public static void ShowMessage(Exception ex)
    {
        Show(ex.GetType().FullName ?? ex.GetType().Name, ex.GetType().Name, ex.Message, ex.ToString());
    }

    // identical messages that are already on screen are not shown again (e.g. a repeating failure)
    private static readonly HashSet<string> Open = new();

    private static void Show(string title, string? header, string content, string? details)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                string key = title + "\n" + content;
                if (!Open.Add(key))
                {
                    return;
                }
                var dialog = new MessageDialog(title, header, content, details);
                dialog.Closed += (_, _) => Open.Remove(key);
                if (MainWindow.Instance is { IsVisible: true } owner)
                {
                    dialog.ShowDialog(owner);
                }
                else
                {
                    dialog.Show();
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "showing dialog");
            }
        });
    }
}
