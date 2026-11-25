using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// THE HEADER OF UTILTUBE'S PLAYLISTS PAGE
// Two tabs: "My playlists" (the ones you make yourself, shown as chips you can click) and
// "From YouTube" (paste a link, or pick one from your account / ones loaded before). The actions
// for the list on screen (Play, Shuffle, Loop, Save all) sit on the right of the tab row.
// Drawn by hand in UtilTube's look, so it matches the rest of the window.
// ======================================================

internal sealed class PlaylistHeader : Panel
{
    public enum Tab { Mine, YouTube }

    private enum Look { Tab, Primary, Ghost, Chip, ChipActive, Menu }

    private sealed class Btn
    {
        public Rectangle Bounds;
        public string Text = "";
        public Look Look;
        public bool Enabled = true;
        public Action? Click;
    }

    public event Action<Tab>? ModeChanged;
    public event Action<string>? ListChosen;
    public event Action? NewListClicked, RenameClicked, DeleteClicked;
    public event Action? PlayClicked, ShuffleClicked, LoopClicked, SaveAllClicked;
    public event Action<string>? LoadLink;
    public event Action<SavedPlaylist>? YouTubeListChosen;
    public event Action? SignInClicked, RefreshClicked, AllPlaylistsClicked;

    private readonly TextBox link = new();
    private readonly List<Btn> buttons = new();
    private Btn? hover;

    private List<LocalPlaylist> lists = new();
    private string activeId = "";
    private List<SavedPlaylist> mine = new();
    private List<SavedPlaylist> recent = new();
    private bool signedIn;
    private string currentYtId = "";
    private string ytStatus = "";
    private bool ytError;
    private bool viewingList;
    private bool loop;
    private bool hasVideos;

    public Tab Mode { get; private set; } = Tab.Mine;

    public string LinkText
    {
        get => link.Text;
        set => link.Text = value;
    }

    public PlaylistHeader()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        Dock = DockStyle.Top;
        Height = 108;
        BackColor = TubeTheme.Background;

