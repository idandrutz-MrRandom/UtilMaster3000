using System;
using System.Collections.Generic;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// PINNED WINDOWS  (Tools -> Pinned on top...)
// Shows which windows are pinned on top right now (by this program or
// by anything else) and lets you unpin them.
// ======================================================

public sealed partial class MainForm
{
    private const int WS_EX_TOPMOST = 0x8;

    // Read from Windows each time, so it is true even for windows pinned elsewhere.
    private static List<WindowInfo> PinnedWindows() =>
        WindowList.GetOpenWindows()
            .Where(w => (Native.GetWindowLong(w.Handle, -20) & WS_EX_TOPMOST) != 0)
            .ToList();

    private bool IsPinned(IntPtr window) =>
        window != IntPtr.Zero &&
        Native.IsWindow(window) &&
        (Native.GetWindowLong(window, -20) & WS_EX_TOPMOST) != 0;

    // The row that pins / unpins the window you were in before the launcher.
    private string PinRowLabel()
    {
        if (lastExternalWindow == IntPtr.Zero || !Native.IsWindow(lastExternalWindow))
            return "Pin last window on top";

        string title = Trunc(SystemTools.WindowTitle(lastExternalWindow), 26);

        return IsPinned(lastExternalWindow)
            ? $"Unpin \"{title}\" (pinned on top)"
            : $"Pin \"{title}\" on top";
    }

    // "Pinned on top: none" or the first names.
    private MenuEntry PinnedSummaryRow()
    {
        List<WindowInfo> pinned = PinnedWindows();

        string label = pinned.Count == 0
            ? "Pinned on top: nothing"
            : pinned.Count == 1
                ? $"Pinned on top: {Trunc(pinned[0].Title, 28)}"
                : $"Pinned on top: {pinned.Count} windows";

        string detail = pinned.Count == 0
            ? "No window is pinned on top right now."
            : "Pinned on top right now:" + NL +
              string.Join(NL, pinned.Select(w => "• " + Trunc(w.Title, 50)));

        return new MenuEntry(0, "", label, targetPage: "Pinned",
            expandText: detail + NL + "Click to see them and unpin one.");
    }

    private List<MenuEntry> PinnedPage(string query)
    {
        List<MenuEntry> list = new();
        List<WindowInfo> pinned = PinnedWindows();

        list.Add(Act("", PinRowLabel(), PinLastWindow));

        if (pinned.Count == 0)
        {
            list.Add(Info("Nothing is pinned on top."));
        }
        else
        {
            list.Add(Info($"Pinned on top ({pinned.Count}). Click one to unpin it:"));

            foreach (WindowInfo window in pinned)
            {
                if (query.Length > 0 &&
                    !window.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IntPtr handle = window.Handle;

                list.Add(new MenuEntry(0, "", window.Title,
                    iconPath: WindowList.GetProcessPath(handle),
                    run: () =>
                    {
                        SystemTools.TogglePin(handle);
                        Toast("Unpinned: " + Trunc(window.Title, 30), OffColor);
                        RefreshPage();
                    },
                    expandText: window.Title + NL + "Pinned on top. Click to unpin."));
            }
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    private static List<MenuEntry> FilterEntries(List<MenuEntry> list, string query) =>
        list.Where(e => query.Length == 0 ||
                        e.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
