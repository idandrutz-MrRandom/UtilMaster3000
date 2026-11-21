using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// STYLE EDITOR
// One window with everything visual: theme tiles, colour chips, sliders,
// font, background. Every change shows on the launcher at once, and the
// colour picker updates it while you drag.
// ======================================================

public sealed partial class MainForm
{
    private StyleStudio? studio;

    private void OpenStudio()
    {
        if (studio != null && !studio.IsDisposed)
        {
            studio.Activate();
            return;
        }

        studio = new StyleStudio(this);
        studio.FormClosed += (_, _) => studio = null;
        studio.Show(this);
    }

    // Colour changes only: no layout, so it is cheap enough to run while dragging.
    private void RepaintTheme()
    {
        Theme.Configure(settings);

        menuBox.BackColor = Theme.Background;

        ApplySearchBarColors();

        breadcrumb.FillColor = Theme.Strong;
        breadcrumb.OutlineColor = Theme.Background;
        breadcrumb.Invalidate();
        StyleBackButton();

        foreach (LauncherRow row in visibleRows)
            row.ApplyAppearance();

        notification.ApplyTheme();
        UpdateStatus(translator.Enabled);

        searchBackground.Invalidate();
        searchBox.Invalidate();
        menuBox.Invalidate(true);

        studio?.OnApplied();
    }

    private IWin32Window DialogOwner => (IWin32Window?)studio ?? this;

    // One colour, changed live. Cancel puts everything back.
    private void PickColourLive(string which)
    {
        string themeName = settings.ThemeName;
        string background = settings.CustomBackground, accent = settings.CustomAccent, text = settings.CustomText;
        string row = settings.CustomRowHighlight, border = settings.CustomBorder, bar = settings.CustomTopBar;
        bool barOn = settings.TopBarBackgroundEnabled;

        bool basic = which is "background" or "accent" or "text";

        // A custom theme starts from whatever is on screen now.
        if (basic && settings.ThemeName != Theme.CustomName)
        {
            settings.CustomBackground = Theme.ToHex(Theme.Background);
            settings.CustomAccent = Theme.ToHex(Theme.Accent);
            settings.CustomText = Theme.ToHex(Theme.Text);
        }

        (string title, Color current) = which switch
        {
            "background" => ("Background colour", Theme.Background),
            "accent" => ("Accent colour", Theme.Accent),
            "text" => ("Text colour", Theme.Text),
            "row" => ("Active row colour", Theme.ActiveRow),
            "border" => ("Border colour", Theme.BorderColor),
            _ => ("Search bar colour", Theme.SearchBar)
        };

        void Live(Color c)
        {
            string hex = Theme.ToHex(c);

            switch (which)
            {
                case "background": settings.CustomBackground = hex; break;
                case "accent": settings.CustomAccent = hex; break;
                case "text": settings.CustomText = hex; break;
                case "row": settings.CustomRowHighlight = hex; break;
                case "border": settings.CustomBorder = hex; break;
                default:
                    settings.CustomTopBar = hex;
                    settings.TopBarBackgroundEnabled = true;
                    break;
            }

            if (basic)
                settings.ThemeName = Theme.CustomName;

            RepaintTheme();
        }

        Form owner = (Form?)studio ?? this;
        Color? chosen = LiveColorPicker.Pick(owner, title, current, Live);

        if (chosen == null)
        {
            settings.ThemeName = themeName;
            settings.CustomBackground = background;
            settings.CustomAccent = accent;
            settings.CustomText = text;
            settings.CustomRowHighlight = row;
            settings.CustomBorder = border;
            settings.CustomTopBar = bar;
            settings.TopBarBackgroundEnabled = barOn;
            RepaintTheme();
            return;
        }

        SaveSettings();
        ApplyAppearance(reloadBackground: false);
    }

    // ======================================================
    // THE WINDOW
    // ======================================================

    private sealed class StyleStudio : Form, IMessageFilter
    {
        private readonly MainForm owner;
        private readonly Panel viewport = new() { Dock = DockStyle.Fill };
        private readonly Panel body = new();
        private readonly ThemedScrollBar scroll = new() { Dock = DockStyle.Right };
        private readonly List<Action> syncers = new();
        private readonly List<ThemeTile> tiles = new();
        private readonly System.Windows.Forms.Timer applyTimer = new() { Interval = 30 };
        private bool updating;
        private int y = 8;

