using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// UTILTUBE'S POPUP MENUS
// The default Windows popup menu is a different program's look. These use UtilTube's colours (the
// launcher's by default): a flat dark panel, an accent line on the row under the mouse, small
// section headings, shortcut keys in a quiet colour on the right, accent check marks.
// ======================================================

internal sealed class TubeMenuRenderer : ToolStripProfessionalRenderer
{
    public TubeMenuRenderer() : base(new ProfessionalColorTable { UseSystemColors = false })
    {
        RoundedEdges = false;
    }

    private static Color Panel => TubeTheme.Mix(TubeTheme.Background, TubeTheme.Search, 0.55f);

    private static bool IsHeader(ToolStripItem item) => item.Tag as string == TubeMenu.HeaderTag;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using SolidBrush fill = new(Panel);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        Rectangle r = new(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);

        using Pen pen = new(TubeTheme.Mix(Panel, TubeTheme.Accent, 0.55f));
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // No separate margin column.
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        ToolStripItem item = e.Item;

        if (!item.Selected || !item.Enabled || IsHeader(item))
            return;

        Rectangle r = new(3, 1, item.Width - 6, item.Height - 2);

        using (SolidBrush hover = new(TubeTheme.Mix(Panel, TubeTheme.Accent, 0.2f)))
            e.Graphics.FillRectangle(hover, r);

        using SolidBrush bar = new(TubeTheme.Accent);
        e.Graphics.FillRectangle(bar, r.X, r.Y, 3, r.Height);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        ToolStripItem item = e.Item;
        bool shortcut = item is ToolStripMenuItem m && m.ShortcutKeyDisplayString == e.Text &&
                        !string.IsNullOrEmpty(e.Text);

        Color colour;
        Font font = e.TextFont ?? item.Font;
        TextFormatFlags flags = e.TextFormat | TextFormatFlags.NoPrefix;

        if (IsHeader(item))
        {
            colour = TubeTheme.Accent;
            font = TubeTheme.Font(8f, FontStyle.Bold);
        }
        else if (shortcut)
        {
            colour = TubeTheme.Mix(TubeTheme.Text, Panel, 0.2f);
            font = TubeTheme.Font(8.5f);
            flags = (flags & ~TextFormatFlags.HorizontalCenter & ~TextFormatFlags.Left) | TextFormatFlags.Right;
        }
        else
        {
            colour = item.Enabled
                ? (item.Selected ? Color.White.Equals(TubeTheme.Strong) ? Color.White : TubeTheme.Strong : TubeTheme.Strong)
                : TubeTheme.Mix(TubeTheme.Text, Panel, 0.45f);
        }

        Rectangle area = e.TextRectangle;

        if (IsHeader(item))
            area.X = 14;
        else if (!shortcut)
            area.X = Math.Max(area.X, 26);

        TextRenderer.DrawText(e.Graphics, e.Text, font, area, colour, flags);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Rectangle box = new(10, e.Item.Height / 2 - 4, 8, 8);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using SolidBrush dot = new(TubeTheme.Accent);
        e.Graphics.FillEllipse(dot, box);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;

        using Pen pen = new(TubeTheme.Mix(Panel, TubeTheme.Accent, 0.22f));
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        int cx = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2;
        int cy = e.ArrowRectangle.Y + e.ArrowRectangle.Height / 2;

        using Pen pen = new(e.Item.Enabled ? TubeTheme.Accent : TubeTheme.Text, 1.6f);
        e.Graphics.DrawLines(pen, new[] { new Point(cx - 2, cy - 4), new Point(cx + 2, cy), new Point(cx - 2, cy + 4) });
    }
}

internal static class TubeMenu
{
    public const string HeaderTag = "tube-header";

    private static readonly TubeMenuRenderer Renderer = new();

    /// <summary>Gives a popup menu (and its sub-menus) UtilTube's look.</summary>
    public static ContextMenuStrip Create()
    {
        ContextMenuStrip menu = new();
        Style(menu);
        return menu;
    }

    public static void Style(ToolStripDropDown menu)
    {
        menu.Renderer = Renderer;
        menu.BackColor = TubeTheme.Mix(TubeTheme.Background, TubeTheme.Search, 0.55f);
        menu.ForeColor = TubeTheme.Strong;
        menu.Font = TubeTheme.Font(9.5f);
        menu.Padding = new Padding(0, 4, 0, 4);

        if (menu is ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowImageMargin = false;
            dropDown.ShowCheckMargin = true;
            dropDown.MinimumSize = new Size(240, 0);
        }

        // Every item is styled as it is added (also sub-menus), so menus that are rebuilt each time they open work too.
        menu.ItemAdded += (_, e) => StyleItem(e.Item);

        foreach (ToolStripItem existing in menu.Items)
            StyleItem(existing);
    }

