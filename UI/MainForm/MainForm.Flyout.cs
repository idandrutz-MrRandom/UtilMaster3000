using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// SLIDE-OUT MENUS
// Rest the mouse on a row that opens another menu and that menu
// slides out of the launcher's edge like a drawer: click what you
// want straight away instead of opening each menu first.
// Long menus stay compact and scroll (mouse wheel or the thin bar).
// (Setup: "Slide-out menus on hover")
// ======================================================

public sealed partial class MainForm
{
    private const int FlyoutWidth = 320;
    private const int FlyoutScreenMargin = 8;    // kept free below the drawer
    private const int FlyoutRowInset = 12;      // the drawer's padding above its first row
    private const int FlyoutMaxRows = 40;       // entries read from a menu
    private const int FlyoutVisibleRows = 9;    // taller than this: it scrolls
    // How long the pointer must rest on a row before its menu slides out (a setting).
    private int FlyoutDwellMs => Math.Clamp(settings.FlyoutDelayMs, 0, 10000);
    private const int FlyoutLeaveMs = 420;

    private readonly System.Windows.Forms.Timer flyoutTimer = new() { Interval = 100 };
    private FlyoutForm? flyout;
    private string? flyoutPage;
    private string? hoverPage;
    private int hoverMs;
    private int leaveMs;

    // The drawer. It never takes focus, so typing in the launcher carries on.
    private sealed class FlyoutForm : Form
    {
        private const int Pad = 12;
        private const int Bar = 6;

        private readonly System.Windows.Forms.Timer anim = new() { Interval = 15 };
        private readonly System.Diagnostics.Stopwatch clock = new();

        private readonly Panel content = new();    // the whole drawer, slides
        private readonly Panel viewport = new();   // clips the rows
        private readonly Panel list = new();       // the rows, scrolls inside the viewport
        private readonly Panel thumb = new();

        private readonly bool onRight;
        private readonly int edge;                 // screen x of the launcher edge
        private readonly int fullWidth = FlyoutWidth;

        private double progress;
        private int direction = 1;                 // 1 opening, -1 closing
        private long last;
        private int scroll;
        private int rowsHeight;
        private int dragFrom = -1;
        private int dragStartScroll;
        private Action? afterClose;

        public readonly List<LauncherRow> Rows = new();

        /// <summary>True while it is sliding back in.</summary>
        public bool Closing => direction < 0;

        public FlyoutForm(bool onRight, int edge, int top, int height, int gripTop, int gripHeight, int viewHeight)
        {
            this.onRight = onRight;
            this.edge = edge;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Background;
            DoubleBuffered = true;

            Top = top;
            Height = height;
            Width = 1;
            Left = onRight ? edge : edge - 1;

            content.Size = new Size(fullWidth, height);
            content.BackColor = Theme.Background;
            content.Paint += (_, e) =>
            {
                // Same frame as the launcher: its border colour and thickness and its
                // corner rounding.
                // Drawn a little wider and further out, like the launcher's, so the part
                // beyond the window's edge is clipped away and the edge is fully covered.
                const float overlap = 0.75f;

                float thickness = Theme.BorderThickness;
                float centre = thickness / 2f - overlap;

                using Pen border = new(Theme.BorderColor, thickness + overlap * 2f)
                {
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };

                using System.Drawing.Drawing2D.GraphicsPath frame = FramePath(
                    new RectangleF(centre, centre, content.Width - 2 * centre, content.Height - 2 * centre),
                    Math.Max(0f, Theme.CornerRadius - centre));

                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(border, frame);

                // The launcher's scroll bar, now the curtain's edge: it slid
                // out with the drawer (nobody scrolls the launcher meanwhile).
                using SolidBrush grip = new(Theme.Mix(Theme.Background, Theme.Accent, 0.55f));
                int gx = onRight ? 4 : content.Width - 7;
                e.Graphics.FillRectangle(grip, gx, gripTop, 3, gripHeight);
            };

            viewport.Location = new Point(Pad, Pad);
            viewport.Size = new Size(fullWidth - Pad * 2 - Bar - 2, viewHeight);
            viewport.BackColor = Theme.Background;

            list.Location = new Point(0, 0);
            list.BackColor = Theme.Background;

            thumb.BackColor = Theme.Mix(Theme.Background, Theme.Accent, 0.55f);
            thumb.Width = Bar;
            thumb.Visible = false;
            thumb.Cursor = Cursors.Hand;
            thumb.MouseDown += (_, e) =>
            {
                dragFrom = Cursor.Position.Y;
                dragStartScroll = scroll;
            };
            thumb.MouseMove += (_, _) =>
            {
                if (dragFrom < 0 || (MouseButtons & MouseButtons.Left) == 0)
                {
                    dragFrom = -1;
                    return;
                }

                int track = Math.Max(1, viewport.Height - thumb.Height);
                int maxScroll = Math.Max(1, rowsHeight - viewport.Height);
                SetScroll(dragStartScroll + (Cursor.Position.Y - dragFrom) * maxScroll / track);
            };
            thumb.MouseUp += (_, _) => dragFrom = -1;

            viewport.Controls.Add(list);
            content.Controls.Add(viewport);
            content.Controls.Add(thumb);
            Controls.Add(content);

            viewport.MouseWheel += (_, e) => ScrollBy(-e.Delta);
            MouseWheel += (_, e) => ScrollBy(-e.Delta);

            anim.Tick += (_, _) => Step();
            PlaceContent(0);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x08000000 | 0x00000080;   // never activates, tool window
                return p;
            }
        }

