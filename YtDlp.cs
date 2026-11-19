using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

internal sealed record VideoInfo(string Id, string Title, string Channel, int Duration);

internal sealed record ProcResult(int ExitCode, string Stdout, string Stderr);

internal sealed record DownloadProgress(double Percent, string Speed, string Eta);

/// <summary>A loaded playlist: its title and videos.</summary>
internal sealed record PlaylistData(string Id, string Title, string Channel, List<VideoInfo> Videos);

internal sealed record StreamInput(string Url, Dictionary<string, string> Headers);

/// <summary>One subtitle track of a video (a language, written by hand or automatic).</summary>
internal sealed record SubtitleTrack(string Language, string Name, string Url, string Ext, bool Auto);

/// <summary>What to play for a YouTube video (see YtDlp.PlanAsync).</summary>
internal sealed record StreamPlan(
    List<StreamInput> Inputs, bool Progressive, double Duration, bool CopyVideo, int Height = 0);

// ======================================================
// YT-DLP WRAPPER
// UtilTube does not talk to YouTube itself: every search, list
// and download goes through the open-source yt-dlp program.
// ======================================================

internal static class YtDlp
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private static string? cachedPath;

    public static string ToolFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000", "tools");

    public static string OwnCopyPath => Path.Combine(ToolFolder, "yt-dlp.exe");

    // ---- locating ----

    public static string? Find(bool refresh = false)
    {
        if (!refresh && cachedPath != null && File.Exists(cachedPath))
            return cachedPath;

        cachedPath = null;

        // Explicit override (used for testing).
        string? env = Environment.GetEnvironmentVariable("UTILTUBE_YTDLP");

        if (!string.IsNullOrEmpty(env) && File.Exists(env))
            return cachedPath = env;

        if (File.Exists(OwnCopyPath))
            return cachedPath = OwnCopyPath;

        try
        {
            string[] extra =
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links")
            };

            string[] fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (string folder in fromPath.Concat(extra))
            {
                string candidate = Path.Combine(folder.Trim('"'), "yt-dlp.exe");

                if (File.Exists(candidate))
                    return cachedPath = candidate;
            }
        }
        catch
        {
            // Treat as not installed.
        }

        return null;
    }

    public static bool IsInstalled => Find() != null;

    // YouTube needs a JavaScript runtime for some videos. yt-dlp only
    // uses Deno by default, so when Node.js is installed we enable it.
    private static readonly Lazy<bool> NodeAvailable = new(() =>
    {
        try
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(d => File.Exists(Path.Combine(d.Trim('"'), "node.exe")));
        }
        catch
        {
            return false;
        }
    });

    // ---- installing / updating (own copy, checksum verified) ----

    private const string ReleaseBase =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    /// <summary>
    /// Downloads yt-dlp.exe from the project's official GitHub release into
    /// the app's tools folder and verifies it against the published SHA-256.
    /// </summary>
    public static async Task InstallAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ToolFolder);

        byte[] exe = await Http.GetByteArrayAsync(ReleaseBase + "yt-dlp.exe", ct);
        string sums = await Http.GetStringAsync(ReleaseBase + "SHA2-256SUMS", ct);

        string? expected = sums
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split(
                new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].TrimStart('*') == "yt-dlp.exe")
            .Select(p => p[0])
            .FirstOrDefault();

        string actual = Convert.ToHexString(SHA256.HashData(exe));

        if (expected == null ||
            !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The downloaded file did not match its published checksum, " +
                "so it was not installed.");
        }

        string temp = OwnCopyPath + ".new";
        await File.WriteAllBytesAsync(temp, exe, ct);
        File.Move(temp, OwnCopyPath, true);

        Find(refresh: true);
    }

    public static async Task<string> VersionAsync(CancellationToken ct)
    {
        ProcResult r = await RunAsync(new[] { "--version" }, ct);
        return r.Stdout.Trim();
    }

    // ---- running ----

    public static async Task<ProcResult> RunAsync(
        IEnumerable<string> args,
        CancellationToken ct,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        bool captureStdout = true)
    {
        string exe = Find()
            ?? throw new InvalidOperationException("yt-dlp is not installed.");

        ProcessStartInfo info = new(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (NodeAvailable.Value)
        {
            info.ArgumentList.Add("--js-runtimes");
            info.ArgumentList.Add("node");
        }

        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using Process process = new() { StartInfo = info };

        StringBuilder stdout = new();
        StringBuilder stderr = new();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;

            // Progress lines are consumed as they arrive and never stored:
            // a long download prints thousands of them.
            if (captureStdout)
            {
                lock (stdout)
                    stdout.AppendLine(e.Data);
            }

            onStdout?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;

            lock (stderr)
            {
                stderr.AppendLine(e.Data);

                // Keep only the recent part: it holds the error message.
                if (stderr.Length > 16000)
                    stderr.Remove(0, stderr.Length - 8000);
            }

            onStderr?.Invoke(e.Data);
        };

        process.Start();
        ChildJob.Add(process);

        // Background work must never slow the PC down.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Not allowed / already exited.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using CancellationTokenRegistration registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already finished.
            }
        });

        await process.WaitForExitAsync(CancellationToken.None);

        // Make sure the output readers have drained.
        process.WaitForExit();

        ct.ThrowIfCancellationRequested();

        return new ProcResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    // ---- searching / listing ----

    public static async Task<List<VideoInfo>> SearchAsync(
        string query, int count, bool newestFirst, CancellationToken ct)
    {
        string prefix = newestFirst ? "ytsearchdate" : "ytsearch";

        ProcResult r = await RunAsync(new[]
        {
            "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors",
            $"{prefix}{count}:{query}"
        }, ct);

        return ParseVideos(r.Stdout, r);
    }

    public static async Task<List<VideoInfo>> ListAsync(
        string url, int count, CancellationToken ct)
    {
        ProcResult r = await RunAsync(new[]
        {
            "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors",
            "--playlist-end", count.ToString(), url
        }, ct);

        return ParseVideos(r.Stdout, r);
    }

    public static async Task<VideoInfo?> InfoAsync(string url, CancellationToken ct)
    {
        ProcResult r = await RunAsync(new[]
        {
            "--dump-single-json", "--no-playlist", "--no-warnings", url
        }, ct);

        List<VideoInfo> parsed = ParseVideos(r.Stdout, r);

        return parsed.FirstOrDefault();
    }

    // ---- account feeds (need the signed-in cookies) ----

    private static bool LooksLikeAuthError(string message) =>
        Regex.IsMatch(message,
            @"sign in|log ?in|logged in|cookies|authenticat|account", RegexOptions.IgnoreCase);

    /// <summary>
    /// Home recommendations (":ytrec") or subscriptions (":ytsubs").
    /// Only reads the video list; nothing is downloaded with the account.
    /// </summary>
    public static async Task<List<VideoInfo>> FeedAsync(
        string feed, int count, string cookieFile, CancellationToken ct)
    {
        List<VideoInfo> list;

        try
        {
            ProcResult r = await RunAsync(new[]
            {
                "--cookies", cookieFile,
                "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors",
                "--playlist-end", count.ToString(), feed
            }, ct);

            list = ParseVideos(r.Stdout, r);
        }
        catch (InvalidOperationException ex) when (LooksLikeAuthError(ex.Message))
        {
            throw new AuthExpiredException();
        }

        // An empty feed from a signed-in account means the session was refused.
        if (list.Count == 0)
            throw new AuthExpiredException();

        return list;
    }

    /// <summary>
    /// A direct link to a progressive MP4 (video + audio in one file) so the
    /// built-in player can start instantly without downloading first.
    /// </summary>
    public static async Task<string> StreamUrlAsync(
        string videoId, string? cookieFile, CancellationToken ct)
    {
        List<string> args = new()
        {
            "--no-playlist", "--no-warnings", "-g",
            "-f", "18/b[ext=mp4][height<=480]/b[ext=mp4]"
        };

        if (cookieFile != null)
        {
            args.Add("--cookies");
            args.Add(cookieFile);
        }

        args.Add("https://www.youtube.com/watch?v=" + videoId);

        ProcResult r = await RunAsync(args, ct);

        string[] urls = r.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("http"))
            .ToArray();

        if (r.ExitCode != 0 || urls.Length != 1)
        {
            string? error = LastError(r.Stderr);

            if (error != null && LooksLikeAuthError(error))
                throw new AuthExpiredException();

            throw new InvalidOperationException(error ?? "No directly playable stream for this video.");
        }

        return urls[0];
    }

    // ---- what to play: the exact stream(s) yt-dlp would use, with its headers ----

    // Prefer one file with sound (instant start, seekable); otherwise the
    // best 720p video + audio pair; the bridge joins them.
    // Best picture up to the chosen height (joined with the audio by the
    // bridge); the 360p single file is only the fallback.
    private static string StreamSelector(int maxHeight) =>
        $"bv*[height<={maxHeight}][vcodec^=avc1]+ba[acodec^=mp4a]" +
        $"/bv*[height<={maxHeight}]+ba" +
        "/18/b[protocol=https][ext=mp4]/b";

    /// <summary>
    /// Asks yt-dlp what to play. Besides the address it returns the HTTP
    /// headers YouTube expects for that stream (some streams are refused
    /// unless the exact client headers are sent).
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (StreamPlan Plan, DateTime Expires)> PlanCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Prefetching = new();

    // Two videos are prepared at a time, and none while YouTube is pushing back
    // or while you are waiting for a video you clicked.
    private static readonly SemaphoreSlim PrefetchGate = new(2, 2);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> PrefetchStarted = new();

    /// <summary>Resolves a video's stream ahead of time so it starts at once.</summary>
    public static void Prefetch(string videoId, int maxHeight)
    {
        string key = videoId + "@" + maxHeight;

        if (PlanCache.ContainsKey(key) || videoId.StartsWith("loc_") || DateTime.UtcNow < blockedUntil)
            return;

        Prefetching.GetOrAdd(key, _ => Task.Run(async () =>
        {
            await PrefetchGate.WaitAsync();
            PrefetchStarted[key] = true;

            // A video you clicked comes first: do not compete with it.
            while (Volatile.Read(ref clickedLookups) > 0 && DateTime.UtcNow >= blockedUntil)
                await Task.Delay(150);

            if (DateTime.UtcNow < blockedUntil)
            {
                PrefetchGate.Release();
                PrefetchStarted.TryRemove(key, out bool _);
                Prefetching.TryRemove(key, out Task? _);
                return;
            }

            try
            {
                // (Resolved here directly: PlanAsync would wait for this very task.)
                StreamPlan plan = await ResolvePlanAsync(videoId, null, maxHeight, CancellationToken.None);
                PlanCache[key] = (plan, DateTime.UtcNow.AddMinutes(90));

                // Also fetch the first seconds of picture and sound.
                StreamProxy.Warm(plan.Inputs.Select(i => (i.Url, (Dictionary<string, string>?)i.Headers)));
            }
            catch
            {
                // It will be resolved again (and report the error) on play.
            }
            finally
            {
                PrefetchGate.Release();
                PrefetchStarted.TryRemove(key, out bool _);
                Prefetching.TryRemove(key, out Task? _);
            }
        }));
    }

    /// <summary>Drops what is remembered about a video (its addresses went bad).</summary>
    public static void Forget(string videoId)
    {
        foreach (string key in PlanCache.Keys.Where(k => k.StartsWith(videoId + "@")).ToList())
            PlanCache.TryRemove(key, out _);
    }

    public static async Task<StreamPlan> PlanAsync(
        string videoId, string? cookieFile, int maxHeight, CancellationToken ct)
    {
        string key = videoId + "@" + maxHeight;

        if (cookieFile == null)
        {
            if (PlanCache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
                return cached.Plan;

            // Being resolved right now (hover prefetch): wait for it.
            if (Prefetching.TryGetValue(key, out Task? running) && PrefetchStarted.ContainsKey(key))
            {
                try { await running.WaitAsync(ct); } catch (OperationCanceledException) { throw; } catch { }

                if (PlanCache.TryGetValue(key, out cached) && cached.Expires > DateTime.UtcNow)
                    return cached.Plan;
            }
        }

        StreamPlan plan;

        Interlocked.Increment(ref clickedLookups);

        try
        {
            plan = await ResolvePlanAsync(videoId, cookieFile, maxHeight, ct);
        }
        finally
        {
            Interlocked.Decrement(ref clickedLookups);
        }

        if (cookieFile == null)
            PlanCache[key] = (plan, DateTime.UtcNow.AddMinutes(90));

        StreamProxy.Warm(plan.Inputs.Select(i => (i.Url, (Dictionary<string, string>?)i.Headers)));

        return plan;
    }

    // Streams play at the quality you chose, from the first second.
    // (Switching to a better stream in the middle of a video froze the
    // picture in every real test, so there is no mid-play switch.)
    public static int StartHeight
    {
        get
        {
            TubeSettings s = TubeLibrary.Instance.Settings;

            if (!s.AutoStreamQuality)
                return s.Quality;

            // Chosen before the video starts, from how fast YouTube has been
            // answering: sharper when the connection is fast, gentler when it
            // is not. (Nothing is shown; there is no switch mid-video.)
            double mbps = StreamProxy.RecentMbps;

            if (mbps >= 14)
                return Math.Max(s.Quality, 1080);

            if (mbps > 0 && mbps < 8)
                return Math.Min(s.Quality, 720);

            return s.Quality;
        }
    }

    public static int UpgradeHeight => 0;

    // After YouTube asks "are you a bot?", background lookups rest a while.
    private static DateTime blockedUntil = DateTime.MinValue;
    private static int clickedLookups;

    private static async Task<StreamPlan> ResolvePlanAsync(
        string videoId, string? cookieFile, int maxHeight, CancellationToken ct)
    {
        try
        {
            StreamPlan plan = await ResolveWithAccountAsync(videoId, cookieFile, maxHeight, ct);
            blockedUntil = DateTime.MinValue;
            return plan;
        }
        catch (AuthExpiredException)
        {
            blockedUntil = DateTime.UtcNow.AddMinutes(15);
            throw;
        }
        catch (InvalidOperationException ex) when (
            Regex.IsMatch(ex.Message, @"No video formats|needs to be reloaded|not a bot|rate.?limit|HTTP Error 429",
                RegexOptions.IgnoreCase))
        {
            // YouTube is holding back: rest the background lookups for a while.
            blockedUntil = DateTime.UtcNow.AddMinutes(10);
            throw;
        }
    }

    // Signed in: every lookup goes through the account, which YouTube
    // almost never challenges. Without one (or if it is refused), anonymous.
    private static async Task<StreamPlan> ResolveWithAccountAsync(
        string videoId, string? cookieFile, int maxHeight, CancellationToken ct)
    {
        if (cookieFile == null && YtAccount.SignedIn)
        {
            try
            {
                return await YtAccount.WithCookieFile(file =>
                    ResolvePlanWithAsync(videoId, file, maxHeight, ct));
            }
            catch (AuthExpiredException)
            {
                // The saved session was refused: try without it.
            }
        }

        return await ResolvePlanWithAsync(videoId, cookieFile, maxHeight, ct);
    }

    private static async Task<StreamPlan> ResolvePlanWithAsync(
        string videoId, string? cookieFile, int maxHeight, CancellationToken ct)
    {
        try
        {
            return await ResolvePlanCoreAsync(videoId, cookieFile, maxHeight, ct, noSkip: false);
        }
        catch (InvalidOperationException)
        {
            // Maybe a live stream or an unusual video: look at everything.
            return await ResolvePlanCoreAsync(videoId, cookieFile, maxHeight, ct, noSkip: true);
        }
    }

    private static async Task<StreamPlan> ResolvePlanCoreAsync(
        string videoId, string? cookieFile, int maxHeight, CancellationToken ct, bool noSkip)
    {
        List<string> args = new()
        {
            "--no-playlist", "--no-warnings", "--dump-json", "-f", StreamSelector(maxHeight)
        };

        // Skipping YouTube's extra stream lists saves over a second.
        // (Live streams need them: those retry without this below.)
        if (!noSkip)
        {
            args.Add("--extractor-args");
            args.Add("youtube:skip=hls,dash,translated_subs");
        }

        if (cookieFile != null)
        {
            args.Add("--cookies");
            args.Add(cookieFile);
        }

        args.Add("https://www.youtube.com/watch?v=" + videoId);

        ProcResult r = await RunAsync(args, ct);
        string json = r.Stdout.Trim();

        if (r.ExitCode != 0 || !json.StartsWith('{'))
        {
            string? error = LastError(r.Stderr);

            if (error != null && LooksLikeAuthError(error))
                throw new AuthExpiredException();

            throw new InvalidOperationException(error ?? "No playable stream was found.");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        double duration = root.TryGetProperty("duration", out JsonElement d) &&
                          d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : 0;

        List<StreamInput> inputs = new();
        bool progressive = false;
        bool copyVideo = false;

        if (root.TryGetProperty("requested_formats", out JsonElement formats) &&
            formats.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement f in formats.EnumerateArray())
            {
                StreamInput? input = ToInput(f);

                if (input != null)
                    inputs.Add(input);

                // The video part: H.264 can be passed on untouched.
                if ((Str(f, "vcodec") ?? "none") != "none")
                    copyVideo = (Str(f, "vcodec") ?? "").StartsWith("avc1", StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            StreamInput? single = ToInput(root);

            if (single != null)
            {
                inputs.Add(single);
                copyVideo = (Str(root, "vcodec") ?? "").StartsWith("avc1", StringComparison.OrdinalIgnoreCase);

                string protocol = Str(root, "protocol") ?? "";

                // One plain https file that has both picture and sound.
                progressive =
                    protocol is "https" or "http" &&
                    (Str(root, "vcodec") ?? "none") != "none" &&
                    (Str(root, "acodec") ?? "none") != "none";
            }
        }

        if (inputs.Count == 0)
            throw new InvalidOperationException("No playable stream was found.");

        int height = 0;

        if (root.TryGetProperty("requested_formats", out JsonElement rf) && rf.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement f in rf.EnumerateArray())
            {
                if (f.TryGetProperty("height", out JsonElement fh) && fh.ValueKind == JsonValueKind.Number)
                    height = Math.Max(height, fh.GetInt32());
            }
        }
        else if (root.TryGetProperty("height", out JsonElement rh) && rh.ValueKind == JsonValueKind.Number)
        {
            height = rh.GetInt32();
        }

        return new StreamPlan(inputs, progressive, duration, copyVideo, height);
    }

    private static StreamInput? ToInput(JsonElement format)
    {
        string? url = Str(format, "url");

        if (url == null)
            return null;

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        if (format.TryGetProperty("http_headers", out JsonElement h) &&
            h.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in h.EnumerateObject())
            {
                // The relay wants plain bytes, not compressed ones.
                if (p.Name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    p.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                headers[p.Name] = p.Value.GetString() ?? "";
            }
        }

        return new StreamInput(url, headers);
    }

    // yt-dlp prints one JSON object per line.
    private static List<VideoInfo> ParseVideos(string output, ProcResult result)
    {
        List<VideoInfo> list = new();

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();

            if (!line.StartsWith('{'))
                continue;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;

                string? id = Str(root, "id");

                if (id == null || id.Length != 11)
                    continue;

                string? live = Str(root, "live_status");

                if (live == "is_live" || live == "is_upcoming")
                    continue;

                string title = Str(root, "title") ?? id;
                string channel = Str(root, "channel") ??
                                 Str(root, "uploader") ??
                                 Str(root, "uploader_id") ?? "";

                int duration = 0;

                if (root.TryGetProperty("duration", out JsonElement d) &&
                    d.ValueKind == JsonValueKind.Number)
                {
                    duration = (int)d.GetDouble();
                }

                list.Add(new VideoInfo(id, title, channel, duration));
            }
            catch (JsonException)
            {
                // Not a video line (warning text etc.).
            }
        }

        // Nothing parsed and yt-dlp failed: surface its own message.
        if (list.Count == 0 && result.ExitCode != 0)
            throw new InvalidOperationException(LastError(result.Stderr) ?? "yt-dlp failed.");

        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static string? LastError(string stderr)
    {
        string? line = stderr
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase));

        return line == null ? null : Regex.Replace(line, @"^ERROR:\s*(\[[^\]]*\]\s*)?", "");
    }

    // ---- sources ----

    /// <summary>Turns "@handle", a channel name or a URL into a listable URL.</summary>
    public static string NormalizeSource(string kind, string value)
    {
        value = value.Trim();

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            bool channel = Regex.IsMatch(value, @"youtube\.com/(@|channel/|c/|user/)", RegexOptions.IgnoreCase);
            bool already = Regex.IsMatch(value, @"/(videos|shorts|streams|playlists)|list=", RegexOptions.IgnoreCase);

            return channel && !already ? value.TrimEnd('/') + "/videos" : value;
        }

        if (kind == "playlist")
            return "https://www.youtube.com/playlist?list=" + value;

        string handle = value.StartsWith('@') ? value : "@" + value;

        return $"https://www.youtube.com/{handle}/videos";
    }

    // ---- links ----

    private static readonly Regex VideoLink = new(
        @"(?:youtube\.com/(?:watch\?(?:[^#\s]*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? ExtractVideoId(string text)
    {
        Match m = VideoLink.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool IsPlaylistLink(string text) =>
        Regex.IsMatch(text, @"youtube\.com/playlist\?(?:[^#\s]*&)?list=", RegexOptions.IgnoreCase);

    // ---- downloading ----

    /// <summary>
    /// H.264 + AAC in MP4 only, because that is what the built-in player
    /// (Windows Media Foundation) can play without extra codecs.
    /// </summary>
    public static string BuildFormat(int maxHeight) =>
        $"bv*[vcodec^=avc1][height<={maxHeight}]+ba[acodec^=mp4a]" +
        $"/b[ext=mp4][height<={maxHeight}]" +
        "/bv*[vcodec^=avc1]+ba[acodec^=mp4a]/b[ext=mp4]";

    public static async Task<string> DownloadAsync(
        string videoId,
        string folder,
        int maxHeight,
        int rateKb,
        Action<DownloadProgress> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(folder);

        List<string> args = new()
        {
            "--no-playlist", "--no-warnings", "--newline", "--no-mtime",
            "--retries", "10", "--fragment-retries", "10",
            "--concurrent-fragments", "2",
            "-f", BuildFormat(maxHeight),
            "--merge-output-format", "mp4",
            "--windows-filenames", "--trim-filenames", "120",
            "-o", Path.Combine(folder, "%(title)s [%(id)s].%(ext)s"),
            "--progress-template",
            "download:UT|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s"
        };

        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (ffmpeg != null)
        {
            args.Add("--ffmpeg-location");
            args.Add(Path.GetDirectoryName(ffmpeg)!);
        }

        // Your own limit, lowered to the ceiling when you are in another program.
        rateKb = NetworkLimiter.DownloadLimitKb(rateKb);

        if (rateKb > 0)
        {
            args.Add("-r");
            args.Add(rateKb + "K");
        }

        args.Add("https://www.youtube.com/watch?v=" + videoId);

        // yt-dlp downloads audio and video separately, each counting 0-100%.
        // Combine them so the bar moves once from 0 to 100.
        int streams = 1;
        int stream = 0;
        double previous = 0;

        ProcResult r = await RunAsync(args, ct, line =>
        {
            line = Ansi.Replace(line, "").Trim();

            // "[download] Destination: ...\id.f140.m4a" = a separate DASH stream.
            if (line.StartsWith("[download] Destination:") &&
                Regex.IsMatch(line, @"\.f\d+\.\w+$"))
            {
                streams = 2;
                return;
            }

            if (!line.StartsWith("UT|"))
                return;

            string[] p = line.Split('|');

            if (p.Length >= 4 &&
                double.TryParse(
                    p[1].Trim().TrimEnd('%'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double percent))
            {
                // A drop means the next stream started.
                if (percent + 30 < previous && stream < streams - 1)
                    stream++;

                previous = percent;

                double overall = (stream + percent / 100.0) / streams * 100.0;

                progress(new DownloadProgress(
                    Math.Min(99.5, overall), p[2].Trim(), p[3].Trim()));
            }
        }, captureStdout: false);

        string? file = FindOutput(folder, videoId);

        if (r.ExitCode != 0 || file == null)
        {
            throw new InvalidOperationException(
                LastError(r.Stderr) ?? "The download failed.");
        }

        return file;
    }

    /// <summary>The subtitle tracks a video offers (looked up through your account when signed in).</summary>
    public static async Task<List<SubtitleTrack>> SubtitlesAsync(string videoId, CancellationToken ct)
    {
        async Task<List<SubtitleTrack>> Run(string? cookies)
        {
            List<string> args = new() { "--no-playlist", "--no-warnings", "--skip-download", "--dump-json" };

            if (cookies != null)
            {
                args.Add("--cookies");
                args.Add(cookies);
            }

            args.Add("https://www.youtube.com/watch?v=" + videoId);

            ProcResult r = await RunAsync(args, ct);
            string json = r.Stdout.Trim();

            if (!json.StartsWith('{'))
                throw new InvalidOperationException(LastError(r.Stderr) ?? "Could not look up the subtitles.");

            using JsonDocument doc = JsonDocument.Parse(json);
            List<SubtitleTrack> tracks = new();

            void Read(string section, bool auto)
            {
                if (!doc.RootElement.TryGetProperty(section, out JsonElement all) ||
                    all.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (JsonProperty lang in all.EnumerateObject())
                {
                    // Automatic captions come in 100+ translations; keep the spoken language only.
                    if (auto && !lang.Name.EndsWith("-orig", StringComparison.OrdinalIgnoreCase))
                        continue;

                    JsonElement? best = null;

                    foreach (string ext in new[] { "json3", "vtt" })
                    {
                        foreach (JsonElement f in lang.Value.EnumerateArray())
                        {
                            if (Str(f, "ext") == ext && Str(f, "url") != null)
                            {
                                best = f;
                                break;
                            }
                        }

                        if (best != null)
                            break;
                    }

                    if (best == null)
                        continue;

                    string code = auto ? lang.Name[..^5] : lang.Name;
                    string name = Str(best.Value, "name") ?? code;

                    tracks.Add(new SubtitleTrack(
                        code, auto ? name.Replace(" (Original)", "") + " (automatic)" : name,
                        Str(best.Value, "url")!, Str(best.Value, "ext")!, auto));
                }
            }

            Read("subtitles", false);
            Read("automatic_captions", true);

            return tracks;
        }

        if (YtAccount.SignedIn)
        {
            try
            {
                return await YtAccount.WithCookieFile(file => Run(file));
            }
            catch (AuthExpiredException)
            {
                // Fall back to anonymous below.
            }
        }

        return await Run(null);
    }

    /// <summary>The playlist id in a link (any form with list=...) or a bare id.</summary>
    public static string? ExtractPlaylistId(string text)
    {
        Match m = Regex.Match(text, @"[?&]list=([A-Za-z0-9_-]{2,})");

        if (m.Success)
            return m.Groups[1].Value;

        string t = text.Trim();

        return Regex.IsMatch(t, @"^(PL|UU|OL|LL|FL|RD)[A-Za-z0-9_-]{10,}$") || t is "WL" or "LL"
            ? t
            : null;
    }

    /// <summary>
    /// The playlists of the signed-in account: the ones you made, Liked videos,
    /// Watch later and so on.
    /// </summary>
    public static async Task<List<SavedPlaylist>> MyPlaylistsAsync(CancellationToken ct)
    {
        if (!YtAccount.SignedIn)
            throw new AuthExpiredException();

        return await YtAccount.WithCookieFile(async file =>
        {
            ProcResult r = await RunAsync(new[]
            {
                "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors",
                "--cookies", file, "https://www.youtube.com/feed/playlists"
            }, ct);

            List<SavedPlaylist> found = new();

            foreach (string line in r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement e = doc.RootElement;

                    string? id = Str(e, "id");
                    string? title = Str(e, "title");

                    if (id != null && title != null && !found.Any(p => p.Id == id))
                        found.Add(new SavedPlaylist { Id = id, Title = title });
                }
                catch
                {
                    // A line that is not a playlist.
                }
            }

            if (found.Count == 0 && r.ExitCode != 0)
            {
                string? error = LastError(r.Stderr);

                if (error != null && LooksLikeAuthError(error))
                    throw new AuthExpiredException();

                throw new InvalidOperationException(error ?? "Could not load your playlists.");
            }

            // Your own first, the YouTube ones (Liked, Watch later...) after them.
            return found
                .OrderBy(p => p.Id.Length <= 3 ? 1 : 0)
                .ToList();
        });
    }

    /// <summary>Loads a playlist (title + videos). Private ones need your account.</summary>
    public static async Task<PlaylistData> PlaylistAsync(string linkOrId, int max, CancellationToken ct)
    {
        string id = ExtractPlaylistId(linkOrId)
            ?? throw new InvalidOperationException("That does not look like a playlist link.");

        string url = "https://www.youtube.com/playlist?list=" + id;

        async Task<PlaylistData> Run(string? cookies)
        {
            List<string> args = new()
            {
                "--flat-playlist", "--dump-single-json", "--no-warnings", "--ignore-errors",
                "--playlist-end", max.ToString()
            };

            if (cookies != null)
            {
                args.Add("--cookies");
                args.Add(cookies);
            }

            args.Add(url);

            ProcResult r = await RunAsync(args, ct);
            string json = r.Stdout.Trim();

            if (!json.StartsWith('{'))
            {
                string? error = LastError(r.Stderr);

                if (error != null && LooksLikeAuthError(error))
                    throw new AuthExpiredException();

                throw new InvalidOperationException(error ?? "Could not load the playlist.");
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            List<VideoInfo> videos = new();

            if (root.TryGetProperty("entries", out JsonElement entries) &&
                entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in entries.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object)
                        continue;

                    string? vid = Str(e, "id");
                    string title = Str(e, "title") ?? "";

                    // Removed and private videos have no usable title.
                    if (vid == null || vid.Length != 11 ||
                        title.StartsWith("[Private", StringComparison.OrdinalIgnoreCase) ||
                        title.StartsWith("[Deleted", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int duration = e.TryGetProperty("duration", out JsonElement d) && d.ValueKind == JsonValueKind.Number
                        ? (int)d.GetDouble()
                        : 0;

                    videos.Add(new VideoInfo(vid, title, Str(e, "channel") ?? Str(e, "uploader") ?? "", duration));
                }
            }

            return new PlaylistData(
                id,
                Str(root, "title") ?? "Playlist",
                Str(root, "channel") ?? Str(root, "uploader") ?? "",
                videos);
        }

        if (YtAccount.SignedIn)
        {
            try
            {
                return await YtAccount.WithCookieFile(file => Run(file));
            }
            catch (AuthExpiredException)
            {
                // Fall back to anonymous below.
            }
        }

        return await Run(null);
    }

    public static string? FindOutput(string folder, string videoId)
    {
        try
        {
            // "Title [id].mp4" (and the older "id.mp4").
            return Directory.GetFiles(folder, videoId + ".*")
                .Concat(Directory.GetFiles(folder, "* [" + videoId + "].*"))
                .FirstOrDefault(f =>
                    f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains(".boost", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
