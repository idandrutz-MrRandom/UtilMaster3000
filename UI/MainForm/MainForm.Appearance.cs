using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UtilMaster3000;

// Customisation: themes, colours and a background image / GIF / video.
public sealed partial class MainForm
{
    private const string SkinImageFilter =
        "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*";

    private static readonly string[] FitNames =
        { "fill", "fit", "stretch", "original size", "tile" };

    private static readonly string[] AlignNames =
    {
        "top left", "top", "top right",
        "left", "center", "right",
        "bottom left", "bottom", "bottom right"
    };

    private static readonly int[] StrengthChoices = { 15, 25, 40, 60, 85, 100 };
    private static readonly int[] OpacityChoices = { 30, 40, 50, 60, 70, 80, 90, 100 };
    private static readonly int[] SearchBarOpacityChoices = { 25, 40, 55, 70, 85, 100 };
    private static readonly int[] BrightnessChoices = { 70, 85, 100, 115, 130 };
    private static readonly int[] FontScaleChoices = { 80, 90, 100, 110, 120, 130, 140 };
    private static readonly int[] MenuWidthChoices = { 320, 360, 380, 420, 460, 500 };
    private static readonly int[] RowHeightChoices = { 28, 30, 32, 36, 40, 44, 48 };
    private static readonly int[] BorderThicknessChoices = { 1, 2, 3, 4, 5, 6 };
    private static readonly int[] CornerChoices = { 0, 6, 12, 18, 24 };
    private static readonly int[] AnimationSpeedChoices = { 0, 60, 100, 160, 240 };

    private int framePending;

    // --------------------------------------------------
    // STARTUP
    // --------------------------------------------------

    // Called after the interface exists.
    private void InitAppearance()
    {
        restingOpacity = Math.Clamp(settings.LauncherOpacity, 30, 100) / 100.0;
        targetOpacity = restingOpacity;
        Opacity = restingOpacity;

        Theme.FrameChanged += OnBackgroundFrame;
        Theme.LoadBackground(settings.BackgroundPath);
        FitSearchBar();
        ApplySearchBarColors();

        menuBox.Invalidate(true);
    }

