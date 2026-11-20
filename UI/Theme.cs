using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

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
        ["Light"]       = new("#eff1f5", "#e2e5ec", "#5c5f77", "#1e66f5", "#11111b", "#2f9e44", "#d20f39")
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

    public static Color ActiveRow => Mix(Background, Accent, 0.16f);
    public static Color Number => Mix(Text, Background, 0.45f);

    // ---- background ----

    public static Image? BackgroundImage { get; private set; }
    public static BackgroundFit Fit { get; private set; } = BackgroundFit.Fill;
    public static int Align { get; private set; } = 4; // 0..8, row-major, 4 = centre
    public static float Strength { get; private set; } = 0.4f;
    public static bool HasBackground => BackgroundImage != null;

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

        Fit = Enum.IsDefined(typeof(BackgroundFit), s.BackgroundFit)
            ? (BackgroundFit)s.BackgroundFit
            : BackgroundFit.Fill;

        Align = Math.Clamp(s.BackgroundAlign, 0, 8);
        Strength = Math.Clamp(s.BackgroundStrength, 5, 100) / 100f;
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

                Bitmap prepared = new(
                    Math.Max(1, (int)(loaded.Width * scale)),
                    Math.Max(1, (int)(loaded.Height * scale)),
                    PixelFormat.Format32bppPArgb);

                using (Graphics g = Graphics.FromImage(prepared))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(loaded, 0, 0, prepared.Width, prepared.Height);
                }

                loaded.Dispose();
                loaded = null;
                stream.Dispose();
                stream = null;

                animated = false;
                BackgroundImage = prepared;
            }

            return true;
        }
        catch
        {
            ClearBackground();
            return false;
        }
        finally
        {
            loaded?.Dispose();
            stream?.Dispose();
        }
    }

    private static void OnFrame(object? sender, EventArgs e) =>
        FrameChanged?.Invoke();

    public static void ClearBackground()
    {
        Image? old = BackgroundImage;

        BackgroundImage = null;

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

    /// <summary>Paints the background image into <paramref name="area"/>.</summary>
    public static void PaintBackground(Graphics g, Rectangle area)
    {
        Image? image = BackgroundImage;

        if (image == null || area.Width <= 0 || area.Height <= 0)
            return;

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
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private static readonly string[] VideoTypes =
        { ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v" };

    public const string FileFilter =
        "Images, GIFs and videos|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.webm;*.mov;*.mkv;*.avi;*.m4v" +
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
