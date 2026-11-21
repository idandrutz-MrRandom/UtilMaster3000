using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace UtilMaster3000;

// ======================================================
// FFMPEG DOWNLOAD
// MP4 screen recording, joining video and audio in UtilTube downloads and video
// backgrounds all need ffmpeg. This fetches a Windows build into the app's tools
// folder (next to yt-dlp), checks it against the published SHA-256 and unpacks
// only ffmpeg.exe. Nothing is installed system-wide.
// ======================================================

internal static class FfmpegInstaller
{
    // Tried in this order: (the zip, the file that lists its checksum). GitHub first: it
    // is by far the faster of the two (measured: about 1.9 MB/s against 55 KB/s, so the
    // smaller file from the other site would have taken half an hour). A source that
    // turns out to be very slow is dropped for the next one.
    private static readonly (string Zip, string Sums)[] Sources =
    {
        ("https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip",
         "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/checksums.sha256"),

        ("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
         "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256")
    };

    /// <summary>Where the app keeps its own copy.</summary>
    public static string OwnCopyPath => Path.Combine(YtDlp.ToolFolder, "ffmpeg.exe");

    public static string ApproxSize => "about 200 MB";

    /// <summary>Downloads and unpacks ffmpeg.exe, reporting progress (0-100, speed, time left).</summary>
    public static async Task InstallAsync(Action<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(YtDlp.ToolFolder);

        List<string> problems = new();

        for (int index = 0; index < Sources.Length; index++)
        {
            (string zipUrl, string sumsUrl) = Sources[index];
            string zipPath = Path.Combine(YtDlp.ToolFolder, "ffmpeg-download.zip");
            bool last = index == Sources.Length - 1;
            Stopwatch clock = Stopwatch.StartNew();

            try
            {
                // 0-95 % is the download, the rest is checking and unpacking.
                // Not the last source: give up on it when it is crawling (under 150 KB/s
                // with more than 4 minutes to go, after the first 20 s), try the next.
                await Downloader.DownloadAsync(zipUrl, zipPath, progress, ct, 0, 95,
                    last ? null : p => clock.Elapsed.TotalSeconds > 20 &&
                                       p.BytesPerSecond > 0 && p.BytesPerSecond < 150 * 1024 &&
                                       p.SecondsLeft > 240);

                progress?.Invoke(new ToolDownloadProgress(96, 0, 0, 0, 0));
                await VerifyAsync(zipUrl, sumsUrl, zipPath, ct);

                progress?.Invoke(new ToolDownloadProgress(98, 0, 0, 0, 0));
                Unpack(zipPath);

                FfmpegRecorder.ForgetSearch();
                progress?.Invoke(new ToolDownloadProgress(100, 0, 0, 0, 0));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                problems.Add($"{new Uri(zipUrl).Host}: {ex.Message}");
            }
            finally
            {
                try { File.Delete(zipPath); } catch (IOException) { }
            }
        }

        throw new InvalidOperationException(
            "ffmpeg could not be downloaded (" + string.Join("; ", problems) + "). " +
            "Check your internet connection and try again.");
    }

    // The download must match the checksum its publisher lists.
    private static async Task VerifyAsync(
        string zipUrl, string sumsUrl, string zipPath, CancellationToken ct)
    {
        string sums = await Downloader.GetStringAsync(sumsUrl, ct);
        string fileName = Path.GetFileName(new Uri(zipUrl).AbsolutePath);

        string? expected = null;

        foreach (string line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts[0].Length != 64)
                continue;

            // A bare hash (one file), or "hash  name" for the right file.
            if (parts.Length == 1 ||
                parts[^1].TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0];
                break;
            }
        }

        await using FileStream stream = File.OpenRead(zipPath);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

        if (expected == null ||
            !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "the download did not match its published checksum, so it was not installed");
        }
    }

    private static void Unpack(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);

        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("ffmpeg.exe was not in the download");

        string temp = OwnCopyPath + ".new";
        entry.ExtractToFile(temp, true);
        File.Move(temp, OwnCopyPath, true);
    }
}
