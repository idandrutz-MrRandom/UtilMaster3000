using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// COLOUR PICKER THAT UPDATES THE APP WHILE YOU DRAG
// The normal Windows colour box only reports a colour when it is closed.
// This one reports every change as it happens, and puts the old colour
// back if you cancel.
// ======================================================

internal sealed class LiveColorPicker : Form
{
    // The look of the app when the picker opened (it stays put while you drag).
    private readonly Color back = StudioPalette.Back;
    private readonly Color fore = StudioPalette.Text;
    private readonly Color accent = StudioPalette.Accent;
    private readonly Color strong = StudioPalette.Strong;

    private const int BoxSize = 220;

    private readonly Action<Color> live;
    private readonly Color original;

    private float hue, saturation, value;
    private Bitmap? squareBitmap;
    private float squareHue = -1f;
    private bool updating;
    private bool accepted;
    private readonly Stopwatch throttle = Stopwatch.StartNew();

    private readonly Panel content = new();
    private readonly DoublePanel square = new();
    private readonly DoublePanel strip = new();
    private readonly DoublePanel oldChip = new();
    private readonly DoublePanel newChip = new();
    private readonly TextBox hex = new();

    public Color Current => FromHsv(hue, saturation, value);

    /// <summary>
    /// Shows the picker next to <paramref name="owner"/>. <paramref name="live"/> is
    /// called for every change; returns the chosen colour, or null (after calling
    /// <paramref name="live"/> with the start colour again) when cancelled.
    /// </summary>
    public static Color? Pick(Form owner, string title, Color start, Action<Color> live)
    {
        using LiveColorPicker picker = new(title, start, live);

        // Beside the window being styled, never on top of it.
        Rectangle area = Screen.FromControl(owner).WorkingArea;
        int x = owner.Right + 8;

        if (x + picker.Width > area.Right)
            x = owner.Left - picker.Width - 8;

        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - picker.Width));

        picker.StartPosition = FormStartPosition.Manual;
        picker.Location = new Point(x, Math.Clamp(owner.Top, area.Top, Math.Max(area.Top, area.Bottom - picker.Height)));

        return picker.ShowDialog(owner) == DialogResult.OK
            ? picker.Current
            : null;
    }

    private LiveColorPicker(string title, Color start, Action<Color> live)
    {
        this.live = live;
        original = start;

        (hue, saturation, value) = ToHsv(start);

        Text = title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        KeyPreview = true;

        // One pixel of the border colour around everything, like the launcher.
        BackColor = Theme.Mix(back, accent, 0.4f);
        Padding = new Padding(1);
        ForeColor = fore;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(308, 378);

        content.BackColor = back;
        content.Dock = DockStyle.Fill;

        square.Bounds = new Rectangle(16, 16, BoxSize, BoxSize);
        square.Cursor = Cursors.Cross;
        square.Paint += PaintSquare;
        square.MouseDown += (_, e) => DragSquare(e);
        square.MouseMove += (_, e) => DragSquare(e);
        square.MouseUp += (_, _) => Report(force: true);

        strip.Bounds = new Rectangle(250, 16, 28, BoxSize);
        strip.Cursor = Cursors.HSplit;
        strip.Paint += PaintStrip;
        strip.MouseDown += (_, e) => DragStrip(e);
        strip.MouseMove += (_, e) => DragStrip(e);
        strip.MouseUp += (_, _) => Report(force: true);

        oldChip.Bounds = new Rectangle(16, 250, 110, 30);
        oldChip.BackColor = original;
        oldChip.Cursor = Cursors.Hand;
        oldChip.Paint += (_, e) => Outline(e.Graphics, oldChip, "before");

        // Clicking the old colour goes back to it.
        oldChip.Click += (_, _) => SetColor(original);

        newChip.Bounds = new Rectangle(126, 250, 110, 30);
        newChip.BackColor = start;
        newChip.Paint += (_, e) => Outline(e.Graphics, newChip, "now");

        hex.Bounds = new Rectangle(16, 292, 86, 24);
        hex.BorderStyle = BorderStyle.FixedSingle;
        hex.BackColor = Theme.Mix(back, Color.Black, 0.2f);
        hex.ForeColor = strong;
        hex.Font = new Font("Consolas", 10f);
        hex.MaxLength = 7;
        hex.Text = Theme.ToHex(start);
        hex.TextChanged += (_, _) => HexTyped();

        Button ok = MakeButton("OK", new Rectangle(140, 290, 70, 28));
        ok.DialogResult = DialogResult.OK;
        ok.Click += (_, _) => accepted = true;

        Button cancel = MakeButton("Cancel", new Rectangle(218, 290, 70, 28));
        cancel.DialogResult = DialogResult.Cancel;

        Label tip = new()
        {
            Text = "Drag to change. The old colour (left) puts it back.",
            Bounds = new Rectangle(16, 322, 280, 16),
            ForeColor = Theme.Mix(fore, back, 0.35f),
            BackColor = back,
            Font = new Font("Segoe UI", 8f)
        };

        content.Controls.AddRange(new Control[] { square, strip, oldChip, newChip, hex, ok, cancel, tip });

        Controls.Add(content);
        Controls.Add(new TitleBar(this, title, back, accent, strong));

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Outline(Graphics g, Control chip, string caption)
    {
        using Pen edge = new(Theme.Mix(back, accent, 0.4f), 1f);
        g.DrawRectangle(edge, 0, 0, chip.Width - 1, chip.Height - 1);

        Color ink = chip.BackColor.GetBrightness() > 0.55f ? Color.Black : Color.White;

        TextRenderer.DrawText(
            g, caption, Font, new Rectangle(0, 0, chip.Width, chip.Height), ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private Button MakeButton(string text, Rectangle bounds)
    {
        Button button = new()
        {
            Text = text,
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Mix(back, accent, 0.16f),
            ForeColor = strong,
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };

        button.FlatAppearance.BorderColor = Theme.Mix(back, accent, 0.4f);
        return button;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        squareBitmap?.Dispose();

        // Cancelled (or closed with the X): the app goes back to how it was.
        if (!accepted)
            live(original);
    }

    // ---- input ----

    private void DragSquare(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        saturation = Math.Clamp(e.X / (float)(BoxSize - 1), 0f, 1f);
        value = 1f - Math.Clamp(e.Y / (float)(BoxSize - 1), 0f, 1f);
        Changed();
    }

    private void DragStrip(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        hue = Math.Clamp(e.Y / (float)(BoxSize - 1), 0f, 1f) * 360f;

        // Dragging the strip should keep a visible colour even from white / black.
        if (saturation < 0.02f)
            saturation = 0.6f;

        if (value < 0.05f)
            value = 0.8f;

        Changed();
    }

    private void HexTyped()
    {
        if (updating)
            return;

        string text = hex.Text.Trim().TrimStart('#');

        if (text.Length != 6 ||
            !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return;
        }

        (hue, saturation, value) = ToHsv(Color.FromArgb(rgb >> 16 & 255, rgb >> 8 & 255, rgb & 255));
        Changed(fromHex: true);
    }

    private void SetColor(Color color)
    {
        (hue, saturation, value) = ToHsv(color);
        Changed();
        Report(force: true);
    }

    private void Changed(bool fromHex = false)
    {
        if (!fromHex)
        {
            updating = true;
            hex.Text = Theme.ToHex(Current);
            updating = false;
        }

        newChip.BackColor = Current;
        square.Invalidate();
        strip.Invalidate();

        Report(force: false);
    }

    // The app is told at most about 40 times a second while dragging, and
    // always once more when the mouse is released.
    private void Report(bool force)
    {
        if (!force && throttle.ElapsedMilliseconds < 25)
            return;

        throttle.Restart();
        live(Current);
    }

    // ---- painting ----

    private void PaintSquare(object? sender, PaintEventArgs e)
    {
        if (squareBitmap == null || Math.Abs(squareHue - hue) > 0.01f)
        {
            squareBitmap ??= new Bitmap(BoxSize, BoxSize, PixelFormat.Format32bppRgb);
            RenderSquare(squareBitmap, hue);
            squareHue = hue;
        }

        e.Graphics.DrawImageUnscaled(squareBitmap, 0, 0);

        int x = (int)(saturation * (BoxSize - 1));
        int y = (int)((1f - value) * (BoxSize - 1));

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using Pen dark = new(Color.Black, 3f);
        using Pen light = new(Color.White, 1.5f);

        e.Graphics.DrawEllipse(dark, x - 6, y - 6, 12, 12);
        e.Graphics.DrawEllipse(light, x - 6, y - 6, 12, 12);

        using Pen edge = new(Theme.Mix(back, accent, 0.4f), 1f);
        e.Graphics.DrawRectangle(edge, 0, 0, BoxSize - 1, BoxSize - 1);
    }

    private void PaintStrip(object? sender, PaintEventArgs e)
    {
        using LinearGradientBrush brush = new(
            new Rectangle(0, 0, strip.Width, strip.Height),
            Color.Red, Color.Red, 90f)
        {
            InterpolationColors = new ColorBlend
            {
                Positions = new[] { 0f, 1f / 6, 2f / 6, 3f / 6, 4f / 6, 5f / 6, 1f },
                Colors = new[]
                {
                    Color.FromArgb(255, 0, 0), Color.FromArgb(255, 255, 0),
                    Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 255),
                    Color.FromArgb(0, 0, 255), Color.FromArgb(255, 0, 255),
                    Color.FromArgb(255, 0, 0)
                }
            }
        };

        e.Graphics.FillRectangle(brush, 0, 0, strip.Width, strip.Height);

        int y = (int)(hue / 360f * (BoxSize - 1));

        using Pen dark = new(Color.Black, 3f);
        using Pen light = new(Color.White, 1.5f);

        e.Graphics.DrawRectangle(dark, 0, y - 3, strip.Width - 1, 6);
        e.Graphics.DrawRectangle(light, 0, y - 3, strip.Width - 1, 6);
    }

    private static void RenderSquare(Bitmap bitmap, float hue)
    {
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            int[] row = new int[bitmap.Width];

            for (int y = 0; y < bitmap.Height; y++)
            {
                float v = 1f - y / (float)(bitmap.Height - 1);

                for (int x = 0; x < bitmap.Width; x++)
                {
                    float s = x / (float)(bitmap.Width - 1);
                    row[x] = FromHsv(hue, s, v).ToArgb();
                }

                System.Runtime.InteropServices.Marshal.Copy(
                    row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    // ---- colour maths ----

    private static (float H, float S, float V) ToHsv(Color c)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        float h = 0f;

        if (delta > 0f)
        {
            if (max == r)
                h = 60f * (((g - b) / delta + 6f) % 6f);
            else if (max == g)
                h = 60f * ((b - r) / delta + 2f);
            else
                h = 60f * ((r - g) / delta + 4f);
        }

        return (h, max <= 0f ? 0f : delta / max, max);
    }

    private static Color FromHsv(float h, float s, float v)
    {
        h = (h % 360f + 360f) % 360f;

        float c = v * s;
        float x = c * (1f - Math.Abs(h / 60f % 2f - 1f));
        float m = v - c;

        (float r, float g, float b) = (int)(h / 60f) switch
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
}

// ======================================================
// FLAT SLIDER  (themed track + thumb, value shown on the right)
// ValueChanged fires while dragging, Committed once when let go.
// ======================================================

internal sealed class FlatSlider : Control
{
    private int minimum, maximum = 100, step = 1, current;
    private bool dragging;

    public event EventHandler? ValueChanged;
    public event EventHandler? Committed;

    public Func<int, string> Format { get; set; } = v => v.ToString();

    public int Minimum { get => minimum; set { minimum = value; Invalidate(); } }
    public int Maximum { get => maximum; set { maximum = Math.Max(value, minimum + 1); Invalidate(); } }
    public int Step { get => step; set => step = Math.Max(1, value); }

    public int Value
    {
        get => current;
        set
        {
            int next = Snap(value);

            if (next == current)
                return;

            current = next;
            Invalidate();
        }
    }

    public FlatSlider()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Height = 30;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    private int Snap(int value)
    {
        value = Math.Clamp(value, minimum, maximum);
        return minimum + (int)Math.Round((value - minimum) / (double)step) * step;
    }

    private const int LabelWidth = 64;

    private Rectangle Track => new(8, 0, Math.Max(10, Width - LabelWidth - 16), Height);

    private void SetFromX(int x)
    {
        Rectangle track = Track;
        double fraction = Math.Clamp((x - track.Left) / (double)track.Width, 0.0, 1.0);
        int next = Snap(minimum + (int)Math.Round(fraction * (maximum - minimum)));

        if (next == current)
            return;

        current = next;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        Focus();
        dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (dragging)
            SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!dragging)
            return;

        dragging = false;
        Capture = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Mouse wheel over this slider (routed by the window that holds it).</summary>
    public void Wheel(int delta) => Nudge(delta > 0 ? step : -step);

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Left)
            Nudge(-step);
        else if (e.KeyCode == Keys.Right)
            Nudge(step);
    }

    private void Nudge(int amount)
    {
        int next = Snap(current + amount);

        if (next == current)
            return;

        current = next;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle track = Track;
        int mid = Height / 2;
        float fraction = (current - minimum) / (float)(maximum - minimum);
        int thumbX = track.Left + (int)(fraction * track.Width);

        Color accent = Enabled ? StudioPalette.Accent : Color.FromArgb(110, 110, 110);

        using (Pen rail = new(Color.FromArgb(70, ForeColor), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(rail, track.Left, mid, track.Right, mid);

        using (Pen filled = new(accent, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(filled, track.Left, mid, thumbX, mid);

        using (SolidBrush thumb = new(accent))
            g.FillEllipse(thumb, thumbX - 7, mid - 7, 14, 14);

        if (Focused)
        {
            using Pen ring = new(Color.FromArgb(120, accent), 1.5f);
            g.DrawEllipse(ring, thumbX - 10, mid - 10, 20, 20);
        }

        TextRenderer.DrawText(
            g, Format(current), Font,
            new Rectangle(Width - LabelWidth, 0, LabelWidth, Height),
            ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}
