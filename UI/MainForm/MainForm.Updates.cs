using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

// ======================================================
// UPDATES  (About > Updates) and UPDATE LOG  (About > Updates > Update log)
// The update log is the embedded CHANGELOG.md (works offline). "Check for updates"
// asks GitHub's public release list (see Core/Updater.cs).
// ======================================================

public sealed partial class MainForm
{
    private ReleaseInfo? foundUpdate;          // a newer release, once one was found
    private string updateStatus = "Not checked yet this session";
    private bool updateBusy;                   // checking or downloading

    // ---- the update log (CHANGELOG.md inside the program) ----

    private sealed record LogSection(string Version, string Date, List<string> Lines);

    private static List<LogSection> ReadChangelog()
    {
        List<LogSection> sections = new();

        try
        {
            Assembly asm = typeof(MainForm).Assembly;
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));

            if (name == null)
                return sections;

            using StreamReader reader = new(asm.GetManifestResourceStream(name)!);

            LogSection? current = null;

            foreach (string raw in reader.ReadToEnd().Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                if (line.StartsWith("## "))
                {
                    // "## 1.0.2 - 2026-11-21"
                    string head = line[3..].Trim();
                    int dash = head.IndexOf(" - ", StringComparison.Ordinal);

                    current = new LogSection(
                        dash >= 0 ? head[..dash].Trim() : head,
                        dash >= 0 ? head[(dash + 3)..].Trim() : "",
                        new List<string>());

                    sections.Add(current);
                }
                else if (current != null && line.TrimStart().StartsWith("- "))
                {
                    current.Lines.Add(line.TrimStart()[2..].Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // No log: the page says so.
        }

        return sections;
    }

    private List<MenuEntry> BuildUpdateLogPage()
    {
        List<MenuEntry> list = new();

        List<LogSection> sections = ReadChangelog();

        if (sections.Count == 0)
            list.Add(Info("The update log could not be read."));

        foreach (LogSection s in sections)
        {
            LogSection captured = s;
            string text = string.Join(NL, s.Lines.Select(l => "- " + l));
            string head = s.Date.Length > 0 ? $"{s.Version}  ({s.Date})" : s.Version;

            list.Add(new MenuEntry(0, "", head,
                run: () => Say($"Version {captured.Version}", string.Join("\n", captured.Lines.Select(l => "- " + l))),
                expandText: text));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    // ---- the Updates page ----

    private List<MenuEntry> BuildUpdatesPage()
    {
        string installed = Updater.Pretty(Updater.Current);
        List<MenuEntry> list = new()
        {
            Info($"Installed: version {installed}"),
            Info(updateStatus)
        };

        if (foundUpdate != null)
        {
            ReleaseInfo r = foundUpdate;
            string size = r.ExeSize > 0 ? $" ({r.ExeSize / 1048576.0:0} MB)" : "";

            list.Add(Act("", $"What's new in {Updater.Pretty(r.Version)}", () => ShowReleaseNotes(r)));

            if (Updater.CanSelfInstall && r.ExeUrl != null && r.ExeSha256 != null)
            {
                list.Add(new MenuEntry(0, "", updateBusy ? "Working..." : $"Download and install {Updater.Pretty(r.Version)}{size}",
                    run: () => { if (!updateBusy) _ = InstallUpdateAsync(r); },
                    expandText: "Downloads the new program from this project's GitHub release, checks it against GitHub's published checksum, " +
                    "then restarts the app on the new version. Your settings are kept; the old file is saved as update\\previous.exe."));
            }

            list.Add(Act("", "Open the release page", () => OpenUrl(r.PageUrl)));
            list.Add(Act("", $"Skip version {Updater.Pretty(r.Version)}", () =>
            {
                settings.SkippedUpdate = Updater.Pretty(r.Version);
                SaveSettings();
                foundUpdate = null;
                updateStatus = "Skipped that version; it will not be announced again";
                RefreshPage();
            }));
        }

        list.Add(Act("", updateBusy ? "Checking..." : "Check for updates now",
            () => { if (!updateBusy) _ = CheckForUpdatesAsync(manual: true); }));

        list.Add(new MenuEntry(0, "", $"Check automatically (once a day): {OnOff(settings.AutoCheckUpdates)}",
            run: () =>
        {
            settings.AutoCheckUpdates = !settings.AutoCheckUpdates;
            SaveSettings();
            RefreshPage();
        }, expandText: "Reads this project's public release list on GitHub at most once a day, a few seconds after the app starts. " +
           "Nothing about you is sent. Turn it off to check only by hand."));

        list.Add(new MenuEntry(0, "", "Update log...", targetPage: "UpdateLog"));

        if (!Updater.CanSelfInstall)
            list.Add(Info("This is a development build: updates open the release page instead of installing."));

        list.Add(BackEntry());

        return Renumber(list);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say("Open page", "Could not open " + url);
        }
    }

    private static void ShowReleaseNotes(ReleaseInfo r)
    {
        string notes = r.Notes.Trim().Length > 0 ? r.Notes.Trim() : "(no notes were written for this release)";

        if (notes.Length > 1500)
            notes = notes[..1500] + "...";

        Say($"Version {Updater.Pretty(r.Version)}" + (r.Title.Length > 0 ? " - " + r.Title : ""), notes);
    }

    /// <summary>Asks GitHub whether a newer version exists. Quiet unless asked by hand or one is found.</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateBusy)
            return;

        updateBusy = true;
        updateStatus = "Checking...";

        if (currentPage == "Updates")
            RefreshPage();

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            List<ReleaseInfo> releases = await Updater.FetchReleasesAsync(cts.Token);

            settings.LastUpdateCheck = DateTime.UtcNow.Ticks;
            SaveSettings();

            ReleaseInfo? newer = Updater.Newer(releases);

            if (newer != null && !manual && Updater.Pretty(newer.Version) == settings.SkippedUpdate)
                newer = null;

            foundUpdate = newer;

            if (newer != null)
            {
                updateStatus = $"Version {Updater.Pretty(newer.Version)} is available";

                if (!manual)
                    Toast($"Update {Updater.Pretty(newer.Version)} available: About > Updates", OnColor);
            }
            else
            {
                updateStatus = "You have the latest version (checked " + DateTime.Now.ToString("HH:mm") + ")";

                if (manual)
                    Toast("You are up to date", OnColor);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       or TaskCanceledException or System.Text.Json.JsonException)
        {
            updateStatus = "Could not check: " + Trunc(ex.Message, 70);

            if (manual)
                Toast("Could not check for updates", OffColor);
        }
        finally
        {
            updateBusy = false;

            if (!IsDisposed && currentPage == "Updates")
                RefreshPage();
        }
    }

    /// <summary>A quiet check a few seconds after start, at most once a day.</summary>
    private async void AutoCheckForUpdates()
    {
        if (!settings.AutoCheckUpdates)
            return;

        long last = settings.LastUpdateCheck;

        if (last > 0 && DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) < TimeSpan.FromHours(24))
            return;

        await Task.Delay(8000);

        if (!IsDisposed)
            await CheckForUpdatesAsync(manual: false);
    }

    private async Task InstallUpdateAsync(ReleaseInfo r)
    {
        DialogResult answer = MessageBox.Show(
            $"Install version {Updater.Pretty(r.Version)} now?\n\n" +
            "The new program is downloaded from this project's GitHub release and checked against its published checksum. " +
            "The app then closes and starts again on the new version.",
            "Install update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        updateBusy = true;
        RefreshPage();

        DownloadBar bar = DownloadBar.Start($"Downloading {Updater.Pretty(r.Version)}");

        try
        {
            string file = await Updater.DownloadAsync(r, bar.Report, CancellationToken.None);

            bar.Finish("Update ready: restarting", true, 1500);

            Updater.InstallAndRestart(file);
            ExitApplication();
        }
        catch (Exception ex)
        {
            bar.Finish("Update failed", false, 4000);
            Say("Install update", "The update was not installed.\n\n" + ex.Message +
                "\n\nYou can still get it from the release page.");
        }
        finally
        {
            updateBusy = false;

            if (!IsDisposed)
                RefreshPage();
        }
    }
}
