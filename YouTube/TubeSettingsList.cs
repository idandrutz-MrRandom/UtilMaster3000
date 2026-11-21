using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// SETTINGS LIST (launcher style)
// The same rows the launcher menu uses: click to change a
// setting, right-click to go back a step, hover to unfold
// the explanation underneath.
// ======================================================

internal sealed class TubeSettingsList : GlassPanel
{
    private sealed class Item
    {
        public LauncherRow Row = null!;
        public string Icon = "";
        public Func<string> Label = () => "";
        public string? Tip;
        public Action<bool>? Click;
        public bool Header;
    }

    private const string Font = "Consolas";

    private readonly List<Item> items = new();
    private readonly GlassPanel inner = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private long lastTick;
    private int laidOutWidth = -1;

    private readonly TubeScrollBar scroll = new();

    public TubeSettingsList()
    {
        // Scrolling is done here (with the themed bar), not by Windows' own scroll bars.
        BackColor = TubeTheme.Background;
        inner.BackColor = TubeTheme.Background;
        scroll.Visible = false;
        scroll.ValueChanged += (_, _) => inner.Top = -scroll.Value;

        Controls.Add(inner);
        Controls.Add(scroll);

        timer.Tick += (_, _) => Step();
        VisibleChanged += (_, _) => timer.Enabled = Visible;
        Resize += (_, _) => Layout_();
        MouseWheel += (_, e) => Scroll(e.Delta);
    }

    public void AddHeader(string text)
    {
        AddItem(new Item { Header = true, Label = () => "— " + text.ToUpperInvariant() + " —" });
    }

    /// <summary>A row. Click = change / run, right-click = the other direction.</summary>
    public void AddRow(string icon, Func<string> label, string? tip, Action<bool>? click)
    {
        AddItem(new Item { Icon = icon, Label = label, Tip = tip, Click = click });
    }

    private void AddItem(Item item)
    {
        LauncherRow row = new(-2, item.Icon, item.Label(), Font)
        {
            Width = Math.Max(300, ClientSize.Width),
            UseTubeLook = true
        };

        row.ApplyAppearance();

        item.Row = row;

        if (!item.Header)
        {
            row.MouseEnter += (_, _) => row.IsActive = true;
            row.MouseLeave += (_, _) => row.IsActive = false;

            row.MouseUp += (_, e) =>
            {
                if (item.Click == null)
                    return;

                if (e.Button == MouseButtons.Left)
                    item.Click(false);
                else if (e.Button == MouseButtons.Right)
                    item.Click(true);

                RefreshTexts();
            };
        }
        else
        {
            row.Cursor = Cursors.Default;
        }

        row.MouseWheel += (_, e) => Scroll(e.Delta);

        items.Add(item);
        inner.Controls.Add(row);
        Layout_();
    }

    /// <summary>Re-reads every row's text (after a setting changed).</summary>
    public void RefreshTexts()
    {
        foreach (Item item in items)
            item.Row.Rebind(-2, item.Icon, item.Label());

        laidOutWidth = -1;
        Layout_();
    }

    private void Scroll(int delta) =>
        scroll.Value -= Math.Sign(delta) * 90;

    private void Layout_()
    {
        int width = Math.Max(300, ClientSize.Width - (scroll.Visible ? scroll.Width : 0));

        if (width != laidOutWidth)
        {
            laidOutWidth = width;

            foreach (Item item in items)
            {
                item.Row.Width = width;
                item.Row.SetReveal(item.Tip == null ? null : item.Label() + Environment.NewLine + item.Tip, null);
            }
        }

        int y = 0;

        foreach (Item item in items)
        {
            int extra = item.Header ? 14 : 0;
            int h = item.Row.CurrentHeight;

            if (item.Row.Top != y + extra || item.Row.Height != h || item.Row.Width != width)
                item.Row.SetBounds(0, y + extra, width, h);

            y += h + extra;
        }

        if (inner.Width != width || inner.Height != y + 16)
            inner.Size = new Size(width, y + 16);

        // The themed scroll bar appears when the list is taller than the window.
        bool needed = inner.Height > ClientSize.Height && ClientSize.Height > 0;

        scroll.Maximum = Math.Max(1, inner.Height);
        scroll.LargeChange = Math.Max(1, ClientSize.Height);

        if (scroll.Visible != needed)
        {
            scroll.Visible = needed;
            laidOutWidth = -1;
        }

        if (!needed)
            scroll.Value = 0;
    }

    private void Step()
    {
        long now = clock.ElapsedMilliseconds;
        double dt = Math.Max(1, now - lastTick) / 1000.0;
        lastTick = now;

        foreach (Item item in items)
            item.Row.Step(dt);

        Layout_();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            timer.Dispose();

        base.Dispose(disposing);
    }
}
