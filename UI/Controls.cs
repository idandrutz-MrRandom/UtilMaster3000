using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// DOUBLE-BUFFERED PANEL
// Repaints fully on resize: without this the old border
// lines are left behind while the window tweens.
// ======================================================

internal class DoublePanel : Panel
{
    private int cornerRadius;

    public DoublePanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;

        // Lets BackColor = Transparent show the parent's background image.
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
    }

    public int CornerRadius
    {
        get => cornerRadius;
        set
        {
            int next = Math.Max(0, value);

            if (cornerRadius == next)
                return;

            cornerRadius = next;
            UpdateRegion();
            Invalidate();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateRegion();
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));

        if (diameter < 2)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void UpdateRegion()
    {
        Region? old = Region;
        int radius = Math.Min(
            cornerRadius,
            Math.Min(Width, Height) / 2);

        if (radius == 0 || Width <= 0 || Height <= 0)
        {
            Region = null;
            old?.Dispose();
            return;
        }

        using GraphicsPath path = RoundedPath(
            new Rectangle(0, 0, Width, Height),
            radius);

        Region = new Region(path);
        old?.Dispose();
    }
}

// ======================================================
// OUTLINED TITLE (drawn straight onto the see-through window)
// ======================================================

internal sealed class OutlinedLabel : Control
{
    public Color FillColor { get; set; } = Color.White;
    public Color OutlineColor { get; set; } = Color.Black;
    public Color KeyColor { get; set; } = Color.Black;

    public OutlinedLabel()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true
        );
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Solid key colour (not Transparent) so the form's
        // TransparencyKey punches straight through it.
        e.Graphics.Clear(KeyColor);

        if (Text.Length == 0)
            return;

        e.Graphics.SmoothingMode =
            System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using StringFormat format = new(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        float emSize = e.Graphics.DpiY * Font.SizeInPoints / 72f;

        using System.Drawing.Drawing2D.GraphicsPath path = new();

        path.AddString(
            Text,
            Font.FontFamily,
            (int)Font.Style,
            emSize,
            new RectangleF(0, 0, Width, Height),
            format
        );

        using Pen outline = new(OutlineColor, 4f)
        {
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round
        };

        using SolidBrush fill = new(FillColor);

        e.Graphics.DrawPath(outline, path);
        e.Graphics.FillPath(fill, path);
    }
}

// ======================================================
// LAUNCHER ROW
// ======================================================

internal sealed class LauncherRow : Control
{
    private static Color BackgroundColor => Theme.Background;

    private static Color NormalText => Theme.Text;

    private static Color NumberText => Theme.Number;

    private static Color ActiveBackground => Theme.ActiveRow;

    private string indexText;
    private string iconText;
    private string labelText;

    private Font indexFont;
    private Font iconFont;
    private Font labelFont;
    private float fontScale;
    private string fontFamilyName;
    private int fontVersion;
    private readonly bool useThemeFont;

    private bool active;

    // ---- hover reveal: long text / a picture unfolds under the row ----
    public static int BaseHeight => Theme.RowHeight;
    private static float RevealDelay => Theme.UnfoldDelaySeconds;

    private string? revealText;
    private Image? revealImage;
    private int revealTextHeight;
    private int extraHeight;
    private float reveal;
    private float activeFor;

    /// <summary>0 = folded, 1 = fully unfolded.</summary>
    public float RevealAmount => reveal;

    /// <summary>Height right now, between folded and unfolded.</summary>
    public int CurrentHeight =>
        BaseHeight + (int)Math.Round(extraHeight * reveal);

    /// <summary>
    /// Gives the row something to unfold on hover: wrapped text and / or a
    /// picture. Call after the row has its width.
    /// </summary>
    public void SetReveal(string? text, Image? image)
    {
        revealText = null;
        revealImage = null;
        extraHeight = 0;

        int width = Math.Max(40, Width - 78);

        const TextFormatFlags wrap =
            TextFormatFlags.Left | TextFormatFlags.Top |
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak |
            TextFormatFlags.TextBoxControl;

        if (!string.IsNullOrEmpty(text))
        {
            int line = TextRenderer.MeasureText(
                "Ag", labelFont, Size.Empty,
                TextFormatFlags.NoPadding).Height;

            int needed = TextRenderer.MeasureText(
                text, labelFont, new Size(width, 0), wrap).Height;

            int maxLines = 11;
            int lines = Math.Min(maxLines, (needed + line - 1) / line);

            // Fits on the single line already: nothing to unfold.
            if (lines > 1)
            {
                revealText = text;
                revealTextHeight = lines * line;
                extraHeight = Math.Max(0, 8 + revealTextHeight + 8 - BaseHeight);
            }
        }

        if (image != null)
        {
            revealImage = image;
            extraHeight = Math.Max(extraHeight, image.Height + 10);
        }
    }

