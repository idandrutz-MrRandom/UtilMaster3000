using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// THE EDITOR'S OWN COLOURS
// Built from the launcher's theme (its tint and accent) but with the contrast
// fixed: a dim or low-contrast theme must not make the editor hard to read.
// ======================================================

internal static class StudioPalette
{
    private static bool Dark => Theme.Background.GetBrightness() < 0.5f;

    private static Color Hsl(float hue, float saturation, float lightness)
    {
        saturation = Math.Clamp(saturation, 0f, 1f);
        lightness = Math.Clamp(lightness, 0f, 1f);

        float c = (1f - Math.Abs(2f * lightness - 1f)) * saturation;
        float x = c * (1f - Math.Abs(hue / 60f % 2f - 1f));
        float m = lightness - c / 2f;

        (float r, float g, float b) = (int)(((hue % 360f) + 360f) % 360f / 60f) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };

        return Color.FromArgb(
            (int)Math.Round((r + m) * 255f),
            (int)Math.Round((g + m) * 255f),
            (int)Math.Round((b + m) * 255f));
    }

    /// <summary>Window colour: the theme's tint, always clearly dark (or clearly light).</summary>
    public static Color Back
    {
        get
        {
            Color t = Theme.Background;
            return Dark
                ? Hsl(t.GetHue(), Math.Min(t.GetSaturation(), 0.35f) * 0.8f, 0.125f)
                : Hsl(t.GetHue(), Math.Min(t.GetSaturation(), 0.30f) * 0.6f, 0.955f);
        }
    }

    public static Color Text => Dark ? Color.FromArgb(222, 224, 234) : Color.FromArgb(40, 42, 52);

    public static Color Strong => Dark ? Color.White : Color.FromArgb(12, 12, 20);

    /// <summary>The theme's accent, moved into a range that reads on this window.</summary>
    public static Color Accent
    {
        get
        {
            Color a = Theme.Accent;
            float saturation = Math.Max(a.GetSaturation(), 0.35f);

            // A grey accent stays grey.
            if (a.GetSaturation() < 0.12f)
                saturation = a.GetSaturation();

            return Dark
                ? Hsl(a.GetHue(), saturation, Math.Clamp(a.GetBrightness(), 0.66f, 0.82f))
                : Hsl(a.GetHue(), saturation, Math.Clamp(a.GetBrightness(), 0.30f, 0.45f));
        }
    }
}

// ======================================================
// THEMED WINDOW PARTS
// The system title bar, scroll bar and drop-down are drawn by Windows in
// its own colours. These are drawn in the theme, so the style editor and
// the colour picker look like the launcher.
// ======================================================

internal static class WindowDrag
{
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

    /// <summary>Starts dragging a window that has no system title bar.</summary>
    public static void Begin(Form form)
    {
        ReleaseCapture();
        SendMessage(form.Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
    }
}

internal sealed class TitleBar : Control
{
    private readonly Form form;
    private readonly Color? back, accent, strong;
    private bool overClose;

    private Color Back => back ?? StudioPalette.Back;
    private Color Accent => accent ?? StudioPalette.Accent;
    private Color Strong => strong ?? StudioPalette.Strong;

