using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// DOWNLOAD BY LINK PAGE  (Tools -> Download by link)
// Paste a link, press Enter: the video (or just the audio) is saved
// into one folder and the file is copied to the clipboard, ready to
// paste into a chat or a folder.
// ======================================================

public sealed partial class MainForm
{
    private CancellationTokenSource? grabCts;
    private volatile bool grabbing;
    private double grabPercent;
    private string grabText = "";
    private string grabName = "";

    private static readonly int[] GrabQualities = { 0, 1080, 720, 480 };

    private string GrabFolder() =>
        string.IsNullOrWhiteSpace(settings.GrabFolder) ? LinkDownloader.DefaultFolder : settings.GrabFolder;

    private static string ShortUrl(string url) =>
        url.Length <= 54 ? url : url[..51] + "...";

    /// <summary>Starts a download (from the page, the clipboard or the home search box).</summary>
    private void StartGrab(string link)
    {
        link = link.Trim();

        if (!LinkDownloader.LooksLikeLink(link))
        {
            Toast("That is not a link (it should start with http)", OffColor);
            return;
        }

        if (grabbing)
        {
            Toast("Already downloading: wait for it, or cancel it from Tools -> Download by link", OffColor);
            return;
        }

        grabbing = true;
        grabPercent = 0;
        grabName = LinkDownloader.SiteName(link);
        grabText = "starting...";

        CancellationTokenSource cts = grabCts = new CancellationTokenSource();
        string folder = GrabFolder();
        bool audio = settings.GrabAudioOnly;
        int height = settings.GrabQuality;
        bool copy = settings.GrabCopy;

        // Out of the way, so you can carry on; the banner shows the progress.
        if (Visible && !hiding)
            HideToTray();

        Toast($"Downloading from {grabName}...", AccentColor);

        _ = Task.Run(async () =>
        {
            GrabResult result;

            try
            {
                result = await LinkDownloader.DownloadAsync(link, audio, height, folder, (percent, text) =>
                {
                    grabPercent = percent;
                    grabText = text;
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                result = new GrabResult(false, null, "Cancelled");
            }
            catch (Exception ex)
            {
                result = new GrabResult(false, null, ex.Message);
            }

            grabbing = false;

            try
            {
                BeginInvoke(new Action(() => FinishGrab(result, copy)));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        });
    }

    private void FinishGrab(GrabResult result, bool copy)
    {
        if (!result.Ok || result.File == null)
        {
            Toast("Download failed: " + result.Message, OffColor);
        }
        else
        {
            string name = Path.GetFileName(result.File);
            bool copied = copy && CopyFileToClipboard(result.File);

            Toast((copied ? "Downloaded and copied to the clipboard: " : "Downloaded: ") + Trunc(name, 46) +
                  (result.Message.Contains("YouTube") ? "  (from YouTube)" : ""), OnColor);
        }

        if (Visible && currentPage == "Grab")
            RefreshPage();
    }

    private void CancelGrab()
    {
        grabCts?.Cancel();
        Toast("Download cancelled", OffColor);
    }

    // The newest files in the downloads folder.
    private List<FileInfo> RecentGrabs(int max)
    {
        try
        {
            string folder = GrabFolder();

            if (!Directory.Exists(folder))
                return new List<FileInfo>();

            return new DirectoryInfo(folder).GetFiles()
                .Where(f => !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                            !f.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) &&
                            !Regex.IsMatch(f.Name, @"\.f\d+\.\w+$"))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(max)
                .ToList();
        }
        catch
        {
            return new List<FileInfo>();
        }
    }

    private MenuEntry GrabCycle<T>(
        string icon, string label, T[] values, Func<T> get, Action<T> set, Func<T, string> format, string tip)
    {
        void Step(int direction)
        {
            int i = Array.IndexOf(values, get());
            i = (Math.Max(0, i) + direction + values.Length) % values.Length;
            set(values[i]);
            SaveSettings();
            RefreshPage();
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(0, icon, text, run: () => Step(1), runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next, right-click = previous.");
    }

    private List<MenuEntry> GrabPage(string query)
    {
        List<MenuEntry> list = new();

        string typed = query.Trim();
        string? clip = null;

        try
        {
            clip = GetClipboardText()?.Trim();
        }
        catch
        {
            // No clipboard text.
        }

        // 1. what is downloading right now
        if (grabbing)
        {
            string running = $"DOWNLOADING from {grabName}  {grabText}  -  click to cancel";

            list.Add(new MenuEntry(0, "", running, run: CancelGrab,
                expandText: running + NL + "It keeps going in the background. The file is copied to the clipboard when it is done."));
        }

        // 2. the link to download
        if (LinkDownloader.LooksLikeLink(typed))
        {
            string site = LinkDownloader.SiteName(typed);

            list.Add(new MenuEntry(0, "", $"Download from {site}: {ShortUrl(typed)}",
                run: () => StartGrab(typed),
                expandText: $"Download this link ({site})" + NL + typed));
        }
        else if (clip != null && LinkDownloader.LooksLikeLink(clip))
        {
            string site = LinkDownloader.SiteName(clip);
            string captured = clip;

            list.Add(new MenuEntry(0, "", $"Download the link in the clipboard ({site}): {ShortUrl(clip)}",
                run: () => StartGrab(captured),
                expandText: $"Download the link in the clipboard ({site})" + NL + clip));
        }
        else if (!grabbing)
        {
            list.Add(Info("Copy a link, then press Enter here  -  or type / paste it above"));
        }

        // 3. options
        list.Add(GrabCycle("", "What to get", new[] { false, true },
            () => settings.GrabAudioOnly, v => settings.GrabAudioOnly = v,
            v => v ? "audio only (mp3)" : "the video (mp4)",
            "Video keeps the picture. Audio only saves just the sound as an mp3."));

        list.Add(GrabCycle("", "Video quality", GrabQualities,
            () => settings.GrabQuality, v => settings.GrabQuality = v,
            v => v == 0 ? "best available" : v + "p at most",
            "A lower quality is a smaller file."));

        list.Add(GrabCycle("", "Copy the file to the clipboard when done", new[] { true, false },
            () => settings.GrabCopy, v => settings.GrabCopy = v, v => OnOff(v),
            "Then Ctrl+V pastes the file into a chat, an email or a folder."));

        string folder = GrabFolder();

        list.Add(Act("", "Open the downloads folder", () =>
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", folder);
        }));

        list.Add(Act("", "Folder: " + Trunc(folder, 44), () =>
        {
            HideToTray();

            using FolderBrowserDialog dialog = new()
            {
                Description = "Where should downloads go?",
                SelectedPath = Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                settings.GrabFolder = dialog.SelectedPath;
                SaveSettings();
            }

            ShowPage("Grab");
            OpenLauncher();
        }));

        list.Add(Act("", "What can I download?", () => Say("Download by link",
            "Instagram, TikTok, X / Twitter, Facebook, Reddit, SoundCloud, Vimeo, Twitch, Dailymotion," + NL +
            "Pinterest, YouTube and hundreds of other sites (the ones yt-dlp understands)." + NL + NL +
            "Private, age-restricted or login-only posts can't be downloaded (this tool can't sign in)." + NL + NL +
            "Spotify: its audio is copy-protected, so it can't be downloaded from Spotify itself. " +
            "A Spotify link is read for the song and artist, and the same song is downloaded from YouTube instead " +
            "(always as audio). It is usually the right track, but not always the exact version.")));

        // 4. what was downloaded before
        List<FileInfo> recent = RecentGrabs(8);

        if (recent.Count > 0)
            list.Add(Info("Downloaded  -  click to copy the file, right-click to show it in the folder"));

        foreach (FileInfo file in recent)
        {
            FileInfo captured = file;
            string size = captured.Length >= 1 << 20 ? $"{captured.Length / 1048576.0:0.0} MB" : $"{captured.Length / 1024} KB";

            list.Add(new MenuEntry(0, captured.Extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "" : "",
                $"{Trunc(Path.GetFileNameWithoutExtension(captured.Name), 40)}  -  {size}",
                run: () =>
                {
                    if (CopyFileToClipboard(captured.FullName))
                        Toast("Copied to the clipboard: " + Trunc(captured.Name, 40), OnColor);
                },
                runAlt: () => Process.Start("explorer.exe", "/select,\"" + captured.FullName + "\""),
                expandText: captured.Name + NL + size + NL + "Click = copy the file to the clipboard, right-click = show it in its folder."));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }
}
