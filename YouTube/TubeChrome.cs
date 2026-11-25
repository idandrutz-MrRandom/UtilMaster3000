using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// UTILTUBE'S WINDOW PARTS
// The system title bar and scroll bars are drawn by Windows in its own colours, which
// is what made UtilTube look like a different program from the menu. These are drawn
// in UtilTube's look (the launcher's by default).
// ======================================================

/// <summary>
/// The menu's background picture, behind UtilTube's panels, laid out over the whole window
/// the way the menu lays it out over itself. Only while UtilTube follows the launcher's look
/// (a look with colours of its own has no picture).
/// </summary>
internal static class TubeGlass
{
    public static bool Active => TubeTheme.FollowsLauncher && Theme.HasBackground;

    /// <summary>For flat things that sit on the picture: see-through while it shows.</summary>
    public static Color Surface => Active ? Color.Transparent : TubeTheme.Background;

    public static void Paint(Graphics g, Control control)
    {
        if (!Active)
            return;

        Form? form = control.FindForm();

        if (form == null || !form.IsHandleCreated)
            return;

        Point at = form.PointToClient(control.PointToScreen(Point.Empty));
        GraphicsState state = g.Save();

        g.TranslateTransform(-at.X, -at.Y);
        TubeSettings s = TubeLibrary.Instance.Settings;

        Theme.PaintBackground(g, new Rectangle(Point.Empty, form.ClientSize), form.ClientSize,
            s.PictureFit >= 0 && Enum.IsDefined(typeof(BackgroundFit), s.PictureFit) ? (BackgroundFit)s.PictureFit : null,
            s.PictureAlign is >= 0 and <= 8 ? s.PictureAlign : null,
            s.PictureStrength >= 5 ? Math.Min(s.PictureStrength, 100) / 100f : null,
            slot: 1);

        g.Restore(state);
    }
}

/// <summary>
/// The search field: a flat box with a thin border that lights up while it has the keyboard,
/// holding a borderless text box centred in it. (A text box with its own border ignores any
/// height it is given and takes the font's, which left it shorter than the button next to it.)
/// </summary>
internal sealed class SearchField : Panel
{
    private readonly TextBox box;