    public TitleBar(Form form, string title, Color? back = null, Color? accent = null, Color? strong = null)
    {
        this.form = form;
        this.back = back;
        this.accent = accent;
        this.strong = strong;

        Text = title;
        Dock = DockStyle.Top;
        Height = 32;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private Rectangle CloseArea => new(Width - 42, 0, 42, Height);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool over = CloseArea.Contains(e.Location);

        if (over != overClose)
        {
            overClose = over;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        overClose = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        if (CloseArea.Contains(e.Location))
            form.Close();
        else
            WindowDrag.Begin(form);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        using (SolidBrush fill = new(Theme.Mix(Back, Accent, 0.12f)))
            g.FillRectangle(fill, ClientRectangle);

        using (Pen line = new(Theme.Mix(Back, Accent, 0.3f), 1f))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        if (overClose)
        {
            using SolidBrush hot = new(Theme.Mix(Back, Theme.Off, 0.65f));
            g.FillRectangle(hot, CloseArea);
        }

        TextRenderer.DrawText(
            g, Text, new Font("Segoe UI Semibold", 9f),
            new Rectangle(12, 0, Width - 60, Height), Strong,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        using Pen cross = new(overClose ? Color.White : Strong, 1.5f);
        int cx = CloseArea.Left + CloseArea.Width / 2, cy = Height / 2;

        g.DrawLine(cross, cx - 5, cy - 5, cx + 5, cy + 5);
        g.DrawLine(cross, cx - 5, cy + 5, cx + 5, cy - 5);
    }
}

internal sealed class ThemedScrollBar : Control
{
    private int maximum = 100, page = 100, value;
    private bool dragging, overThumb;
    private int grab;

    public event EventHandler? Scrolled;

    public int Maximum { get => maximum; set { maximum = Math.Max(1, value); Reclamp(); } }
    public int Page { get => page; set { page = Math.Max(1, value); Reclamp(); } }

    private void Reclamp()
    {
        value = Math.Clamp(value, 0, Math.Max(0, maximum - page));
        Invalidate();
    }

    public int Value
    {
        get => value;
        set
        {
            int next = Math.Clamp(value, 0, Math.Max(0, maximum - page));

            if (next == this.value)
                return;

            this.value = next;
            Invalidate();
        }
    }

    public ThemedScrollBar()
    {
        Width = 12;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private int Range => Math.Max(1, maximum - page);

    private Rectangle Thumb
    {
        get
        {
            int height = Math.Max(30, (int)((long)Height * page / maximum));
            height = Math.Min(height, Height);
            int top = (int)((long)(Height - height) * value / Range);
            return new Rectangle(2, top, Width - 4, height);
        }
    }

    private void Raise()
    {
        Invalidate();
        Scrolled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || maximum <= page)
            return;

        Rectangle thumb = Thumb;

        if (thumb.Contains(e.Location))
        {
            dragging = true;
            grab = e.Y - thumb.Top;
            Capture = true;
            return;
        }

        // A click on the track pages up or down.
        Value += e.Y < thumb.Top ? -page : page;
        Raise();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool over = Thumb.Contains(e.Location);

        if (over != overThumb)
        {
            overThumb = over;
            Invalidate();
        }

        if (!dragging)
            return;

        int travel = Math.Max(1, Height - Thumb.Height);
        int next = (int)((long)(e.Y - grab) * Range / travel);

        if (next != value)
        {
            Value = next;
            Raise();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        overThumb = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Mix(StudioPalette.Back, Color.Black, 0.2f));

        if (maximum <= page)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = DoublePanel.RoundedPath(Thumb, 4);
        using SolidBrush fill = new(Color.FromArgb(
            dragging ? 230 : overThumb ? 200 : 150, StudioPalette.Accent));

        g.FillPath(fill, path);
    }
}

// A drop-down drawn in the theme (the system one is always white).
internal sealed class ThemedDropDown : Control
{
    public List<string> Items { get; } = new();
    public event EventHandler? SelectedIndexChanged;

    private int selected = -1;

    public int SelectedIndex
    {
        get => selected;
        set { if (selected != value) { selected = value; Invalidate(); } }
    }

    public object? SelectedItem => selected >= 0 && selected < Items.Count ? Items[selected] : null;

    public ThemedDropDown()
    {
        Cursor = Cursors.Hand;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (!Enabled || Items.Count == 0 || e.Button != MouseButtons.Left)
            return;

        ContextMenuStrip menu = new()
        {
            Renderer = new ToolStripProfessionalRenderer(new ThemedColors()),
            BackColor = Theme.Mix(StudioPalette.Back, Color.Black, 0.25f),
            ForeColor = StudioPalette.Strong,
            ShowImageMargin = false,
            MaximumSize = new Size(Width + 60, 340),
            Font = Font
        };

        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;

            ToolStripMenuItem item = new(Items[i])
            {
                Checked = i == selected,
                ForeColor = StudioPalette.Strong
            };

            item.Click += (_, _) =>
            {
                selected = index;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            };

            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => menu.BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Color.Black);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = DoublePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 5);
        using SolidBrush fill = new(Theme.Mix(StudioPalette.Back, Color.Black, 0.2f));
        using Pen edge = new(Theme.Mix(StudioPalette.Back, StudioPalette.Accent, Enabled ? 0.4f : 0.15f), 1.2f);

        g.FillPath(fill, path);
        g.DrawPath(edge, path);

        Color ink = Enabled ? StudioPalette.Strong : Color.FromArgb(120, StudioPalette.Text);

        TextRenderer.DrawText(
            g, SelectedItem as string ?? "", Font,
            new Rectangle(8, 0, Width - 28, Height), ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        using Pen arrow = new(ink, 1.6f);
        int cx = Width - 12, cy = Height / 2;

        g.DrawLines(arrow, new[]
        {
            new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2)
        });
    }
}

internal sealed class ThemedColors : ProfessionalColorTable
{
    private static Color Panel => Theme.Mix(StudioPalette.Back, Color.Black, 0.25f);
    private static Color Hot => Theme.Mix(StudioPalette.Back, StudioPalette.Accent, 0.4f);

    public override Color ToolStripDropDownBackground => Panel;
    public override Color ImageMarginGradientBegin => Panel;
    public override Color ImageMarginGradientMiddle => Panel;
    public override Color ImageMarginGradientEnd => Panel;
    public override Color MenuBorder => Hot;
    public override Color MenuItemBorder => Hot;
    public override Color MenuItemSelected => Hot;
    public override Color MenuItemSelectedGradientBegin => Hot;
    public override Color MenuItemSelectedGradientEnd => Hot;
    public override Color CheckBackground => Hot;
    public override Color CheckSelectedBackground => Hot;
    public override Color CheckPressedBackground => Hot;
}
