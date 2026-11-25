using System;
using System.Drawing;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// The program's icon follows the menu's theme (see UI/IconArt.cs and UI/AppIcon.cs).
// ======================================================

public sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer iconTimer = new() { Interval = 400 };

    private void StartIcons()
    {
        ApplyIcons();

        // A theme change (or a colour being dragged) redraws the icon once things settle.
        iconTimer.Tick += (_, _) =>
        {
            iconTimer.Stop();
            ApplyIcons();
        };

        Theme.Configured += RestartIconTimer;
        FormClosed += (_, _) =>
        {
            Theme.Configured -= RestartIconTimer;
            iconTimer.Dispose();
        };
    }

    private void RestartIconTimer()
    {
        iconTimer.Stop();
        iconTimer.Start();
    }

    private void ApplyIcons()
    {
        trayIcon.Icon = AppIcon.Get(IconKind.App, SystemInformation.SmallIconSize);
        Icon = AppIcon.Get(IconKind.App, SystemInformation.IconSize);
    }
}