        public void AddRow(LauncherRow row)
        {
            row.SetBounds(0, rowsHeight, viewport.Width, LauncherRow.BaseHeight);
            row.MouseWheel += (_, e) => ScrollBy(-e.Delta);

            list.Controls.Add(row);
            Rows.Add(row);
            rowsHeight += LauncherRow.BaseHeight;

            list.Size = new Size(viewport.Width, rowsHeight);

            bool scrolls = rowsHeight > viewport.Height;
            thumb.Visible = scrolls;

            if (scrolls)
            {
                // Rows leave room for the thin scroll bar.
                thumb.Height = Math.Max(28, viewport.Height * viewport.Height / rowsHeight);
                thumb.Location = new Point(viewport.Right + 2, viewport.Top);
            }
        }

        private void ScrollBy(int delta) => SetScroll(scroll + Math.Sign(delta) * LauncherRow.BaseHeight * 2);

        private void SetScroll(int value)
        {
            int max = Math.Max(0, rowsHeight - viewport.Height);
            scroll = Math.Clamp(value, 0, max);
            list.Top = -scroll;

            // Redraw everything: moved rows must not leave old pictures behind.
            viewport.Invalidate(true);
            viewport.Update();

            if (thumb.Visible)
            {
                int track = viewport.Height - thumb.Height;
                thumb.Top = viewport.Top + (max == 0 ? 0 : track * scroll / max);
            }
        }

