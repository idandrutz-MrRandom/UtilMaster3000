using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

/// <summary>
/// A smooth fade-in when a big window is opened. Building and painting a window the first time takes a
/// moment, and doing it in front of the user looks rough. So the window is built and painted off-screen
/// first, a picture of it is faded in (cheap: see <see cref="FadeGhost"/>), and only then is the real
/// window put in place under that picture. Follows the menu's animation speed; "no animation" there
/// turns it off.
/// </summary>
internal static class OpenAnimation
{
    private const int BaseMs = 200;

    /// <summary>Tests: how many frames the last fade drew.</summary>
    public static int LastFrames { get; private set; }

    /// <summary>Tests: shifts where the window ends up (so a test can run off-screen).</summary>
    public static Point TargetShift { get; set; }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    /// <summary>Shows the window with a fade, or plainly when that is not possible.</summary>
    public static void Show(Form form)
    {
        try
        {
            if (Run(form))
                return;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // Fall through to the plain way below.
        }

        if (!form.IsDisposed && !form.Visible)
            form.Show();
    }

    private static bool Run(Form form)
    {
        if (Theme.AnimationScale >= 30 || form.WindowState == FormWindowState.Minimized)
            return false;

        FormWindowState wanted = form.WindowState;
        Screen screen = Screen.FromPoint(Cursor.Position);

        // Where the window will really be.
        Size size = wanted == FormWindowState.Maximized
            ? screen.WorkingArea.Size
            : form.Size;

        size = new Size(Math.Max(size.Width, form.MinimumSize.Width), Math.Max(size.Height, form.MinimumSize.Height));

        Point target = wanted == FormWindowState.Maximized
            ? screen.WorkingArea.Location
            : form.StartPosition == FormStartPosition.CenterScreen
                ? new Point(
                    screen.WorkingArea.X + (screen.WorkingArea.Width - size.Width) / 2,
                    screen.WorkingArea.Y + (screen.WorkingArea.Height - size.Height) / 2)
                : form.Location;

        target = new Point(target.X + TargetShift.X, target.Y + TargetShift.Y);

        // Built and painted far off-screen, at its final size.
        form.StartPosition = FormStartPosition.Manual;
        form.WindowState = FormWindowState.Normal;
        form.Bounds = new Rectangle(-32000, -32000, size.Width, size.Height);
        form.Show();

        for (int i = 0; i < 6; i++)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }

        form.Update();

        using Bitmap picture = new(size.Width, size.Height);

        using (Graphics g = Graphics.FromImage(picture))
        {
            IntPtr dc = g.GetHdc();

            try
            {
                PrintWindow(form.Handle, dc, 2);
            }
            finally
            {
                g.ReleaseHdc(dc);
            }
        }

        FadeGhost? ghost = FadeGhost.TryFromBitmap(picture, target, 0);

        void Place()
        {
            form.Location = target;

            if (wanted == FormWindowState.Maximized)
                form.WindowState = FormWindowState.Maximized;
        }

        if (ghost == null)
        {
            Place();
            return true;
        }

        int ms = (int)Math.Clamp(BaseMs / Math.Max(0.2, Theme.AnimationScale), 200, 420);
        Stopwatch clock = Stopwatch.StartNew();
        int frames = 0;

        System.Windows.Forms.Timer timer = new() { Interval = 8 };

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, clock.ElapsedMilliseconds / (double)ms);
            double eased = t * t * (3 - 2 * t);

            ghost.SetAlpha((byte)Math.Round(255 * eased));
            frames++;

            if (t < 1.0)
                return;

            timer.Stop();
            timer.Dispose();
            LastFrames = frames;

            // The picture now covers the spot completely: put the real window under it, let it settle,
            // then take the picture away. They look the same, so nothing seems to change.
            if (!form.IsDisposed)
            {
                Place();

                for (int i = 0; i < 4; i++)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                }

                form.Activate();
            }

            ghost.Dispose();
        };

        timer.Start();

        return true;
    }
}