    // 0 = idle look, 1 = fully highlighted (animated toward `active`).
    private float hover;

    // 0 = just created (faded out, shifted), 1 = settled.
    private float appear = 1f;
    private double appearDelay;

    public bool IsActive
    {
        get => active;
        set
        {
            if (active == value)
                return;

            active = value;
            Invalidate();
        }
    }

    private Image? iconImage;
    private string? customIconKey;

    /// <summary>Real app / file icon; drawn instead of the glyph when set.</summary>
    public Image? IconImage
    {
        get => iconImage;
        set
        {
            iconImage = value;
            Invalidate();
        }
    }

    public string? CustomIconKey
    {
        get => customIconKey;
        set
        {
            if (customIconKey == value)
                return;

            customIconKey = value;
            Invalidate();
        }
    }

    /// <summary>Jump straight to the current highlight state.</summary>
    public void SnapActive()
    {
        hover = active ? 1f : 0f;
        Invalidate();
    }

    /// <summary>
    /// A row that is reused for a new page: forget what the old content was
    /// doing (unfolded, highlighted) and slide in like a new row.
    /// </summary>
    public void Recycle(double delaySeconds)
    {
        reveal = 0f;
        activeFor = 0f;
        StartAppear(delaySeconds);
    }

    public void StartAppear(double delaySeconds)
    {
        appear = 0f;
        appearDelay = delaySeconds;
        Invalidate();
    }

    /// <summary>Advances animations; returns true while still moving.</summary>
    public bool Step(double dt) => Step(dt, dt);

