using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// SUBTITLE PICKER (player: "..." > Subtitles, or the C key's neighbour)
// Opens at once, looks the tracks up by itself and shows what it is doing, groups the tracks (written by
// the uploader / automatic), lets you search when there are many and change the text size.
// ======================================================

internal sealed class SubtitlePopup : Form
{
    private enum RowKind { Off, Heading, Track }

    private sealed record Row(RowKind Kind, string Text, SubtitleTrack? Track = null);

    public event Action<SubtitleTrack>? Chosen;
    public event Action? TurnedOff;
    public event Action<int>? ScaleChanged;

    private readonly TextBox search = new();
    private readonly Label title = new();
    private readonly Label state = new();
    private readonly ListSurface list = new();
    private readonly Label sizeLabel = new();
    private readonly System.Windows.Forms.Timer spinner = new() { Interval = 260 };

    private List<SubtitleTrack> tracks = new();
    private string? current;
    private string message = "";
    private bool busy;
    private int dots;

    public int Scale { get; set; } = 100;

    /// <summary>Tests turn this off so the popup stays open without being the active window.</summary>
    public bool CloseOnDeactivate { get; set; } = true;

    public SubtitlePopup(string? currentLabel, int scale)
    {
        current = currentLabel;
        Scale = scale;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = TubeTheme.Mix(TubeTheme.Background, TubeTheme.Search, 0.55f);
        ForeColor = TubeTheme.Strong;
        Font = TubeTheme.Font(9.5f);
        Padding = new Padding(1);
        DoubleBuffered = true;
        KeyPreview = true;
        Size = new Size(340, 430);

        title.Text = "Subtitles";
        title.Font = TubeTheme.Font(11f, FontStyle.Bold);
        title.ForeColor = TubeTheme.Strong;
        title.AutoSize = true;
        title.Location = new Point(16, 14);

        state.AutoSize = false;
        state.TextAlign = ContentAlignment.MiddleRight;
        state.Font = TubeTheme.Font(8.5f);
        state.ForeColor = TubeTheme.Text;
        state.SetBounds(150, 14, 174, 20);

        search.BorderStyle = BorderStyle.FixedSingle;
        search.BackColor = TubeTheme.Search;
        search.ForeColor = TubeTheme.Strong;
        search.PlaceholderText = "Search languages...";
        search.SetBounds(16, 46, 308, 26);
        search.TextChanged += (_, _) => Rebuild();
        search.Visible = false;

        list.Owner = this;
        list.SetBounds(8, 80, 324, 290);
        list.Chosen += row =>
        {
            if (row.Kind == RowKind.Off)
                TurnedOff?.Invoke();
            else if (row.Track != null)
                Chosen?.Invoke(row.Track);
        };

        // The footer: text size.
        Label sizeCaption = new()
        {
            Text = "Text size",
            AutoSize = true,
            ForeColor = TubeTheme.Text,
            Location = new Point(16, 387)
        };

        Button Make(string text, int left, int delta)
        {
            Button b = new()
            {
                Text = text,
                Location = new Point(left, 380),
                Size = new Size(34, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = TubeTheme.Search,
                ForeColor = TubeTheme.Strong,
                Cursor = Cursors.Hand,
                TabStop = false
            };

            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = TubeTheme.ActiveRow;
            b.Click += (_, _) => ChangeScale(delta);
            return b;
        }

        Button smaller = Make("A-", 190, -10);
        Button larger = Make("A+", 290, 10);

        sizeLabel.AutoSize = false;
        sizeLabel.TextAlign = ContentAlignment.MiddleCenter;
        sizeLabel.SetBounds(226, 380, 62, 30);
        sizeLabel.Font = TubeTheme.Font(9f, FontStyle.Bold);
        sizeLabel.ForeColor = TubeTheme.Accent;
        UpdateSizeLabel();

        Controls.AddRange(new Control[] { title, state, search, list, sizeCaption, smaller, sizeLabel, larger });

        spinner.Tick += (_, _) =>
        {
            dots = (dots + 1) % 4;
            list.Invalidate();
        };

        Deactivate += (_, _) =>
        {
            if (CloseOnDeactivate && !IsDisposed && Visible)
                Close();
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                Close();
            }
        };

        Paint += (_, e) =>
        {
            using Pen border = new(TubeTheme.Mix(BackColor, TubeTheme.Accent, 0.55f));
            e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            using Pen line = new(TubeTheme.Mix(BackColor, TubeTheme.Accent, 0.2f));
            e.Graphics.DrawLine(line, 1, 372, Width - 2, 372);
        };
    }

    protected override bool ShowWithoutActivation => false;

    /// <summary>Shown above the given screen position, kept inside the screen.</summary>
    public void ShowAbove(Form owner, Point anchor)
    {
        Rectangle area = Screen.FromPoint(anchor).WorkingArea;

        int x = Math.Clamp(anchor.X - Width + 40, area.Left + 8, area.Right - Width - 8);
        int y = Math.Clamp(anchor.Y - Height - 8, area.Top + 8, area.Bottom - Height - 8);

        Location = new Point(x, y);
        Show(owner);
        Activate();

        Rebuild();
    }

    // ---- states ----

    public void SetLoading(string text)
    {
        busy = true;
        message = text;
        tracks = new List<SubtitleTrack>();
        state.Text = "";
        spinner.Start();
        Rebuild();
    }

    public void SetMessage(string text)
    {
        busy = false;
        message = text;
        tracks = new List<SubtitleTrack>();
        spinner.Stop();
        state.Text = "";
        Rebuild();
    }