    // A GIF / video background moved to its next frame (worker thread).
    private void OnBackgroundFrame()
    {
        if (!Visible || IsDisposed)
            return;

        // At most one repaint queued at a time.
        if (Interlocked.Exchange(ref framePending, 1) == 1)
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref framePending, 0);
                menuBox.Invalidate(true);
            }));
        }
        catch (InvalidOperationException)
        {
            Interlocked.Exchange(ref framePending, 0);
        }
    }

    // Applies colours, opacity and background after a setting changed.
    private void ApplyAppearance(bool reloadBackground)
    {
        Theme.Configure(settings);
        Theme.ConfigureAssets(settings);

        ClientSize = new Size(WindowWidth, ClientSize.Height);
        breadcrumb.Size = new Size(MenuWidth, 25);
        menuBox.Width = MenuWidth;
        menuBox.CornerRadius = Theme.CornerRadius;
        searchBackground.Width = RowsWidth;
        searchBackground.CornerRadius = Math.Min(Theme.CornerRadius, 14);
        searchBox.Width = RowsWidth - 16;
        rowsPanel.Width = RowsWidth;
        statusLabel.Width = RowsWidth;

        SetScaledFont(breadcrumb, 11f, FontStyle.Bold);
        SetScaledFont(searchBox, 9f);
        SetScaledFont(statusLabel, 8f);
        FitSearchBar();

        foreach (LauncherRow row in visibleRows)
            row.ApplyAppearance();

        restingOpacity = Math.Clamp(settings.LauncherOpacity, 30, 100) / 100.0;

        if (!hiding)
        {
            targetOpacity = restingOpacity;

            if (Visible)
                Opacity = restingOpacity;
        }

        if (reloadBackground)
            Theme.LoadBackground(settings.BackgroundPath);

        menuBox.BackColor = Theme.Background;
        ApplySearchBarColors();
        searchBackground.Invalidate();
        searchBox.Invalidate();

        breadcrumb.FillColor = Theme.Strong;
        breadcrumb.OutlineColor = Theme.Background;
        breadcrumb.Invalidate();
        StyleBackButton();

        notification.ApplyTheme();

        UpdateStatus(translator.Enabled);
        menuBox.Invalidate(true);

        // Rebuild the rows so they pick up the new colours / transparency.
        RefreshPage();

        studio?.OnApplied();
    }

    // The bar is as tall as its text (plus an even margin) and the text sits in the
    // middle of it. The text box is exactly one line high, so a fixed 28 px bar left
    // the text pinned to the top.
    private void FitSearchBar()
    {
        int bar = Math.Max(24, searchBox.Height + 12);

        searchExtra = bar - 28;
        searchBackground.Height = bar;
        searchBox.Top = (bar - searchBox.Height) / 2;
        rowsPanel.Top = 50 + searchExtra;
    }

    // The search bar is one flat colour all over. (A semi-transparent panel with
    // a text box on top looked uneven: the text box cannot be see-through.)
    private void ApplySearchBarColors()
    {
        Color barColor = settings.TopBarBackgroundEnabled ? Theme.SearchBar : Theme.Search;
        int percent = settings.TopBarBackgroundEnabled ? Theme.SearchBarOpacityPercent : 100;

        Size layout = new(Theme.MenuWidth, Chrome + MaxRows * Theme.RowHeight);

        Color behind = percent >= 100
            ? Theme.Background
            : Theme.AverageBehind(
                new Rectangle(searchBackground.Location, searchBackground.Size), layout);

        Color flat = percent >= 100
            ? barColor
            : Theme.Mix(behind, barColor, percent / 100f);

        searchBackground.BackColor = flat;
        searchBox.BackColor = flat;
        searchBox.ForeColor = Theme.Strong;
    }

    private static void SetScaledFont(Control control, float baseSize, FontStyle style = FontStyle.Regular)
    {
        float size = baseSize * Theme.MenuFontScale;

        if (Math.Abs(control.Font.Size - size) < 0.1f &&
            control.Font.Style == style &&
            string.Equals(
                control.Font.FontFamily.Name,
                Theme.MenuFontFamily,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Font old = control.Font;
        control.Font = Theme.CreateMenuFont(size, style);
        old.Dispose();
    }

    // --------------------------------------------------
    // STYLE PAGES
    // Style is the front door; Colours, Layout, Text and Background are the
    // rooms behind it. On every value row a click gives the next value and a
    // right-click the previous one, and the menu itself is the preview.
    // --------------------------------------------------

    private const string CycleHint = "Click = next value, right-click = previous.";

    // An icon for each kind of value row (Segoe MDL2 Assets glyphs).
    private static readonly (string Name, string Glyph)[] StyleGlyphs =
    {
        ("Search bar own", "\uE932"), ("Search bar visible", "\uE71E"), ("Brightness", "\uE706"),
        ("Menu width", "\uE9A6"), ("Row height", "\uECA5"), ("Corner", "\uE7C8"),
        ("Border", "\uE9D4"), ("Menu visible", "\uE7B3"), ("Animation", "\uEB9D"),
        ("Font size", "\uE8E9"), ("Fit", "\uE9A6"), ("Position", "\uE8A9"), ("Strength", "\uE9E9")
    };

    private static string StyleGlyph(string label)
    {
        foreach ((string name, string glyph) in StyleGlyphs)
        {
            if (label.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return glyph;
        }

        return "\uE713";
    }

    private MenuEntry StyleCycle<T>(
        string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> format, string tip, Image? icon = null)
    {
        void Step(int direction)
        {
            int i = Array.IndexOf(values, get());

            // A value that is not in the list yet (an old settings file):
            // the first click goes to the nearest end.
            if (i < 0)
                i = direction > 0 ? -1 : 0;

            i = (i + direction + values.Length) % values.Length;

            set(values[i]);
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(0, StyleGlyph(label), text,
            run: () => Step(1), runAlt: () => Step(-1), iconBitmap: icon,
            expandText: text + NL + tip + NL + CycleHint);
    }

    // The same for plain numbers: a stored value that is not one of the
    // choices counts as the nearest choice.
    private MenuEntry StyleNumber(
        string label, int[] choices, Func<int> get, Action<int> set,
        string suffix, string tip)
    {
        int Snap(int value) =>
            choices.OrderBy(c => Math.Abs(c - value)).First();

        return StyleCycle(label, choices, () => Snap(get()), set,
            v => v + suffix, tip);
    }

    private static readonly Dictionary<int, Bitmap> swatches = new();

    // A small colour chip shown as the row's icon.
    private static Bitmap Swatch(Color color)
    {
        int key = color.ToArgb();

        if (swatches.TryGetValue(key, out Bitmap? cached))
            return cached;

        if (swatches.Count > 48)
            swatches.Clear();

        Bitmap chip = new(22, 22, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

        using (Graphics g = Graphics.FromImage(chip))
        using (System.Drawing.Drawing2D.GraphicsPath path =
            DoublePanel.RoundedPath(new Rectangle(1, 3, 19, 15), 4))
        using (SolidBrush fill = new(color))
        using (Pen edge = new(Color.FromArgb(150, 140, 140, 140), 1f))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        swatches[key] = chip;
        return chip;
    }

    private MenuEntry ColourRow(string label, Color current, Action pick) =>
        new(0, "", label, run: pick, iconBitmap: Swatch(current),
            expandText: label + NL + "Opens the colour picker. The chip shows the colour now.");

    private MenuEntry ThemeRow()
    {
        string[] names = Theme.PresetNames
            .Where(n => n != Theme.CustomName ||
                        !string.IsNullOrEmpty(settings.CustomBackground))
            .ToArray();

        void Step(int direction)
        {
            int i = Math.Max(0, Array.IndexOf(names, Theme.Name));

            settings.ThemeName = names[(i + direction + names.Length) % names.Length];
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }

        int at = Array.IndexOf(names, Theme.Name) + 1;
        string text = $"Theme: {Theme.Name}  ({Math.Max(1, at)}/{names.Length})";

        return new MenuEntry(0, "", text,
            run: () => Step(1), runAlt: () => Step(-1),
            iconBitmap: Swatch(Theme.Accent),
            expandText: text + NL +
                "A ready-made set of colours. Picking any colour yourself makes a Custom theme." +
                NL + CycleHint);
    }

    private List<MenuEntry> BuildStylePage()
    {
        List<MenuEntry> list = new()
        {
            new MenuEntry(0, "\uE771", "Style editor (sliders, colours, preview)...", run: OpenStudio,
                expandText: "Opens one window with every look setting: theme tiles, colour pickers that " +
                    "update this menu while you drag, sliders and the background picture. " +
                    "The lists below do the same one value at a time."),
            ThemeRow(),
            new MenuEntry(0, "\uE790", "Colours...", targetPage: "StyleColours"),
            new MenuEntry(0, "\uE8A9", "Layout and animation...", targetPage: "StyleLayout"),
            new MenuEntry(0, "\uE8D2", "Text and font...", targetPage: "StyleText"),
            new MenuEntry(0, "\uE8B9", "Background image...", targetPage: "StyleBackground"),
            new MenuEntry(0, "\uE899", "Skin images and icons...", targetPage: "SkinAssets"),
            new MenuEntry(0, "", "Notifications...", targetPage: "Notifications"),
            Act("\uE74E", "Save this look to a file...", ExportStyle),
            Act("\uE8E5", "Load a look from a file...", ImportStyle),
            Act("\uE777", "Reset appearance", ResetAppearance),
            BackEntry()
        };

        return Renumber(list);
    }

    private List<MenuEntry> BuildColoursPage()
    {
        List<MenuEntry> list = new()
        {
            ThemeRow(),
            ColourRow("Background colour...", Theme.Background, () => PickThemeColor("background")),
            ColourRow("Accent colour...", Theme.Accent, () => PickThemeColor("accent")),
            ColourRow("Text colour...", Theme.Text, () => PickThemeColor("text")),
            ColourRow("Active row colour...", Theme.ActiveRow, () => PickCustomizeColor("row")),
            ColourRow("Border colour...", Theme.BorderColor, () => PickCustomizeColor("border")),
            ColourRow("Search bar colour...", Theme.SearchBar, () => PickCustomizeColor("searchbar")),

            StyleCycle("Search bar own background", new[] { false, true },
                () => settings.TopBarBackgroundEnabled,
                v => settings.TopBarBackgroundEnabled = v,
                v => v ? "on" : "off",
                "Gives the search bar its own colour (and see-through level) instead of the plain one."),

            StyleNumber("Search bar visible", SearchBarOpacityChoices,
                () => Theme.SearchBarOpacityPercent,
                v => settings.TopBarOpacityPercent = v, "%",
                "How solid the search bar's own background is."),

            StyleNumber("Brightness", BrightnessChoices,
                () => settings.ThemeBrightnessPercent,
                v => settings.ThemeBrightnessPercent = v, "%",
                "Makes every colour of the theme lighter or darker."),

            Act("\uE777", "Reset colours", () =>
            {
                settings.ThemeName = "Tokyo Night";
                settings.CustomBackground = "";
                settings.CustomAccent = "";
                settings.CustomText = "";
                settings.CustomTopBar = "";
                settings.CustomRowHighlight = "";
                settings.CustomBorder = "";
                settings.TopBarBackgroundEnabled = false;
                settings.TopBarOpacityPercent = 85;
                settings.ThemeBrightnessPercent = 100;
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),

            BackEntry()
        };

        return Renumber(list);
    }

    private List<MenuEntry> BuildLayoutPage()
    {
        List<MenuEntry> list = new()
        {
            StyleNumber("Menu width", MenuWidthChoices, () => Theme.MenuWidth,
                v => settings.MenuWidth = v, "px", "How wide the launcher is."),

            StyleNumber("Row height", RowHeightChoices, () => Theme.RowHeight,
                v => settings.MenuRowHeight = v, "px", "How tall each row is."),

            StyleNumber("Corner radius", CornerChoices, () => Theme.CornerRadius,
                v => settings.CornerRadius = v, "px", "0 = square corners."),

            StyleNumber("Border thickness", BorderThicknessChoices, () => Theme.BorderThickness,
                v => settings.BorderThickness = v, "px", "The line around the menu."),

            StyleNumber("Menu visible", OpacityChoices, OpacityValue,
                v => settings.LauncherOpacity = v, "%",
                "Lower = the whole launcher is more see-through."),

            StyleCycle("Animation speed", AnimationSpeedChoices,
                AnimationSpeedValue,
                v => settings.AnimationSpeedPercent = v,
                v => v == 0 ? "off" : v < 100 ? "slow" : v == 100 ? "normal" : v < 200 ? "fast" : "very fast",
                "How quickly the menu slides, fades and highlights. Off shows every change at once."),

            BackEntry()
        };

        return Renumber(list);
    }

    private List<MenuEntry> BuildFontPage()
    {
        List<MenuEntry> list = new()
        {
            StyleNumber("Font size", FontScaleChoices, () => settings.MenuFontScalePercent,
                v => settings.MenuFontScalePercent = v, "%", "The size of all text in the launcher."),

            Act("\uE8D2", $"Font: {Theme.MenuFontFamily}", ChooseInstalledFont),
            Act("\uE8E5", "Load a font file...", ChooseFontFile),

            Act("\uE777", "Reset font", () =>
            {
                settings.MenuFontScalePercent = 100;
                settings.MenuFontFamily = "";
                settings.MenuFontPath = "";
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),

            BackEntry()
        };

        return Renumber(list);
    }

    private List<MenuEntry> BuildBackgroundPage()
    {
        List<MenuEntry> list = new()
        {
            Act("\uE91B", "Choose an image, GIF or video...", ChooseBackground)
        };

        if (Theme.HasBackground)
        {
            list.Add(Act("\uE74D", "Remove the background", RemoveBackground));

            list.Add(StyleCycle("Fit", Enumerable.Range(0, FitNames.Length).ToArray(),
                FitIndex, v => settings.BackgroundFit = v, v => FitNames[v],
                "How the picture is scaled to the menu."));

            list.Add(StyleCycle("Position", Enumerable.Range(0, AlignNames.Length).ToArray(),
                AlignIndex, v => settings.BackgroundAlign = v, v => AlignNames[v],
                "Which part of the picture stays in view."));

            list.Add(StyleNumber("Strength", StrengthChoices, StrengthValue,
                v => settings.BackgroundStrength = v, "%",
                "How strongly the picture shows through."));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    // ---- save / load a look (to move it between computers) ----

    private sealed class StyleFile
    {
        public int Version { get; set; } = 1;
        public string ThemeName { get; set; } = "Tokyo Night";
        public string CustomBackground { get; set; } = "";
        public string CustomAccent { get; set; } = "";
        public string CustomText { get; set; } = "";
        public string CustomTopBar { get; set; } = "";
        public string CustomRowHighlight { get; set; } = "";
        public string CustomBorder { get; set; } = "";
        public bool TopBarBackgroundEnabled { get; set; }
        public int TopBarOpacityPercent { get; set; } = 85;
        public int ThemeBrightnessPercent { get; set; } = 100;
        public int MenuFontScalePercent { get; set; } = 100;
        public string MenuFontFamily { get; set; } = "";
        public int MenuWidth { get; set; } = 380;
        public int MenuRowHeight { get; set; } = 32;
        public int BorderThickness { get; set; } = 2;
        public int CornerRadius { get; set; }
        public int LauncherOpacity { get; set; } = 100;
        public int AnimationSpeedPercent { get; set; } = 100;
        public int BackgroundFit { get; set; }
        public int BackgroundAlign { get; set; } = 4;
        public int BackgroundStrength { get; set; } = 40;
    }

    private void ExportStyle()
    {
        using SaveFileDialog dialog = new()
        {
            Title = "Save this look",
            Filter = "UtilMaster3000 look|*.umlook.json|All files|*.*",
            FileName = "my-look.umlook.json"
        };

        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        StyleFile file = new()
        {
            ThemeName = settings.ThemeName,
            CustomBackground = settings.CustomBackground,
            CustomAccent = settings.CustomAccent,
            CustomText = settings.CustomText,
            CustomTopBar = settings.CustomTopBar,
            CustomRowHighlight = settings.CustomRowHighlight,
            CustomBorder = settings.CustomBorder,
            TopBarBackgroundEnabled = settings.TopBarBackgroundEnabled,
            TopBarOpacityPercent = settings.TopBarOpacityPercent,
            ThemeBrightnessPercent = settings.ThemeBrightnessPercent,
            MenuFontScalePercent = settings.MenuFontScalePercent,
            // A font loaded from a file does not exist on the other computer.
            MenuFontFamily = string.IsNullOrEmpty(settings.MenuFontPath) ? settings.MenuFontFamily : "",
            MenuWidth = settings.MenuWidth,
            MenuRowHeight = settings.MenuRowHeight,
            BorderThickness = settings.BorderThickness,
            CornerRadius = settings.CornerRadius,
            LauncherOpacity = settings.LauncherOpacity,
            AnimationSpeedPercent = settings.AnimationSpeedPercent,
            BackgroundFit = settings.BackgroundFit,
            BackgroundAlign = settings.BackgroundAlign,
            BackgroundStrength = settings.BackgroundStrength
        };

        try
        {
            File.WriteAllText(dialog.FileName,
                System.Text.Json.JsonSerializer.Serialize(file,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            Toast("Look saved (pictures and fonts stay on this PC)", OnColor);
        }
        catch (Exception ex)
        {
            Say("Save this look", "Could not save the file:\n\n" + ex.Message);
        }
    }

    private void ImportStyle()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Load a look",
            Filter = "UtilMaster3000 look|*.umlook.json;*.json|All files|*.*"
        };

        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        try
        {
            StyleFile? file = System.Text.Json.JsonSerializer.Deserialize<StyleFile>(
                File.ReadAllText(dialog.FileName));

            if (file == null)
                throw new InvalidDataException("The file is empty.");

            settings.ThemeName = Theme.PresetNames.Contains(file.ThemeName)
                ? file.ThemeName
                : "Tokyo Night";
            settings.CustomBackground = file.CustomBackground ?? "";
            settings.CustomAccent = file.CustomAccent ?? "";
            settings.CustomText = file.CustomText ?? "";
            settings.CustomTopBar = file.CustomTopBar ?? "";
            settings.CustomRowHighlight = file.CustomRowHighlight ?? "";
            settings.CustomBorder = file.CustomBorder ?? "";
            settings.TopBarBackgroundEnabled = file.TopBarBackgroundEnabled;
            settings.TopBarOpacityPercent = Math.Clamp(file.TopBarOpacityPercent, 0, 100);
            settings.ThemeBrightnessPercent = Math.Clamp(file.ThemeBrightnessPercent, 60, 140);
            settings.MenuFontScalePercent = Math.Clamp(file.MenuFontScalePercent, 80, 140);
            settings.MenuFontFamily = file.MenuFontFamily ?? "";
            settings.MenuFontPath = "";
            settings.MenuWidth = Math.Clamp(file.MenuWidth, 320, 520);
            settings.MenuRowHeight = Math.Clamp(file.MenuRowHeight, 28, 48);
            settings.BorderThickness = Math.Clamp(file.BorderThickness, 1, 6);
            settings.CornerRadius = Math.Clamp(file.CornerRadius, 0, 24);
            settings.LauncherOpacity = Math.Clamp(file.LauncherOpacity, 30, 100);
            settings.AnimationSpeedPercent = Math.Clamp(file.AnimationSpeedPercent, 0, 300);
            settings.BackgroundFit = Math.Clamp(file.BackgroundFit, 0, FitNames.Length - 1);
            settings.BackgroundAlign = Math.Clamp(file.BackgroundAlign, 0, AlignNames.Length - 1);
            settings.BackgroundStrength = Math.Clamp(file.BackgroundStrength, 5, 100);

            SaveSettings();
            ApplyAppearance(reloadBackground: false);
            Toast("Look loaded", OnColor);
        }
        catch (Exception ex)
        {
            Say("Load a look", "That file is not a saved look:\n\n" + ex.Message);
        }
    }

    private List<MenuEntry> BuildSkinAssetsPage()
    {
        List<MenuEntry> list = new()
        {
            Act("", $"Menu row image: {AssetStatus("row")}", () => ManageSkinImage("row")),
            Act("", $"Frame image: {AssetStatus("frame")}", () => ManageSkinImage("frame")),
            Act("", $"Search bar image: {AssetStatus("search")}", () => ManageSkinImage("search")),
            Act("", $"Shared button icon: {SharedButtonIconStatus()}", ManageSharedButtonIcon),
            Act("", "Per-item button icons...", ChoosePerItemButtonIcon),
            Act("", "Clear all skin images and icons", ClearSkinImages),
            BackEntry()
        };

        return Renumber(list);
    }

    private string AssetStatus(string role) =>
        settings.ThemeImages.TryGetValue(role, out SkinImageAsset? asset) &&
        !string.IsNullOrWhiteSpace(asset.Path)
            ? "set"
            : "not set";

    private string SharedButtonIconStatus() =>
        !string.IsNullOrWhiteSpace(settings.SharedButtonIcon?.Path)
            ? "set"
            : "not set";

    private static int NextChoice(int current, int[] choices)
    {
        int index = Array.IndexOf(choices, current);
        return choices[(Math.Max(0, index) + 1) % choices.Length];
    }

    private int FitIndex() =>
        Math.Clamp(settings.BackgroundFit, 0, FitNames.Length - 1);

    private int AlignIndex() =>
        Math.Clamp(settings.BackgroundAlign, 0, AlignNames.Length - 1);

    private int StrengthValue() =>
        StrengthChoices.Contains(settings.BackgroundStrength)
            ? settings.BackgroundStrength
            : 40;

    private int AnimationSpeedValue() =>
        AnimationSpeedChoices.OrderBy(c => Math.Abs(c - settings.AnimationSpeedPercent)).First();

    private int OpacityValue() =>
        OpacityChoices.Contains(settings.LauncherOpacity)
            ? settings.LauncherOpacity
            : 100;

    private void PickCustomizeColor(string which) => PickColourLive(which);

    private void ChooseInstalledFont()
    {
        using Font current = Theme.CreateMenuFont(9f * Theme.MenuFontScale);
        using FontDialog dialog = new()
        {
            Font = current,
            ShowColor = false,
            ShowEffects = false
        };

        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        settings.MenuFontFamily = dialog.Font.FontFamily.Name;
        settings.MenuFontPath = "";
        SaveSettings();
        ApplyAppearance(reloadBackground: false);
    }

    private void ChooseFontFile()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Choose a font file",
            Filter = "Font files|*.ttf;*.otf;*.ttc|All files|*.*"
        };

        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        try
        {
            using PrivateFontCollection fonts = new();
            fonts.AddFontFile(dialog.FileName);

            if (fonts.Families.Length == 0)
                throw new InvalidDataException("The selected file contains no usable font.");

            Directory.CreateDirectory(Theme.AssetFolder);
            string extension = Path.GetExtension(dialog.FileName);
            string target = Path.Combine(
                Theme.AssetFolder,
                "font-" + Guid.NewGuid().ToString("N") + extension);
            File.Copy(dialog.FileName, target);

            settings.MenuFontFamily = fonts.Families[0].Name;
            settings.MenuFontPath = target;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            Say("Font", "Could not load that font:\n\n" + ex.Message);
        }
    }

    private void ManageSkinImage(string role)
    {
        if (!settings.ThemeImages.TryGetValue(role, out SkinImageAsset? asset) ||
            string.IsNullOrWhiteSpace(asset.Path) ||
            !File.Exists(asset.Path))
        {
            ChooseSkinImage(role);
            return;
        }

        DialogResult choice = MessageBox.Show(
            this,
            "Yes: replace this image\nNo: adjust its crop and fit",
            $"Manage {role} image",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (choice == DialogResult.Yes)
            ChooseSkinImage(role);
        else if (choice == DialogResult.No)
            EditSkinImage(role);
    }

    private void ChooseSkinImage(string role)
    {
        using OpenFileDialog picker = new()
        {
            Title = $"Choose the {role} image",
            Filter = SkinImageFilter
        };

        if (picker.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        string? copiedPath = null;

        try
        {
            copiedPath = CopySkinImage(picker.FileName, role);

            using Bitmap preview = Theme.LoadWithWic(copiedPath);
            SkinImageAsset initial = new() { Path = copiedPath };
            using ImageAssetCropDialog crop = new(
                $"Position {role} image",
                preview,
                SkinAssetPreviewSize(role),
                initial);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
            {
                File.Delete(copiedPath);
                return;
            }

            settings.ThemeImages[role] = crop.Result;

            if (role == "search")
                settings.TopBarBackgroundEnabled = true;

            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            if (copiedPath != null && File.Exists(copiedPath))
                File.Delete(copiedPath);

            Say("Skin image", "Could not use that image:\n\n" + ex.Message);
        }
    }

    private void EditSkinImage(string role)
    {
        if (!settings.ThemeImages.TryGetValue(role, out SkinImageAsset? asset))
            return;

        try
        {
            using Bitmap preview = Theme.LoadWithWic(asset.Path);
            using ImageAssetCropDialog crop = new(
                $"Position {role} image",
                preview,
                SkinAssetPreviewSize(role),
                asset);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
                return;

            settings.ThemeImages[role] = crop.Result;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            Say("Skin image", "Could not edit that image:\n\n" + ex.Message);
        }
    }

    private static Size SkinAssetPreviewSize(string role) => role switch
    {
        "row" => new Size(Theme.MenuWidth - 28, Theme.RowHeight),
        "frame" => new Size(Theme.MenuWidth, 388),
        "search" => new Size(Theme.MenuWidth - 28, 28),
        _ => new Size(22, 22)
    };

    private static string CopySkinImage(string source, string name)
    {
        Directory.CreateDirectory(Theme.AssetFolder);
        string target = Path.Combine(
            Theme.AssetFolder,
            name + "-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(source).ToLowerInvariant());

        File.Copy(source, target);
        return target;
    }

    private void ChooseSharedButtonIcon()
    {
        using OpenFileDialog picker = new()
        {
            Title = "Choose a shared button icon",
            Filter = SkinImageFilter
        };

        if (picker.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        string? copiedPath = null;

        try
        {
            copiedPath = CopySkinImage(picker.FileName, "shared-icon");
            using Bitmap preview = Theme.LoadWithWic(copiedPath, 128);
            SkinImageAsset initial = new() { Path = copiedPath };
            using ImageAssetCropDialog crop = new(
                "Position shared button icon",
                preview,
                new Size(22, 22),
                initial);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
            {
                File.Delete(copiedPath);
                return;
            }

            settings.SharedButtonIcon = crop.Result;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            if (copiedPath != null && File.Exists(copiedPath))
                File.Delete(copiedPath);

            Say("Button icon", "Could not use that image:\n\n" + ex.Message);
        }
    }

    private void ManageSharedButtonIcon()
    {
        SkinImageAsset asset = settings.SharedButtonIcon;

        if (string.IsNullOrWhiteSpace(asset.Path) || !File.Exists(asset.Path))
        {
            ChooseSharedButtonIcon();
            return;
        }

        DialogResult choice = MessageBox.Show(
            this,
            "Yes: replace this icon\nNo: adjust its crop and fit",
            "Manage shared button icon",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        if (choice == DialogResult.Yes)
        {
            ChooseSharedButtonIcon();
        }
        else if (choice == DialogResult.No)
        {
            EditSharedButtonIcon(asset);
        }
    }

    private void EditSharedButtonIcon(SkinImageAsset asset)
    {
        try
        {
            using Bitmap preview = Theme.LoadWithWic(asset.Path, 128);
            using ImageAssetCropDialog crop = new(
                "Position shared button icon",
                preview,
                new Size(22, 22),
                asset);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
                return;

            settings.SharedButtonIcon = crop.Result;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            Say("Button icon", "Could not edit that image:\n\n" + ex.Message);
        }
    }

    private sealed record MenuIconTarget(string Key, string Label);

    private void ChoosePerItemButtonIcon()
    {
        List<MenuIconTarget> targets = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        void AddTarget(string page, MenuEntry entry)
        {
            if (entry.command == "back")
                return;

            string key = MenuIconKey(page, entry);

            if (seen.Add(key))
                targets.Add(new MenuIconTarget(key, $"{page} / {entry.Label}"));
        }

        foreach (MenuEntry entry in fullMainEntries)
            AddTarget("home", entry);

        foreach ((string page, List<MenuEntry> entries) in pages)
        {
            foreach (MenuEntry entry in entries)
                AddTarget(page, entry);
        }

        using Form picker = new()
        {
            Text = "Choose a menu item",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(500, 440)
        };

        ListBox list = new()
        {
            Location = new Point(12, 12),
            Size = new Size(476, 350),
            DataSource = targets,
            DisplayMember = nameof(MenuIconTarget.Label)
        };
        picker.Controls.Add(list);

        Button assign = new()
        {
            Text = "Choose icon...",
            Location = new Point(12, 375),
            Size = new Size(130, 32)
        };
        assign.Click += (_, _) =>
        {
            if (list.SelectedItem is MenuIconTarget selected)
                AssignMenuIcon(selected);
        };
        picker.Controls.Add(assign);

        Button remove = new()
        {
            Text = "Remove override",
            Location = new Point(278, 375),
            Size = new Size(122, 32)
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not MenuIconTarget selected)
                return;

            settings.CustomButtonIcons.Remove(selected.Key);
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        };
        picker.Controls.Add(remove);

        Button edit = new()
        {
            Text = "Edit crop...",
            Location = new Point(150, 375),
            Size = new Size(122, 32)
        };
        edit.Click += (_, _) =>
        {
            if (list.SelectedItem is not MenuIconTarget selected)
                return;

            if (settings.CustomButtonIcons.TryGetValue(
                    selected.Key,
                    out SkinImageAsset? asset) &&
                !string.IsNullOrWhiteSpace(asset.Path) &&
                File.Exists(asset.Path))
            {
                EditMenuIcon(selected, asset);
            }
            else
            {
                AssignMenuIcon(selected);
            }
        };
        picker.Controls.Add(edit);

        Button close = new()
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Location = new Point(408, 375),
            Size = new Size(80, 32)
        };
        picker.Controls.Add(close);
        picker.CancelButton = close;

        picker.ShowDialog(DialogOwner);
    }

    private void AssignMenuIcon(MenuIconTarget target)
    {
        using OpenFileDialog picker = new()
        {
            Title = "Choose this menu item's icon",
            Filter = SkinImageFilter
        };

        if (picker.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        string? copiedPath = null;

        try
        {
            copiedPath = CopySkinImage(picker.FileName, "button-icon");
            using Bitmap preview = Theme.LoadWithWic(copiedPath, 128);
            SkinImageAsset initial = settings.CustomButtonIcons.TryGetValue(
                target.Key,
                out SkinImageAsset? current)
                    ? new SkinImageAsset
                    {
                        Path = copiedPath,
                        Fit = current.Fit,
                        Zoom = current.Zoom,
                        CropX = current.CropX,
                        CropY = current.CropY
                    }
                    : new SkinImageAsset { Path = copiedPath };

            using ImageAssetCropDialog crop = new(
                $"Position icon for {target.Label}",
                preview,
                new Size(22, 22),
                initial);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
            {
                File.Delete(copiedPath);
                return;
            }

            settings.CustomButtonIcons[target.Key] = crop.Result;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            if (copiedPath != null && File.Exists(copiedPath))
                File.Delete(copiedPath);

            Say("Button icon", "Could not use that image:\n\n" + ex.Message);
        }
    }

    private void EditMenuIcon(MenuIconTarget target, SkinImageAsset asset)
    {
        try
        {
            using Bitmap preview = Theme.LoadWithWic(asset.Path, 128);
            using ImageAssetCropDialog crop = new(
                $"Position icon for {target.Label}",
                preview,
                new Size(22, 22),
                asset);

            if (crop.ShowDialog(DialogOwner) != DialogResult.OK)
                return;

            settings.CustomButtonIcons[target.Key] = crop.Result;
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }
        catch (Exception ex)
        {
            Say("Button icon", "Could not edit that image:\n\n" + ex.Message);
        }
    }

    private void ClearSkinImages()
    {
        DialogResult answer = MessageBox.Show(
            "Remove all custom skin images and button icons?",
            "Clear skin images",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        settings.ThemeImages.Clear();
        settings.SharedButtonIcon = new SkinImageAsset();
        settings.CustomButtonIcons.Clear();
        SaveSettings();
        ApplyAppearance(reloadBackground: false);
    }

    // --------------------------------------------------
    // COLOURS
    // --------------------------------------------------

    private void PickThemeColor(string which) => PickColourLive(which);

    // --------------------------------------------------
    // BACKGROUND MEDIA
    // --------------------------------------------------

    private void ChooseBackground()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Choose a background image, GIF or video",
            Filter = BackgroundMedia.FileFilter
        };

        if (dialog.ShowDialog(DialogOwner) != DialogResult.OK)
            return;

        string source = dialog.FileName;

        if (!BackgroundMedia.IsSupported(source))
        {
            Say("Background", "That file type is not supported.\n\n" +
                "Use PNG, JPG, BMP, GIF, WebP, or a video (MP4, WebM, MOV, MKV, AVI).");
            return;
        }

        if (BackgroundMedia.IsVideo(source))
        {
            if (FfmpegRecorder.FindFfmpeg() == null)
            {
                Say("Video background",
                    "Video backgrounds need ffmpeg to convert them.\n\n" +
                    "It will be offered for download now; pick the video again afterwards. " +
                    "Images and GIFs work without it.");

                DownloadFfmpeg();
                return;
            }

            Toast("Converting the video (first 10 s)...");

            // Converting can take a while: keep the UI responsive.
            Task.Run(() =>
            {
                try
                {
                    string path = BackgroundMedia.Import(source);

                    BeginInvoke(new Action(() => SetBackground(path)));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                        Toast("Video failed: " + Trunc(ex.Message, 60), OffColor)));
                }
            });

            return;
        }

        try
        {
            SetBackground(BackgroundMedia.Import(source));
        }
        catch (Exception ex)
        {
            Say("Background", "Could not use that file:\n\n" + ex.Message);
        }
    }

    private void SetBackground(string path)
    {
        settings.BackgroundPath = path;
        SaveSettings();

        ApplyAppearance(reloadBackground: true);

        if (Theme.HasBackground)
            Toast("Background set", OnColor);
        else
            Toast(
                "Image failed: " +
                Trunc(Theme.BackgroundLoadError ?? "Unknown image error.", 60),
                OffColor);
    }

    private void RemoveBackground()
    {
        Theme.ClearBackground();
        BackgroundMedia.Clear();

        settings.BackgroundPath = "";
        SaveSettings();

        ApplyAppearance(reloadBackground: false);
    }

    private void ResetAppearance()
    {
        DialogResult answer = MessageBox.Show(
            "Reset the theme, colours, background and window opacity to the defaults?",
            "Reset appearance",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        Theme.ClearBackground();
        BackgroundMedia.Clear();

        settings.ThemeName = "Tokyo Night";
        settings.CustomBackground = "";
        settings.CustomAccent = "";
        settings.CustomText = "";
        settings.CustomTopBar = "";
        settings.CustomRowHighlight = "";
        settings.CustomBorder = "";
        settings.TopBarBackgroundEnabled = false;
        settings.TopBarOpacityPercent = 85;
        settings.ThemeBrightnessPercent = 100;
        settings.MenuFontScalePercent = 100;
        settings.MenuWidth = 380;
        settings.MenuRowHeight = 32;
        settings.BorderThickness = 2;
        settings.CornerRadius = 0;
        settings.MenuFontFamily = "";
        settings.MenuFontPath = "";
        settings.ThemeImages.Clear();
        settings.SharedButtonIcon = new SkinImageAsset();
        settings.CustomButtonIcons.Clear();
        settings.BackgroundPath = "";
        settings.BackgroundFit = 0;
        settings.BackgroundAlign = 4;
        settings.BackgroundStrength = 40;
        settings.LauncherOpacity = 100;
        settings.AnimationSpeedPercent = 100;
        settings.FlyoutDelayMs = 1000;
        settings.UnfoldDelayMs = 1000;
        settings.FlyoutSide = 0;
        settings.FlyoutPlace = 0;
        SaveSettings();

        ApplyAppearance(reloadBackground: false);
    }
}
