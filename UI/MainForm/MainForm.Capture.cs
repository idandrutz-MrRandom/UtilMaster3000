using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

// Screenshots, screen recording, the colour picker and the Print Screen keys.
public sealed partial class MainForm
{
    private GifRecorder? gifRecorder;
    private FfmpegRecorder? mp4Recorder;

    private System.Windows.Forms.Timer? recordingTimer;
    private DateTime recordingStart;
    private ToolStripItem? stopRecordingItem;

    private PrintScreenHook? printScreen;

    // True while a selection / countdown is in progress, so a second
    // Print Screen press cannot start a second capture on top of it.
    private bool captureBusy;

    private bool IsRecording => gifRecorder != null || mp4Recorder != null;

    private static readonly string[] ScreenshotModes =
        { "clipboard", "file", "clipboard + file" };

    private static readonly string[] QualityNames =
        { "standard", "high", "max", "small file", "near-lossless" };

    // x264 constant rate factor: lower = better quality, bigger files.
    // (The first three are the older choices, kept so saved settings still mean the same.)
    private static readonly int[] QualityCrf = { 23, 18, 14, 28, 10 };

    private static readonly int[] FpsChoices = { 10, 15, 24, 30, 48, 60 };

    private static readonly string[] PresetNames = { "ultrafast", "veryfast", "faster", "medium" };