    public SearchField(TextBox box)
    {
        this.box = box;

        SetStyle(
            ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        box.BorderStyle = BorderStyle.None;
        box.GotFocus += (_, _) => Invalidate();
        box.LostFocus += (_, _) => Invalidate();
        Controls.Add(box);

        Click += (_, _) => box.Focus();
    }

    /// <summary>Puts the text box in the middle of the field.</summary>
    public void FitBox()
    {
        box.SetBounds(12, (Height - box.Height) / 2, Math.Max(10, Width - 24), box.Height);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        FitBox();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(TubeTheme.Search);

        using Pen border = new(
            TubeTheme.Mix(TubeTheme.Search, TubeTheme.Accent, box.Focused ? 0.85f : 0.3f), 1f);

        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>A panel that shows the menu's background picture (see <see cref="TubeGlass"/>).</summary>
internal class GlassPanel : Panel
{
    public GlassPanel()
    {
        SetStyle(
            ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        TubeGlass.Paint(e.Graphics, this);
    }
}

/// <summary>A title bar with minimise, maximise and close, in UtilTube's colours.</summary>
internal sealed class TubeTitleBar : Control
{
    private readonly Form form;
    private int hot = -1;            // 0 minimise, 1 maximise, 2 close

    public TubeTitleBar(Form form, string title)
    {
        this.form = form;

        Text = title;
        Dock = DockStyle.Top;
        Height = 34;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private Rectangle ButtonArea(int index) => new(Width - 46 * (3 - index), 0, 46, Height);

    private int HitButton(Point p)
    {
        for (int i = 0; i < 3; i++)
        {
            if (ButtonArea(i).Contains(p))
                return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int over = HitButton(e.Location);

        if (over != hot)
        {
            hot = over;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hot = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        switch (HitButton(e.Location))
        {
            case 0:
                form.WindowState = FormWindowState.Minimized;
                break;

            case 1:
                Toggle();
                break;

            case 2:
                form.Close();
                break;

            default:
                if (e.Clicks == 2)
                    Toggle();
                else
                    WindowDrag.Begin(form);
                break;
        }
    }

    private void Toggle() =>
        form.WindowState = form.WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        Color back = TubeTheme.Mix(TubeTheme.Background, TubeTheme.Accent, 0.12f);

        using (SolidBrush fill = new(back))
            g.FillRectangle(fill, ClientRectangle);

        using (Pen line = new(TubeTheme.Mix(TubeTheme.Background, TubeTheme.Accent, 0.32f), 1f))
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        using (Font font = TubeTheme.Font(9.5f, FontStyle.Bold))
        {
            TextRenderer.DrawText(
                g, Text, font, new Rectangle(14, 0, Width - 160, Height), TubeTheme.Strong,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 0; i < 3; i++)
        {
            Rectangle area = ButtonArea(i);

            if (hot == i)
            {
                using SolidBrush hover = new(i == 2
                    ? TubeTheme.Mix(back, TubeTheme.Off, 0.7f)
                    : TubeTheme.Mix(back, TubeTheme.Strong, 0.14f));

                g.FillRectangle(hover, area);
            }

            using Pen pen = new(hot == i && i == 2 ? Color.White : TubeTheme.Strong, 1.4f);
            int cx = area.Left + area.Width / 2, cy = Height / 2;

            switch (i)
            {
                case 0:
                    g.DrawLine(pen, cx - 5, cy + 3, cx + 5, cy + 3);
                    break;

                case 1:
                    if (form.WindowState == FormWindowState.Maximized)
                    {
                        g.DrawRectangle(pen, cx - 4, cy - 2, 7, 7);
                        g.DrawLines(pen, new[] { new Point(cx - 2, cy - 2), new Point(cx - 2, cy - 5), new Point(cx + 5, cy - 5), new Point(cx + 5, cy + 2), new Point(cx + 3, cy + 2) });
                    }
                    else
                    {
                        g.DrawRectangle(pen, cx - 5, cy - 5, 10, 9);
                    }

                    break;

                default:
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 4);
                    g.DrawLine(pen, cx - 5, cy + 4, cx + 5, cy - 5);
                    break;
            }
        }
    }
}

/// <summary>A scroll bar in UtilTube's colours; a drop-in for the parts of VScrollBar the grid uses.</summary>
internal sealed class TubeScrollBar : Control
{
    private int maximum = 100, large = 100, small = 60, value;
    private bool dragging, overThumb;
    private int grab;

    public event EventHandler? ValueChanged;

    public int Maximum { get => maximum; set { maximum = Math.Max(1, value); Reclamp(); } }
    public int LargeChange { get => large; set { large = Math.Max(1, value); Reclamp(); } }
    public int SmallChange { get => small; set => small = Math.Max(1, value); }

    private int Range => Math.Max(1, maximum - large);

    public int Value
    {
        get => value;
        set
        {
            int next = Math.Clamp(value, 0, Math.Max(0, maximum - large));

            if (next == this.value)
                return;

            this.value = next;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TubeScrollBar()
    {
        Width = 12;
        Dock = DockStyle.Right;

        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private void Reclamp()
    {
        int next = Math.Clamp(value, 0, Math.Max(0, maximum - large));

        if (next != value)
        {
            value = next;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        Invalidate();
    }

    private Rectangle Thumb
    {
        get
        {
            int height = Math.Min(Height, Math.Max(30, (int)((long)Height * large / maximum)));
            int top = (int)((long)(Height - height) * value / Range);
            return new Rectangle(2, top, Width - 4, height);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || maximum <= large)
            return;

        Rectangle thumb = Thumb;

        if (thumb.Contains(e.Location))
        {
            dragging = true;
            grab = e.Y - thumb.Top;
            Capture = true;
            return;
        }

        Value += e.Y < thumb.Top ? -large : large;
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
        Value = (int)((long)(e.Y - grab) * Range / travel);
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

        g.Clear(TubeTheme.Mix(TubeTheme.Background, Color.Black, 0.25f));

        if (maximum <= large)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = DoublePanel.RoundedPath(Thumb, 4);
        using SolidBrush fill = new(Color.FromArgb(
            dragging ? 230 : overThumb ? 200 : 140, TubeTheme.Accent));

        g.FillPath(fill, path);
    }
}
