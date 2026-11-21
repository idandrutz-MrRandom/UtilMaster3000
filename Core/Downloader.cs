using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace UtilMaster3000;

/// <summary>How far a download is, how fast it goes and how long it will take.</summary>
internal readonly record struct ToolDownloadProgress(
    int Percent, long Done, long Total, double BytesPerSecond, double? SecondsLeft)
{
    /// <summary>"3.2 MB/s" ("" until the speed is known).</summary>
    public string SpeedText => BytesPerSecond <= 0
        ? ""
        : BytesPerSecond >= 1024 * 1024
            ? $"{BytesPerSecond / (1024 * 1024):0.0} MB/s"
            : $"{BytesPerSecond / 1024:0} KB/s";

    /// <summary>"12 of 120 MB".</summary>
    public string SizeText => Total > 0
        ? $"{Done / (1024.0 * 1024):0} of {Total / (1024.0 * 1024):0} MB"
        : $"{Done / (1024.0 * 1024):0} MB";

    /// <summary>"about 25 s left" / "about 2 min 10 s left" ("working out the time..." at first).</summary>
    public string TimeLeftText => FormatLeft(SecondsLeft);

    public static string FormatLeft(double? seconds)
    {
        if (seconds == null)
            return "working out the time...";

        int s = (int)Math.Ceiling(seconds.Value);

        if (s <= 1)
            return "almost done";

        if (s < 60)
            return $"about {s} s left";

        int minutes = s / 60;
        int rest = s % 60;

        return rest == 0 || minutes >= 10
            ? $"about {minutes} min left"
            : $"about {minutes} min {rest} s left";
    }
}

// ======================================================
// DOWNLOADER
// One place for the app's own tool downloads (ffmpeg, yt-dlp): streams to a file,
// reports percent, speed and the time left (speed is smoothed so the estimate
// does not jump about).
// ======================================================

internal static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destination"/>. The reported
    /// percent runs from <paramref name="fromPercent"/> to <paramref name="toPercent"/>
    /// (so a download that is only part of a longer job fits in its share of the bar).
    /// </summary>
    public static async Task DownloadAsync(
        string url,
        string destination,
        Action<ToolDownloadProgress>? progress,
        CancellationToken ct,
        int fromPercent = 0,
        int toPercent = 100,
        Func<ToolDownloadProgress, bool>? tooSlow = null)
    {
        using HttpResponseMessage response = await Http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? 0;

        await using Stream source = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream target = File.Create(destination);

        byte[] buffer = new byte[128 * 1024];
        long done = 0;

        Stopwatch clock = Stopwatch.StartNew();
        long lastReport = -1000;
        long lastSampleMs = 0;
        long lastSampleBytes = 0;
        double speed = 0;
        int read;

        void Report(bool final)
        {
            long now = clock.ElapsedMilliseconds;

            // About 6 reports a second is plenty for a progress bar.
            if (!final && now - lastReport < 160)
                return;

            lastReport = now;

            // Speed over the last ~second, blended with what it was before.
            if (now - lastSampleMs >= 500 || final)
            {
                double window = Math.Max(1, now - lastSampleMs) / 1000.0;
                double current = (done - lastSampleBytes) / window;

                speed = speed <= 0 ? current : speed * 0.7 + current * 0.3;
                lastSampleMs = now;
                lastSampleBytes = done;
            }

            int percent = total > 0
                ? fromPercent + (int)((toPercent - fromPercent) * (double)done / total)
                : fromPercent;

            // No time estimate in the first moments: it would be wild.
            double? left = total > 0 && speed > 1 && now > 1500
                ? (total - done) / speed
                : null;

            ToolDownloadProgress now2 = new(percent, done, total, speed, final ? 0 : left);
            progress?.Invoke(now2);

            // Lets the caller abandon a source that is far too slow.
            if (!final && tooSlow != null && tooSlow(now2))
                throw new TimeoutException("this source is too slow");
        }

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            Report(false);
        }

        Report(true);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct) =>
        await Http.GetStringAsync(url, ct);
}
