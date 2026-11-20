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
        Color searchBarColor = settings.TopBarBackgroundEnabled
            ? Theme.SearchBar
            : Theme.Search;
        int searchBarOpacity = settings.TopBarBackgroundEnabled
            ? Theme.SearchBarOpacityPercent
            : 100;
        searchBackground.BackColor = Color.FromArgb(
            searchBarOpacity * 255 / 100,
            searchBarColor);
        searchBox.BackColor = searchBarColor;
        searchBox.ForeColor = Theme.Strong;
        searchBackground.Invalidate();
        searchBox.Invalidate();

        breadcrumb.FillColor = Theme.Strong;
        breadcrumb.OutlineColor = Theme.Background;
        breadcrumb.Invalidate();

        notification.ApplyTheme();

        UpdateStatus(translator.Enabled);
        menuBox.Invalidate(true);

        // Rebuild the rows so they pick up the new colours / transparency.
        RefreshPage();
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
    // STYLE PAGE
    // --------------------------------------------------

    private List<MenuEntry> BuildStylePage()
    {
        List<MenuEntry> list = new()
        {
            Act("", $"Theme: {Theme.Name}", () =>
            {
                string[] names = Theme.PresetNames;
                int next = (Array.IndexOf(names, Theme.Name) + 1) % names.Length;

                // "Custom" only makes sense once a colour was picked.
                if (names[next] == Theme.CustomName &&
                    string.IsNullOrEmpty(settings.CustomBackground))
                {
                    next = (next + 1) % names.Length;
                }

                settings.ThemeName = names[next];
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),

            Act("", "Background colour...", () => PickThemeColor("background")),
            Act("", "Accent colour...", () => PickThemeColor("accent")),
            Act("", "Text colour...", () => PickThemeColor("text")),

            Act("", "More customization...", () => ShowPage("Customize")),

            Act("", "Background image / GIF / video...", ChooseBackground)
        };

        if (Theme.HasBackground)
        {
            list.Add(Act("", "Remove background", RemoveBackground));

            list.Add(Act("", $"Background fit: {FitNames[FitIndex()]}", () =>
            {
                settings.BackgroundFit = (FitIndex() + 1) % FitNames.Length;
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }));

            list.Add(Act("", $"Background position: {AlignNames[AlignIndex()]}", () =>
            {
                settings.BackgroundAlign = (AlignIndex() + 1) % AlignNames.Length;
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }));

            list.Add(Act("", $"Background strength: {StrengthValue()}%", () =>
            {
                int next = (Array.IndexOf(StrengthChoices, StrengthValue()) + 1)
                    % StrengthChoices.Length;

                settings.BackgroundStrength = StrengthChoices[next];
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }));
        }

        list.Add(new MenuEntry(0, "\uE7E7", "Notifications...", targetPage: "Notifications"));

        list.Add(Act("", "Reset appearance", ResetAppearance));
        list.Add(BackEntry());

        return Renumber(list);
    }

    private List<MenuEntry> BuildCustomizePage()
    {
        List<MenuEntry> list = new()
        {
            Act("", $"Search bar custom background: {(settings.TopBarBackgroundEnabled ? "on" : "off")}", () =>
            {
                settings.TopBarBackgroundEnabled = !settings.TopBarBackgroundEnabled;
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", "Search bar color...", () => PickCustomizeColor("searchbar")),
            Act("", $"Search bar opacity: {Theme.SearchBarOpacityPercent}%", () =>
            {
                settings.TopBarOpacityPercent = NextChoice(
                    Theme.SearchBarOpacityPercent,
                    SearchBarOpacityChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Theme brightness: {settings.ThemeBrightnessPercent}%", () =>
            {
                settings.ThemeBrightnessPercent = NextChoice(
                    settings.ThemeBrightnessPercent,
                    BrightnessChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", "Active row color...", () => PickCustomizeColor("row")),
            Act("", "Border color...", () => PickCustomizeColor("border")),
            Act("", $"Border thickness: {Theme.BorderThickness}px", () =>
            {
                settings.BorderThickness = NextChoice(
                    Theme.BorderThickness,
                    BorderThicknessChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Font size: {settings.MenuFontScalePercent}%", () =>
            {
                settings.MenuFontScalePercent = NextChoice(
                    settings.MenuFontScalePercent,
                    FontScaleChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Font: {Theme.MenuFontFamily}", ChooseInstalledFont),
            Act("", "Load font file...", ChooseFontFile),
            Act("", "Skin images and icons...", () => ShowPage("SkinAssets")),
            Act("", $"Menu width: {Theme.MenuWidth}px", () =>
            {
                settings.MenuWidth = NextChoice(Theme.MenuWidth, MenuWidthChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Row height: {Theme.RowHeight}px", () =>
            {
                settings.MenuRowHeight = NextChoice(
                    Theme.RowHeight,
                    RowHeightChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Menu opacity: {OpacityValue()}%", () =>
            {
                settings.LauncherOpacity = NextChoice(
                    OpacityValue(),
                    OpacityChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            Act("", $"Corner radius: {Theme.CornerRadius}px", () =>
            {
                settings.CornerRadius = NextChoice(
                    Theme.CornerRadius,
                    CornerChoices);
                SaveSettings();
                ApplyAppearance(reloadBackground: false);
            }),
            BackEntry()
        };

        return Renumber(list);
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

    private int OpacityValue() =>
        OpacityChoices.Contains(settings.LauncherOpacity)
            ? settings.LauncherOpacity
            : 100;

    private void PickCustomizeColor(string which)
    {
        Color current = which switch
        {
            "searchbar" => Theme.SearchBar,
            "row" => Theme.ActiveRow,
            _ => Theme.BorderColor
        };

        using ColorDialog dialog = new()
        {
            Color = current,
            FullOpen = true,
            AnyColor = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string color = Theme.ToHex(dialog.Color);

        switch (which)
        {
            case "searchbar":
                settings.CustomTopBar = color;
                settings.TopBarBackgroundEnabled = true;
                break;
            case "row":
                settings.CustomRowHighlight = color;
                break;
            default:
                settings.CustomBorder = color;
                break;
        }

        SaveSettings();
        ApplyAppearance(reloadBackground: false);
    }

    private void ChooseInstalledFont()
    {
        using Font current = Theme.CreateMenuFont(9f * Theme.MenuFontScale);
        using FontDialog dialog = new()
        {
            Font = current,
            ShowColor = false,
            ShowEffects = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
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

        if (dialog.ShowDialog(this) != DialogResult.OK)
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

        if (picker.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

        if (picker.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

        foreach (MenuEntry entry in mainEntries)
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

        picker.ShowDialog(this);
    }

    private void AssignMenuIcon(MenuIconTarget target)
    {
        using OpenFileDialog picker = new()
        {
            Title = "Choose this menu item's icon",
            Filter = SkinImageFilter
        };

        if (picker.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

            if (crop.ShowDialog(this) != DialogResult.OK)
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

    private void PickThemeColor(string which)
    {
        // Start the custom theme from whatever is on screen now.
        if (settings.ThemeName != Theme.CustomName)
        {
            settings.CustomBackground = Theme.ToHex(Theme.Background);
            settings.CustomAccent = Theme.ToHex(Theme.Accent);
            settings.CustomText = Theme.ToHex(Theme.Text);
        }

        Color current = which switch
        {
            "background" => Theme.Background,
            "accent" => Theme.Accent,
            _ => Theme.Text
        };

        using ColorDialog dialog = new()
        {
            Color = current,
            FullOpen = true,
            AnyColor = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        string hex = Theme.ToHex(dialog.Color);

        switch (which)
        {
            case "background": settings.CustomBackground = hex; break;
            case "accent": settings.CustomAccent = hex; break;
            default: settings.CustomText = hex; break;
        }

        settings.ThemeName = Theme.CustomName;
        SaveSettings();

        ApplyAppearance(reloadBackground: false);
    }

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

        if (dialog.ShowDialog(this) != DialogResult.OK)
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
                    "Install it with:\n\nwinget install ffmpeg\n\n" +
                    "Images and GIFs work without it.");
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
        SaveSettings();

        ApplyAppearance(reloadBackground: false);
    }
}
