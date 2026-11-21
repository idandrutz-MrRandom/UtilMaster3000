using System;
using System.Collections.Generic;

namespace UtilMaster3000;

// ======================================================
// SET-UP GUIDE  (home -> Set-up guide, also Setup -> Set-up guide)
// A walk through everything worth setting up, in the order most people want it.
// Each step explains itself when you rest on it and does (or opens) the thing
// when you click it. Shown once on the very first start.
// ======================================================

public sealed partial class MainForm
{
    private List<MenuEntry> BuildGuidePage()
    {
        string launcher = FormatHotkey(launcherKey);
        string toggle = FormatHotkey(toggleKey);
        string clipboard = KeyLabel("clipboard");

        MenuEntry Step(string icon, string label, string detail, Action run) =>
            new(0, icon, label, run: run, expandText: label + NL + detail);

        List<MenuEntry> list = new()
        {
            Info("Click a step to do it. Rest on one to read more. Lost? Type what you want in the search bar."),

            Step("", $"1. Open the menu: {launcher}",
                $"Press {launcher} anywhere to open or close the launcher (this guide is still here when it reopens), " +
                "or double-click the tray icon. " +
                $"{clipboard} opens the clipboard history. Click this step to change any shortcut.",
                () => ShowPage("Shortcuts")),

            Step("", $"2. Typing: {ModeNames[(int)translator.Mode]}",
                "Letter conversion En -> Ru turns English letters into Russian as you type (privet -> привет). " +
                "Weird text restyles your letters instead. Off removes the keyboard hook completely." + NL +
                $"Tap {toggle} to switch it on or off (hold it to open the menu). Click this step to choose.",
                () => ShowPage("Typing")),

            Step("", "3. Make it look like yours",
                "Opens the style editor: theme tiles, colours that update while you drag, sizes, rounded corners, " +
                "a background picture, GIF or video. You can save your look to a file and share it.",
                OpenStudio),

            Step("", "4. Slide-out menus and speed",
                "Rest the mouse on a row that opens a menu and it slides out beside the launcher. " +
                "On the Setup page you choose how long it waits, which side it opens on, where it lines up, " +
                "or turn it off and open menus by clicking.",
                () => ShowPage("Setup")),

            Step("", "5. Screenshots and recording",
                $"{KeyLabel("shot-area")}: screenshot of an area. {KeyLabel("rec-area")}: record an area. " +
                "Press the same key again to stop. Pictures go to Pictures\\UtilMaster3000 and recordings to " +
                "Videos\\UtilMaster3000. Click this step to see every option.",
                () => ShowPage("Capture")),

            Step("",
                HasFfmpeg ? "6. ffmpeg: installed" : "6. Download ffmpeg (MP4 recording, UtilTube)",
                HasFfmpeg
                    ? "ffmpeg is ready: MP4 recordings, UtilTube downloads and video backgrounds will work."
                    : $"MP4 recordings, UtilTube downloads and video backgrounds need ffmpeg ({FfmpegInstaller.ApproxSize}). " +
                      "Click to download it. It is checked against its published checksum and kept in the app's own folder.",
                DownloadFfmpeg),

            Step("", "7. UtilTube: YouTube you can watch offline",
                "Search or paste a YouTube link. The first time it asks before downloading yt-dlp. " +
                "While a game or any fullscreen program is in front, downloads pause so your ping stays low; " +
                "the speed limits are in UtilTube > Settings.",
                () => OpenTube(null)),

            Step("", "8. Clipboard history",
                $"Copy things as usual, then press {clipboard}: click an item to paste it into the window you came from. " +
                "Right-click opens a picture or video in the built-in player.",
                () => ShowPage("Clipboard")),

            Step("", "9. Vault: your passwords",
                "Stores logins encrypted with your master password. The master password is never saved and cannot " +
                "be recovered, so choose one you will remember.",
                () => ShowPage("Vault")),

            Step("", "10. Add your own tools (modules)",
                "A module is a small text file in the modules folder that adds your own entries: open a folder or " +
                "website, run a command or script, copy or paste text. Click to see your modules and create an example. " +
                "Your entries also turn up in the search.",
                () => ShowPage("Modules")),

            new MenuEntry(0, "", $"11. Start with Windows: {OnOff(StartupManager.IsEnabled())}",
                command: "startup",
                expandText: "Starts the launcher hidden in the tray when you sign in. Click to switch."),

            new MenuEntry(0, "", $"12. Hide when I click elsewhere: {OnOff(settings.HideOnFocusLoss)}",
                command: "focus-hide",
                expandText: "On: the launcher closes when you click another window. Off: it stays until you close it."),

            Info("That is everything. Reopen this guide any time: home > Set-up guide."),

            BackEntry()
        };

        return Renumber(list);
    }
}