    public void SetTracks(List<SubtitleTrack> found, string? currentLabel)
    {
        busy = false;
        message = "";
        tracks = found;
        current = currentLabel;
        spinner.Stop();

        state.Text = found.Count == 1 ? "1 track" : found.Count + " tracks";
        search.Visible = found.Count > 8;
        Rebuild();
    }

    private void ChangeScale(int delta)
    {
        Scale = Math.Clamp(Scale + delta, 50, 250);
        UpdateSizeLabel();
        ScaleChanged?.Invoke(Scale);
    }

    private void UpdateSizeLabel() => sizeLabel.Text = Scale + "%";

    // ---- the rows ----

    private void Rebuild()
    {
        List<Row> rows = new() { new Row(RowKind.Off, "Off") };

        string filter = search.Visible ? search.Text.Trim() : "";

        IEnumerable<SubtitleTrack> shown = tracks.Where(t =>
            filter.Length == 0 ||
            t.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
            t.Language.Contains(filter, StringComparison.OrdinalIgnoreCase));

        List<SubtitleTrack> written = shown.Where(t => !t.Auto).ToList();
        List<SubtitleTrack> auto = shown.Where(t => t.Auto).ToList();

        if (written.Count > 0)
        {
            rows.Add(new Row(RowKind.Heading, "Written by the uploader"));
            rows.AddRange(written.Select(t => new Row(RowKind.Track, t.Name, t)));
        }

        if (auto.Count > 0)
        {
            rows.Add(new Row(RowKind.Heading, "Automatic"));
            rows.AddRange(auto.Select(t => new Row(RowKind.Track, t.Name, t)));
        }

        list.SetRows(rows);
    }

    // ---- the list: drawn by hand so it matches UtilTube and scrolls smoothly ----

    private sealed class ListSurface : Control
    {
        public SubtitlePopup Owner = null!;
        public event Action<Row>? Chosen;

        private List<Row> rows = new();
        private int scroll;
        private int hover = -1;

        public ListSurface()
        {
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Cursor = Cursors.Default;
        }

        private static int RowHeight(Row r) => r.Kind == RowKind.Heading ? 30 : 32;

        private int TotalHeight => rows.Sum(RowHeight) + 6;

        public void SetRows(List<Row> value)
        {
            rows = value;
            scroll = Math.Clamp(scroll, 0, Math.Max(0, TotalHeight - Height));
            Invalidate();
        }

        private int RowAt(int y)
        {
            int top = -scroll + 3;

            for (int i = 0; i < rows.Count; i++)
            {
                int h = RowHeight(rows[i]);

                if (y >= top && y < top + h)
                    return i;

                top += h;
            }

            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int i = RowAt(e.Y);

            if (i >= 0 && rows[i].Kind == RowKind.Heading)
                i = -1;

            if (i != hover)
            {
                hover = i;
                Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            scroll = Math.Clamp(scroll - Math.Sign(e.Delta) * 64, 0, Math.Max(0, TotalHeight - Height));
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);

            int i = RowAt(e.Y);

            if (i >= 0 && rows[i].Kind != RowKind.Heading)
                Chosen?.Invoke(rows[i]);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Owner.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int top = -scroll + 3;

            for (int i = 0; i < rows.Count; i++)
            {
                Row row = rows[i];
                int h = RowHeight(row);
                Rectangle r = new(4, top, Width - 8, h);
                top += h;

                if (r.Bottom < 0 || r.Top > Height)
                    continue;

                if (row.Kind == RowKind.Heading)
                {
                    TextRenderer.DrawText(g, row.Text.ToUpperInvariant(), TubeTheme.Font(8f, FontStyle.Bold),
                        new Rectangle(r.X + 12, r.Y + 8, r.Width - 12, r.Height - 8), TubeTheme.Accent,
                        TextFormatFlags.Left | TextFormatFlags.NoPadding);
                    continue;
                }

                bool on = row.Kind == RowKind.Off
                    ? Owner.current == null
                    : Owner.current != null && row.Text == Owner.current;

                if (i == hover)
                {
                    using SolidBrush back = new(TubeTheme.Mix(Owner.BackColor, TubeTheme.Accent, 0.2f));
                    g.FillRectangle(back, r);

                    using SolidBrush bar = new(TubeTheme.Accent);
                    g.FillRectangle(bar, r.X, r.Y, 3, r.Height);
                }

                if (on)
                {
                    using SolidBrush dot = new(TubeTheme.Accent);
                    g.FillEllipse(dot, r.X + 12, r.Y + r.Height / 2 - 4, 8, 8);
                }

                TextRenderer.DrawText(g, row.Text, TubeTheme.Font(9.5f, on ? FontStyle.Bold : FontStyle.Regular),
                    new Rectangle(r.X + 30, r.Y, r.Width - 38, r.Height), on ? TubeTheme.Strong : TubeTheme.Strong,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }

            // A message under "Off": looking up / nothing found.
            if (Owner.message.Length > 0)
            {
                string text = Owner.message + (Owner.busy ? new string('.', Owner.dots) : "");

                TextRenderer.DrawText(g, text, TubeTheme.Font(9.5f),
                    new Rectangle(16, top + 14, Width - 32, 60), TubeTheme.Text,
                    TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }

            // A thin scroll thumb.
            int total = TotalHeight;

            if (total > Height)
            {
                int thumb = Math.Max(30, Height * Height / total);
                int y = (int)((Height - thumb) * (scroll / (double)(total - Height)));

                using SolidBrush brush = new(Color.FromArgb(140, TubeTheme.Accent));
                g.FillRectangle(brush, Width - 5, y, 3, thumb);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            spinner.Dispose();

        base.Dispose(disposing);
    }
}
