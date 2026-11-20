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
    private const int FlyoutMaxRows = 40;       // entries read from a menu
    private const int FlyoutVisibleRows = 9;    // taller than this: it scrolls
    private const int FlyoutDwellMs = 220;
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

        public FlyoutForm(bool onRight, int edge, int top, int height, int gripTop, int gripHeight)
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
                // Same frame as the launcher: a 2 px accent border.
                using Pen border = new(Theme.Accent, 2f);
                e.Graphics.DrawRectangle(border, 1, 1, content.Width - 3, content.Height - 3);

                // The launcher's scroll bar, now the curtain's edge: it slid
                // out with the drawer (nobody scrolls the launcher meanwhile).
                using SolidBrush grip = new(Theme.Mix(Theme.Background, Theme.Accent, 0.55f));
                int gx = onRight ? 4 : content.Width - 7;
                e.Graphics.FillRectangle(grip, gx, gripTop, 3, gripHeight);
            };

            viewport.Location = new Point(Pad, Pad);
            viewport.Size = new Size(fullWidth - Pad * 2 - Bar - 2, height - Pad * 2);
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

        // Where the drawer's contents sit while the window is `width` wide.
        private void PlaceContent(int width)
        {
            Width = Math.Max(1, width);

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
        float progress = scrollTop / (float)(count - MaxRows);
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

    private LauncherRow? RowUnder(Point screen)
    {
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
        bool onRight = menu.Right + FlyoutWidth <= area.Right;
        int edge = onRight ? menu.Right : menu.Left;
        int top = Math.Clamp(menu.Top, area.Top, Math.Max(area.Top, area.Bottom - height));

        // Where the bar sits (its own size if the list scrolls, else beside the row).
        int gripHeight = 40;
        int gripTop = Math.Clamp(anchor.RectangleToScreen(anchor.ClientRectangle).Top - top, 14, height - 14 - gripHeight);

        if (LauncherThumb(out int thumbTop, out int thumbHeight))
        {
            gripHeight = thumbHeight;
            gripTop = Math.Clamp(rowsPanel.RectangleToScreen(new Rectangle(0, thumbTop, 1, 1)).Top - top, 6, Math.Max(6, height - 6 - thumbHeight));
        }

        FlyoutForm f = new(onRight, edge, top, height, gripTop, gripHeight);
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
