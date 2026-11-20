using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace UtilMaster3000;

internal enum BackgroundFit
{
    Fill,       // cover the whole area, cropping the overflow
    Fit,        // whole image visible, may leave bars
    Stretch,    // distort to the exact area
    Original,   // no scaling
    Tile        // repeat
}

// ======================================================
// THEME  (colours + background image used by every window)
// ======================================================

internal static class Theme
{
    private sealed record Preset(
        string Background, string Search, string Text, string Accent,
        string Strong, string On, string Off);

    private static readonly Dictionary<string, Preset> Presets = new()
    {
        ["Tokyo Night"] = new("#1a1b26", "#13141c", "#8a91b0", "#b4befe", "#ffffff", "#a6e3a1", "#f38ba8"),
        ["Dracula"]     = new("#282a36", "#1e1f29", "#a8aed0", "#bd93f9", "#f8f8f2", "#50fa7b", "#ff5555"),
        ["Nord"]        = new("#2e3440", "#272c36", "#a3adc2", "#88c0d0", "#eceff4", "#a3be8c", "#bf616a"),
        ["Gruvbox"]     = new("#282828", "#1d2021", "#bdae93", "#fabd2f", "#fbf1c7", "#b8bb26", "#fb4934"),
        ["Catppuccin"]  = new("#1e1e2e", "#181825", "#a6adc8", "#cba6f7", "#cdd6f4", "#a6e3a1", "#f38ba8"),
        ["Black"]       = new("#000000", "#0b0b0b", "#8c8c8c", "#ffffff", "#ffffff", "#7ee787", "#ff7b72"),
        ["Light"]       = new("#eff1f5", "#e2e5ec", "#5c5f77", "#1e66f5", "#11111b", "#2f9e44", "#d20f39"),
        ["Rose Pine"]   = new("#191724", "#12101c", "#908caa", "#c4a7e7", "#e0def4", "#9ccfd8", "#eb6f92"),
        ["One Dark"]    = new("#282c34", "#21252b", "#abb2bf", "#61afef", "#ffffff", "#98c379", "#e06c75"),
        ["Midnight"]    = new("#0d1117", "#090c10", "#8b949e", "#58a6ff", "#f0f6fc", "#3fb950", "#f85149"),
        ["Ocean"]       = new("#0f1c2e", "#0a1422", "#8fa6c4", "#4fc3f7", "#eaf4ff", "#69f0ae", "#ff8a80"),
        ["Everforest"]  = new("#2d353b", "#232a2e", "#9da9a0", "#a7c080", "#d3c6aa", "#a7c080", "#e67e80"),
        ["Sunset"]      = new("#2b1b2e", "#211522", "#c9a7bd", "#ff9e64", "#fff1e6", "#9ece6a", "#f7768e"),
        ["Solarized"]   = new("#002b36", "#00212b", "#93a1a1", "#b58900", "#fdf6e3", "#859900", "#dc322f"),
        ["Paper"]       = new("#fdf6e3", "#eee8d5", "#657b83", "#268bd2", "#073642", "#859900", "#dc322f")
    };

    public const string CustomName = "Custom";

    public static string[] PresetNames { get; } =
        Presets.Keys.Append(CustomName).ToArray();

    public static string Name { get; private set; } = "Tokyo Night";

    public static Color Background { get; private set; }
    public static Color Search { get; private set; }
    public static Color Text { get; private set; }
    public static Color Accent { get; private set; }
    public static Color Strong { get; private set; }
    public static Color On { get; private set; }
    public static Color Off { get; private set; }

    public static Color ActiveRow { get; private set; }
    public static Color SearchBar { get; private set; }
    public static Color BorderColor { get; private set; }
    public static Color Number => Mix(Text, Background, 0.45f);
    public static int MenuWidth { get; private set; } = 380;
    public static int RowHeight { get; private set; } = 32;
    public static int BorderThickness { get; private set; } = 2;
    public static int CornerRadius { get; private set; }
    public static int SearchBarOpacityPercent { get; private set; } = 85;
    public static float MenuFontScale { get; private set; } = 1f;

