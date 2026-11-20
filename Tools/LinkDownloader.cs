using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

// ======================================================
// DOWNLOAD BY LINK
// Paste a link from Instagram, TikTok, X / Twitter, Facebook, Reddit,
// SoundCloud, Vimeo, Twitch, YouTube and the many other sites yt-dlp
// understands. The file lands in one folder and is copied to the clipboard.
//
// Spotify is different: its audio is copy-protected, so nothing can be
// downloaded from Spotify itself. A Spotify link is read for the song's
// name and artist, and the same song is fetched from YouTube instead.
// ======================================================

internal sealed record GrabResult(bool Ok, string? File, string Message);

internal static class LinkDownloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UtilMaster3000", "Downloads");

    public static bool LooksLikeLink(string text) =>
        Regex.IsMatch(text.Trim(), @"^https?://[^\s]+\.[^\s]+$", RegexOptions.IgnoreCase);

    public static bool IsSpotify(string link) =>
        Regex.IsMatch(link, @"^https?://(open|play)\.spotify\.com/", RegexOptions.IgnoreCase);

    /// <summary>A friendly name for the site a link is from.</summary>
    public static string SiteName(string link)
    {
        Match m = Regex.Match(link.Trim(), @"^https?://(?:www\.|m\.|mobile\.|open\.|play\.|vm\.|music\.)?([^/:?#]+)", RegexOptions.IgnoreCase);

        if (!m.Success)
            return "this link";

        string host = m.Groups[1].Value.ToLowerInvariant();

        return host switch
        {
            "youtu.be" or "youtube.com" => "YouTube",
            "instagram.com" => "Instagram",
            "tiktok.com" => "TikTok",
            "twitter.com" or "x.com" => "X / Twitter",
            "facebook.com" or "fb.watch" => "Facebook",
            "reddit.com" or "redd.it" => "Reddit",
            "soundcloud.com" => "SoundCloud",
            "spotify.com" => "Spotify",
            "vimeo.com" => "Vimeo",
            "twitch.tv" or "clips.twitch.tv" => "Twitch",
            _ => host
        };
    }

    /// <summary>For a Spotify song link: "song artist", read from Spotify's public embed page.</summary>
    public static async Task<string?> SpotifyQueryAsync(string link, CancellationToken ct)
    {
        try
        {
            Match id = Regex.Match(link, @"/(track|episode)/([A-Za-z0-9]+)", RegexOptions.IgnoreCase);

            if (!id.Success)
                return null;

            using HttpRequestMessage request = new(HttpMethod.Get,
                $"https://open.spotify.com/embed/{id.Groups[1].Value.ToLowerInvariant()}/{id.Groups[2].Value}");
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");

            using HttpResponseMessage response = await Http.SendAsync(request, ct);
            string html = await response.Content.ReadAsStringAsync(ct);

            string? Unjson(string s)
            {
                try
                {
                    return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + s + "\"");
                }
                catch
                {
                    return s;
                }
            }

            Match name = Regex.Match(html, "\"name\":\"(?<n>(?:[^\"\\\\]|\\\\.)+)\"");
            Match artist = Regex.Match(html, "\"artists\":\\[\\{\"name\":\"(?<a>(?:[^\"\\\\]|\\\\.)+)\"");

            if (!name.Success)
                return null;

            string song = Unjson(name.Groups["n"].Value) ?? name.Groups["n"].Value;
            string by = artist.Success ? Unjson(artist.Groups["a"].Value) ?? "" : "";

            return $"{song} {by}".Trim();
        }
        catch
        {
            // Handled by the caller.
        }

        return null;
    }

    /// <summary>
    /// Downloads one link into the folder. progress(percent, text) is called
    /// while it works; the result carries the final file.
    /// </summary>
    public static async Task<GrabResult> DownloadAsync(
        string link, bool audioOnly, int maxHeight, string folder,
        Action<double, string> progress, CancellationToken ct)
    {
        link = link.Trim();
        Directory.CreateDirectory(folder);

        string source = link;
        string note = "";

        if (IsSpotify(link))
        {
            if (!Regex.IsMatch(link, @"/(track|episode)/", RegexOptions.IgnoreCase))
            {
                return new GrabResult(false, null,
                    "Spotify albums, playlists and artists can't be downloaded this way. Paste the link of a single song.");
            }

            progress(0, "Reading the Spotify link...");
            string? query = await SpotifyQueryAsync(link, ct);

            if (query == null)
            {
                return new GrabResult(false, null,
                    "Could not read that Spotify link. Spotify itself cannot be downloaded (its audio is protected).");
            }

            source = "ytsearch1:" + query + " audio";
            audioOnly = true;
            note = $" (Spotify itself is protected: this is \"{query}\" from YouTube)";
        }

        List<string> args = new()
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime", "--windows-filenames",
            "--trim-filenames", "120",
            "--retries", "5", "--fragment-retries", "5",
            "-o", Path.Combine(folder, "%(title)s [%(id)s].%(ext)s"),
            "--print", "after_move:filepath",
            "--progress-template", "download:GR|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s"
        };

        if (audioOnly)
        {
            args.AddRange(new[] { "-f", "bestaudio/best", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        }
        else
        {
            string cap = maxHeight > 0 ? $"[height<={maxHeight}]" : "";
            args.AddRange(new[]
            {
                "-f", $"bv*{cap}[vcodec^=avc1]+ba[acodec^=mp4a]/bv*{cap}+ba/b{cap}/b",
                "--merge-output-format", "mp4"
            });
        }

        int limitKb = NetworkLimiter.DownloadLimitKb(0);

        if (limitKb > 0)
        {
            args.Add("-r");
            args.Add(limitKb + "K");
        }

        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (ffmpeg != null)
        {
            args.Add("--ffmpeg-location");
            args.Add(Path.GetDirectoryName(ffmpeg)!);
        }

        args.Add(source);

        string? finalFile = null;
        int streams = 1, stream = 0;
        double previous = 0;

        ProcResult r = await YtDlp.RunAsync(args, ct, line =>
        {
            line = Regex.Replace(line, "\u001b\\[[0-9;]*m", "").Trim();

            if (line.StartsWith("[download] Destination:") && Regex.IsMatch(line, @"\.f\d+\.\w+$"))
            {
                streams = 2;
                return;
            }

            if (line.StartsWith("GR|"))
            {
                string[] p = line.Split('|');

                if (p.Length >= 4 &&
                    double.TryParse(p[1].Trim().TrimEnd('%'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double percent))
                {
                    if (percent + 30 < previous && stream < streams - 1)
                        stream++;

                    previous = percent;
                    double overall = Math.Min(99, (stream + percent / 100.0) / streams * 100.0);
                    progress(overall, $"{overall:0}%  {p[2].Trim()}  {p[3].Trim()}".TrimEnd());
                }
                else if (p.Length >= 5 && long.TryParse(p[4].Trim(), out long bytes))
                {
                    // The site does not say how big it is: show what has arrived.
                    progress(previous, $"{bytes / 1048576.0:0.0} MB  {p[2].Trim()}".TrimEnd());
                }

                return;
            }

            // The final file name printed by --print after_move:filepath.
            if (line.Length > 3 && (line.Contains(":\\") || line.StartsWith("/")) && File.Exists(line))
                finalFile = line;
        }, null, captureStdout: false);

        ct.ThrowIfCancellationRequested();

        if (finalFile == null || !File.Exists(finalFile))
        {
            // The path may not have been printed: newest file in the folder.
            finalFile = new DirectoryInfo(folder).GetFiles()
                .Where(f => DateTime.Now - f.LastWriteTime < TimeSpan.FromMinutes(5) &&
                            !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault()?.FullName;
        }

        if (r.ExitCode != 0 || finalFile == null)
        {
            string error = FriendlyError(r.Stderr, link);
            return new GrabResult(false, null, error);
        }

        if (!audioOnly && ffmpeg != null &&
            !Regex.IsMatch(Path.GetExtension(finalFile), @"^\.(mp4|m4v|mov)$", RegexOptions.IgnoreCase))
        {
            progress(99, "converting to mp4...");
            string converted = Path.ChangeExtension(finalFile, ".mp4");

            if (await ConvertToMp4Async(ffmpeg, finalFile, converted, ct))
            {
                try
                {
                    File.Delete(finalFile);
                }
                catch
                {
                    // Leave the original beside it.
                }

                finalFile = converted;
            }
        }

        return new GrabResult(true, finalFile, "Downloaded" + note);
    }

    private static async Task<bool> ConvertToMp4Async(string ffmpeg, string input, string output, CancellationToken ct)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo info = new(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            foreach (string a in new[]
                     {
                         "-y", "-i", input, "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                         "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output
                     })
            {
                info.ArgumentList.Add(a);
            }

            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
            _ = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 10_000;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    // The raw yt-dlp error, made readable (and with the usual reasons spelled out).
    private static string FriendlyError(string stderr, string link)
    {
        string last = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? "";

        string site = SiteName(link);

        if (Regex.IsMatch(last, @"HTTP Error 404|404: Not Found|Video unavailable|has been removed|does not exist|no longer available", RegexOptions.IgnoreCase))
            return $"That {site} link doesn't lead to anything (the post may be deleted, or the link is wrong).";

        if (Regex.IsMatch(last, @"Unsupported URL", RegexOptions.IgnoreCase))
            return $"{site} isn't a site this tool understands, or that link isn't a video / audio post.";

        if (Regex.IsMatch(last, @"No video could be found|no video in this post|There is no video", RegexOptions.IgnoreCase))
            return "There is no video in that post (it may be only pictures).";

        if (Regex.IsMatch(last, @"logged[- ]in|log ?in\b|sign in|--cookies|--username|private video|confirm your age|age[- ]restricted|members[- ]only|login required|authenticat", RegexOptions.IgnoreCase))
            return $"{site} wants you signed in for this one (private, age-restricted or login-only), which this tool can't do.";

        if (Regex.IsMatch(last, @"HTTP Error 429|Too Many Requests|rate.?limit", RegexOptions.IgnoreCase))
            return $"{site} is asking to slow down. Try again in a few minutes.";

        return last.Length > 0 ? Regex.Replace(last, @"^ERROR:\s*(\[[^\]]+\]\s*[^:]*:\s*)?", "").Trim() : "The download failed.";
    }
}