    private static string DefaultPicturesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "UtilMaster3000");

    private static string DefaultVideosFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "UtilMaster3000");

    // Where screenshots / recordings go: the folder chosen in Capture > Quality and output, else the default.
    private string PicturesFolder =>
        settings.ScreenshotFolder.Length > 0 ? settings.ScreenshotFolder : DefaultPicturesFolder;

    private string VideosFolder =>
        settings.RecordingFolder.Length > 0 ? settings.RecordingFolder : DefaultVideosFolder;

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");

    private static bool HasFfmpeg => FfmpegRecorder.FindFfmpeg() != null;

    private bool ffmpegBusy;

    // Offers to download ffmpeg (MP4 recordings, UtilTube downloads, video backgrounds
    // need it), then does it in the background with progress pop-ups.
    private async void DownloadFfmpeg()
    {
        if (ffmpegBusy)
        {
            Toast("ffmpeg is already downloading...");
            return;
        }

        if (HasFfmpeg)
        {
            Toast("ffmpeg is already installed", OnColor);
            return;
        }

        DialogResult answer = MessageBox.Show(
            "ffmpeg is a free, open-source tool. It is needed for MP4 screen recordings, " +
            "for joining the video and audio of UtilTube downloads and for video backgrounds.\n\n" +
            $"Download it now ({FfmpegInstaller.ApproxSize}) from its official build site? " +
            "The file is checked against its published checksum and only ffmpeg.exe is kept, " +
            "in this app's own folder.",
            "Download ffmpeg",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        ffmpegBusy = true;
        RefreshPage();

        // A bar with the percent, the speed and how long it will take.
        DownloadBar bar = DownloadBar.Start("Downloading ffmpeg");

        try
        {
            await FfmpegInstaller.InstallAsync(bar.Report, CancellationToken.None);

            bar.Finish("ffmpeg is installed: MP4 recording is ready", true);
        }
        catch (Exception ex)
        {
            bar.Finish("Download failed", false, 4000);
            Say("Download ffmpeg", "Could not download ffmpeg.\n\n" + ex.Message);
        }
        finally
        {
            ffmpegBusy = false;

            if (!IsDisposed)
                RefreshPage();
        }
    }

    // --------------------------------------------------
    // PRINT SCREEN KEYS
    // --------------------------------------------------

    private void SetupPrintScreen()
    {
        // Older settings: "Shift + Print Screen records full screen".
        if (settings.Shortcuts.Count == 0 && settings.RecordFullScreen)
        {
            settings.Shortcuts["rec-full"] =
                new ShortcutBinding(ShortcutCatalog.Shift, (int)Keys.Snapshot);

            settings.Shortcuts["rec-area"] = new ShortcutBinding(
                ShortcutCatalog.Ctrl | ShortcutCatalog.Shift, (int)Keys.Snapshot);
        }

        printScreen = new PrintScreenHook
        {
            IsEnabled = id => id == "clipboard"
                ? settings.ClipboardWinV
                : id == "autoclick"
                    ? settings.AcHotkeyOn
                    : id.StartsWith("macro-")
                        ? settings.MacroHotkeysOn
                        : settings.PrintScreenKeys
        };

        ApplyShortcuts();

        // Called from inside the keyboard hook: just hand over to the UI thread.
        printScreen.Triggered += id =>
        {
            try
            {
                BeginInvoke(new Action(() => OnShortcut(id)));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        };
    }

    // Gives the keyboard hook the current bindings.
    private void ApplyShortcuts()
    {
        if (printScreen == null)
            return;

        foreach (ShortcutCatalog.Info info in ShortcutCatalog.All)
        {
            ShortcutBinding b = ShortcutCatalog.Get(settings, info);
            printScreen.Set(info.Id, b.Mods, b.Key);
        }
    }

    private string KeyLabel(string id)
    {
        ShortcutCatalog.Info info = ShortcutCatalog.All.First(i => i.Id == id);
        return ShortcutCatalog.Describe(ShortcutCatalog.Get(settings, info));
    }

    private void OnShortcut(string id)
    {
        switch (id)
        {
            case "clipboard":
                if (Visible && !hiding && currentPage == "Clipboard")
                {
                    HideToTray();
                }
                else
                {
                    // What was just copied is in the list when it opens.
                    FlushClipboardRecord();
                    ShowPage("Clipboard");
                    OpenLauncher();
                }
                break;

            case "autoclick":
                ToggleAutoClick();
                break;

            case "macro-record":
                ToggleMacroRecording();
                break;

            case "macro-play":
                PlayMacro(null);
                break;

            case "shot-area":
                ScreenshotRegion();
                break;

            case "shot-full":
                ScreenshotFullScreen();
                break;

            case "shot-window":
                ScreenshotWindow(ForegroundTarget());
                break;

            case "rec-area":
            case "rec-full":
                if (IsRecording)
                    StopRecording();
                else
                    StartRecording(HasFfmpeg, id == "rec-full");
                break;
        }
    }

    // --------------------------------------------------
    // CHANGING SHORTCUTS
    // --------------------------------------------------

    private string? bindingShortcut;

    private void BeginShortcutBinding(string id)
    {
        if (printScreen == null || !printScreen.Installed)
        {
            Toast("The keyboard hook is not available", OffColor);
            return;
        }

        bindingShortcut = id;

        statusLabel.Text = "PRESS NEW SHORTCUT...  Esc cancels, Backspace clears";
        statusLabel.ForeColor = AccentColor;

        searchBox.Enabled = false;
        Focus();

        printScreen.CaptureModifiers = mods =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (bindingShortcut == id)
                    {
                        statusLabel.Text = mods == 0
                            ? "PRESS NEW SHORTCUT...  Esc cancels, Backspace clears"
                            : "PRESS NEW SHORTCUT:  " + HeldLabel(mods) + " + ...";
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        };

        // Reported by the hook, so even Windows + something can be chosen.
        printScreen.BeginCapture((mods, key) =>
        {
            try
            {
                BeginInvoke(new Action(() => FinishShortcutBinding(id, mods, key)));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        });
    }

    private static string HeldLabel(uint mods)
    {
        List<string> parts = new();

        if ((mods & ShortcutCatalog.Win) != 0) parts.Add("Windows");
        if ((mods & ShortcutCatalog.Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & ShortcutCatalog.Alt) != 0) parts.Add("Alt");
        if ((mods & ShortcutCatalog.Shift) != 0) parts.Add("Shift");

        return string.Join(" + ", parts);
    }

    private void CancelShortcutBinding()
    {
        if (bindingShortcut == null)
            return;

        bindingShortcut = null;
        printScreen?.EndCapture();

        searchBox.Enabled = true;
        UpdateStatus(translator.Enabled);
    }

    private void FinishShortcutBinding(string id, uint mods, int key)
    {
        if (bindingShortcut != id)
            return;

        bindingShortcut = null;
        searchBox.Enabled = true;

        ShortcutCatalog.Info info = ShortcutCatalog.All.First(i => i.Id == id);

        if (key == (int)Keys.Escape && mods == 0)
        {
            UpdateStatus(translator.Enabled);
            searchBox.Focus();
            return;
        }

        bool clear = (key == (int)Keys.Back || key == (int)Keys.Delete) && mods == 0;

        if (clear)
        {
            key = 0;
            mods = 0;
        }
        else
        {
            if (mods == 0 && !ShortcutCatalog.AllowedWithoutModifier(key))
            {
                Toast("Hold Ctrl, Alt, Shift or Windows with that key", OffColor);
                UpdateStatus(translator.Enabled);
                searchBox.Focus();
                return;
            }

            string? clash = ShortcutCatalog.All
                .Where(i => i.Id != id)
                .Where(i =>
                {
                    ShortcutBinding other = ShortcutCatalog.Get(settings, i);
                    return other.Key == key && other.Mods == mods;
                })
                .Select(i => i.Name)
                .FirstOrDefault();

            if (clash == null && toggleKey.Mods == mods && (int)toggleKey.Key == key)
                clash = toggleKey.Name;

            if (clash == null && launcherKey.Mods == mods && (int)launcherKey.Key == key)
                clash = launcherKey.Name;

            if (clash != null)
            {
                Toast($"Already used by: {clash}", OffColor);
                UpdateStatus(translator.Enabled);
                searchBox.Focus();
                return;
            }
        }

        settings.Shortcuts[id] = new ShortcutBinding(mods, key);
        ApplyShortcuts();
        SaveSettings();

        Toast(
            key == 0
                ? $"{info.Name}: shortcut removed"
                : $"{info.Name}: {ShortcutCatalog.Describe(mods, key)}",
            OnColor);

        RefreshPage();
        searchBox.Focus();
    }

    private List<MenuEntry> ShortcutsPage(string query)
    {
        List<MenuEntry> list = new();

        void Add(string name, string keys, Action run)
        {
            string label = $"{name}:  {keys}";

            if (query.Length > 0 &&
                !label.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            list.Add(Act("\uE765", label, run));
        }

        Add("Open the launcher", FormatHotkey(launcherKey),
            () => BeginHotkeyBinding(launcherKey));

        Add("Letter conversion En -> Ru on / off", FormatHotkey(toggleKey),
            () => BeginHotkeyBinding(toggleKey));

        foreach (ShortcutCatalog.Info info in ShortcutCatalog.All)
        {
            string id = info.Id;
            Add(info.Name, KeyLabel(id), () => BeginShortcutBinding(id));
        }

        if (query.Length == 0)
        {
            list.Add(Info("Pick a row, then press the new keys"));

            list.Add(Act("\uE777", "Reset shortcuts to defaults", () =>
            {
                settings.Shortcuts.Clear();
                ApplyShortcuts();
                SaveSettings();
                Toast("Shortcuts reset", OnColor);
                RefreshPage();
            }));

            list.Add(BackEntry());
        }

        return Renumber(list);
    }

    // The window the user is in right now (not our own launcher).
    private IntPtr ForegroundTarget()
    {
        IntPtr window = Native.GetForegroundWindow();

        return window != IntPtr.Zero &&
               !WindowList.IsOwnWindow(window) &&
               !WindowList.IsShellWindow(window)
            ? window
            : lastExternalWindow;
    }

    // Hides the launcher (if shown) and returns how long to wait for the
    // fade to finish before grabbing the screen.
    private int HideLauncherForCapture()
    {
        bool wasVisible = Visible;

        HideToTray();

        return wasVisible ? 320 : 60;
    }

    // --------------------------------------------------
    // CAPTURE PAGE
    // --------------------------------------------------

    private List<MenuEntry> BuildCapturePage()
    {
        List<MenuEntry> list = new();

        string kind = HasFfmpeg ? "MP4" : "GIF";

        if (IsRecording)
        {
            list.Add(Act("", $"Stop recording ({ElapsedLabel()})",
                () => StopRecording()));
        }

        list.Add(Act("", $"Screenshot: choose area  ({KeyLabel("shot-area")})",
            ScreenshotRegion));
        list.Add(Act("", $"Screenshot: full screen  ({KeyLabel("shot-full")})", ScreenshotFullScreen));
        list.Add(Act("", $"Screenshot: window  ({KeyLabel("shot-window")})",
            () => ScreenshotWindow(null)));

        if (!IsRecording)
        {
            list.Add(Act("", $"Record area → {kind}  ({KeyLabel("rec-area")})",
                () => StartRecording(HasFfmpeg, fullScreen: false)));

            list.Add(Act("", $"Record full screen → {kind}  ({KeyLabel("rec-full")})",
                () => StartRecording(HasFfmpeg, fullScreen: true)));

            if (HasFfmpeg)
            {
                list.Add(Act("", $"Record area → GIF ({Math.Clamp(settings.GifMaxSeconds, 5, 600)} s max)",
                    () => StartRecording(false, fullScreen: false)));
            }
            else
            {
                list.Add(Act("", ffmpegBusy
                        ? "Downloading ffmpeg..."
                        : $"Download ffmpeg for MP4 recordings ({FfmpegInstaller.ApproxSize})",
                    DownloadFfmpeg));
            }
        }

        list.Add(Act("",
            $"Screenshots go to: {ScreenshotModes[ScreenshotModeIndex()]}", () =>
            {
                settings.ScreenshotMode =
                    (ScreenshotModeIndex() + 1) % ScreenshotModes.Length;

                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("",
            $"Recording quality: {QualityNames[QualityIndex()]}", () =>
            {
                settings.RecordingQuality =
                    (QualityIndex() + 1) % QualityNames.Length;

                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("", $"Recording frame rate: {FpsValue()} fps", () =>
        {
            int next = (Array.IndexOf(FpsChoices, FpsValue()) + 1) % FpsChoices.Length;

            settings.RecordingFps = FpsChoices[next];
            SaveSettings();
            RefreshPage();
        }));


        list.Add(new MenuEntry(0, "", "Quality and output...", targetPage: "CaptureQuality",
            expandText: "Screenshot format, JPG quality, size, pointer, delay and folder; video quality, frame rate, size, encoder, sound quality and folder; GIF frame rate, width and length."));

        list.Add(Act("",
            $"Capture shortcuts: {OnOff(settings.PrintScreenKeys)}", () =>
            {
                settings.PrintScreenKeys = !settings.PrintScreenKeys;

                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("\uE767",
            $"Record sound with videos: {OnOff(settings.RecordSound)}", () =>
            {
                settings.RecordSound = !settings.RecordSound;
                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("\uE8C8",
            $"Finished recordings go to the clipboard: {OnOff(settings.RecordToClipboard)}", () =>
            {
                settings.RecordToClipboard = !settings.RecordToClipboard;
                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("",
            $"Menu opens while recording: {OnOff(settings.MenuWhileRecording)}", () =>
            {
                settings.MenuWhileRecording = !settings.MenuWhileRecording;
                SaveSettings();
                RefreshPage();
            }));

        list.Add(Act("", "Open screenshots folder",
            () => OpenFolder(PicturesFolder)));

        list.Add(Act("", "Open recordings folder",
            () => OpenFolder(VideosFolder)));

        list.Add(new MenuEntry(0, "\uE765", "Change shortcuts...", targetPage: "Shortcuts"));

        list.Add(BackEntry());

        return Renumber(list);
    }

    private int ScreenshotModeIndex() =>
        Math.Clamp(settings.ScreenshotMode, 0, ScreenshotModes.Length - 1);

    private int QualityIndex() =>
        Math.Clamp(settings.RecordingQuality, 0, QualityNames.Length - 1);

    private int FpsValue() =>
        FpsChoices.Contains(settings.RecordingFps) ? settings.RecordingFps : 30;

    private void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        OpenTarget(folder);
    }

    // --------------------------------------------------
    // SCREENSHOTS
    // --------------------------------------------------

    private static readonly string[] ScreenshotFormats = { "PNG (lossless)", "JPG (smaller)", "BMP (raw)" };

    // Sends a finished screenshot where the user wants it.
    private void FinishScreenshot(Bitmap bitmap)
    {
        int mode = ScreenshotModeIndex();

        // A smaller copy when asked (the clipboard and the file both get it).
        int percent = Math.Clamp(settings.ScreenshotScalePercent, 10, 100);
        Bitmap? scaled = null;

        if (percent < 100)
        {
            scaled = new Bitmap(
                Math.Max(1, bitmap.Width * percent / 100), Math.Max(1, bitmap.Height * percent / 100));

            using Graphics g = Graphics.FromImage(scaled);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(bitmap, 0, 0, scaled.Width, scaled.Height);
        }

        Bitmap output = scaled ?? bitmap;

        try
        {
            List<string> done = new();
            string? fileName = null;

            if (mode != 1)
            {
                Clipboard.SetImage(output);
                done.Add("copied");
            }

            if (mode != 0)
            {
                Directory.CreateDirectory(PicturesFolder);

                int format = Math.Clamp(settings.ScreenshotFormat, 0, 2);
                string extension = format switch { 1 => "jpg", 2 => "bmp", _ => "png" };

                string path = Path.Combine(PicturesFolder, $"screenshot {Stamp()}.{extension}");

                if (format == 1)
                {
                    ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using EncoderParameters quality = new(1);

                    quality.Param[0] = new EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(settings.ScreenshotJpgQuality, 10, 100));

                    output.Save(path, jpeg, quality);
                }
                else
                {
                    output.Save(path, format == 2 ? ImageFormat.Bmp : ImageFormat.Png);
                }

                fileName = Path.GetFileName(path);
                done.Add("saved");
            }

            Toast(
                "Screenshot " + string.Join(" + ", done) +
                (fileName != null ? ": " + fileName : ""),
                OnColor
            );
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // Runs a capture step after the launcher has faded, guarding against
    // a second capture starting while this one is still in progress.
    private void RunCapture(Action step)
    {
        if (captureBusy)
            return;

        captureBusy = true;

        // An optional wait first (Capture > Quality and output), to set the screen up.
        int delay = Math.Clamp(settings.ScreenshotDelaySeconds, 0, 30);

        if (delay > 0)
            Toast($"Capturing in {delay} s...", OnColor);

        After(HideLauncherForCapture() + delay * 1000, () =>
        {
            try
            {
                Safe(step);
            }
            finally
            {
                captureBusy = false;
            }
        });
    }

    // Shows the frozen screen and lets the user drag an area.
    // Returns the area in real screen coordinates, or null if cancelled.
    private static Rectangle? SelectScreenArea(string hint)
    {
        Rectangle virtualScreen = SystemInformation.VirtualScreen;

        using Bitmap shot = SystemTools.CaptureScreen();

        Rectangle? area = RegionSelector.Select(shot, virtualScreen, hint);

        if (area == null)
            return null;

        return new Rectangle(
            area.Value.X + virtualScreen.X,
            area.Value.Y + virtualScreen.Y,
            area.Value.Width,
            area.Value.Height);
    }

    private void ScreenshotRegion()
    {
        RunCapture(() =>
        {
            Rectangle? area = SelectScreenArea(
                "Drag to choose the area to capture  -  Esc to cancel");

            if (area == null)
                return;

            using Bitmap cropped = SystemTools.CaptureRegion(area.Value);
            FinishScreenshot(cropped);
        });
    }

    private void ScreenshotFullScreen()
    {
        RunCapture(() =>
        {
            using Bitmap shot = SystemTools.CaptureScreen();

            if (settings.ScreenshotCursor)
                SystemTools.DrawCursor(shot, SystemInformation.VirtualScreen.Location);

            FinishScreenshot(shot);
        });
    }

    private void ScreenshotWindow(IntPtr? target)
    {
        IntPtr window = target ?? lastExternalWindow;

        if (window == IntPtr.Zero || !Native.IsWindow(window))
        {
            Toast("No window to capture", OffColor);
            return;
        }

        bool alreadyFront = Native.GetForegroundWindow() == window;

        RunCapture(() =>
        {
            if (!alreadyFront)
            {
                WindowList.Activate(window);
                System.Threading.Thread.Sleep(350);
            }

            Rectangle bounds = Rectangle.Intersect(
                WindowList.GetBounds(window),
                SystemInformation.VirtualScreen);

            if (bounds.Width < 2 || bounds.Height < 2)
            {
                Toast("That window is not visible", OffColor);
                return;
            }

            using Bitmap shot = SystemTools.CaptureRegion(bounds);

            if (settings.ScreenshotCursor)
                SystemTools.DrawCursor(shot, bounds.Location);

            FinishScreenshot(shot);
        });
    }

    // --------------------------------------------------
    // COLOUR PICKER (click to pick)
    // --------------------------------------------------

    private void PickColorOverlay()
    {
        Safe(() =>
        {
            Rectangle virtualScreen = SystemInformation.VirtualScreen;

            using Bitmap shot = SystemTools.CaptureScreen();

            Color? picked = ColorPickerForm.Pick(shot, virtualScreen);

            if (picked == null)
                return;

            Color color = picked.Value;
            string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

            SetClipboard(hex);
            Toast($"{hex} copied", color);
        });
    }

    // --------------------------------------------------
    // RECORDING
    // --------------------------------------------------

    private void StartRecording(bool video, bool fullScreen)
    {
        if (IsRecording || captureBusy)
            return;

        captureBusy = true;

        After(HideLauncherForCapture(), () =>
        {
            Rectangle? area = null;

            try
            {
                area = fullScreen
                    ? Screen.PrimaryScreen?.Bounds
                    : SelectScreenArea(
                        "Drag to choose the recording area  -  Esc to cancel");
            }
            catch (Exception ex)
            {
                Toast("Failed: " + Trunc(ex.Message, 60), OffColor);
            }

            if (area == null)
            {
                captureBusy = false;
                return;
            }

            // Let the selector disappear before the first frame (plus the countdown, if one is chosen).
            int countdown = Math.Clamp(settings.RecordingCountdownSeconds, 0, 30);

            if (countdown > 0)
                Toast($"Recording starts in {countdown} s...", OnColor);

            After(200 + countdown * 1000, () =>
            {
                try
                {
                    Safe(() => BeginRecording(area.Value, video));
                }
                finally
                {
                    captureBusy = false;
                }
            });
        });
    }

    private static long FreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    private void BeginRecording(Rectangle area, bool video)
    {
        if (area.Width < 32 || area.Height < 32)
        {
            Toast("That area is too small to record", OffColor);
            return;
        }

        Directory.CreateDirectory(VideosFolder);

        if (FreeSpace(VideosFolder) < 500L * 1024 * 1024)
        {
            Toast("Not enough free disk space to record", OffColor);
            return;
        }

        if (video)
        {
            string ffmpeg = FfmpegRecorder.FindFfmpeg()
                ?? throw new InvalidOperationException("ffmpeg not found");

            mp4Recorder = FfmpegRecorder.Start(
                ffmpeg,
                area,
                Path.Combine(VideosFolder, $"recording {Stamp()}.mp4"),
                new FfmpegRecorder.Options(
                    Fps: FpsValue(),
                    Crf: QualityCrf[QualityIndex()],
                    Preset: PresetNames[Math.Clamp(settings.RecordingPreset, 0, PresetNames.Length - 1)],
                    ScalePercent: settings.RecordingScalePercent,
                    Cursor: settings.RecordingCursor,
                    Encoder: settings.RecordingEncoder,
                    AudioKbps: settings.RecordingAudioKbps,
                    Sound: settings.RecordSound));
        }
        else
        {
            GifRecorder recorder = new(
                area,
                Path.Combine(VideosFolder, $"recording {Stamp()}.gif"),
                settings.GifFps,
                settings.GifMaxWidth)
            {
                MaxSeconds = Math.Clamp(settings.GifMaxSeconds, 5, 600)
            };

            recorder.AutoStopped += () => StopRecording();
            recorder.Start();

            gifRecorder = recorder;
        }

        recordingStart = DateTime.Now;

        recordingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        recordingTimer.Tick += (_, _) => RecordingTick();
        recordingTimer.Start();

        if (stopRecordingItem != null)
            stopRecordingItem.Visible = true;

        ShowRecordingToast();

        if (currentPage == "Capture")
            RefreshPage();
    }

    private string ElapsedLabel()
    {
        TimeSpan elapsed = DateTime.Now - recordingStart;

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
    }

    // The finished recording as a file on the clipboard: paste it anywhere,
    // and it shows up in the clipboard history.
    private bool CopyFileToClipboard(string file)
    {
        try
        {
            var list = new System.Collections.Specialized.StringCollection { file };
            DataObject data = new();
            data.SetFileDropList(list);

            Clipboard.SetDataObject(data, true, 5, 100);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            return false;
        }
    }

    private string? RecordingPath => mp4Recorder?.Path ?? gifRecorder?.Path;

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" :
        $"{bytes / (double)(1L << 20):0} MB";

    private void ShowRecordingToast()
    {
        string stopKey = settings.PrintScreenKeys
            ? (ShortcutCatalog.Get(settings, ShortcutCatalog.All.First(i => i.Id == "rec-area")).Key != 0
                ? KeyLabel("rec-area")
                : KeyLabel("rec-full"))
            : FormatHotkey(launcherKey);

        string size = "";

        try
        {
            string? path = RecordingPath;

            if (path != null && File.Exists(path))
                size = "  " + FormatSize(new FileInfo(path).Length);
        }
        catch
        {
            // Size is just informational.
        }

        Toast($"REC {ElapsedLabel()}{size}  -  {stopKey} to stop", OffColor);
    }

    private void RecordingTick()
    {
        // ffmpeg died on its own (bad area, encoder error, killed...).
        if (mp4Recorder != null && mp4Recorder.HasExited)
        {
            string log = mp4Recorder.LastLog;

            StopRecording();

            if (log.Length > 0)
                Toast("Recording ended: " + Trunc(log, 70), OffColor);

            return;
        }

        // Stop cleanly before the disk fills up and corrupts the file.
        if (FreeSpace(VideosFolder) < 300L * 1024 * 1024)
        {
            StopRecording();
            Toast("Recording stopped: disk almost full", OffColor);
            return;
        }

        ShowRecordingToast();
    }

    private void StopRecording()
    {
        if (!IsRecording)
            return;

        recordingTimer?.Stop();
        recordingTimer?.Dispose();
        recordingTimer = null;

        if (mp4Recorder != null)
            Toast("Finishing the video...", OnColor);

        string? output = null;

        try
        {
            if (gifRecorder != null)
            {
                output = gifRecorder.Stop();
                gifRecorder = null;
            }

            if (mp4Recorder != null)
            {
                output = mp4Recorder.Stop();
                mp4Recorder = null;
            }
        }
        catch (Exception ex)
        {
            gifRecorder = null;
            mp4Recorder = null;

            Toast("Recording failed: " + Trunc(ex.Message, 50), OffColor);
        }

        if (stopRecordingItem != null)
            stopRecordingItem.Visible = false;

        if (output != null && File.Exists(output) && new FileInfo(output).Length > 0)
        {
            bool copied = settings.RecordToClipboard && CopyFileToClipboard(output);

            Toast(
                $"Saved: {Path.GetFileName(output)}  " +
                FormatSize(new FileInfo(output).Length) +
                (copied ? "  (also on the clipboard)" : ""),
                OnColor);
        }

        if (currentPage == "Capture")
            RefreshPage();
    }
}
