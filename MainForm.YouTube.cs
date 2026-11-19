using System;
using System.Windows.Forms;

namespace UtilMaster3000;

// UtilTube hooks: the offline YouTube client and its background downloads.
public sealed partial class MainForm
{
    private System.Windows.Forms.Timer? trimTimer;

    private void SetupTube()
    {
        // Resumes queued downloads and checks followed channels. It is
        // cheap while there is nothing to do.
        // The background session renewal needs the UI thread.
        YtAccount.Init();

        TubeLibrary.Instance.Start();

        // While idle in the tray, regularly return freed memory to Windows.
        trimTimer = new System.Windows.Forms.Timer { Interval = 180_000 };

        trimTimer.Tick += (_, _) =>
        {
            if (!Visible)
                MemoryTrimmer.Trim();
        };

        trimTimer.Start();
    }

    private void DisposeTube()
    {
        trimTimer?.Stop();
        trimTimer?.Dispose();

        // Running downloads are paused and resume next time.
        TubeLibrary.Instance.Stop();
    }

    private void OpenTube(string? query)
    {
        HideToTray();
        TubeClient.Open(query);
    }
}
