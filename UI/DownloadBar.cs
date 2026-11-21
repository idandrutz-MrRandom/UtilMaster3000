using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// DOWNLOAD BAR
// A small themed bar at the bottom of the screen while the app downloads one of its
// tools (ffmpeg, yt-dlp): what is downloading, how far it is, how fast, and about
// how long is left. It never takes the keyboard focus and stays out of screenshots.
// ======================================================

internal sealed class DownloadBar : Form
{
    private readonly string title;
    private readonly Font titleFont = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font smallFont = new("Segoe UI", 9f);
    private readonly System.Windows.Forms.Timer closeTimer = new();

    private ToolDownloadProgress last = new(0, 0, 0, 0, null);
    private string? message;
    private bool failed;

    private DownloadBar(string title)
    {
        this.title = title;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(440, 82);

        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - 190);

        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
    }

    /// <summary>Shows the bar (without taking focus) and returns it.</summary>
    public static DownloadBar Start(string title)
    {
        DownloadBar bar = new(title);
        bar.Show();
        return bar;
    }

    /// <summary>Pass this to an installer as its progress callback (any thread).</summary>
    public void Report(ToolDownloadProgress progress)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => Report(progress)));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }

            return;
        }

        // Repaint only when something visible changed (the percent, the speed or the time).
        bool same = progress.Percent == last.Percent &&
                    progress.SpeedText == last.SpeedText &&
                    progress.TimeLeftText == last.TimeLeftText &&
                    progress.SizeText == last.SizeText;

        last = progress;

        if (!same)
            Invalidate();
    }

    /// <summary>Ends the job: shows the message for a moment, then the bar goes away.</summary>
    public void Finish(string text, bool ok, int holdMs = 2500)
    {
        if (IsDisposed)
            return;

        message = text;
        failed = !ok;
        Invalidate();

        closeTimer.Interval = Math.Max(300, holdMs);
        closeTimer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Never inside screenshots or recordings.
        SetWindowDisplayAffinity(Handle, 0x11);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        using GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width, Height), 12);
        Region? old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        GraphicsPath path = new();

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        g.Clear(Theme.Background);

        using (Pen border = new(Theme.BorderColor, 2f))
        using (GraphicsPath frame = RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 11))
            g.DrawPath(border, frame);

        // Title (left) and the percent (right).
        TextRenderer.DrawText(g, title, titleFont, new Point(16, 10), Theme.Strong);

        string percentText = message != null && !failed ? "100%" : $"{Math.Clamp(last.Percent, 0, 100)}%";

        if (message == null || !failed)
        {
            Size size = TextRenderer.MeasureText(g, percentText, titleFont);
            TextRenderer.DrawText(g, percentText, titleFont, new Point(Width - 16 - size.Width, 10), Theme.Accent);
        }

        // The bar.
        Rectangle track = new(16, 38, Width - 32, 10);
        int fillWidth = (int)(track.Width * Math.Clamp(message != null && !failed ? 100 : last.Percent, 0, 100) / 100.0);

        using (GraphicsPath trackPath = RoundedRect(track, 5))
        using (SolidBrush trackBrush = new(Theme.Mix(Theme.Background, Theme.Text, 0.18f)))
            g.FillPath(trackBrush, trackPath);

        if (fillWidth > 1)
        {
            Rectangle fill = new(track.Left, track.Top, Math.Max(10, fillWidth), track.Height);

            using GraphicsPath fillPath = RoundedRect(fill, 5);
            using SolidBrush fillBrush = new(failed ? Theme.Off : Theme.Accent);
            g.FillPath(fillBrush, fillPath);
        }

        // Under the bar: what is going on, and how long it will take.
        Color dim = Theme.Mix(Theme.Text, Theme.Background, 0.25f);

        if (message != null)
        {
            TextRenderer.DrawText(g, message, smallFont, new Point(16, 54), failed ? Theme.Off : Theme.On);
            return;
        }

        string left = last.Total == 0 && last.Percent >= 96
            ? "Checking and unpacking..."
            : last.Total == 0
                ? "Connecting..."
                : $"{last.SizeText}   {last.SpeedText}".TrimEnd();

        string right = last.Total == 0 ? "" : last.TimeLeftText;

        TextRenderer.DrawText(g, left, smallFont, new Point(16, 54), dim);

        if (right.Length > 0)
        {
            Size size = TextRenderer.MeasureText(g, right, smallFont);
            TextRenderer.DrawText(g, right, smallFont, new Point(Width - 16 - size.Width, 54), Theme.Strong);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closeTimer.Dispose();
            titleFont.Dispose();
            smallFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
