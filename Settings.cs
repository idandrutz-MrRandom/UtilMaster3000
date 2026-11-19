using System.Collections.Generic;
using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UtilMaster3000;

internal enum NotificationPosition
{
    BottomCenter,
    TopCenter,
    BottomLeft,
    BottomRight,
    TopLeft,
    TopRight
}

// ======================================================
// PERSISTED SETTINGS  (%AppData%\UtilMaster3000\settings.json)
// ======================================================

internal sealed class AppSettings
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;

    public uint HotkeyModifiers { get; set; } = MOD_CONTROL | MOD_ALT;
    public int HotkeyKey { get; set; } = (int)Keys.R;

    public double NotificationOpacity { get; set; } = 0.78;
    public NotificationPosition NotificationPosition { get; set; } =
        NotificationPosition.BottomCenter;
    public bool ShowNotification { get; set; } = true;

    // Notification customization
    public bool ShowMessages { get; set; } = true;            // the small "Copied..." style toasts
    public int NotificationHoldMs { get; set; } = 900;        // how long a toast stays
    public int NotificationScale { get; set; } = 100;         // size, percent

    // The "what is running" banner
    public bool ShowRunningBanner { get; set; } = true;
    public NotificationPosition BannerPosition { get; set; } = NotificationPosition.TopCenter;
    public int BannerScale { get; set; } = 100;
    public int BannerOpacityPercent { get; set; } = 94;

    public bool HideOnFocusLoss { get; set; }

    /// <summary>Menus slide out beside the launcher when you rest the mouse on them.</summary>
    public bool HoverSubmenus { get; set; } = true;

    // Global shortcut that opens the launcher (default Ctrl + Alt + U).
    public uint LauncherHotkeyModifiers { get; set; } = MOD_CONTROL | MOD_ALT;
    public int LauncherHotkeyKey { get; set; } = (int)Keys.U;

    // Holding the translator shortcut this long (ms) opens the menu
    // instead of toggling the translator. 0 = disabled.
    public int HoldToOpenMs { get; set; } = 450;

    // 0 = clipboard, 1 = file, 2 = both.
    public int ScreenshotMode { get; set; }

    // Print Screen = screenshot, Shift + Print Screen = record.
    public bool PrintScreenKeys { get; set; } = true;

    // Rebound shortcuts (screenshots, recording, clipboard). Missing = default.
    public Dictionary<string, ShortcutBinding> Shortcuts { get; set; } = new();

    // Windows + V opens the launcher's clipboard instead of Windows' own.
    public bool ClipboardWinV { get; set; } = true;

    // Video files found by the launcher open in the built-in player.
    public bool VideosInPlayer { get; set; } = true;

    // 0 = standard, 1 = high, 2 = max.
    public int RecordingQuality { get; set; } = 1;
    public int RecordingFps { get; set; } = 30;
    public bool RecordFullScreen { get; set; }

    // Screen recordings (MP4) also capture the sound the PC is playing.
    public bool RecordSound { get; set; } = true;
    public bool CalcDegrees { get; set; }

    // Autoclicker
    public bool AcHotkeyOn { get; set; } = true;
    public int AcIntervalMs { get; set; } = 100;
    public int AcJitterPercent { get; set; }
    public int AcButton { get; set; }
    public bool AcDouble { get; set; }
    public bool AcFixedPoint { get; set; }
    public int AcX { get; set; }
    public int AcY { get; set; }
    public int AcRepeat { get; set; }
    public int AcStartDelayMs { get; set; }

    // Macros
    public bool MacroHotkeysOn { get; set; } = true;
    public int MacroRepeat { get; set; } = 1;
    public int MacroSpeedPercent { get; set; } = 100;
    public bool MacroRecordMoves { get; set; } = true;
    public string LastMacro { get; set; } = "";

    // A finished recording is also put on the clipboard (and its history).
    public bool RecordToClipboard { get; set; } = true;

    // Clipboard history: false = left click pastes, right click opens a
    // picture / video in the player. true = the other way round.
    public bool ClipboardLeftOpens { get; set; }

    public int VaultAutoLockMinutes { get; set; } = 5;

    // ---- appearance ----
    public string ThemeName { get; set; } = "Tokyo Night";
    public string CustomBackground { get; set; } = "";
    public string CustomAccent { get; set; } = "";
    public string CustomText { get; set; } = "";

    // Copy of the chosen image / GIF / converted video, inside AppData.
    public string BackgroundPath { get; set; } = "";
    public int BackgroundFit { get; set; }
    public int BackgroundAlign { get; set; } = 4;
    public int BackgroundStrength { get; set; } = 40;

    // Launcher window opacity in percent.
    public int LauncherOpacity { get; set; } = 100;

    // Index into MainForm.SearchEngines.
    public int SearchEngine { get; set; }

    // Most recently launched apps, newest first.
    public System.Collections.Generic.List<string> RecentApps { get; set; } = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000",
        "settings.json"
    );

    // The app used to be called LatinToRussian: carry its settings over.
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LatinToRussian",
        "settings.json"
    );

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }

            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath)
                ) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt or unreadable file: fall back to defaults.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(
                    this,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}

// ======================================================
// START WITH WINDOWS
// ======================================================

internal static class StartupManager
{
    private const string RunKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "UtilMaster3000";
    private const string LegacyValueName = "LatinToRussian";

    /// <summary>
    /// If the old "LatinToRussian" startup entry exists, replace it
    /// with one that points at this (renamed) executable.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using RegistryKey? key =
                Registry.CurrentUser.OpenSubKey(RunKey, writable: true);

            if (key?.GetValue(LegacyValueName) == null)
                return;

            key.DeleteValue(LegacyValueName, false);
            SetEnabled(true);
        }
        catch
        {
            // Best effort only.
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key =
                Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled)
            {
                string? path = Environment.ProcessPath;

                if (string.IsNullOrEmpty(path))
                    return false;

                key.SetValue(ValueName, $"\"{path}\" --startup");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
