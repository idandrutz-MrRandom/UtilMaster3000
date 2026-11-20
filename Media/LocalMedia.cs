using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UtilMaster3000;

internal sealed record MediaProbe(
    double Duration,
    string VideoCodec,
    string PixelFormat,
    string AudioCodec,
    int Width,
    int Height);

// ======================================================
// LOCAL VIDEO FILES
// The player is not only for YouTube: any video file can be opened
// (menu, drag and drop, "Open with", command line, launcher search).
// ======================================================

internal static class LocalMedia
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob"
    };

    /// <summary>Filter text for an "open file" dialog.</summary>
    public static string DialogFilter =>
        "Video files|" + string.Join(";", VideoExtensions.Select(e => "*" + e)) +
        "|All files|*.*";

    private static readonly ConcurrentDictionary<string, string> PathById = new();

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico"
    };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsLocalId(string id) => id.StartsWith("loc_", StringComparison.Ordinal);

    public static string? PathFor(string id) =>
        PathById.TryGetValue(id, out string? path) ? path : null;

    /// <summary>Stable id for a file, so resume positions survive restarts.</summary>
    public static string IdFor(string path)
    {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return "loc_" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>Wraps a file as a playable item for the player.</summary>
    public static TubeVideo ToVideo(string path)
    {
        string full = Path.GetFullPath(path);
        string id = IdFor(full);

        PathById[id] = full;

        MediaProbe? probe = IsImageFile(full) ? null : Probe(full);

        return new TubeVideo
        {
            Id = id,
            Title = Path.GetFileNameWithoutExtension(full),
            Channel = Path.GetFileName(Path.GetDirectoryName(full)) ?? "",
            Duration = probe != null ? (int)probe.Duration : 0,
            File = full,
            Size = SafeLength(full)
        };
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The other videos in the same folder, in name order.</summary>
    public static List<string> Siblings(string path)
    {
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));

            if (folder == null)
                return new List<string>();

            return Directory.EnumerateFiles(folder)
                .Where(IsVideoFile)
                .OrderBy(f => f, new NaturalComparer())
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---- probing (ffprobe ships with ffmpeg) ----

    private static string? FfprobePath()
    {
        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (ffmpeg == null)
            return null;

        string probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");

        return File.Exists(probe) ? probe : null;
    }

    public static MediaProbe? Probe(string path)
    {
        string? ffprobe = FfprobePath();

        if (ffprobe == null)
            return null;

        try
        {
            ProcessStartInfo info = new(ffprobe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string a in new[]
            {
                "-v", "error",
                "-show_entries", "format=duration:stream=codec_type,codec_name,pix_fmt,width,height",
                "-of", "json", path
            })
            {
                info.ArgumentList.Add(a);
            }

            using Process process = Process.Start(info)!;
            ChildJob.Add(process);

            string json = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            double duration = 0;

            if (root.TryGetProperty("format", out JsonElement format) &&
                format.TryGetProperty("duration", out JsonElement d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                duration = parsed;
            }

            string vcodec = "", pix = "", acodec = "";
            int width = 0, height = 0;

            if (root.TryGetProperty("streams", out JsonElement streams))
            {
                foreach (JsonElement s in streams.EnumerateArray())
                {
                    string type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() ?? "" : "";
                    string name = s.TryGetProperty("codec_name", out JsonElement n) ? n.GetString() ?? "" : "";

                    if (type == "video" && vcodec.Length == 0)
                    {
                        vcodec = name;
                        pix = s.TryGetProperty("pix_fmt", out JsonElement p) ? p.GetString() ?? "" : "";
                        width = s.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                        height = s.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
                    }
                    else if (type == "audio" && acodec.Length == 0)
                    {
                        acodec = name;
                    }
                }
            }

            return new MediaProbe(duration, vcodec, pix, acodec, width, height);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when the Windows media engine can play the video as it is:
    /// 8-bit H.264 (the file container is handled separately).
    /// </summary>
    public static bool VideoCanBeCopied(MediaProbe? probe) =>
        probe != null &&
        probe.VideoCodec == "h264" &&
        (probe.PixelFormat is "yuv420p" or "yuvj420p" or "");

    // ---- thumbnails for local files (one frame, made with ffmpeg) ----

    public static void MakeThumbnail(string id, string target)
    {
        string? path = PathFor(id);
        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (path == null || ffmpeg == null || !File.Exists(path))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            MediaProbe? probe = Probe(path);
            double at = probe != null && probe.Duration > 20 ? Math.Min(probe.Duration * 0.1, 60) : 1;

            ProcessStartInfo info = new(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            foreach (string a in new[]
            {
                "-y", "-ss", at.ToString("0.##", CultureInfo.InvariantCulture), "-i", path,
                "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", target
            })
            {
                info.ArgumentList.Add(a);
            }

            using Process process = Process.Start(info)!;
            ChildJob.Add(process);
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            process.WaitForExit(20000);
        }
        catch
        {
            // No thumbnail: the card shows a plain placeholder.
        }
    }

    private sealed class NaturalComparer : IComparer<string>
    {
        public int Compare(string? a, string? b) =>
            StringComparer.OrdinalIgnoreCase.Compare(Pad(a), Pad(b));

        // "Episode 2" sorts before "Episode 10".
        private static string Pad(string? s) =>
            System.Text.RegularExpressions.Regex.Replace(
                s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));
    }
}