    /// <summary>Multiplier on animation time: 1 = normal, large = instant.</summary>
    public static double AnimationScale { get; private set; } = 1.0;
    public static string MenuFontFamily { get; private set; } = "Consolas";
    public static string AssetFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000",
        "theme-assets");

    private static PrivateFontCollection? privateFontCollection;
    private static FontFamily? privateFontFamily;
    private static string configuredFontPath = "";
    private static readonly Dictionary<string, Bitmap> skinBitmaps =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> skinPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SkinImageAsset> skinSettings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Bitmap> buttonIcons =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> buttonIconPaths =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SkinImageAsset> buttonIconSettings =
        new(StringComparer.Ordinal);
    private static Bitmap? sharedButtonIcon;
    private static string sharedButtonIconPath = "";
    private static SkinImageAsset sharedButtonIconSettings = new();
    public static string? AssetLoadError { get; private set; }

    // ---- background ----

    public static Image? BackgroundImage { get; private set; }
    public static BackgroundFit Fit { get; private set; } = BackgroundFit.Fill;
    public static int Align { get; private set; } = 4; // 0..8, row-major, 4 = centre
    public static float Strength { get; private set; } = 0.4f;
    public static bool HasBackground => BackgroundImage != null;
    public static string? BackgroundLoadError { get; private set; }

    private static bool animated;
    private static Stream? backgroundStream;

    /// <summary>Raised (from a worker thread) when a GIF moves to its next frame.</summary>
    public static event Action? FrameChanged;

    static Theme()
    {
        Apply("Tokyo Night", null);
    }

    // ---- colours ----

    public static void Configure(AppSettings s)
    {
        if (s.ThemeName == CustomName)
        {
            Name = CustomName;

            Color bg = Parse(s.CustomBackground, Presets["Tokyo Night"].Background);
            Color accent = Parse(s.CustomAccent, Presets["Tokyo Night"].Accent);
            Color text = Parse(s.CustomText, Presets["Tokyo Night"].Text);

            bool light = Luminance(bg) > 0.5;

            Background = bg;
            Accent = accent;
            Text = text;
            Search = Mix(bg, light ? Color.Black : Color.Black, light ? 0.06f : 0.25f);
            Strong = light ? Color.FromArgb(17, 17, 27) : Color.White;
            On = light ? Color.FromArgb(47, 158, 68) : Color.FromArgb(166, 227, 161);
            Off = light ? Color.FromArgb(210, 15, 57) : Color.FromArgb(243, 139, 168);
        }
        else
        {
            Apply(Presets.ContainsKey(s.ThemeName) ? s.ThemeName : "Tokyo Night", null);
        }

        Color rowHighlight = string.IsNullOrWhiteSpace(s.CustomRowHighlight)
            ? Mix(Background, Accent, 0.16f)
            : Parse(s.CustomRowHighlight, ToHex(Mix(Background, Accent, 0.16f)));
        Color searchBar = string.IsNullOrWhiteSpace(s.CustomTopBar)
            ? Mix(Background, Accent, 0.12f)
            : Parse(s.CustomTopBar, ToHex(Mix(Background, Accent, 0.12f)));
        Color border = string.IsNullOrWhiteSpace(s.CustomBorder)
            ? Accent
            : Parse(s.CustomBorder, ToHex(Accent));

        int brightness = Math.Clamp(s.ThemeBrightnessPercent, 60, 140);
        Background = AdjustBrightness(Background, brightness);
        Search = AdjustBrightness(Search, brightness);
        Text = AdjustBrightness(Text, brightness);
        Accent = AdjustBrightness(Accent, brightness);
        Strong = AdjustBrightness(Strong, brightness);
        On = AdjustBrightness(On, brightness);
        Off = AdjustBrightness(Off, brightness);
        ActiveRow = AdjustBrightness(rowHighlight, brightness);
        SearchBar = AdjustBrightness(searchBar, brightness);
        BorderColor = AdjustBrightness(border, brightness);

        Fit = Enum.IsDefined(typeof(BackgroundFit), s.BackgroundFit)
            ? (BackgroundFit)s.BackgroundFit
            : BackgroundFit.Fill;

        Align = Math.Clamp(s.BackgroundAlign, 0, 8);
        Strength = Math.Clamp(s.BackgroundStrength, 5, 100) / 100f;
        MenuWidth = Math.Clamp(s.MenuWidth, 320, 520);
        RowHeight = Math.Clamp(s.MenuRowHeight, 28, 48);
        BorderThickness = Math.Clamp(s.BorderThickness, 1, 6);
        CornerRadius = Math.Clamp(s.CornerRadius, 0, 24);
        SearchBarOpacityPercent = Math.Clamp(s.TopBarOpacityPercent, 0, 100);
        MenuFontScale = Math.Clamp(s.MenuFontScalePercent, 80, 140) / 100f;
        AnimationScale = s.AnimationSpeedPercent <= 0
            ? 60.0
            : Math.Clamp(s.AnimationSpeedPercent, 20, 400) / 100.0;
        ConfigureFont(s);
    }

    public static Font CreateMenuFont(float size, FontStyle style = FontStyle.Regular) =>
        privateFontFamily != null
            ? new Font(privateFontFamily, size, style)
            : new Font(MenuFontFamily, size, style);

    // Fonts the rows share. Creating three fonts per row made opening a page
    // visibly slow; these are made once and live as long as the program.
    private static readonly Dictionary<(string Family, string Path, int Size, FontStyle Style), Font> sharedFonts = new();

    /// <summary>Raised by one whenever the menu font changes; rows compare it.</summary>
    public static int FontVersion { get; private set; }

    /// <summary>A menu font shared by everything that asks for it. Never dispose it.</summary>
    public static Font SharedMenuFont(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (MenuFontFamily, configuredFontPath, (int)Math.Round(size * 100), style);

        if (!sharedFonts.TryGetValue(key, out Font? font))
        {
            font = CreateMenuFont(size, style);
            sharedFonts[key] = font;
        }

        return font;
    }

    /// <summary>The icon-glyph font, shared. Never dispose it.</summary>
    public static Font SharedIconFont(float size)
    {
        var key = ("Segoe MDL2 Assets", "", (int)Math.Round(size * 100), FontStyle.Regular);

        if (!sharedFonts.TryGetValue(key, out Font? font))
        {
            font = new Font("Segoe MDL2 Assets", size);
            sharedFonts[key] = font;
        }

        return font;
    }

    private static void ConfigureFont(AppSettings settings)
    {
        string path = settings.MenuFontPath ?? "";
        string familyBefore = MenuFontFamily;

        if (!string.Equals(path, configuredFontPath, StringComparison.OrdinalIgnoreCase))
        {
            // Fonts made from the old file must not be used again. (They are
            // dropped, not disposed: a row may still be painting with one.)
            sharedFonts.Clear();
            FontVersion++;

            privateFontCollection?.Dispose();
            privateFontCollection = null;
            privateFontFamily = null;
            configuredFontPath = path;

            if (path.Length > 0 && File.Exists(path))
            {
                PrivateFontCollection collection = new();

                try
                {
                    collection.AddFontFile(path);

                    if (collection.Families.Length > 0)
                    {
                        privateFontCollection = collection;
                        privateFontFamily = collection.Families[0];
                    }
                    else
                    {
                        collection.Dispose();
                    }
                }
                catch
                {
                    collection.Dispose();
                }
            }
        }

        MenuFontFamily = privateFontFamily?.Name ??
            (string.IsNullOrWhiteSpace(settings.MenuFontFamily)
                ? "Consolas"
                : settings.MenuFontFamily);

        if (!string.Equals(familyBefore, MenuFontFamily, StringComparison.Ordinal))
            FontVersion++;
    }

    public static void ConfigureAssets(AppSettings settings)
    {
        AssetLoadError = null;
        ClearAssetBakes();
        Dictionary<string, SkinImageAsset> configured =
            settings.ThemeImages ?? new Dictionary<string, SkinImageAsset>();

        foreach (string key in skinSettings.Keys.Except(configured.Keys).ToArray())
        {
            skinSettings.Remove(key);
            RemoveBitmap(skinBitmaps, skinPaths, key);
        }

        foreach ((string key, SkinImageAsset asset) in configured)
        {
            skinSettings[key] = new SkinImageAsset
            {
                Path = asset.Path,
                Fit = Enum.IsDefined(typeof(SkinImageFit), asset.Fit)
                    ? asset.Fit
                    : SkinImageFit.Cover,
                Zoom = Math.Clamp(asset.Zoom, 1f, 4f),
                CropX = Math.Clamp(asset.CropX, 0f, 1f),
                CropY = Math.Clamp(asset.CropY, 0f, 1f)
            };

            UpdateBitmap(skinBitmaps, skinPaths, key, asset.Path, 1200);
        }

        Dictionary<string, SkinImageAsset> icons =
            settings.CustomButtonIcons ?? new Dictionary<string, SkinImageAsset>();

        foreach (string key in buttonIconPaths.Keys.Except(icons.Keys).ToArray())
        {
            RemoveBitmap(buttonIcons, buttonIconPaths, key);
            buttonIconSettings.Remove(key);
        }

        foreach ((string key, SkinImageAsset asset) in icons)
        {
            buttonIconSettings[key] = CopyAssetSettings(asset);
            UpdateBitmap(buttonIcons, buttonIconPaths, key, asset.Path, 128);
        }

        SkinImageAsset sharedAsset = settings.SharedButtonIcon ?? new SkinImageAsset();
        string sharedPath = sharedAsset.Path ?? "";
        sharedButtonIconSettings = CopyAssetSettings(sharedAsset);
        if (!string.Equals(sharedPath, sharedButtonIconPath, StringComparison.OrdinalIgnoreCase))
        {
            sharedButtonIcon?.Dispose();
            sharedButtonIcon = null;
            sharedButtonIconPath = sharedPath;

            if (sharedPath.Length > 0)
            {
                try
                {
                    sharedButtonIcon = LoadWithWic(sharedPath, 128);
                }
                catch (Exception ex)
                {
                    AssetLoadError ??= ex.Message;
                }
            }
        }
    }

    public static bool DrawButtonIcon(
        Graphics graphics,
        string key,
        Rectangle area,
        float opacity,
        bool allowSharedIcon = true)
    {
        if (buttonIcons.TryGetValue(key, out Bitmap? image) &&
            buttonIconSettings.TryGetValue(key, out SkinImageAsset? settings))
        {
            DrawFittedImage(graphics, image, area, settings, opacity);
            return true;
        }

        if (allowSharedIcon && sharedButtonIcon != null)
        {
            DrawFittedImage(
                graphics,
                sharedButtonIcon,
                area,
                sharedButtonIconSettings,
                opacity);
            return true;
        }

        return false;
    }

    public static bool DrawAsset(
        Graphics graphics,
        string key,
        Rectangle area,
        float opacity = 1f)
    {
        if (!skinBitmaps.TryGetValue(key, out Bitmap? image) ||
            !skinSettings.TryGetValue(key, out SkinImageAsset? asset))
        {
            return false;
        }

        if (area.Width <= 0 || area.Height <= 0)
            return true;

        // Laid out once per size, then copied: rows repaint on every frame.
        var bakeKey = (key, area.Size);

        if (!assetBakes.TryGetValue(bakeKey, out Bitmap? bake))
        {
            if (assetBakes.Count >= 16)
                ClearAssetBakes();

            bake = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb);

            using (Graphics bakeGraphics = Graphics.FromImage(bake))
            {
                bakeGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                DrawFittedImage(
                    bakeGraphics, image,
                    new Rectangle(Point.Empty, area.Size), asset, 1f);
            }

            assetBakes[bakeKey] = bake;
        }

        if (opacity >= 0.999f)
        {
            graphics.DrawImageUnscaled(bake, area.Location);
            return true;
        }

        using ImageAttributes fade = new();
        fade.SetColorMatrix(new ColorMatrix
        {
            Matrix33 = Math.Clamp(opacity, 0f, 1f)
        });

        graphics.DrawImage(
            bake, area, 0, 0, bake.Width, bake.Height,
            GraphicsUnit.Pixel, fade);

        return true;
    }

    private static readonly Dictionary<(string Key, Size Size), Bitmap> assetBakes = new();

    private static void ClearAssetBakes()
    {
        foreach (Bitmap bake in assetBakes.Values)
            bake.Dispose();

        assetBakes.Clear();
    }

    public static void DrawFittedImage(
        Graphics graphics,
        Image image,
        Rectangle area,
        SkinImageAsset asset,
        float opacity = 1f)
    {
        if (area.Width <= 0 || area.Height <= 0 ||
            image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        GraphicsState state = graphics.Save();

        try
        {
            graphics.SetClip(area, CombineMode.Intersect);
            using ImageAttributes? attributes = opacity < 0.999f
                ? new ImageAttributes()
                : null;

            if (attributes != null)
            {
                attributes.SetColorMatrix(new ColorMatrix
                {
                    Matrix33 = Math.Clamp(opacity, 0f, 1f)
                });
            }

            if (asset.Fit == SkinImageFit.Stretch)
            {
                DrawFittedBitmap(graphics, image, area, attributes);
                return;
            }

            if (asset.Fit == SkinImageFit.Tile)
            {
                float tileWidth = image.Width;
                float tileHeight = image.Height;
                float originX = area.X + (area.Width - tileWidth) * asset.CropX;
                float originY = area.Y + (area.Height - tileHeight) * asset.CropY;
                float startX = originX - (float)Math.Ceiling((originX - area.X) / tileWidth) * tileWidth;
                float startY = originY - (float)Math.Ceiling((originY - area.Y) / tileHeight) * tileHeight;

                for (float y = startY; y < area.Bottom; y += tileHeight)
                {
                    for (float x = startX; x < area.Right; x += tileWidth)
                    {
                        DrawFittedBitmap(
                            graphics,
                            image,
                            new RectangleF(x, y, tileWidth, tileHeight),
                            attributes);
                    }
                }

                return;
            }

            float scale = Math.Max(
                area.Width / (float)image.Width,
                area.Height / (float)image.Height);
            scale *= Math.Clamp(asset.Zoom, 1f, 4f);

            float width = image.Width * scale;
            float height = image.Height * scale;
            DrawFittedBitmap(
                graphics,
                image,
                new RectangleF(
                    area.X + (area.Width - width) * Math.Clamp(asset.CropX, 0f, 1f),
                    area.Y + (area.Height - height) * Math.Clamp(asset.CropY, 0f, 1f),
                    width,
                    height),
                attributes);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawFittedBitmap(
        Graphics graphics,
        Image image,
        Rectangle area,
        ImageAttributes? attributes) =>
        DrawFittedBitmap(graphics, image, (RectangleF)area, attributes);

    private static void DrawFittedBitmap(
        Graphics graphics,
        Image image,
        RectangleF destination,
        ImageAttributes? attributes)
    {
        if (attributes == null)
        {
            graphics.DrawImage(image, destination);
            return;
        }

        graphics.DrawImage(
            image,
            Rectangle.Round(destination),
            0,
            0,
            image.Width,
            image.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static SkinImageAsset CopyAssetSettings(SkinImageAsset asset) => new()
    {
        Path = asset.Path,
        Fit = Enum.IsDefined(typeof(SkinImageFit), asset.Fit)
            ? asset.Fit
            : SkinImageFit.Cover,
        Zoom = Math.Clamp(asset.Zoom, 1f, 4f),
        CropX = Math.Clamp(asset.CropX, 0f, 1f),
        CropY = Math.Clamp(asset.CropY, 0f, 1f)
    };

    private static void UpdateBitmap(
        Dictionary<string, Bitmap> images,
        Dictionary<string, string> paths,
        string key,
        string path,
        int maxDimension)
    {
        if (paths.TryGetValue(key, out string? previous) &&
            string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RemoveBitmap(images, paths, key);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            images[key] = LoadWithWic(path, maxDimension);
            paths[key] = path;
        }
        catch (Exception ex)
        {
            paths[key] = path;
            AssetLoadError ??= ex.Message;
        }
    }

    private static void RemoveBitmap(
        Dictionary<string, Bitmap> images,
        Dictionary<string, string> paths,
        string key)
    {
        if (images.Remove(key, out Bitmap? image))
            image.Dispose();

        paths.Remove(key);
    }

    private static void Apply(string name, object? _)
    {
        Preset p = Presets[name];

        Name = name;
        Background = ColorTranslator.FromHtml(p.Background);
        Search = ColorTranslator.FromHtml(p.Search);
        Text = ColorTranslator.FromHtml(p.Text);
        Accent = ColorTranslator.FromHtml(p.Accent);
        Strong = ColorTranslator.FromHtml(p.Strong);
        On = ColorTranslator.FromHtml(p.On);
        Off = ColorTranslator.FromHtml(p.Off);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color Parse(string? hex, string fallback)
    {
        try
        {
            return ColorTranslator.FromHtml(
                string.IsNullOrWhiteSpace(hex) ? fallback : hex);
        }
        catch
        {
            return ColorTranslator.FromHtml(fallback);
        }
    }

    private static double Luminance(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Color AdjustBrightness(Color color, int percent)
    {
        float scale = percent / 100f;

        return Color.FromArgb(
            color.A,
            Math.Clamp((int)Math.Round(color.R * scale), 0, 255),
            Math.Clamp((int)Math.Round(color.G * scale), 0, 255),
            Math.Clamp((int)Math.Round(color.B * scale), 0, 255));
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    // ---- background image ----

    /// <summary>Loads (or clears, when path is null/missing) the background.</summary>
    public static bool LoadBackground(string? path)
    {
        ClearBackground();
        BackgroundLoadError = null;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        Stream? stream = null;
        Image? loaded = null;

        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            loaded = Image.FromStream(stream);

            if (ImageAnimator.CanAnimate(loaded))
            {
                animated = true;
                BackgroundImage = loaded;
                backgroundStream = stream;
                ImageAnimator.Animate(loaded, OnFrame);
                loaded = null;
                stream = null;
            }
            else
            {
                // Keep static images small: they are repainted often.
                const int max = 1200;

                double scale = Math.Min(1.0,
                    max / (double)Math.Max(loaded.Width, loaded.Height));

                Bitmap? prepared = new(
                    Math.Max(1, (int)(loaded.Width * scale)),
                    Math.Max(1, (int)(loaded.Height * scale)),
                    PixelFormat.Format32bppPArgb);

                try
                {
                    using (Graphics g = Graphics.FromImage(prepared))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(loaded, 0, 0, prepared.Width, prepared.Height);
                    }

                    animated = false;
                    BackgroundImage = prepared;
                    prepared = null;
                }
                finally
                {
                    prepared?.Dispose();
                }

                loaded.Dispose();
                loaded = null;
                stream.Dispose();
                stream = null;
            }

            return true;
        }
        catch (Exception gdiError)
        {
            ClearBackground();
            loaded?.Dispose();
            loaded = null;
            stream?.Dispose();
            stream = null;

            try
            {
                BackgroundImage = LoadWithWic(path);
                animated = false;
                return true;
            }
            catch (Exception wicError)
            {
                ClearBackground();
                BackgroundLoadError =
                    $"{gdiError.Message}; WIC fallback: {wicError.Message}";
                return false;
            }
        }
        finally
        {
            loaded?.Dispose();
            stream?.Dispose();
        }
    }

    public static Bitmap LoadWithWic(string path, int maxDimension = 1200)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);

        System.Windows.Media.Imaging.BitmapDecoder decoder =
            System.Windows.Media.Imaging.BitmapDecoder.Create(
                stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

        System.Windows.Media.Imaging.BitmapSource source = decoder.Frames[0];
        int max = Math.Max(1, maxDimension);
        double scale = Math.Min(1.0,
            max / (double)Math.Max(source.PixelWidth, source.PixelHeight));

        if (scale < 1.0)
        {
            System.Windows.Media.Imaging.TransformedBitmap resized = new(
                source,
                new System.Windows.Media.ScaleTransform(scale, scale));
            resized.Freeze();
            source = resized;
        }

        System.Windows.Media.Imaging.FormatConvertedBitmap pixels = new(
            source,
            System.Windows.Media.PixelFormats.Pbgra32,
            null,
            0);
        pixels.Freeze();

        int stride = checked(pixels.PixelWidth * 4);
        byte[] buffer = new byte[checked(stride * pixels.PixelHeight)];
        pixels.CopyPixels(buffer, stride, 0);

        Bitmap bitmap = new(
            pixels.PixelWidth,
            pixels.PixelHeight,
            PixelFormat.Format32bppPArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);

        try
        {
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref backgroundFrame);
        FrameChanged?.Invoke();
    }

    public static void ClearBackground()
    {
        Image? old = BackgroundImage;

        BackgroundImage = null;
        backgroundVersion++;
        backgroundBake?.Dispose();
        backgroundBake = null;

        if (old != null)
        {
            if (animated)
                ImageAnimator.StopAnimate(old, OnFrame);

            old.Dispose();
        }

        backgroundStream?.Dispose();
        backgroundStream = null;
        animated = false;
    }

    // The picture is laid out (scaled, cropped, faded) once into this bitmap;
    // every repaint after that is a plain copy. Rows repaint on every
    // animation frame, so re-scaling the picture each time was the main
    // cost of a menu with a background.
    private static Bitmap? backgroundBake;
    private static (int Image, int Frame, Size Size, BackgroundFit Fit, int Align, float Strength) backgroundBakeKey;
    private static int backgroundVersion;
    private static int backgroundFrame;

    /// <summary>
    /// Paints the background image into <paramref name="area"/>. The picture is
    /// laid out for <paramref name="layout"/> (the menu's full size) so it does
    /// not rescale while the window grows and shrinks; only the part inside
    /// <paramref name="area"/> shows.
    /// </summary>
    public static void PaintBackground(Graphics g, Rectangle area, Size layout = default)
    {
        Image? image = BackgroundImage;

        if (image == null || area.Width <= 0 || area.Height <= 0)
            return;

        try
        {
            Size size = layout.IsEmpty ? area.Size : layout;
            size = new Size(
                Math.Max(size.Width, area.Right),
                Math.Max(size.Height, area.Bottom));

            var key = (backgroundVersion, animated ? backgroundFrame : 0,
                size, Fit, Align, Strength);

            if (backgroundBake == null || !key.Equals(backgroundBakeKey))
            {
                if (backgroundBake == null || backgroundBake.Size != size)
                {
                    backgroundBake?.Dispose();
                    backgroundBake = new Bitmap(
                        size.Width, size.Height, PixelFormat.Format32bppPArgb);
                }

                using (Graphics bake = Graphics.FromImage(backgroundBake))
                {
                    bake.Clear(Color.Transparent);
                    RenderBackground(bake, image, new Rectangle(Point.Empty, size));
                }

                backgroundBakeKey = key;
            }

            // Only the part that is actually being repainted.
            Rectangle clip = Rectangle.Intersect(
                area, Rectangle.Ceiling(g.ClipBounds));

            if (clip.Width <= 0 || clip.Height <= 0)
                return;

            GraphicsState state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(
                backgroundBake, clip,
                clip.X - area.X, clip.Y - area.Y, clip.Width, clip.Height,
                GraphicsUnit.Pixel);
            g.Restore(state);
        }
        catch
        {
            // A broken frame must never break painting.
        }
    }

    private static void RenderBackground(Graphics g, Image image, Rectangle area)
    {
        try
        {
            if (animated)
                ImageAnimator.UpdateFrames(image);

            GraphicsState state = g.Save();

            g.SetClip(area, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            using ImageAttributes attributes = new();

            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Strength });

            float iw = image.Width;
            float ih = image.Height;
            float aw = area.Width;
            float ah = area.Height;

            // Alignment anchors: 0 = start, 0.5 = middle, 1 = end.
            float ax = Align % 3 * 0.5f;
            float ay = Align / 3 * 0.5f;

            void Draw(RectangleF dest) =>
                g.DrawImage(
                    image,
                    Rectangle.Round(dest),
                    0, 0, image.Width, image.Height,
                    GraphicsUnit.Pixel,
                    attributes);

            switch (Fit)
            {
                case BackgroundFit.Stretch:
                    Draw(area);
                    break;

                case BackgroundFit.Tile:
                {
                    float originX = area.X + (aw - iw) * ax;
                    float originY = area.Y + (ah - ih) * ay;

                    float startX = originX - (float)Math.Ceiling((originX - area.X) / iw) * iw;
                    float startY = originY - (float)Math.Ceiling((originY - area.Y) / ih) * ih;

                    for (float y = startY; y < area.Bottom; y += ih)
                    {
                        for (float x = startX; x < area.Right; x += iw)
                            Draw(new RectangleF(x, y, iw, ih));
                    }

                    break;
                }

                default:
                {
                    float scale = Fit switch
                    {
                        BackgroundFit.Fill => Math.Max(aw / iw, ah / ih),
                        BackgroundFit.Fit => Math.Min(aw / iw, ah / ih),
                        _ => 1f
                    };

                    float w = iw * scale;
                    float h = ih * scale;

                    Draw(new RectangleF(
                        area.X + (aw - w) * ax,
                        area.Y + (ah - h) * ay,
                        w, h));

                    break;
                }
            }

            g.Restore(state);
        }
        catch
        {
            // A broken frame must never break painting.
        }
    }
}

// ======================================================
// BACKGROUND IMPORT  (image / GIF copied as is; video -> looping GIF)
// ======================================================

internal static class BackgroundMedia
{
    private static readonly string[] ImageTypes =
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private static readonly string[] VideoTypes =
        { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v" };

    public const string FileFilter =
        "Images, GIFs and videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.mp4;*.webm;*.mov;*.mkv;*.avi;*.m4v" +
        "|All files|*.*";

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000",
        "background");

    public static bool IsVideo(string path) =>
        VideoTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageTypes.Contains(ext) || VideoTypes.Contains(ext);
    }

    /// <summary>
    /// Copies the chosen file into the app's folder (so deleting the
    /// original is harmless) and returns the path to use. Videos are
    /// converted to a looping GIF: up to 10 s, 480 px wide, 15 fps.
    /// Blocking: call from a background thread for videos.
    /// </summary>
    public static string Import(string source)
    {
        string ext = Path.GetExtension(source).ToLowerInvariant();

        Directory.CreateDirectory(Folder);

        string target;

        if (ImageTypes.Contains(ext))
        {
            target = Path.Combine(Folder, "background" + ext);

            string temp = Path.Combine(Folder, "incoming" + ext);
            File.Copy(source, temp, true);

            Clear();
            File.Move(temp, target, true);

            return target;
        }

        if (!VideoTypes.Contains(ext))
            throw new InvalidOperationException("Unsupported file type.");

        string ffmpeg = FfmpegRecorder.FindFfmpeg()
            ?? throw new InvalidOperationException(
                "Video backgrounds need ffmpeg (winget install ffmpeg).");

        string output = Path.Combine(Folder, "incoming.gif");
        target = Path.Combine(Folder, "background.gif");

        ProcessStartInfo info = new(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        foreach (string arg in new[]
        {
            "-y", "-i", source, "-t", "10", "-an",
            "-vf",
            "fps=15,scale=480:-2:flags=lanczos,split[a][b];" +
            "[a]palettegen=max_colors=128[p];[b][p]paletteuse=dither=bayer",
            output
        })
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("ffmpeg did not start.");

        ChildJob.Add(process);

        // ffmpeg logs to stderr; read it so the pipe never fills up.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();

        if (!process.WaitForExit(180_000))
        {
            process.Kill();
            throw new TimeoutException("Converting the video took too long.");
        }

        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException("ffmpeg could not read that video.");

        Clear();
        File.Move(output, target, true);

        return target;
    }

    /// <summary>Deletes any previously imported background.</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;

            foreach (string file in Directory.GetFiles(Folder, "background.*"))
                File.Delete(file);
        }
        catch
        {
            // A locked file is replaced on the next import.
        }
    }
}