        private AppSettings S => owner.settings;

        public StyleStudio(MainForm owner)
        {
            this.owner = owner;

            Text = "Style editor";
            FormBorderStyle = FormBorderStyle.None;
            Padding = new Padding(1);
            ShowInTaskbar = false;
            KeyPreview = true;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(442, 702);

            // Beside the launcher, on the side with room.
            Rectangle area = Screen.FromControl(owner).WorkingArea;
            int x = owner.Right + 8;

            if (x + Width > area.Right)
                x = owner.Left - Width - 8;

            StartPosition = FormStartPosition.Manual;
            Height = Math.Min(Height, area.Height - 20);
            Location = new Point(
                Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width)),
                Math.Clamp(owner.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));

            viewport.Controls.Add(body);
            viewport.Controls.Add(scroll);
            scroll.BringToFront();

            Controls.Add(viewport);
            Controls.Add(BuildBottomBar());
            Controls.Add(new TitleBar(this, "Style editor"));

            scroll.Scrolled += (_, _) => body.Top = -scroll.Value;
            viewport.Resize += (_, _) => FitScroll();

            BuildThemeSection();
            BuildColourSection();
            BuildShapeSection();
            BuildTextSection();
            BuildMotionSection();
            BuildBackgroundSection();
            BuildMoreSection();

            body.Bounds = new Rectangle(0, 0, 442 - 2 - scroll.Width, y + 16);
            FitScroll();

            Application.AddMessageFilter(this);

            applyTimer.Tick += (_, _) =>
            {
                applyTimer.Stop();
                owner.ApplyAppearance(reloadBackground: false);
            };

