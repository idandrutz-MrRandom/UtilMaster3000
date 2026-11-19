using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// "WHAT IS RUNNING" BANNER
// While the autoclicker runs, a macro records or plays, or the screen
// is being recorded, a small banner says so and names the key that stops
// it. It ignores the mouse, never takes focus, and is left out of screen
// recordings. Position, size and see-through are set under
// Style -> Notifications.
// ======================================================

public sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer overlayTimer = new() { Interval = 250 };
    private StatusBanner? banner;
    private readonly Stopwatch screenRecordWatch = new();

    private sealed class StatusBanner : Form
    {
        private List<(Color Dot, string Text)> lines = new();
        private Font font = new("Consolas", 11f, FontStyle.Bold);

        private NotificationPosition shownPosition = (NotificationPosition)(-1);
        private int shownScale = -1;
        private int shownOpacity = -1;

        public StatusBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 20, 28);
            Opacity = 0.94;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                // never activates, tool window, ignores the mouse
                p.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;
                return p;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            try
            {
                // Not part of screen recordings or screenshots (Windows 10 2004+).
                SetWindowDisplayAffinity(Handle, 0x11);
            }
            catch
            {
                // Older Windows: it simply shows up in recordings.
            }
        }

        private double Scale => Math.Clamp(shownScale, 50, 300) / 100.0;

        public void SetLines(
            List<(Color Dot, string Text)> newLines,
            NotificationPosition position, int scalePercent, int opacityPercent)
        {
            bool same = newLines.SequenceEqual(lines) &&
                        position == shownPosition &&
                        scalePercent == shownScale &&
                        opacityPercent == shownOpacity;

            if (same)
                return;

            if (scalePercent != shownScale)
            {
                Font old = font;
                font = new Font("Consolas", (float)(11f * Math.Clamp(scalePercent, 50, 300) / 100.0), FontStyle.Bold);
                old.Dispose();
            }

            lines = newLines;
            shownPosition = position;
            shownScale = scalePercent;
            shownOpacity = opacityPercent;

            Opacity = Math.Clamp(opacityPercent, 20, 100) / 100.0;

            double s = Scale;
            Rectangle area = Screen.PrimaryScreen!.WorkingArea;

            int width = lines.Max(l => TextRenderer.MeasureText(l.Text, font).Width) + (int)(56 * s);
            Size = new Size(Math.Min(width, area.Width - 40), (int)(lines.Count * 30 * s) + (int)(14 * s));

            const int margin = 12;

            int x = position switch
            {
                NotificationPosition.BottomLeft or NotificationPosition.TopLeft => area.Left + margin,
                NotificationPosition.BottomRight or NotificationPosition.TopRight => area.Right - Width - margin,
                _ => area.Left + (area.Width - Width) / 2
            };

            int y = position switch
            {
                NotificationPosition.TopCenter or NotificationPosition.TopLeft or NotificationPosition.TopRight
                    => area.Top + margin,
                _ => area.Bottom - Height - 60
            };

            Location = new Point(x, y);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Color edge = lines.Count > 0 ? lines[0].Dot : Color.Gray;

            using Pen border = new(edge, 2f);
            e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);

            double s = Scale;
            int rowHeight = (int)(30 * s);
            int y = (int)(9 * s);
            int dotSize = (int)(12 * s);

            foreach ((Color dot, string text) in lines)
            {
                using SolidBrush brush = new(dot);
                e.Graphics.FillEllipse(brush, (int)(14 * s), y + (int)(8 * s), dotSize, dotSize);

                TextRenderer.DrawText(e.Graphics, text, font, new Point((int)(34 * s), y + (int)(3 * s)), Color.White,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                y += rowHeight;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                font.Dispose();

            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    }

    private void SetupOverlay()
    {
        overlayTimer.Tick += (_, _) => OverlayTick();
        overlayTimer.Start();
    }

    // The key that stops something, or where to stop it when that key is off.
    private string StopKey(string id, bool hotkeyOn)
    {
        string key = KeyLabel(id);

        return hotkeyOn && key != "not set"
            ? "press " + key + " to stop"
            : "stop it from the launcher (Tools menu)";
    }

    private List<(Color Dot, string Text)> OverlayLines()
    {
        List<(Color Dot, string Text)> lines = new();

        if (macroRecorder.Recording)
        {
            lines.Add((Color.FromArgb(235, 60, 60),
                $"RECORDING MACRO  -  {macroRecorder.Count} events  -  {StopKey("macro-record", settings.MacroHotkeysOn).Replace("to stop", "to stop and save")}"));
        }

        if (macroPlayer.Playing)
        {
            lines.Add((Theme.Accent,
                $"PLAYING MACRO  -  {StopKey("macro-play", settings.MacroHotkeysOn)}"));
        }

        if (autoClicker.Running)
        {
            lines.Add((Theme.On,
                $"AUTOCLICKER ON  -  {autoClicker.Clicks} clicks  -  {StopKey("autoclick", settings.AcHotkeyOn)}" +
                "  (or move the mouse into the top-left corner)"));
        }

        if (IsRecording)
        {
            if (!screenRecordWatch.IsRunning)
                screenRecordWatch.Restart();

            string a = StopKey("rec-area", settings.PrintScreenKeys);
            string b = StopKey("rec-full", settings.PrintScreenKeys);
            string keys = a == b
                ? a
                : a.StartsWith("press ") && b.StartsWith("press ")
                    ? $"press {a[6..^8]} or {b[6..^8]} to stop"
                    : a;

            lines.Add((Color.FromArgb(235, 60, 60),
                $"RECORDING SCREEN  -  {screenRecordWatch.Elapsed:mm\\:ss}  -  {keys}"));
        }
        else if (screenRecordWatch.IsRunning)
        {
            screenRecordWatch.Reset();
        }

        return lines;
    }

    private void OverlayTick()
    {
        List<(Color Dot, string Text)> lines = settings.ShowRunningBanner
            ? OverlayLines()
            : new List<(Color Dot, string Text)>();

        // While you adjust it, a sample shows even when nothing is running.
        if (lines.Count == 0 && settings.ShowRunningBanner && DateTime.UtcNow < bannerPreviewUntil)
            lines.Add((Theme.Accent, "BANNER PREVIEW  -  press F9 to stop and save"));

        if (lines.Count == 0)
        {
            if (banner != null && banner.Visible)
                banner.Hide();

            return;
        }

        banner ??= new StatusBanner();
        banner.SetLines(lines, settings.BannerPosition, settings.BannerScale, settings.BannerOpacityPercent);

        if (!banner.Visible)
            banner.Show();
    }
}
