using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// YOUR YOUTUBE PLAYLISTS AS CARDS (Playlists > From YouTube)
// Like the Playlists page on YouTube: a cover picture (the first video's) with a stacked look, a badge with
// the number of videos, the name, who can see it, when it changed and "View full playlist". The list comes
// from your account by itself; the details (counts, privacy) arrive a few at a time and fill in.
// ======================================================

internal sealed class PlaylistCards : Control
{
    public enum State { Cards, Loading, None, Error, SignIn }

    public event Action<SavedPlaylist>? Opened;
    public event Action? SignInClicked, RetryClicked;

    private const int Pad = 26, Gap = 24, MinCard = 270, MaxCard = 360;

    // Made once: a font made on every repaint would leak.
    private static readonly Font IconBig = new("Segoe MDL2 Assets", 26f);
    private static readonly Font IconMedium = new("Segoe MDL2 Assets", 11f);
    private static readonly Font IconSmall = new("Segoe MDL2 Assets", 8f);

    private List<SavedPlaylist> items = new();
    private State state = State.Loading;
    private string message = "";
    private int scroll;
    private int hover = -1;
    private bool hoverButton;

    private Rectangle buttonRect;

    public PlaylistCards()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        BackColor = TubeTheme.Background;
        Dock = DockStyle.Fill;
    }

    public void SetItems(List<SavedPlaylist> value, State newState, string text)
    {
        items = value;
        state = newState;
        message = text;
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        Invalidate();
    }

    public void ScrollToTop()
    {
        scroll = 0;
        Invalidate();
    }

    // ---- geometry ----

    private int Columns => Math.Max(1, (Width - 2 * Pad + Gap) / (MinCard + Gap));

    private int CardWidth => Math.Min(MaxCard, (Width - 2 * Pad - Gap * (Columns - 1)) / Columns);

    private int ThumbHeight => CardWidth * 9 / 16;

    private int CardHeight => ThumbHeight + 14 + 24 + 20 + 20 + 20 + 14;

    private int Rows => (items.Count + Columns - 1) / Columns;

    private int TotalHeight => Pad + Rows * (CardHeight + Gap) + 10;

    private int MaxScroll => Math.Max(0, TotalHeight - Height);

    private Rectangle CardRect(int index)
    {
        int col = index % Columns, row = index / Columns;

        return new Rectangle(
            Pad + col * (CardWidth + Gap),
            Pad + 8 + row * (CardHeight + Gap) - scroll,
            CardWidth, CardHeight);
    }

    private int HitCard(Point p)
    {
        for (int i = 0; i < items.Count; i++)
        {
            Rectangle r = CardRect(i);

            if (r.Bottom < 0 || r.Top > Height)
                continue;

            if (r.Contains(p))
                return i;
        }

        return -1;
    }

    // ---- mouse ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int over = state == State.Cards ? HitCard(e.Location) : -1;
        bool onButton = buttonRect != Rectangle.Empty && buttonRect.Contains(e.Location);

        if (over != hover || onButton != hoverButton)
        {
            hover = over;
            hoverButton = onButton;
            Cursor = over >= 0 || onButton ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        hoverButton = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * 110, 0, MaxScroll);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (e.Button != MouseButtons.Left)
            return;

        if (buttonRect != Rectangle.Empty && buttonRect.Contains(e.Location))
        {
            if (state == State.SignIn)
                SignInClicked?.Invoke();
            else if (state == State.Error)
                RetryClicked?.Invoke();

            return;
        }

        int i = state == State.Cards ? HitCard(e.Location) : -1;

        if (i >= 0)
            Opened?.Invoke(items[i]);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        scroll = Math.Clamp(scroll, 0, MaxScroll);
    }

    // ---- drawing ----

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        TubeGlass.Paint(e.Graphics, this);
    }

    private static GraphicsPath Round(Rectangle r, int radius)
    {
        GraphicsPath path = new();
        int d = radius * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    private static string AccessText(SavedPlaylist p) => p.Availability.ToLowerInvariant() switch
    {
        "private" => "Private  Playlist",
        "unlisted" => "Unlisted  Playlist",
        "public" => "Public  Playlist",
        _ => p.Id.Length <= 3 ? "Private  Playlist" : "Playlist"
    };

    private static string UpdatedText(DateTime? when)
    {
        if (when == null)
            return "";

        int days = (int)(DateTime.Today - when.Value.Date).TotalDays;

        return days <= 0 ? "Updated today"
            : days == 1 ? "Updated yesterday"
            : days < 7 ? $"Updated {days} days ago"
            : "Updated " + when.Value.ToString("d MMM yyyy");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        buttonRect = Rectangle.Empty;

        if (state != State.Cards || items.Count == 0)
        {
            DrawMessage(g);
            return;
        }

        for (int i = 0; i < items.Count; i++)
        {
            Rectangle card = CardRect(i);

            if (card.Bottom < -4 || card.Top > Height + 4)
                continue;

            DrawCard(g, items[i], card, i == hover);
        }

        // A thin scroll thumb.
        if (MaxScroll > 0)
        {
            int total = TotalHeight;
            int thumb = Math.Max(36, Height * Height / total);
            int y = (int)((Height - thumb) * (scroll / (double)MaxScroll));

            using SolidBrush brush = new(Color.FromArgb(120, TubeTheme.Accent));
            g.FillRectangle(brush, Width - 6, y, 3, thumb);
        }
    }

    private void DrawMessage(Graphics g)
    {
        string text = state switch
        {
            State.Loading => message.Length > 0 ? message : "Loading your playlists...",
            State.None => message.Length > 0 ? message : "There are no playlists on your account yet.",
            State.Error => message.Length > 0 ? message : "Could not load your playlists.",
            State.SignIn => "Sign in with Google to see your playlists here.\n\nYou sign in on Google's own page and stay signed in.",
            _ => message
        };

        Rectangle area = new(0, Height / 2 - 70, Width, 90);

        TextRenderer.DrawText(g, text, TubeTheme.Font(11.5f), area, TubeTheme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);

        string? caption = state == State.SignIn ? "Sign in with Google" : state == State.Error ? "Try again" : null;

        if (caption == null)
            return;

        buttonRect = new Rectangle((Width - 220) / 2, Height / 2 + 36, 220, 44);

        using SolidBrush fill = new(hoverButton ? TubeTheme.Mix(TubeTheme.Accent, Color.White, 0.18f) : TubeTheme.Accent);
        g.FillRectangle(fill, buttonRect);

        TextRenderer.DrawText(g, caption, TubeTheme.Font(11f, FontStyle.Bold), buttonRect, TubeTheme.Background,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawCard(Graphics g, SavedPlaylist p, Rectangle card, bool hot)
    {
        Color back = TubeTheme.Background;
        Rectangle thumb = new(card.X, card.Y, card.Width, ThumbHeight);

        // The stacked look: two thin lines peeking out above the cover.
        foreach ((int inset, int rise, float mix) in new[] { (16, 9, 0.2f), (8, 4, 0.34f) })
        {
            Rectangle strip = new(thumb.X + inset, thumb.Y - rise, thumb.Width - 2 * inset, 4);

            using GraphicsPath path = Round(strip, 2);
            using SolidBrush brush = new(TubeTheme.Mix(back, TubeTheme.Text, mix));
            g.FillPath(brush, path);
        }

        // The cover.
        using (GraphicsPath cover = Round(thumb, 9))
        {
            GraphicsState state0 = g.Save();
            g.SetClip(cover, CombineMode.Intersect);

            Bitmap? picture = p.ThumbId.Length > 0 ? ThumbCache.TryGet(p.ThumbId) : null;

            if (picture != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;

                // Fill the cover, cropping the sides like YouTube does.
                float scale = Math.Max(thumb.Width / (float)picture.Width, thumb.Height / (float)picture.Height);
                float w = picture.Width * scale, h = picture.Height * scale;

                g.DrawImage(picture, thumb.X + (thumb.Width - w) / 2, thumb.Y + (thumb.Height - h) / 2, w, h);
            }
            else
            {
                using SolidBrush empty = new(TubeTheme.Mix(back, TubeTheme.Search, 0.7f));
                g.FillRectangle(empty, thumb);

                TextRenderer.DrawText(g, "", IconBig, thumb,
                    TubeTheme.Mix(TubeTheme.Search, TubeTheme.Text, 0.4f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (hot)
            {
                using SolidBrush shade = new(Color.FromArgb(120, 0, 0, 0));
                g.FillRectangle(shade, thumb);

                TextRenderer.DrawText(g, "   Open playlist", IconMedium, thumb, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            g.Restore(state0);
        }

        // The badge: how many videos.
        string count = p.DetailsKnown ? (p.Count == 0 ? "No videos" : $"{p.Count:N0} videos") : "";

        if (count.Length > 0)
        {
            Font badgeFont = TubeTheme.Font(8.5f, FontStyle.Bold);
            Size size = TextRenderer.MeasureText(count, badgeFont);
            Rectangle badge = new(thumb.Right - size.Width - 34, thumb.Bottom - 28, size.Width + 28, 20);

            using GraphicsPath path = Round(badge, 4);
            using SolidBrush fill = new(Color.FromArgb(200, 0, 0, 0));
            g.FillPath(fill, path);

            TextRenderer.DrawText(g, "", IconSmall, new Rectangle(badge.X + 5, badge.Y, 18, badge.Height),
                Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(g, count, badgeFont, new Rectangle(badge.X + 22, badge.Y, size.Width + 6, badge.Height),
                Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // The text under the cover.
        int y = thumb.Bottom + 12;

        TextRenderer.DrawText(g, p.Title, TubeTheme.Font(10.5f, FontStyle.Bold), new Rectangle(card.X, y, card.Width, 24),
            TubeTheme.Strong, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        y += 25;

        Font small = TubeTheme.Font(9f);
        Color muted = TubeTheme.Text;

        TextRenderer.DrawText(g, AccessText(p), small, new Rectangle(card.X, y, card.Width, 20), muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        y += 20;

        string updated = UpdatedText(p.Modified);

        if (updated.Length > 0)
        {
            TextRenderer.DrawText(g, updated, small, new Rectangle(card.X, y, card.Width, 20), muted,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        y += 20;

        TextRenderer.DrawText(g, "View full playlist", TubeTheme.Font(9f, hot ? FontStyle.Bold : FontStyle.Regular),
            new Rectangle(card.X, y, card.Width, 20), hot ? TubeTheme.Accent : TubeTheme.Mix(muted, TubeTheme.Strong, 0.35f),
            TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}