        link.BorderStyle = BorderStyle.FixedSingle;
        link.BackColor = TubeTheme.Search;
        link.ForeColor = TubeTheme.Strong;
        link.Font = TubeTheme.Font(9.5f);
        link.PlaceholderText = "Paste a playlist link or id...";
        link.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                LoadLink?.Invoke(link.Text);
            }
        };

        Controls.Add(link);
        Layout_();
    }

    // ---- state from the page ----

    public void SetMode(Tab mode)
    {
        Mode = mode;
        Layout_();
    }

    public void SetLists(List<LocalPlaylist> value, string active)
    {
        lists = value;
        activeId = active;
        Layout_();
    }

    public void SetYouTube(List<SavedPlaylist> account, bool isSignedIn, List<SavedPlaylist> loaded,
        string currentId = "", string status = "", bool error = false, bool viewing = false)
    {
        mine = account;
        signedIn = isSignedIn;
        recent = loaded;
        currentYtId = currentId;
        ytStatus = status;
        ytError = error;
        viewingList = viewing;
        Layout_();
    }

    public void SetLoop(bool on)
    {
        loop = on;
        Layout_();
    }

    public void SetHasVideos(bool any)
    {
        hasVideos = any;
        Layout_();
    }

    // ---- layout: every button is a rectangle that is both drawn and clicked ----

    private int Measure(string text, Look look)
    {
        Font font = FontFor(look);
        int pad = look is Look.Tab ? 0 : 30;

        return TextRenderer.MeasureText(text, font).Width + pad;
    }

    private static Font FontFor(Look look) =>
        look == Look.Tab ? TubeTheme.Font(11f, FontStyle.Bold) : TubeTheme.Font(9.5f, look == Look.Primary ? FontStyle.Bold : FontStyle.Regular);

    private Btn Add(Rectangle bounds, string text, Look look, Action? click, bool enabled = true)
    {
        Btn b = new() { Bounds = bounds, Text = text, Look = look, Click = click, Enabled = enabled };
        buttons.Add(b);
        return b;
    }

    private void Layout_()
    {
        buttons.Clear();
        hover = null;

        int width = Width;

        // ---- row 1: the tabs on the left, the actions on the right ----
        int x = 22;

        int mineWidth = Measure("My playlists", Look.Tab);
        Add(new Rectangle(x, 10, mineWidth, 34), "My playlists", Look.Tab, () => Switch(Tab.Mine)).Enabled = true;
        x += mineWidth + 28;

        int ytWidth = Measure("From YouTube", Look.Tab);
        Add(new Rectangle(x, 10, ytWidth, 34), "From YouTube", Look.Tab, () => Switch(Tab.YouTube)).Enabled = true;

        int right = width - 22;

        void Action_(string text, Look look, Action? click, bool enabled)
        {
            int w = Measure(text, look);
            right -= w;
            Add(new Rectangle(right, 10, w, 32), text, look, click, enabled);
            right -= 8;
        }

        // Right to left.
        Action_("Save all", Look.Ghost, () => SaveAllClicked?.Invoke(), hasVideos);
        Action_("Loop: " + (loop ? "On" : "Off"), loop ? Look.ChipActive : Look.Ghost, () => LoopClicked?.Invoke(), true);
        Action_("Shuffle", Look.Ghost, () => ShuffleClicked?.Invoke(), hasVideos);
        Action_("Play", Look.Primary, () => PlayClicked?.Invoke(), hasVideos);

        // ---- row 2 ----
        int y = 62;

        if (Mode == Tab.Mine)
        {
            link.Visible = false;

            // The right end: rename / delete for the list that is open.
            int end = width - 22;

            if (lists.Any(l => l.Id == activeId))
            {
                int dw = Measure("Delete", Look.Ghost);
                end -= dw;
                Add(new Rectangle(end, y, dw, 32), "Delete", Look.Ghost, () => DeleteClicked?.Invoke());
                end -= 8;

                int rw = Measure("Rename", Look.Ghost);
                end -= rw;
                Add(new Rectangle(end, y, rw, 32), "Rename", Look.Ghost, () => RenameClicked?.Invoke());
                end -= 16;
            }

            int cx = 22;

            int newWidth = Measure("+ New playlist", Look.Ghost);
            Add(new Rectangle(cx, y, newWidth, 32), "+ New playlist", Look.Primary, () => NewListClicked?.Invoke());
            cx += newWidth + 14;

            // The playlists as chips; the ones that do not fit go under "More".
            List<LocalPlaylist> rest = new();

            for (int i = 0; i < lists.Count; i++)
            {
                LocalPlaylist list = lists[i];
                string text = $"{Shorten(list.Name, 22)}   {list.Videos.Count}";
                int w = Measure(text, Look.Chip);
                int moreWidth = Measure("More...", Look.Menu);

                bool last = i == lists.Count - 1;

                if (cx + w + 8 > end - (last ? 0 : moreWidth + 8))
                {
                    rest = lists.Skip(i).ToList();
                    break;
                }

                string id = list.Id;
                Add(new Rectangle(cx, y, w, 32), text, list.Id == activeId ? Look.ChipActive : Look.Chip, () => ListChosen?.Invoke(id));
                cx += w + 8;
            }

            if (rest.Count > 0)
            {
                int mw = Measure("More...", Look.Menu);

                Add(new Rectangle(cx, y, mw, 32), "More...", Look.Menu, () =>
                {
                    ContextMenuStrip menu = TubeMenu.Create();
                    menu.Items.Add(TubeMenu.Header("More playlists"));

                    foreach (LocalPlaylist l in rest)
                    {
                        string id = l.Id;
                        menu.Items.Add(TubeMenu.Row($"{l.Name}   ({l.Videos.Count})", null, () => ListChosen?.Invoke(id), l.Id == activeId));
                    }

                    menu.Show(PointToScreen(new Point(cx, y + 34)));
                });
            }
        }
        else
        {
            int ex = 22;
            int end = width - 22;

            // The link box, Load and Paste on the left (the box has a modest fixed width) ...
            int lw = Measure("Load", Look.Primary);
            int pw = Measure("Paste", Look.Ghost);
            int boxWidth = Math.Clamp(width / 4, 200, 340);

            link.Visible = true;
            link.SetBounds(ex, y + 3, boxWidth, 26);

            int cx = ex + boxWidth + 8;
            Add(new Rectangle(cx, y, lw, 32), "Load", Look.Primary, () => LoadLink?.Invoke(link.Text));
            cx += lw + 8;
            Add(new Rectangle(cx, y, pw, 32), "Paste", Look.Ghost, PasteAndLoad);
            cx += pw + 22;

            // ... "Loaded before" at the right end, with a Refresh for the account list beside it ...
            int w3 = Measure("Loaded before", Look.Menu) + 14;
            end -= w3;
            Add(new Rectangle(end, y, w3, 32), "Loaded before", Look.Menu, ShowRecent);
            end -= 8;

            if (signedIn)
            {
                int rw = Measure("Refresh", Look.Ghost);
                end -= rw;
                Add(new Rectangle(end, y, rw, 32), "Refresh", Look.Ghost, () => RefreshClicked?.Invoke());
                end -= 14;
            }
            else
            {
                end -= 6;
            }

            // ... and, while you are inside one of your playlists, a way back to all of them (the cards).
            if (viewingList)
            {
                const string back = "<  All my playlists";
                Add(new Rectangle(cx, y, Measure(back, Look.Ghost), 32), back, Look.Ghost, () => AllPlaylistsClicked?.Invoke());
            }
        }

        Invalidate();
    }

    private void Switch(Tab tab)
    {
        if (Mode == tab)
        {
            // Clicking "From YouTube" again goes back to the cards.
            if (tab == Tab.YouTube)
                AllPlaylistsClicked?.Invoke();

            return;
        }

        Mode = tab;
        Layout_();
        ModeChanged?.Invoke(tab);
    }

    private void PasteAndLoad()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                link.Text = Clipboard.GetText().Trim();
                LoadLink?.Invoke(link.Text);
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy.
        }
    }

    private void ShowMine()
    {
        ContextMenuStrip menu = TubeMenu.Create();
        menu.Items.Add(TubeMenu.Header("Playlists on your account"));

        if (!signedIn)
        {
            menu.Items.Add(TubeMenu.Row("Sign in with Google to see them...", null, () => SignInClicked?.Invoke()));
        }
        else if (mine.Count == 0)
        {
            menu.Items.Add(TubeMenu.Row("Loading your playlists...", null, () => { }, false, false));
            menu.Items.Add(TubeMenu.Row("Refresh", null, () => RefreshClicked?.Invoke()));
        }
        else
        {
            foreach (SavedPlaylist p in mine.Take(40))
            {
                SavedPlaylist captured = p;
                menu.Items.Add(TubeMenu.Row(Shorten(p.Title, 44) + (p.Count > 0 ? $"   ({p.Count})" : ""), null,
                    () => YouTubeListChosen?.Invoke(captured)));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(TubeMenu.Row("Refresh", null, () => RefreshClicked?.Invoke()));
        }

        menu.Show(PointToScreen(new Point(buttons.First(b => b.Text == "Your playlists").Bounds.X, 96)));
    }

    private void ShowRecent()
    {
        ContextMenuStrip menu = TubeMenu.Create();
        menu.Items.Add(TubeMenu.Header("Loaded before"));

        if (recent.Count == 0)
        {
            menu.Items.Add(TubeMenu.Row("Nothing yet: paste a link and press Load", null, () => { }, false, false));
        }
        else
        {
            foreach (SavedPlaylist p in recent)
            {
                SavedPlaylist captured = p;
                menu.Items.Add(TubeMenu.Row(Shorten(p.Title, 44) + (p.Count > 0 ? $"   ({p.Count})" : ""), null,
                    () => YouTubeListChosen?.Invoke(captured)));
            }
        }

        menu.Show(PointToScreen(new Point(buttons.First(b => b.Text == "Loaded before").Bounds.X, 96)));
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "...";

    // ---- mouse ----

    private Btn? HitTest(Point p) => buttons.FirstOrDefault(b => b.Bounds.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        Btn? over = HitTest(e.Location);

        if (over != null && !over.Enabled)
            over = null;

        if (!ReferenceEquals(over, hover))
        {
            hover = over;
            Cursor = over != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (hover != null)
        {
            hover = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        Btn? hit = HitTest(e.Location);

        if (hit is { Enabled: true } && e.Button == MouseButtons.Left)
            hit.Click?.Invoke();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);

        if (Width > 0)
            Layout_();
    }

    // ---- drawing ----

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        TubeGlass.Paint(e.Graphics, this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color background = TubeTheme.Background;
        Color panel = TubeTheme.Search;
        Color accent = TubeTheme.Accent;

        // A line under the tab row, and under the whole header.
        using (Pen line = new(TubeTheme.Mix(background, accent, 0.22f)))
        {
            g.DrawLine(line, 22, 47, Width - 22, 47);
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }

        foreach (Btn b in buttons)
        {
            bool over = ReferenceEquals(b, hover);
            Rectangle r = b.Bounds;
            Font font = FontFor(b.Look);

            Color fill, border, text;
            bool tab = b.Look == Look.Tab;
            bool activeTab = tab && ((b.Text == "My playlists") == (Mode == Tab.Mine));

            switch (b.Look)
            {
                case Look.Tab:
                    fill = Color.Transparent;
                    border = Color.Transparent;
                    text = activeTab ? TubeTheme.Strong : (over ? TubeTheme.Strong : TubeTheme.Text);
                    break;

                case Look.Primary:
                    fill = over ? TubeTheme.Mix(accent, Color.White, 0.18f) : accent;
                    border = fill;
                    text = background;
                    break;

                case Look.ChipActive:
                    fill = TubeTheme.Mix(background, accent, 0.28f);
                    border = accent;
                    text = TubeTheme.Strong;
                    break;

                case Look.Chip:
                    fill = over ? TubeTheme.Mix(panel, accent, 0.16f) : panel;
                    border = TubeTheme.Mix(panel, accent, over ? 0.55f : 0.18f);
                    text = TubeTheme.Strong;
                    break;

                case Look.Menu:
                    fill = over ? TubeTheme.Mix(panel, accent, 0.16f) : panel;
                    border = TubeTheme.Mix(panel, accent, over ? 0.55f : 0.3f);
                    text = TubeTheme.Strong;
                    break;

                default: // Ghost
                    fill = over ? TubeTheme.Mix(panel, accent, 0.16f) : panel;
                    border = TubeTheme.Mix(panel, accent, over ? 0.55f : 0.18f);
                    text = TubeTheme.Strong;
                    break;
            }

            if (!b.Enabled)
            {
                fill = TubeTheme.Mix(background, panel, 0.5f);
                border = fill;
                text = TubeTheme.Mix(TubeTheme.Text, background, 0.5f);
            }

            if (!tab)
            {
                using SolidBrush back = new(fill);
                g.FillRectangle(back, r);

                using Pen outline = new(border);
                g.DrawRectangle(outline, r.X, r.Y, r.Width - 1, r.Height - 1);
            }

            TextRenderer.DrawText(g, b.Text, font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (tab && activeTab)
            {
                using SolidBrush bar = new(accent);
                g.FillRectangle(bar, r.X, 44, r.Width, 3);
            }

            // A chevron on the menu buttons.
            if (b.Look == Look.Menu && b.Text != "More...")
            {
                using Pen chevron = new(b.Enabled ? accent : text, 1.5f);
                int cx = r.Right - 14, cy = r.Y + r.Height / 2;
                g.DrawLines(chevron, new[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
            }
        }
    }
}
