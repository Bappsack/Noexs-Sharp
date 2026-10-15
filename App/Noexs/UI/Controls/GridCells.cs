using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noexs.UI.Controls;

/// <summary>Helpers for the always-editable TextBox cells (class "cell") used in data grids.</summary>
public static class GridCells
{
    /// <summary>Enter commits the edited cell (its binding updates on LostFocus); Escape does the same.</summary>
    public static void CommitOnEnter(DataGrid grid)
    {
        grid.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape) || e.Source is not TextBox { Classes: var classes } || !classes.Contains("cell"))
            {
                return;
            }
            e.Handled = true;
            TopLevel.GetTopLevel(grid)?.FocusManager?.ClearFocus();
        }, RoutingStrategies.Tunnel);

        // clicking a cell selects its whole content, so typing replaces it (like a spreadsheet)
        grid.AddHandler(InputElement.GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox { Classes: var classes } box && classes.Contains("cell"))
            {
                // after the click has placed the caret
                Avalonia.Threading.Dispatcher.UIThread.Post(box.SelectAll, Avalonia.Threading.DispatcherPriority.Input);
            }
        }, RoutingStrategies.Bubble);
    }
}