        // A rectangle with the launcher's corner rounding on all four corners, so beside
        // a rounded launcher the drawer is a matching panel (a flat side would leave a
        // notch against the launcher's rounded corners).
        private static System.Drawing.Drawing2D.GraphicsPath FramePath(RectangleF r, float radius)
        {
            System.Drawing.Drawing2D.GraphicsPath path = new();

            if (radius < 1f)
            {
                path.AddRectangle(r);
                return path;
            }

            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));

            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The window itself has the same rounded corners.
        private void UpdateShape()
        {
            if (Width < 2 || Height < 2)
                return;

            using System.Drawing.Drawing2D.GraphicsPath path = FramePath(
                new RectangleF(0, 0, Width, Height),
                Math.Min(Theme.CornerRadius, Math.Min(Width, Height) / 2f));

            Region? old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        // Where the drawer's contents sit while the window is `width` wide.
        private void PlaceContent(int width)
        {
            Width = Math.Max(1, width);
            UpdateShape();

            if (onRight)
            {
                Left = edge;
                content.Left = Width - fullWidth;       // slides in from behind the launcher
            }
            else
            {
                Left = edge - Width;
                content.Left = 0;
            }

            content.Top = 0;
        }

        public void SlideOut()
        {
            direction = 1;
            last = 0;
            clock.Restart();
            Show();
            anim.Start();
        }

        /// <summary>Slides back in, then disposes itself.</summary>
        public void SlideBack(Action? done = null)
        {
            direction = -1;
            afterClose = done;
            last = clock.ElapsedMilliseconds;
            clock.Start();
            anim.Start();
        }

        private void Step()
        {
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Max(1, now - last) / 1000.0;
            last = now;

            progress = Math.Clamp(progress + direction * dt / (direction > 0 ? 0.17 : 0.12), 0, 1);

            double eased = direction > 0
                ? 1 - Math.Pow(1 - progress, 3)
                : Math.Pow(progress, 2);

            PlaceContent((int)Math.Round(fullWidth * eased));

            foreach (LauncherRow row in Rows)
                row.Step(dt);

            if (direction < 0 && progress <= 0)
            {
                anim.Stop();
                Action? done = afterClose;
                Close();
                done?.Invoke();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                anim.Dispose();

            base.Dispose(disposing);
        }
    }

    private static readonly string[] FlyoutSideNames = { "automatic", "right", "left" };
    private static readonly string[] FlyoutPlaceNames = { "beside the row", "top of the launcher" };

    // (Turning slide-out menus off altogether is the on / off switch above them.)
    private static readonly int[] FlyoutDelayChoices = { 0, 300, 600, 1000, 1500, 2000, 3000 };
    private static readonly int[] UnfoldDelayChoices = { 0, 300, 600, 1000, 1500, 2000, 3000, -1 };

    private static string DelayText(int ms) =>
        ms < 0 ? "never" : ms == 0 ? "instant" : ms % 1000 == 0 ? ms / 1000 + " s" : (ms / 1000.0).ToString("0.0") + " s";

    // A setting row: click for the next delay, right-click for the previous.
    private MenuEntry MotionDelayRow(string label, int[] choices, int current, Action<int> set, string tip)
    {
        int index = Array.IndexOf(choices, current);

        if (index < 0)
        {
            // A stored value that is not one of the choices counts as the nearest one.
            index = 0;

            for (int i = 1; i < choices.Length; i++)
            {
                if (choices[i] >= 0 && Math.Abs(choices[i] - current) < Math.Abs(choices[index] - current))
                    index = i;
            }
        }

        void Step(int direction)
        {
            set(choices[(index + direction + choices.Length) % choices.Length]);
            Theme.Configure(settings);
            SaveSettings();
            RefreshPage();
        }

        string text = $"{label}: {DelayText(choices[index])}";

        return new MenuEntry(0, "\uE916", text,
            run: () => Step(1), runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next value, right-click = previous.");
    }

    private void SetupFlyouts()
    {
        flyoutTimer.Tick += (_, _) => FlyoutTick();
        flyoutTimer.Start();
    }

    // The launcher's own scroll bar hides while the drawer carries it.
    private bool flyoutCurtain;

    private bool LauncherThumb(out int top, out int height)
    {
        top = height = 0;
        int count = displayedEntries.Count;

        if (count <= MaxRows)
            return false;

        height = Math.Max(16, (int)(rowsPanel.Height * (float)MaxRows / count));
        float progress = Math.Clamp(scrollVisual / (count - MaxRows), 0f, 1f);
        top = (int)((rowsPanel.Height - height) * progress);
        return true;
    }

    private void CloseFlyout(bool animate = true)
    {
        hoverPage = null;
        hoverMs = 0;
        leaveMs = 0;
        flyoutPage = null;

        if (flyoutCurtain)
        {
            flyoutCurtain = false;
            rowsPanel.Invalidate();
        }

        if (flyout == null)
            return;

        FlyoutForm old = flyout;
        flyout = null;

        if (animate && old.Visible && Visible && !hiding)
        {
            old.SlideBack(() => old.Dispose());
        }
        else
        {
            old.Close();
            old.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    // Is it really the launcher (or its slid-out menu) that is on screen at this
    // point? The rows only know where they would be: with an app covering the
    // launcher, that place is the app's, and nothing may slide out over it.
    private bool LauncherIsAt(Point screen)
    {
        IntPtr hit = WindowFromPoint(screen);

        if (hit == IntPtr.Zero)
            return false;

        IntPtr root = GetAncestor(hit, 2 /* GA_ROOT */);

        if (root == IntPtr.Zero)
            root = hit;

        if (root == Handle)
            return true;

        FlyoutForm? open = flyout;

        return open != null && !open.IsDisposed && open.IsHandleCreated && root == open.Handle;
    }

    private LauncherRow? RowUnder(Point screen)
    {
        // Behind another window: no row is under the pointer.
        if (!LauncherIsAt(screen))
            return null;

        foreach (LauncherRow row in visibleRows)
        {
            if (row.IsHandleCreated && row.RectangleToScreen(row.ClientRectangle).Contains(screen))
                return row;
        }

        return null;
    }

    // Called every 100 ms: decides when a menu slides out and when it goes away.
    private void FlyoutTick()
    {
        if (!settings.HoverSubmenus || !Visible || hiding || bindingShortcut != null)
        {
            if (flyout != null)
                CloseFlyout(animate: false);

            return;
        }

        Point cursor = Cursor.Position;

        // On the slid-out menu itself: leave it alone.
        if (flyout != null && !flyout.Closing && flyout.Bounds.Contains(cursor))
        {
            leaveMs = 0;
            return;
        }

        LauncherRow? row = RowUnder(cursor);

        if (row?.Tag is RowBinding bound)
        {
            leaveMs = 0;

            string? page = bound.Entry.targetPage;

            // A menu that is already open, or one that needs unlocking, does not slide out.
            if (page == currentPage || page == "Vault")
                page = null;

            if (page == hoverPage)
                hoverMs += flyoutTimer.Interval;
            else
            {
                hoverPage = page;
                hoverMs = 0;
            }

            if (page != null && hoverMs >= FlyoutDwellMs && flyoutPage != page)
                OpenFlyout(page, row);
            else if (page == null && hoverMs >= FlyoutDwellMs + 100 && flyout != null)
                CloseFlyout();

            return;
        }

        // Not on a row and not on the slid-out menu.
        if (flyout != null)
        {
            leaveMs += flyoutTimer.Interval;

            if (leaveMs >= FlyoutLeaveMs)
                CloseFlyout();
        }
    }

    private List<MenuEntry>? EntriesForFlyout(string page)
    {
        try
        {
            List<MenuEntry>? list = page switch
            {
                "Apps" => AppsPage(""),
                "Find" => FindPage(""),
                "Clipboard" => ClipboardPage(""),
                "Shortcuts" => ShortcutsPage(""),
                "Calc" => CalcPage(""),
                "Auto" => AutoClickPage(""),
                "Macros" => MacrosPage(""),
                _ => pages.TryGetValue(page, out List<MenuEntry>? fixedPage) ? fixedPage : null
            };

            return list?
                .Where(e => e.command != "back" && e.Index != -2 && !string.IsNullOrWhiteSpace(e.Label))
                .Take(FlyoutMaxRows)
                .ToList();
        }
        catch
        {
            return null;
        }
    }

    private void OpenFlyout(string page, LauncherRow anchor)
    {
        List<MenuEntry>? entries = EntriesForFlyout(page);

        if (entries == null || entries.Count == 0)
            return;

        CloseFlyout(animate: false);
        flyoutPage = page;

        // Level with the launcher: same top edge, never taller than it.
        Rectangle menu = RectangleToScreen(menuBox.Bounds);
        Rectangle area = Screen.FromControl(this).WorkingArea;

        int wanted = entries.Count * LauncherRow.BaseHeight + 24;
        int most = Math.Min(FlyoutVisibleRows * LauncherRow.BaseHeight + 24, Math.Min(menu.Height, area.Height - 40));
        int height = Math.Max(LauncherRow.BaseHeight + 24, Math.Min(wanted, most));

        // Right against the launcher's edge: touching, never overlapping.
        // (The launcher's scroll bar moves onto the drawer's leading edge.)
        // Which edge is the setting; with no room on that side it uses the other.
        bool roomRight = menu.Right + FlyoutWidth <= area.Right;
        bool roomLeft = menu.Left - FlyoutWidth >= area.Left;

        bool onRight = settings.FlyoutSide switch
        {
            1 => roomRight || !roomLeft,
            2 => !(roomLeft || !roomRight),
            _ => roomRight
        };

        int edge = onRight ? menu.Right : menu.Left;

        // Up and down: level with the row that was pointed at (the drawer's first row
        // sits beside it), or at the launcher's top.
        Rectangle anchorRect = anchor.RectangleToScreen(anchor.ClientRectangle);
        int wantedTop = settings.FlyoutPlace == 1 ? menu.Top : anchorRect.Top - FlyoutRowInset;

        // The drawer stays inside the launcher's own height (and the screen): a list
        // that would hang out below it gets shorter and scrolls instead, so the two
        // line up. Only with room for fewer than three rows does it move up.
        int lowest = Math.Min(menu.Bottom, area.Bottom - FlyoutScreenMargin);
        int highest = Math.Max(menu.Top, area.Top);

        wantedTop = Math.Max(wantedTop, highest);

        int room = lowest - wantedTop;
        int smallest = Math.Min(height, LauncherRow.BaseHeight * 3 + 24);
        height = Math.Max(smallest, Math.Min(height, room));

        int top = Math.Clamp(wantedTop, highest, Math.Max(highest, lowest - height));

        // Where the bar sits (its own size if the list scrolls, else beside the row).
        int gripHeight = 40;
        int gripTop = Math.Clamp(anchorRect.Top - top, 14, Math.Max(14, height - 14 - gripHeight));

        if (LauncherThumb(out int thumbTop, out int thumbHeight))
        {
            gripHeight = thumbHeight;
            gripTop = Math.Clamp(rowsPanel.RectangleToScreen(new Rectangle(0, thumbTop, 1, 1)).Top - top, 6, Math.Max(6, height - 6 - thumbHeight));
        }

        // Only whole rows show: a row cut in half looks broken, and whole rows keep the
        // drawer's rows on the same grid as the launcher's. (What does not fit scrolls;
        // the spare pixels go below the last row, so the bottom edge still lines up.)
        int viewHeight = height - FlyoutRowInset * 2;

        if (entries.Count * LauncherRow.BaseHeight > viewHeight)
            viewHeight = Math.Max(LauncherRow.BaseHeight, viewHeight / LauncherRow.BaseHeight * LauncherRow.BaseHeight);

        FlyoutForm f = new(onRight, edge, top, height, gripTop, gripHeight, viewHeight);
        flyoutCurtain = onRight;
        rowsPanel.Invalidate();

        foreach (MenuEntry entry in entries)
        {
            LauncherRow row = new(-2, entry.Icon, entry.Label);

            if (entry.iconBitmap != null)
                row.IconImage = entry.iconBitmap;
            else
                AttachIcon(row, entry.iconPath);

            row.CustomIconKey = MenuIconKey(page, entry);

            MenuEntry captured = entry;

            row.MouseEnter += (_, _) => row.IsActive = true;
            row.MouseLeave += (_, _) => row.IsActive = false;

            row.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    CloseFlyout();
                    ActivateEntry(captured);
                }
                else if (e.Button == MouseButtons.Right && captured.runAlt != null)
                {
                    CloseFlyout();
                    captured.runAlt();
                }
            };

            f.AddRow(row);
        }

        flyout = f;
        f.SlideOut();
    }
}