    public bool Step(double dt, double realDt)
    {
        bool busy = false;

        if (appearDelay > 0)
        {
            appearDelay -= dt;
            busy = true;
        }
        else if (appear < 1f)
        {
            appear = Math.Min(1f, appear + (float)(dt / 0.18));
            busy = true;
        }

        float target = active ? 1f : 0f;

        if (hover != target)
        {
            float k = (float)(1 - Math.Exp(-dt / 0.05));
            hover += (target - hover) * k;

            if (Math.Abs(target - hover) < 0.01f)
                hover = target;

            busy = true;
        }

        float revealTarget = 0f;

        if (active)
        {
            // The wait before unfolding is real time: the animation speed setting
            // changes how fast things move, not how long you have to rest on a row.
            activeFor += (float)realDt;

            // A negative delay means: never unfold (the text still shows in full
            // in the status line / on click as before).
            if (extraHeight > 0 && RevealDelay >= 0f)
            {
                if (activeFor >= RevealDelay)
                    revealTarget = 1f;
                else
                    busy = true;
            }
        }
        else
        {
            activeFor = 0f;
        }

        if (reveal != revealTarget)
        {
            float k = (float)(1 - Math.Exp(-dt / 0.07));
            reveal += (revealTarget - reveal) * k;

            if (Math.Abs(revealTarget - reveal) < 0.01f)
                reveal = revealTarget;

            busy = true;
        }

        if (busy)
            Invalidate();

        return busy;
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t)
        );
    }

    // -1 = "Back" row, -2 = informational row without a number.
    private static string IndexText(int index) =>
        index >= 0 ? index.ToString()
        : index == -1 ? "←"
        : "";

    /// <summary>
    /// Shows another entry in this same row. Reusing rows (instead of
    /// destroying and rebuilding them) is what keeps updates flicker-free.
    /// </summary>
    public void Rebind(int index, string icon, string label)
    {
        string newIndex = IndexText(index);

        if (newIndex == indexText && icon == iconText && label == labelText && iconImage == null)
            return;

        indexText = newIndex;
        iconText = icon;
        labelText = label;
        iconImage = null;

        Invalidate();
    }

    public LauncherRow(
        int index,
        string icon,
        string label)
        : this(index, icon, label, "", useThemeFont: true)
    {
    }

    public LauncherRow(
        int index,
        string icon,
        string label,
        string monoFont)
        : this(index, icon, label, monoFont, useThemeFont: false)
    {
    }

    private LauncherRow(
        int index,
        string icon,
        string label,
        string monoFont,
        bool useThemeFont)
    {
        indexText = IndexText(index);
        iconText = icon;
        labelText = label;
        this.useThemeFont = useThemeFont;

        fontScale = useThemeFont ? Theme.MenuFontScale : 1f;
        fontFamilyName = useThemeFont ? Theme.MenuFontFamily : monoFont;
        fontVersion = Theme.FontVersion;

        // Menu rows share their fonts (building three per row made opening a
        // page slow); a row with its own font family keeps its own.
        indexFont = useThemeFont
            ? Theme.SharedMenuFont(8f * fontScale)
            : new Font(monoFont, 8f);
        iconFont = Theme.SharedIconFont(14f * fontScale);
        labelFont = useThemeFont
            ? Theme.SharedMenuFont(9f * fontScale)
            : new Font(monoFont, 9f);

        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true
        );

        // With a background image the rows are see-through so it shows.
        BackColor = Theme.HasBackground ? Color.Transparent : BackgroundColor;
        Cursor = Cursors.Hand;

    }

    public void ApplyAppearance()
    {
        if (useThemeFont &&
            (Math.Abs(fontScale - Theme.MenuFontScale) > 0.001f ||
             fontFamilyName != Theme.MenuFontFamily ||
             fontVersion != Theme.FontVersion))
        {
            // Shared fonts: nothing to dispose here.
            fontScale = Theme.MenuFontScale;
            fontFamilyName = Theme.MenuFontFamily;
            fontVersion = Theme.FontVersion;
            indexFont = Theme.SharedMenuFont(8f * fontScale);
            iconFont = Theme.SharedIconFont(14f * fontScale);
            labelFont = Theme.SharedMenuFont(9f * fontScale);
        }

        BackColor = Theme.HasBackground ? Color.Transparent : BackgroundColor;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Color background = Lerp(BackgroundColor, ActiveBackground, hover);

        // Ease-out cubic for the slide-in.
        float eased = 1f - (float)Math.Pow(1f - appear, 3);
        int shift = (int)((1f - eased) * 14f);

        Color foreground = Lerp(
            background,
            Lerp(NormalText, Theme.Strong, hover),
            eased
        );

        Color numberColor = Lerp(background, NumberText, eased);

        bool customBackground = Theme.DrawAsset(
            e.Graphics,
            "row",
            ClientRectangle);

        if (!customBackground && !Theme.HasBackground)
        {
            using SolidBrush bg = new(background);
            e.Graphics.FillRectangle(bg, ClientRectangle);
        }

        if (hover > 0.01f)
        {
            using SolidBrush tint = new(Color.FromArgb(
                (int)(200 * hover), ActiveBackground));

            e.Graphics.FillRectangle(tint, ClientRectangle);
        }

        TextRenderer.DrawText(
            e.Graphics,
            indexText,
            indexFont,
            new Rectangle(10 + shift, 0, 20, BaseHeight),
            numberColor,
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.Left |
            TextFormatFlags.NoPadding
        );

        Rectangle iconTarget = new(
            38 + shift, (BaseHeight - 22) / 2, 22, 22);
        bool customIcon = Theme.DrawButtonIcon(
            e.Graphics,
            customIconKey ?? "",
            iconTarget,
            eased,
            allowSharedIcon: iconImage == null);

        if (!customIcon && iconImage != null)
        {
            // Real app / file icon, faded in with the row.
            e.Graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            using System.Drawing.Imaging.ImageAttributes attributes = new();

            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix
            {
                Matrix33 = eased
            });

            e.Graphics.DrawImage(
                iconImage,
                iconTarget,
                0, 0, iconImage.Width, iconImage.Height,
                GraphicsUnit.Pixel,
                attributes
            );
        }
        else if (!customIcon && iconImage == null)
        {
            TextRenderer.DrawText(
                e.Graphics,
                iconText,
                iconFont,
                new Rectangle(38 + shift, 0, 24, BaseHeight),
                foreground,
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.Left |
                TextFormatFlags.NoPadding
            );
        }

        bool revealing = reveal > 0.02f;

        if (revealing && revealText != null)
        {
            // Wrapped in its final size; the growing row uncovers it.
            TextRenderer.DrawText(
                e.Graphics,
                revealText,
                labelFont,
                new Rectangle(70 + shift, 8, Width - 78, revealTextHeight),
                foreground,
                TextFormatFlags.Left |
                TextFormatFlags.Top |
                TextFormatFlags.NoPadding |
                TextFormatFlags.WordBreak |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.TextBoxControl
            );
        }
        else
        {
            TextRenderer.DrawText(
                e.Graphics,
                labelText,
                labelFont,
                new Rectangle(70 + shift, 0, Width - 78, BaseHeight),
                foreground,
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.Left |
                TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis
            );
        }

        if (revealing && revealImage != null)
        {
            e.Graphics.InterpolationMode =
                System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            using System.Drawing.Imaging.ImageAttributes fade = new();

            fade.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix
            {
                Matrix33 = Math.Clamp(reveal, 0f, 1f)
            });

            e.Graphics.DrawImage(
                revealImage,
                new Rectangle(
                    70 + shift, BaseHeight - 2,
                    revealImage.Width, revealImage.Height),
                0, 0, revealImage.Width, revealImage.Height,
                GraphicsUnit.Pixel,
                fade
            );
        }
    }

    protected override void Dispose(bool disposing)
    {
        // Only a row with its own font family owns its fonts.
        if (disposing && !useThemeFont)
        {
            indexFont.Dispose();
            labelFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

// ======================================================
// MODE NOTIFICATION
// ======================================================

internal sealed class ModeNotification : Form
{
    private static Color Background => Theme.Background;

    private static Color Accent => Theme.Accent;

    private static Color OnColor => Theme.On;

    private static Color OffColor => Theme.Off;

    private readonly Label indicator = new();
    private readonly Label modeText = new();

    private readonly System.Windows.Forms.Timer animationTimer = new();

    private int holdTicks;
    private bool fadingIn;

    private double notificationOpacity = 0.78;

    public double OpacitySetting
    {
        get => notificationOpacity;
        set => notificationOpacity = Math.Clamp(value, 0.2, 1.0);
    }

    public NotificationPosition Position { get; set; } =
        NotificationPosition.BottomCenter;

    public bool Muted { get; set; }

    /// <summary>What the pop-up calls the converter (TRANSLATOR, WEIRD TEXT).</summary>
    public string Label { get; set; } = "TRANSLATOR";

    /// <summary>The small utility toasts ("Copied...") can be switched off on their own.</summary>
    public bool MessagesEnabled { get; set; } = true;

    /// <summary>How long a toast stays, in milliseconds.</summary>
    public int HoldMs { get; set; } = 900;

    /// <summary>Size of the toast, percent (100 = normal).</summary>
    public int ScalePercent { get; set; } = 100;

    private const int HoldDurationTicks = 30;

    public ModeNotification()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;

        ShowInTaskbar = false;
        TopMost = true;

        BackColor = Background;
        ClientSize = new Size(190, 42);

        Opacity = notificationOpacity;

        indicator.AutoSize = false;
        indicator.Location = new Point(10, 8);
        indicator.Size = new Size(14, 26);
        indicator.Font = new Font("Consolas", 10f);
        indicator.TextAlign = ContentAlignment.MiddleCenter;
        indicator.BackColor = Color.Transparent;

        modeText.AutoSize = false;
        modeText.Location = new Point(28, 8);
        modeText.Size = new Size(152, 26);
        modeText.Font = new Font("Consolas", 9f);
        modeText.ForeColor = Theme.Strong;
        modeText.TextAlign = ContentAlignment.MiddleLeft;
        modeText.BackColor = Color.Transparent;

        Controls.Add(indicator);
        Controls.Add(modeText);

        animationTimer.Interval = 30;
        animationTimer.Tick += AnimationTimer_Tick;
    }

    public void CycleOpacity()
    {
        if (notificationOpacity < 0.70)
            notificationOpacity = 0.78;
        else if (notificationOpacity < 0.80)
            notificationOpacity = 0.88;
        else
            notificationOpacity = 0.68;
    }

    /// <summary>Re-applies the current theme colours.</summary>
    public void ApplyTheme()
    {
        BackColor = Background;
        modeText.ForeColor = Theme.Strong;
        Invalidate();
    }

    public void CyclePosition()
    {
        Position = (NotificationPosition)(
            ((int)Position + 1) %
            Enum.GetValues<NotificationPosition>().Length
        );
    }

    /// <summary>Shows the popup even when notifications are muted.</summary>
    public void Preview(bool enabled)
    {
        Display(enabled);
    }

    public void ShowStatus(bool enabled)
    {
        if (Muted)
            return;

        Display(enabled);
    }

    private void Display(bool enabled)
    {
        DisplayCore(
            enabled ? $"{Label}: ON" : $"{Label}: OFF",
            enabled ? OnColor : OffColor
        );
    }

    /// <summary>Generic toast used by the utilities (always shown).</summary>
    public void ShowMessage(string text, Color? dot = null)
    {
        if (!MessagesEnabled)
            return;

        DisplayCore(text, dot ?? Accent);
    }

    private void DisplayCore(string text, Color stateColor)
    {
        indicator.Text = "●";
        indicator.ForeColor = stateColor;

        modeText.Text = text;
        modeText.AutoEllipsis = true;

        // Size follows the setting (80% .. 200%).
        double scale = Math.Clamp(ScalePercent, 50, 300) / 100.0;

        if (Math.Abs(modeText.Font.Size - 9f * scale) > 0.2)
        {
            Font oldText = modeText.Font, oldDot = indicator.Font;
            modeText.Font = new Font("Consolas", (float)(9f * scale));
            indicator.Font = new Font("Consolas", (float)(10f * scale));
            oldText.Dispose();
            oldDot.Dispose();
        }

        int heightPx = (int)Math.Round(42 * scale);
        indicator.Location = new Point((int)(10 * scale), (heightPx - (int)(26 * scale)) / 2);
        indicator.Size = new Size((int)(14 * scale) + 2, (int)(26 * scale));
        modeText.Location = new Point((int)(28 * scale), (heightPx - (int)(26 * scale)) / 2);

        // Grow to fit longer messages.
        int textWidth = TextRenderer.MeasureText(text, modeText.Font).Width;
        int width = Math.Clamp(textWidth + (int)(50 * scale), (int)(190 * scale), (int)(520 * scale));

        ClientSize = new Size(width, heightPx);
        modeText.Size = new Size(width - (int)(38 * scale), (int)(26 * scale));

        // Fade in when freshly shown; just refresh if already visible.
        fadingIn = !Visible;
        Opacity = fadingIn ? 0 : notificationOpacity;

        Rectangle area = Screen.PrimaryScreen?.WorkingArea
            ?? Screen.GetBounds(this);

        const int margin = 24;

        int x = Position switch
        {
            NotificationPosition.BottomLeft or
            NotificationPosition.TopLeft => area.Left + margin,

            NotificationPosition.BottomRight or
            NotificationPosition.TopRight => area.Right - Width - margin,

            _ => area.Left + (area.Width - Width) / 2
        };

        int y = Position switch
        {
            NotificationPosition.TopCenter or
            NotificationPosition.TopLeft or
            NotificationPosition.TopRight => area.Top + margin,

            _ => area.Bottom - Height - 100
        };

        Location = new Point(x, y);

        holdTicks = Math.Max(1, HoldMs / animationTimer.Interval);

        animationTimer.Stop();

        if (!Visible)
            Show();

        animationTimer.Start();

        Invalidate();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (fadingIn)
        {
            Opacity = Math.Min(notificationOpacity, Opacity + 0.14);

            if (Opacity >= notificationOpacity)
                fadingIn = false;

            return;
        }

        if (holdTicks > 0)
        {
            holdTicks--;
            return;
        }

        Opacity -= 0.06;

        if (Opacity <= 0.15)
        {
            animationTimer.Stop();
            Hide();
        }
    }

    protected override bool ShowWithoutActivation => true;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr window, uint affinity);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // WDA_EXCLUDEFROMCAPTURE: toasts (e.g. "REC 0:12") never appear
        // inside screenshots or recordings.
        SetWindowDisplayAffinity(Handle, 0x11);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;

            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW

            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        using Pen border = new(Accent, 1f);

        e.Graphics.DrawRectangle(
            border,
            0,
            0,
            Width - 1,
            Height - 1
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animationTimer.Stop();
            animationTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
