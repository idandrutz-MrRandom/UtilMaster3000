using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// RE-ORDER THE MENU  (Setup -> Re-order the main menu)
// A small window with the rows of the home menu. Grab a row, pull it up or down and
// let go: the other rows slide out of the way, and the menu has the new order at once.
// ======================================================

/// <summary>One row of the list: what it is called, its symbol, its text.</summary>
internal sealed record ReorderItem(string Key, string Glyph, string Text);

/// <summary>A list whose rows are re-ordered by dragging them.</summary>
internal sealed class ReorderList : Control
{
    private const int RowHeight = 38;

    private List<ReorderItem> items = new();
    private float[] shown = Array.Empty<float>();     // where each row is drawn (content pixels)

    private bool pressed, dragging;
    private int dragIndex = -1, slot = -1, hover = -1;
    private int grabOffset;
    private float dragY;
    private Point downAt;
    private int scroll;
    private int lastMouseY;

    private readonly System.Windows.Forms.Timer glide = new() { Interval = 15 };
    private readonly Font textFont = new("Segoe UI", 10f);
    private readonly Font glyphFont = new("Segoe MDL2 Assets", 12f);
    private readonly Font numberFont = new("Segoe UI", 8.5f);

    /// <summary>Raised after a row was dropped in a new place: the new order (by key).</summary>
    public event Action<List<string>>? Reordered;

