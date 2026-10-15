using Avalonia.Automation;
using Avalonia.Controls;
using Noexs.UI.Services;

namespace Noexs.UI.Views;

/// <summary>Drop-down of dumps created by the Search tab ("From Search ▾" buttons).</summary>
public static class DumpPicker
{
    public static void Show(Control anchor, Action<DumpEntry> picked)
    {
        List<DumpEntry> dumps = DumpHistory.List();
        if (dumps.Count == 0)
        {
            MessageDialog.ShowMessage("No dumps yet. Run a search in the Search tab first (or use Browse).",
                AlertType.INFORMATION);
            return;
        }
        var menu = new ContextMenu();
        foreach (DumpEntry entry in dumps)
        {
            var item = new MenuItem
            {
                // a TextBlock, so "_" in the label isn't taken as an access-key marker
                Header = new TextBlock { Text = entry.Label },
                FontWeight = entry.Full && !entry.Label.StartsWith("older:", StringComparison.Ordinal)
                    ? Avalonia.Media.FontWeight.SemiBold
                    : Avalonia.Media.FontWeight.Normal,
            };
            AutomationProperties.SetName(item, entry.Label);
            ToolTip.SetTip(item, entry.Path);
            item.Click += (_, _) => picked(entry);
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }
}
