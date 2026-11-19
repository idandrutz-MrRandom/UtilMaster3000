using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UtilMaster3000;

// Customisation: themes, colours and a background image / GIF / video.
public sealed partial class MainForm
{
    private static readonly string[] FitNames =
        { "fill", "fit", "stretch", "original size", "tile" };

    private static readonly string[] AlignNames =
    {
        "top left", "top", "top right",
        "left", "center", "right",
        "bottom left", "bottom", "bottom right"
    };

    private static readonly int[] StrengthChoices = { 15, 25, 40, 60, 85, 100 };
    private static readonly int[] OpacityChoices = { 70, 80, 90, 100 };

    private int framePending;

    // --------------------------------------------------
    // STARTUP
    // --------------------------------------------------

    // Called after the interface exists.
    private void InitAppearance()
    {
        restingOpacity = Math.Clamp(settings.LauncherOpacity, 40, 100) / 100.0;
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

        restingOpacity = Math.Clamp(settings.LauncherOpacity, 40, 100) / 100.0;

        if (!hiding)
        {
            targetOpacity = restingOpacity;

            if (Visible)
                Opacity = restingOpacity;
        }

        if (reloadBackground)
            Theme.LoadBackground(settings.BackgroundPath);

        menuBox.BackColor = Theme.Background;
        searchBackground.BackColor = Theme.Search;
        searchBox.BackColor = Theme.Search;
        searchBox.ForeColor = Theme.Strong;

        breadcrumb.FillColor = Theme.Strong;
        breadcrumb.OutlineColor = Theme.Background;
        breadcrumb.Invalidate();

        notification.ApplyTheme();

        UpdateStatus(translator.Enabled);
        menuBox.Invalidate(true);

        // Rebuild the rows so they pick up the new colours / transparency.
        RefreshPage();
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

        list.Add(Act("", $"Window opacity: {OpacityValue()}%", () =>
        {
            int next = (Array.IndexOf(OpacityChoices, OpacityValue()) + 1)
                % OpacityChoices.Length;

            settings.LauncherOpacity = OpacityChoices[next];
            SaveSettings();
            ApplyAppearance(reloadBackground: false);
        }));

        list.Add(new MenuEntry(0, "\uE7E7", "Notifications...", targetPage: "Notifications"));

        list.Add(Act("", "Reset appearance", ResetAppearance));
        list.Add(BackEntry());

        return Renumber(list);
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
                "Use PNG, JPG, BMP, GIF, or a video (MP4, WebM, MOV, MKV, AVI).");
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
            Toast("Could not load that image", OffColor);
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
        settings.BackgroundPath = "";
        settings.BackgroundFit = 0;
        settings.BackgroundAlign = 4;
        settings.BackgroundStrength = 40;
        settings.LauncherOpacity = 100;
        SaveSettings();

        ApplyAppearance(reloadBackground: false);
    }
}