    private static void StyleItem(ToolStripItem item)
    {
        bool header = item.Tag as string == HeaderTag;

        item.Padding = header ? new Padding(2, 7, 2, 3) : new Padding(2, 5, 2, 5);
        item.Margin = Padding.Empty;

        if (item is ToolStripMenuItem sub)
        {
            Style(sub.DropDown);

            foreach (ToolStripItem inner in sub.DropDownItems)
                StyleItem(inner);
        }
    }

    /// <summary>A small, quiet section heading (not clickable).</summary>
    public static ToolStripLabel Header(string text) => new(text.ToUpperInvariant())
    {
        Tag = HeaderTag,
        Enabled = true,
        AutoSize = true
    };

    /// <summary>A menu row with a shortcut hint shown quietly on the right.</summary>
    public static ToolStripMenuItem Row(string text, string? shortcut, Action click, bool isChecked = false, bool enabled = true)
    {
        ToolStripMenuItem item = new(text)
        {
            ShortcutKeyDisplayString = shortcut,
            Checked = isChecked,
            Enabled = enabled
        };

        item.Click += (_, _) => click();
        return item;
    }
}

/// <summary>A small themed box asking for one line of text.</summary>
internal static class TubePrompt
{
    public static string? Ask(IWin32Window? owner, string title, string label, string initial = "")
    {
        using Form form = new()
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 132),
            BackColor = TubeTheme.Background,
            ForeColor = TubeTheme.Strong,
            Font = TubeTheme.Font(9.5f)
        };

        Label text = new() { Text = label, AutoSize = true, Location = new Point(18, 16), ForeColor = TubeTheme.Text };

        TextBox box = new()
        {
            Text = initial,
            Location = new Point(18, 42),
            Width = 344,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = TubeTheme.Search,
            ForeColor = TubeTheme.Strong
        };

        Button Make(string caption, int left, bool primary)
        {
            Button b = new()
            {
                Text = caption,
                Location = new Point(left, 86),
                Size = new Size(84, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? TubeTheme.Accent : TubeTheme.Search,
                ForeColor = primary ? TubeTheme.Background : TubeTheme.Strong,
                Cursor = Cursors.Hand,
                DialogResult = primary ? DialogResult.OK : DialogResult.Cancel
            };

            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        Button ok = Make("OK", 192, true);
        Button cancel = Make("Cancel", 278, false);

        form.Controls.AddRange(new Control[] { text, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };

        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0
            ? box.Text.Trim()
            : null;
    }
}

/// <summary>The "Add to playlist" choices for a video (used by the player and the video lists).</summary>
internal static class TubeListsMenu
{
    /// <summary>Fills a menu (or sub-menu) with "Watch later", your playlists with a tick on those holding the video, and "New playlist...".</summary>
    public static void Fill(ToolStripItemCollection items, TubeVideo video, Action<string> say, IWin32Window? owner, Action? changed = null)
    {
        TubeLibrary library = TubeLibrary.Instance;

        items.Add(TubeMenu.Row("Watch later", null, () =>
        {
            library.ToggleWatchLater(video);
            say(library.IsWatchLater(video.Id) ? "Added to Watch later" : "Removed from Watch later");
            changed?.Invoke();
        }, library.IsWatchLater(video.Id)));

        List<LocalPlaylist> lists = library.MyLists();

        if (lists.Count > 0)
            items.Add(new ToolStripSeparator());

        foreach (LocalPlaylist list in lists)
        {
            string id = list.Id, name = list.Name;

            items.Add(TubeMenu.Row($"{name}   ({list.Videos.Count})", null, () =>
            {
                bool now = library.ToggleInList(id, video);
                say(now ? $"Added to \"{name}\"" : $"Removed from \"{name}\"");
                changed?.Invoke();
            }, library.ListHas(id, video.Id)));
        }

        items.Add(new ToolStripSeparator());

        items.Add(TubeMenu.Row("New playlist...", null, () =>
        {
            string? name = TubePrompt.Ask(owner, "New playlist", "Name of the playlist:");

            if (name == null)
                return;

            LocalPlaylist made = library.CreateList(name);
            library.ToggleInList(made.Id, video);
            say($"Added to \"{made.Name}\"");
            changed?.Invoke();
        }));
    }

    /// <summary>A popup with the choices, shown at the given screen position.</summary>
    public static void Show(Point where, TubeVideo video, Action<string> say, IWin32Window? owner, Action? changed = null)
    {
        ContextMenuStrip menu = TubeMenu.Create();

        menu.Items.Add(TubeMenu.Header("Add to playlist"));
        Fill(menu.Items, video, say, owner, changed);

        menu.Show(where);
    }
}
