using System;
using System.Collections.Generic;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// RE-ORDER THE MENU  (Setup -> Re-order the main menu)
// Opens a window where the rows of the home menu are dragged into the order you
// want (see UI/MenuOrderWindow.cs). The order is saved and used at once.
// ======================================================

public sealed partial class MainForm
{
    // The home menu as it ships: what "back to the default order" goes back to.
    private List<string> defaultMainOrder = new();

    private MenuOrderWindow? orderWindow;

    // Puts the home menu's rows in the saved order (rows that are not in it, for example
    // ones added by a newer version, keep their usual order at the end).
    private void ApplyMenuOrder()
    {
        List<string> order = settings.MenuOrder.Count > 0 ? settings.MenuOrder : defaultMainOrder;

        List<MenuEntry> ordered = fullMainEntries
            .OrderBy(e =>
            {
                int at = order.IndexOf(e.Label);
                return at < 0 ? int.MaxValue : at;
            })
            .ToList();

        List<MenuEntry> numbered = Renumber(ordered);

        fullMainEntries = numbered;
        ApplyMenuMode();
    }

    private void OpenMenuOrder()
    {
        if (orderWindow != null && !orderWindow.IsDisposed)
        {
            orderWindow.Activate();
            return;
        }

        orderWindow = new MenuOrderWindow(
            this,
            CurrentMenuItems,
            ReorderMainEntries,
            ResetMenuOrder);

        orderWindow.FormClosed += (_, _) => orderWindow = null;
        orderWindow.Show(this);
    }

    // The rows as they are now, for the window's list.
    private List<ReorderItem> CurrentMenuItems() =>
        fullMainEntries.Select(e => new ReorderItem(e.Label, e.Icon, e.Label)).ToList();

    // The window dropped a row somewhere: the menu takes that order and keeps it.
    private void ReorderMainEntries(List<string> keys)
    {
        settings.MenuOrder = keys;
        ApplyMenuOrder();
        SaveSettings();

        if (currentPage == "home")
            RefreshPage();
    }

    private void ResetMenuOrder()
    {
        settings.MenuOrder = new List<string>();
        ApplyMenuOrder();
        SaveSettings();

        if (currentPage == "home")
            RefreshPage();
    }
}