    public ReorderList()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        glide.Tick += (_, _) => Glide();
    }

    public IReadOnlyList<ReorderItem> Items => items;

    /// <summary>Replaces the rows (and puts them straight in place).</summary>
    public void SetItems(IEnumerable<ReorderItem> list)
    {
        items = list.ToList();
        shown = Enumerable.Range(0, items.Count).Select(i => (float)(i * RowHeight)).ToArray();
        dragging = pressed = false;
        dragIndex = slot = -1;
        ClampScroll();
        Invalidate();
    }

    /// <summary>The height that shows every row.</summary>
    public int FullHeight => items.Count * RowHeight + 8;

    private int MaxScroll => Math.Max(0, items.Count * RowHeight + 8 - Height);

    private void ClampScroll() => scroll = Math.Clamp(scroll, 0, MaxScroll);

    // ---- where everything is ----

    // The row order as it will be if the dragged row is dropped now.
    private List<int> VirtualOrder()
    {
        List<int> order = Enumerable.Range(0, items.Count).ToList();

        if (dragging && dragIndex >= 0)
        {
            order.Remove(dragIndex);
            order.Insert(Math.Clamp(slot, 0, order.Count), dragIndex);
        }

        return order;
    }

    private float TargetY(int item, List<int> order) => order.IndexOf(item) * RowHeight;

    private void Glide()
    {
        List<int> order = VirtualOrder();
        bool moving = false;

        for (int i = 0; i < items.Count; i++)
        {
            if (dragging && i == dragIndex)
                continue;

            float target = TargetY(i, order);
            float diff = target - shown[i];

            if (Math.Abs(diff) < 0.6f)
            {
                shown[i] = target;
            }
            else
            {
                shown[i] += diff * 0.35f;
                moving = true;
            }
        }

        // Dragging near the top or bottom edge scrolls the list.
        if (dragging)
        {
            if (lastMouseY < 28 && scroll > 0)
                scroll = Math.Max(0, scroll - 9);
            else if (lastMouseY > Height - 28 && scroll < MaxScroll)
                scroll = Math.Min(MaxScroll, scroll + 9);

            UpdateDrag();
            moving = true;
        }

        Invalidate();

        if (!moving)
            glide.Stop();
    }

    private void UpdateDrag()
    {
        float maxY = (items.Count - 1) * RowHeight;
        dragY = Math.Clamp(lastMouseY + scroll - grabOffset, 0, maxY);
        slot = Math.Clamp((int)((dragY + RowHeight / 2f) / RowHeight), 0, items.Count - 1);
    }

    // ---- the mouse ----

    private int RowAt(int y)
    {
        int row = (y + scroll) / RowHeight;
        return y + scroll >= 0 && row >= 0 && row < items.Count ? row : -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        int row = RowAt(e.Y);

        if (row < 0)
            return;

        pressed = true;
        dragIndex = row;
        downAt = e.Location;
        lastMouseY = e.Y;
        grabOffset = e.Y + scroll - row * RowHeight;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        lastMouseY = e.Y;

        if (pressed && !dragging &&
            Math.Abs(e.Y - downAt.Y) + Math.Abs(e.X - downAt.X) > 4)
        {
            dragging = true;
            Capture = true;
            Cursor = Cursors.SizeNS;
            slot = dragIndex;
            UpdateDrag();
            glide.Start();
        }

        if (dragging)
        {
            UpdateDrag();
            Invalidate();
            return;
        }

        int over = RowAt(e.Y);

        if (over != hover)
        {
            hover = over;
            Cursor = over >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left)
            return;

        bool wasDragging = dragging;

        if (wasDragging)
        {
            // The dragged row is dropped where it was let go; it slides the last bit.
            ReorderItem moved = items[dragIndex];
            float from = dragY;
            int to = Math.Clamp(slot, 0, items.Count - 1);

            items.RemoveAt(dragIndex);
            items.Insert(to, moved);

            // The other rows keep where they are drawn: re-index them to the new order.
            List<float> ys = shown.ToList();
            ys.RemoveAt(dragIndex);
            ys.Insert(to, from);
            shown = ys.ToArray();

            Reordered?.Invoke(items.Select(i => i.Key).ToList());
        }

        pressed = dragging = false;
        dragIndex = slot = -1;
        Capture = false;
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;

        if (wasDragging)
            glide.Start();

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (!dragging && hover != -1)
        {
            hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Clamp(scroll - e.Delta / 120 * 60, 0, MaxScroll);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(StudioPalette.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        List<int> order = VirtualOrder();

        // The row being dragged is drawn last, on top.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < items.Count; i++)
            {
                bool isDragged = dragging && i == dragIndex;

                if ((pass == 0) == isDragged)
                    continue;

                float y = isDragged ? dragY : shown[i];
                Rectangle row = new(8, (int)Math.Round(y) - scroll + 4, Width - 16, RowHeight - 4);

                if (row.Bottom < 0 || row.Top > Height)
                    continue;

                int position = order.IndexOf(i);
                DrawRow(g, items[i], row, position, isDragged, !dragging && i == hover);
            }
        }
    }

    private void DrawRow(Graphics g, ReorderItem item, Rectangle r, int position, bool dragged, bool hot)
    {
        Color back = StudioPalette.Back;

        if (dragged)
        {
            // A soft shadow under the row that is being carried.
            using GraphicsPath shadow = DoublePanel.RoundedPath(new Rectangle(r.X + 2, r.Y + 4, r.Width, r.Height), 8);
            using SolidBrush shade = new(Color.FromArgb(70, 0, 0, 0));
            g.FillPath(shade, shadow);
        }

        using GraphicsPath path = DoublePanel.RoundedPath(r, 8);

        using (SolidBrush fill = new(dragged
            ? Theme.Mix(back, StudioPalette.Accent, 0.28f)
            : Theme.Mix(back, StudioPalette.Strong, hot ? 0.13f : 0.06f)))
        {
            g.FillPath(fill, path);
        }

        if (dragged)
        {
            using Pen edge = new(StudioPalette.Accent, 1.5f);
            g.DrawPath(edge, path);
        }

        // The grip, the row's own symbol and its name.
        Color dim = Theme.Mix(StudioPalette.Text, back, 0.45f);

        TextRenderer.DrawText(g, "", glyphFont, new Rectangle(r.X + 6, r.Y, 30, r.Height), dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, item.Glyph, glyphFont, new Rectangle(r.X + 40, r.Y, 30, r.Height),
            dragged ? StudioPalette.Strong : StudioPalette.Accent,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, item.Text, textFont, new Rectangle(r.X + 78, r.Y, r.Width - 130, r.Height),
            StudioPalette.Strong,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        // Where it sits in the menu now.
        TextRenderer.DrawText(g, (position + 1).ToString(), numberFont,
            new Rectangle(r.Right - 44, r.Y, 36, r.Height), dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            glide.Dispose();
            textFont.Dispose();
            glyphFont.Dispose();
            numberFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The window around the list.</summary>
internal sealed class MenuOrderWindow : Form
{
    private readonly ReorderList list = new() { Dock = DockStyle.Fill };
    private readonly Func<List<ReorderItem>> current;

    public MenuOrderWindow(
        Form owner,
        Func<List<ReorderItem>> current,
        Action<List<string>> changed,
        Action reset)
    {
        this.current = current;

        Text = "Re-order the menu";
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        ShowInTaskbar = false;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9f);
        BackColor = Theme.Mix(StudioPalette.Back, StudioPalette.Accent, 0.4f);
        StartPosition = FormStartPosition.Manual;

        list.SetItems(current());
        list.Reordered += keys => changed(keys);

        Panel intro = new()
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = StudioPalette.Back
        };

        intro.Paint += (_, e) =>
        {
            TextRenderer.DrawText(
                e.Graphics, "Drag a row up or down. The menu follows as you let go.",
                Font, new Rectangle(14, 0, intro.Width - 28, intro.Height),
                Theme.Mix(StudioPalette.Text, StudioPalette.Back, 0.15f),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        };

        Panel bottom = new()
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            BackColor = StudioPalette.Back,
            Padding = new Padding(10, 10, 10, 10)
        };

        Button done = FlatButton("Done", accent: true);
        done.Dock = DockStyle.Right;
        done.Width = 110;
        done.Click += (_, _) => Close();

        Button back = FlatButton("Back to the default order", accent: false);
        back.Dock = DockStyle.Left;
        back.Width = 210;

        back.Click += (_, _) =>
        {
            reset();
            list.SetItems(current());
        };

        bottom.Controls.Add(done);
        bottom.Controls.Add(back);

        // The window is as tall as the list needs, up to what the screen has room for.
        Rectangle area = Screen.FromControl(owner).WorkingArea;
        int chrome = 32 + intro.Height + bottom.Height + 2;
        int height = Math.Min(chrome + list.FullHeight, area.Height - 30);

        ClientSize = new Size(400, height);

        Controls.Add(list);
        Controls.Add(intro);
        Controls.Add(bottom);
        Controls.Add(new TitleBar(this, "Re-order the menu"));

        // Beside the launcher, on the side with room.
        int x = owner.Right + 8;

        if (x + Width > area.Right)
            x = owner.Left - Width - 8;

        Location = new Point(
            Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(owner.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
            Close();
    }

    private static Button FlatButton(string text, bool accent)
    {
        Color back = accent
            ? StudioPalette.Accent
            : Theme.Mix(StudioPalette.Back, StudioPalette.Strong, 0.10f);

        Button button = new()
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = accent ? StudioPalette.Back : StudioPalette.Strong,
            Cursor = Cursors.Hand,
            TabStop = false,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };

        button.FlatAppearance.BorderSize = 0;

        // Explicit hover colours (Windows picks near-white for some buttons).
        button.FlatAppearance.MouseOverBackColor = Theme.Mix(back, StudioPalette.Strong, 0.14f);
        button.FlatAppearance.MouseDownBackColor = Theme.Mix(back, StudioPalette.Strong, 0.24f);

        return button;
    }
}
