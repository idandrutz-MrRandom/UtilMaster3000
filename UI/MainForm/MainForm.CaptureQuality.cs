using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// CAPTURE > QUALITY AND OUTPUT
// Everything that decides how good a screenshot or a recording looks and where it ends up:
// screenshot format / JPG quality / size / pointer / delay / folder, video quality / frame rate /
// size / encoder (CPU or graphics card) / speed / pointer / sound quality / countdown / folder,
// and the GIF frame rate, width and length. Click a row for the next value, right-click for the previous.
// ======================================================

public sealed partial class MainForm
{
    private static readonly string[] EncoderLabels = { "CPU (x264)", "NVIDIA graphics card", "AMD graphics card", "Intel graphics" };

    private static readonly string[] PresetLabels = { "lightest on the CPU", "fast", "balanced", "smallest files" };

    // A row that steps through values (click = next, right-click = previous) and remembers the choice.
    private MenuEntry CaptureCycle<T>(
        string icon, string label, T[] values, Func<T> get, Action<T> set, Func<T, string> format, string tip)
    {
        void Step(int direction)
        {
            int i = Array.IndexOf(values, get());

            if (i < 0)
                i = 0;

            set(values[(i + direction + values.Length) % values.Length]);
            SaveSettings();
            RefreshPage();
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(0, icon, text,
            run: () => Step(1), runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next value, right-click = previous.");
    }

    // A row that picks a folder (click) or goes back to the default one (right-click).
    private MenuEntry CaptureFolderRow(
        string label, string description, Func<string> get, string fallback, Action<string> set)
    {
        string folder = get();
        bool custom = folder != fallback;

        return new MenuEntry(0, "", $"{label}: {(custom ? Trunc(folder, 40) : "default")}",
            run: () =>
            {
                HideToTray();

                using FolderBrowserDialog dialog = new()
                {
                    Description = description,
                    SelectedPath = Directory.Exists(folder) ? folder : fallback
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    set(dialog.SelectedPath);
                    SaveSettings();
                }

                ShowPage("CaptureQuality");
                OpenLauncher();
            },
            runAlt: () =>
            {
                set("");
                SaveSettings();
                RefreshPage();
            },
            expandText: $"{label}: {folder}" + NL + description + NL + "Click = choose a folder, right-click = back to the default.");
    }

    // Tries the next encoder in the direction given and keeps the first one this PC really supports.
    private async void StepEncoder(int direction)
    {
        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (ffmpeg == null)
        {
            Toast("Video recording needs ffmpeg (Capture > Download ffmpeg)", OffColor);
            return;
        }

        int current = Math.Clamp(settings.RecordingEncoder, 0, EncoderLabels.Length - 1);
        List<string> unavailable = new();

        for (int step = 1; step <= EncoderLabels.Length; step++)
        {
            int candidate = ((current + direction * step) % EncoderLabels.Length + EncoderLabels.Length) % EncoderLabels.Length;

            if (candidate != 0)
                Toast("Testing " + EncoderLabels[candidate] + "...", OnColor);

            bool works = await Task.Run(() => FfmpegRecorder.EncoderWorks(ffmpeg, candidate));

            if (IsDisposed)
                return;

            if (works)
            {
                settings.RecordingEncoder = candidate;
                SaveSettings();
                RefreshPage();

                Toast(unavailable.Count > 0
                    ? $"Encoder: {EncoderLabels[candidate]}  ({string.Join(", ", unavailable)} not available on this PC)"
                    : "Encoder: " + EncoderLabels[candidate], OnColor);
                return;
            }

            unavailable.Add(EncoderLabels[candidate]);
        }
    }

    private List<MenuEntry> BuildCaptureQualityPage()
    {
        List<MenuEntry> list = new();

        // ---- screenshots ----
        list.Add(Info("Screenshots"));

        list.Add(CaptureCycle("", "Format", new[] { 0, 1, 2 },
            () => Math.Clamp(settings.ScreenshotFormat, 0, 2), v => settings.ScreenshotFormat = v,
            v => ScreenshotFormats[v],
            "PNG is lossless and sharp (best for text and screens). JPG files are much smaller. BMP is raw and huge."));

        list.Add(CaptureCycle("", "JPG quality", new[] { 60, 70, 80, 85, 90, 92, 95, 100 },
            () => settings.ScreenshotJpgQuality, v => settings.ScreenshotJpgQuality = v, v => v + "%",
            "Only used when the format is JPG. Higher = sharper and bigger."));

        list.Add(CaptureCycle("", "Size", new[] { 100, 75, 50, 25 },
            () => settings.ScreenshotScalePercent, v => settings.ScreenshotScalePercent = v,
            v => v == 100 ? "original" : v + "% of the original",
            "Shrinks every screenshot, for the clipboard and the file. 100% keeps the screen's own size."));

        list.Add(CaptureCycle("", "Include the mouse pointer", new[] { false, true },
            () => settings.ScreenshotCursor, v => settings.ScreenshotCursor = v, v => OnOff(v),
            "Draws the pointer onto full-screen and window screenshots. (A chosen area never has it: the screen is frozen before you pick.)"));

        list.Add(CaptureCycle("", "Wait before capturing", new[] { 0, 3, 5, 10 },
            () => settings.ScreenshotDelaySeconds, v => settings.ScreenshotDelaySeconds = v,
            v => v == 0 ? "no wait" : v + " s",
            "A pause after the menu hides and before the screen is taken, so you can open a menu or a tooltip first."));

        list.Add(CaptureFolderRow("Screenshots folder", "Where should screenshots be saved?",
            () => PicturesFolder, DefaultPicturesFolder, v => settings.ScreenshotFolder = v));

        // ---- video ----
        list.Add(Info("Video recordings (MP4)"));

        list.Add(CaptureCycle("", "Quality", new[] { 0, 1, 2, 3, 4 },
            QualityIndex, v => settings.RecordingQuality = v, v => QualityNames[v],
            "small file: smallest files, softer picture. standard: a good balance. high: sharp. max: very sharp, big files. " +
            "near-lossless: practically no loss, huge files."));

        list.Add(CaptureCycle("", "Frame rate", FpsChoices,
            FpsValue, v => settings.RecordingFps = v, v => v + " fps",
            "Smoother motion needs more power and bigger files. 30 is fine for most things, 60 for games."));

        list.Add(CaptureCycle("", "Video size", new[] { 100, 75, 50, 25 },
            () => settings.RecordingScalePercent, v => settings.RecordingScalePercent = v,
            v => v == 100 ? "original" : v + "% of the area",
            "Records at a smaller size: much smaller files and less load on the PC."));

        list.Add(new MenuEntry(0, "",
            $"Encoder: {EncoderLabels[Math.Clamp(settings.RecordingEncoder, 0, EncoderLabels.Length - 1)]}",
            run: () => StepEncoder(1), runAlt: () => StepEncoder(-1),
            expandText: "Encoder: " + EncoderLabels[Math.Clamp(settings.RecordingEncoder, 0, EncoderLabels.Length - 1)] + NL +
                        "A graphics card encodes with almost no CPU use (good while gaming). Only cards this PC really supports are offered; each is tested first." +
                        NL + "Click = next, right-click = previous."));

        list.Add(CaptureCycle("", "CPU speed (CPU encoder only)", new[] { 0, 1, 2, 3 },
            () => Math.Clamp(settings.RecordingPreset, 0, 3), v => settings.RecordingPreset = v, v => PresetLabels[v],
            "Lightest keeps the PC free; smallest files uses more CPU for a smaller video of the same quality."));

        list.Add(CaptureCycle("", "Include the mouse pointer", new[] { true, false },
            () => settings.RecordingCursor, v => settings.RecordingCursor = v, v => OnOff(v),
            "Shows the pointer in video recordings."));

        list.Add(CaptureCycle("", "Sound quality", new[] { 96, 128, 192, 256, 320 },
            () => settings.RecordingAudioKbps, v => settings.RecordingAudioKbps = v, v => v + " kbps",
            "The bitrate of the recorded sound. 192 is a good default."));

        list.Add(CaptureCycle("", "Countdown before recording", new[] { 0, 3, 5, 10 },
            () => settings.RecordingCountdownSeconds, v => settings.RecordingCountdownSeconds = v,
            v => v == 0 ? "none" : v + " s",
            "A short wait after you choose the area, before recording starts."));

        list.Add(CaptureFolderRow("Recordings folder", "Where should recordings be saved?",
            () => VideosFolder, DefaultVideosFolder, v => settings.RecordingFolder = v));

        // ---- GIF ----
        list.Add(Info("GIF recordings"));

        list.Add(CaptureCycle("", "Frame rate", new[] { 5, 10, 15, 20 },
            () => settings.GifFps, v => settings.GifFps = v, v => v + " fps",
            "More frames = smoother but a much bigger file."));

        list.Add(CaptureCycle("", "Widest picture", new[] { 480, 720, 1080 },
            () => settings.GifMaxWidth, v => settings.GifMaxWidth = v, v => v + " px",
            "Wider areas are shrunk to this width. Smaller = smaller file."));

        list.Add(CaptureCycle("", "Longest recording", new[] { 15, 30, 60, 120 },
            () => settings.GifMaxSeconds, v => settings.GifMaxSeconds = v, v => v + " s",
            "A GIF stops by itself after this long (long GIFs get very big)."));

        list.Add(BackEntry());

        return Renumber(list);
    }

}