            OnApplied();
        }

        // ---- scrolling with the themed scroll bar ----

        private void FitScroll()
        {
            // (Not scroll.Visible: that reads false until the window is shown.)
            bool needed = body.Height > viewport.Height;

            scroll.Maximum = body.Height;
            scroll.Page = viewport.Height;
            scroll.Visible = needed;
            body.Width = viewport.Width - (needed ? scroll.Width : 0);
            body.Top = -scroll.Value;
        }

        private void ScrollBy(int pixels)
        {
            scroll.Value += pixels;
            body.Top = -scroll.Value;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // The wheel goes to the control under the pointer: a slider changes its
        // value, anything else scrolls the page (never a slider that merely has focus).
        bool IMessageFilter.PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A /* WM_MOUSEWHEEL */ || IsDisposed || !Visible)
                return false;

            Point at = Cursor.Position;

            if (!Bounds.Contains(at) || (GetForegroundWindow() != Handle && GetForegroundWindow() != owner.Handle))
                return false;

            int delta = (short)((long)m.WParam >> 16);
            Control hit = this;

            while (hit.GetChildAtPoint(hit.PointToClient(at)) is Control next)
                hit = next;

            if (hit is FlatSlider slider)
                slider.Wheel(delta);
            else
                ScrollBy(-delta / 120 * 60);

            return true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
                Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            Application.RemoveMessageFilter(this);
            applyTimer.Dispose();
            owner.SaveSettings();
        }

        // Called after the launcher's look changed: follow it, and refresh every control.
        public void OnApplied()
        {
            if (IsDisposed)
                return;

            Color back = StudioPalette.Back;
            Color fore = StudioPalette.Text;

            // The one-pixel frame is the form itself; everything inside is the theme.
            BackColor = Theme.Mix(back, StudioPalette.Accent, 0.4f);
            ForeColor = fore;
            Recolour(this, back, fore);

            updating = true;

            foreach (Action sync in syncers)
                sync();

            updating = false;

            Invalidate(true);
        }

        private static void Recolour(Control parent, Color back, Color fore)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button b)
                {
                    b.BackColor = Theme.Mix(back, StudioPalette.Accent, 0.16f);
                    b.ForeColor = StudioPalette.Strong;
                    b.FlatAppearance.BorderColor = Theme.Mix(back, StudioPalette.Accent, 0.4f);
                }
                else if (c is CheckBox)
                {
                    c.BackColor = back;
                    c.ForeColor = fore;
                }
                else if (c is Label label)
                {
                    label.BackColor = back;

                    if (label.Tag as string != "header")
                        label.ForeColor = fore;
                    else
                        label.ForeColor = StudioPalette.Accent;
                }
                else if (c is not ThemeTile && c is not ColourChip && c is not PositionGrid && c is not ThemedDropDown && c is not TitleBar && c is not ThemedScrollBar)
                {
                    c.BackColor = back;
                    c.ForeColor = fore;
                }

                if (c.HasChildren)
                    Recolour(c, back, fore);
            }
        }

        // ---- building blocks ----

        private void Header(string text)
        {
            y += 10;

            Label label = new()
            {
                Text = text.ToUpperInvariant(),
                Tag = "header",
                Font = new Font("Segoe UI Semibold", 9f),
                Bounds = new Rectangle(14, y, 400, 20)
            };

            body.Controls.Add(label);
            y += 26;
        }

        private Label Caption(string text, int top, int left = 14, int width = 142)
        {
            Label label = new()
            {
                Text = text,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(left, top, width, 28)
            };

            body.Controls.Add(label);
            return label;
        }

        private void Slider(
            string label, int min, int max, int step,
            Func<int> get, Action<int> set, Func<int, string> format,
            bool relayout = true)
        {
            Caption(label, y);

            FlatSlider slider = new()
            {
                Minimum = min,
                Maximum = max,
                Step = step,
                Format = format,
                Bounds = new Rectangle(160, y - 1, 250, 30),
                Value = get()
            };

            slider.ValueChanged += (_, _) =>
            {
                if (updating)
                    return;

                set(slider.Value);

                // Changing the size of the launcher takes a full re-layout; colours
                // alone only repaint. Either way at most about 30 times a second.
                if (relayout)
                {
                    applyTimer.Stop();
                    applyTimer.Start();
                }
                else
                {
                    owner.RepaintTheme();
                }
            };

            slider.Committed += (_, _) =>
            {
                applyTimer.Stop();
                owner.ApplyAppearance(reloadBackground: false);
                owner.SaveSettings();
            };

            body.Controls.Add(slider);
            syncers.Add(() => slider.Value = get());
            y += 32;
        }

        private Button MakeButton(string text, int left, int top, int width, Action click)
        {
            Button button = new()
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Bounds = new Rectangle(left, top, width, 28),
                Cursor = Cursors.Hand
            };

            button.Click += (_, _) => click();
            body.Controls.Add(button);
            return button;
        }

        // ---- sections ----

        private void BuildThemeSection()
        {
            Header("Theme");

            string[] names = Theme.PresetNames.Where(n => n != Theme.CustomName).ToArray();
            int columns = 4, tileW = 98, tileH = 46, gap = 6;

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];

                ThemeTile tile = new(name)
                {
                    Bounds = new Rectangle(
                        14 + i % columns * (tileW + gap),
                        y + i / columns * (tileH + gap),
                        tileW, tileH)
                };

                tile.Click += (_, _) =>
                {
                    S.ThemeName = name;
                    owner.SaveSettings();
                    owner.ApplyAppearance(reloadBackground: false);
                };

                tiles.Add(tile);
                body.Controls.Add(tile);
            }

            y += (names.Length + columns - 1) / columns * (tileH + gap);

            Label custom = new()
            {
                Text = "",
                ForeColor = StudioPalette.Text,
                Bounds = new Rectangle(14, y, 410, 20),
                Font = new Font("Segoe UI", 8f)
            };

            body.Controls.Add(custom);
            y += 22;

            syncers.Add(() =>
            {
                foreach (ThemeTile tile in tiles)
                    tile.Selected = tile.PresetName == Theme.Name;

                custom.Text = Theme.Name == Theme.CustomName
                    ? "Using your own colours (pick a theme above to leave them)."
                    : "Change any colour below to make it your own.";
            });
        }

        private void BuildColourSection()
        {
            Header("Colours  (drag in the picker: the launcher follows)");

            void Row(string label, string which, Func<Color> get)
            {
                Caption(label, y, 14, 142);

                ColourChip chip = new() { Bounds = new Rectangle(160, y + 2, 150, 24) };
                chip.Click += (_, _) => owner.PickColourLive(which);

                body.Controls.Add(chip);
                syncers.Add(() => chip.Colour = get());
                y += 32;
            }

            Row("Background", "background", () => Theme.Background);
            Row("Accent", "accent", () => Theme.Accent);
            Row("Text", "text", () => Theme.Text);
            Row("Highlighted row", "row", () => Theme.ActiveRow);
            Row("Border", "border", () => Theme.BorderColor);
            Row("Search bar", "searchbar", () => Theme.SearchBar);

            CheckBox own = new()
            {
                Text = "Search bar uses its own colour",
                Bounds = new Rectangle(14, y, 300, 24),
                FlatStyle = FlatStyle.Flat
            };

            own.CheckedChanged += (_, _) =>
            {
                if (updating)
                    return;

                S.TopBarBackgroundEnabled = own.Checked;
                owner.SaveSettings();
                owner.RepaintTheme();
            };

            body.Controls.Add(own);
            syncers.Add(() => own.Checked = S.TopBarBackgroundEnabled);
            y += 30;

            Slider("Search bar solidity", 0, 100, 5,
                () => Math.Clamp(S.TopBarOpacityPercent, 0, 100),
                v => S.TopBarOpacityPercent = v, v => v + "%", relayout: false);

            Slider("Brightness", 60, 140, 5,
                () => Math.Clamp(S.ThemeBrightnessPercent, 60, 140),
                v => S.ThemeBrightnessPercent = v, v => v + "%", relayout: false);

            MakeButton("Reset colours", 14, y, 130, () =>
            {
                S.ThemeName = "Tokyo Night";
                S.CustomBackground = S.CustomAccent = S.CustomText = "";
                S.CustomTopBar = S.CustomRowHighlight = S.CustomBorder = "";
                S.TopBarBackgroundEnabled = false;
                S.TopBarOpacityPercent = 85;
                S.ThemeBrightnessPercent = 100;
                owner.SaveSettings();
                owner.ApplyAppearance(reloadBackground: false);
            });

            y += 36;
        }

        private void BuildShapeSection()
        {
            Header("Shape");

            Slider("Width", 320, 520, 20, () => Theme.MenuWidth, v => S.MenuWidth = v, v => v + " px");
            Slider("Row height", 28, 48, 2, () => Theme.RowHeight, v => S.MenuRowHeight = v, v => v + " px");
            Slider("Corner roundness", 0, 24, 2, () => Theme.CornerRadius, v => S.CornerRadius = v, v => v == 0 ? "square" : v + " px");
            Slider("Border thickness", 1, 6, 1, () => Theme.BorderThickness, v => S.BorderThickness = v, v => v + " px");
            Slider("See-through", 30, 100, 5,
                () => Math.Clamp(S.LauncherOpacity, 30, 100),
                v => S.LauncherOpacity = v, v => v == 100 ? "solid" : v + "%");
        }

        private void BuildTextSection()
        {
            Header("Text");

            Slider("Size", 80, 140, 5,
                () => Math.Clamp(S.MenuFontScalePercent, 80, 140),
                v => S.MenuFontScalePercent = v, v => v + "%");

            Caption("Font", y);

            ThemedDropDown fonts = new() { Bounds = new Rectangle(160, y + 2, 250, 24) };

            using (InstalledFontCollection installed = new())
                fonts.Items.AddRange(installed.Families.Select(f => f.Name));

            fonts.SelectedIndexChanged += (_, _) =>
            {
                if (updating || fonts.SelectedItem is not string family)
                    return;

                S.MenuFontFamily = family;
                S.MenuFontPath = "";
                owner.SaveSettings();
                owner.ApplyAppearance(reloadBackground: false);
            };

            body.Controls.Add(fonts);
            syncers.Add(() =>
            {
                fonts.SelectedIndex = fonts.Items.IndexOf(Theme.MenuFontFamily);
            });

            y += 34;

            MakeButton("Load a font file...", 14, y, 150, owner.ChooseFontFile);
            MakeButton("Default font", 172, y, 120, () =>
            {
                S.MenuFontFamily = "";
                S.MenuFontPath = "";
                S.MenuFontScalePercent = 100;
                owner.SaveSettings();
                owner.ApplyAppearance(reloadBackground: false);
            });

            y += 36;
        }

        private void BuildMotionSection()
        {
            Header("Motion");

            // The slider moves over the same steps the list menu offers.
            int[] speeds = AnimationSpeedChoices;
            string[] names = { "off", "slow", "normal", "fast", "very fast" };

            Slider("Animation speed", 0, speeds.Length - 1, 1,
                () => Array.IndexOf(speeds, owner.AnimationSpeedValue()),
                v => S.AnimationSpeedPercent = speeds[Math.Clamp(v, 0, speeds.Length - 1)],
                v => names[Math.Clamp(v, 0, names.Length - 1)]);

            ChoiceRow("Slide-out side", new[] { "Automatic", "Right", "Left" },
                () => S.FlyoutSide, v => S.FlyoutSide = v);

            ChoiceRow("Slide-out position", new[] { "Beside the row", "Top of the launcher" },
                () => S.FlyoutPlace, v => S.FlyoutPlace = v);

            // Waiting times. (For unfolding text, the far end of the slider means "never".)
            Slider("Slide-out menu after", 0, 3000, 100,
                () => Math.Clamp(S.FlyoutDelayMs, 0, 3000),
                v => S.FlyoutDelayMs = v, DelayLabel);

            Slider("Unfold long text after", 0, 3100, 100,
                () => S.UnfoldDelayMs < 0 ? 3100 : Math.Min(S.UnfoldDelayMs, 3000),
                v => S.UnfoldDelayMs = v > 3000 ? -1 : v, DelayLabel);
        }

        // A small named-choice drop-down in the Motion section.
        private void ChoiceRow(string label, string[] names, Func<int> get, Action<int> set)
        {
            Caption(label, y);

            ThemedDropDown box = new() { Bounds = new Rectangle(160, y + 2, 250, 24) };
            box.Items.AddRange(names);

            box.SelectedIndexChanged += (_, _) =>
            {
                if (updating || box.SelectedIndex < 0)
                    return;

                set(box.SelectedIndex);
                owner.SaveSettings();
            };

            body.Controls.Add(box);
            syncers.Add(() => box.SelectedIndex = Math.Clamp(get(), 0, names.Length - 1));
            y += 34;
        }

        private static string DelayLabel(int ms) =>
            ms > 3000 ? "never" : ms == 0 ? "instant" : (ms / 1000.0).ToString("0.0") + " s";

        private void BuildBackgroundSection()
        {
            Header("Background picture");

            Button choose = MakeButton("Choose image, GIF or video...", 14, y, 200, owner.ChooseBackground);
            Button remove = MakeButton("Remove", 222, y, 90, owner.RemoveBackground);
            y += 36;

            Caption("Fit", y);

            ThemedDropDown fit = new() { Bounds = new Rectangle(160, y + 2, 150, 24) };

            fit.Items.AddRange(FitNames);

            fit.SelectedIndexChanged += (_, _) =>
            {
                if (updating || fit.SelectedIndex < 0)
                    return;

                S.BackgroundFit = fit.SelectedIndex;
                owner.SaveSettings();
                owner.ApplyAppearance(reloadBackground: false);
            };

            body.Controls.Add(fit);
            y += 34;

            Caption("Which part shows", y);

            PositionGrid grid = new() { Bounds = new Rectangle(160, y, 84, 84) };

            grid.Picked += position =>
            {
                S.BackgroundAlign = position;
                owner.SaveSettings();
                owner.ApplyAppearance(reloadBackground: false);
            };

            body.Controls.Add(grid);
            y += 92;

            FlatSlider strength = new()
            {
                Minimum = 5,
                Maximum = 100,
                Step = 5,
                Format = v => v + "%"
            };

            Caption("Strength", y);
            strength.Bounds = new Rectangle(160, y - 1, 250, 30);

            strength.ValueChanged += (_, _) =>
            {
                if (updating)
                    return;

                S.BackgroundStrength = strength.Value;
                applyTimer.Stop();
                applyTimer.Start();
            };

            strength.Committed += (_, _) =>
            {
                applyTimer.Stop();
                owner.ApplyAppearance(reloadBackground: false);
                owner.SaveSettings();
            };

            body.Controls.Add(strength);
            y += 34;

            syncers.Add(() =>
            {
                bool has = Theme.HasBackground;

                remove.Enabled = has;
                fit.Enabled = has;
                grid.Enabled = has;
                strength.Enabled = has;

                fit.SelectedIndex = Math.Clamp(S.BackgroundFit, 0, FitNames.Length - 1);
                grid.Position = Math.Clamp(S.BackgroundAlign, 0, 8);
                strength.Value = Math.Clamp(S.BackgroundStrength, 5, 100);
            });
        }

        private void BuildMoreSection()
        {
            Header("More");

            MakeButton("Skin images and icons...", 14, y, 190, () =>
            {
                owner.ShowPage("SkinAssets");
                owner.Activate();
            });

            MakeButton("Notifications...", 212, y, 130, () =>
            {
                owner.ShowPage("Notifications");
                owner.Activate();
            });

            y += 36;
        }

        private Panel BuildBottomBar()
        {
            Panel bar = new() { Dock = DockStyle.Bottom, Height = 46 };

            void Add(string text, int left, int width, Action click)
            {
                Button b = new()
                {
                    Text = text,
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    Bounds = new Rectangle(left, 8, width, 30),
                    Cursor = Cursors.Hand
                };

                b.Click += (_, _) => click();
                bar.Controls.Add(b);
            }

            Add("Save look...", 12, 100, owner.ExportStyle);
            Add("Load look...", 118, 100, owner.ImportStyle);
            Add("Reset all", 224, 90, owner.ResetAppearance);
            Add("Done", 342, 86, Close);

            return bar;
        }
    }

    // ---- small custom controls ----

    private sealed class ThemeTile : Control
    {
        public string PresetName { get; }
        public bool Selected { get => selected; set { if (selected != value) { selected = value; Invalidate(); } } }
        private bool selected;
        private bool hover;

        public ThemeTile(string name)
        {
            PresetName = name;
            Cursor = Cursors.Hand;

            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            (Color back, Color accent, Color text) = Theme.Preview(PresetName);
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new(1, 1, Width - 3, Height - 3);

            using GraphicsPath path = DoublePanel.RoundedPath(r, 6);
            using SolidBrush fill = new(back);
            g.FillPath(fill, path);

            using SolidBrush stripe = new(accent);
            g.FillRectangle(stripe, 10, Height - 14, Width - 20, 4);

            using Pen border = new(
                selected ? StudioPalette.Strong : hover ? accent : Color.FromArgb(70, 128, 128, 128),
                selected ? 2.5f : 1.2f);

            g.DrawPath(border, path);

            TextRenderer.DrawText(
                g, PresetName, Font,
                new Rectangle(4, 5, Width - 8, 18),
                text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private sealed class ColourChip : Control
    {
        private Color colour = Color.Gray;

        public Color Colour { get => colour; set { colour = value; Invalidate(); } }

        public ColourChip()
        {
            Cursor = Cursors.Hand;

            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using GraphicsPath path = DoublePanel.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 5);
            using SolidBrush fill = new(colour);
            using Pen edge = new(Color.FromArgb(120, 140, 140, 140), 1.2f);

            g.FillPath(fill, path);
            g.DrawPath(edge, path);

            // Readable on any colour.
            Color ink = colour.GetBrightness() > 0.55f ? Color.Black : Color.White;

            TextRenderer.DrawText(
                g, Theme.ToHex(colour) + "   ·   change", Font,
                new Rectangle(0, 0, Width, Height), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // The 3 x 3 grid for "which part of the picture shows".
    private sealed class PositionGrid : Control
    {
        public int Position { get => position; set { if (position != value) { position = value; Invalidate(); } } }
        private int position = 4;

        public event Action<int>? Picked;

        public PositionGrid()
        {
            Cursor = Cursors.Hand;

            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (!Enabled)
                return;

            int col = Math.Clamp(e.X * 3 / Width, 0, 2);
            int row = Math.Clamp(e.Y * 3 / Height, 0, 2);

            Position = row * 3 + col;
            Picked?.Invoke(Position);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cell = Width / 3;

            for (int i = 0; i < 9; i++)
            {
                Rectangle r = new(i % 3 * cell + 2, i / 3 * cell + 2, cell - 4, cell - 4);
                bool on = i == position && Enabled;

                using GraphicsPath path = DoublePanel.RoundedPath(r, 4);
                using SolidBrush fill = new(on
                    ? StudioPalette.Accent
                    : Color.FromArgb(Enabled ? 70 : 30, StudioPalette.Text));

                g.FillPath(fill, path);
            }
        }
    }
}
