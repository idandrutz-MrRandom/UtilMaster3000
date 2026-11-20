using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

// ======================================================
// LOCAL STREAM PROXY
//
// The Windows media element used by the player only accepts plain "http"
// addresses (it rejects every "https" one), but YouTube streams are https.
// This tiny server listens on this PC only (loopback), fetches the real
// stream and hands it to the player as http. It forwards Range requests,
// so seeking works, and keeps nothing in memory: bytes flow straight
// through.
// ======================================================

internal sealed class StreamProxy : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    /// <summary>Diagnostics hook (tests).</summary>
    public static Action<string>? Trace { get; set; }

    private static void Note(string message)
    {
        StreamLog.Write("proxy", message);
        Trace?.Invoke(message);
    }

    private readonly HttpListener listener = new();
    // Shared by every player window, so connections to YouTube (already
    // negotiated) are reused instead of set up again for each video.
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true
    })
    {
        // Streams are long; the connection itself is what times out.
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient http = SharedHttp;
    private sealed record Target(
        string Url, Dictionary<string, string>? Headers, BridgeSpec? Bridge);

    private readonly ConcurrentDictionary<string, Target> targets = new();
    private readonly CancellationTokenSource stop = new();

    private readonly string prefix;

    public StreamProxy()
    {
        // Find a free port, then listen on it (loopback only).
        int port;

        using (TcpListener probe = new(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Returns a local http address that plays the given stream.</summary>
    public string Register(string httpsUrl, Dictionary<string, string>? headers = null)
    {
        string token = Guid.NewGuid().ToString("N");
        targets[token] = new Target(httpsUrl, headers, null);

        // The .mp4 ending helps the media engine pick the right decoder.
        return $"{prefix}{token}.mp4";
    }

    /// <summary>
    /// Returns a local http address that plays the given source after
    /// converting it with ffmpeg (any container/codec, joins separate video
    /// and audio streams). The result is a live fragmented MP4, so it has no
    /// seek index: the player restarts it at a new start time to seek.
    /// </summary>
    public string RegisterBridge(BridgeSpec spec)
    {
        // YouTube inputs go through this proxy too (chunked, read ahead).
        spec = spec with
        {
            Inputs = spec.Inputs
                .Select(i => i.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                             ChunkedLength(i.Source) != null
                    ? new BridgeInput(Register(i.Source, i.Headers), null)
                    : i)
                .ToList()
        };

        // Start every input's first piece downloading together.
        Warm(spec.Inputs.Select(i => (i.Source, i.Headers)));

        string token = Guid.NewGuid().ToString("N");
        targets[token] = new Target("", null, spec);

        return $"{prefix}{token}.mp4";
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                return; // listener closed
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath.Trim('/') ?? "";
            string token = Path.GetFileNameWithoutExtension(path);

            if (!targets.TryGetValue(token, out Target? target))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            if (target.Bridge != null)
            {
                Note($"bridge request: {context.Request.HttpMethod} range={context.Request.Headers["Range"] ?? "-"} token={token[..6]}");
                await ServeBridge(context, token, target.Bridge);
                return;
            }

            bool head = context.Request.HttpMethod == "HEAD";

            // YouTube media: fetched in chunks, a little ahead of playback.
            long? total = ChunkedLength(target.Url);

            if (total != null)
            {
                await ServeChunked(context, target, total.Value, head);
                return;
            }

            using HttpRequestMessage request = new(
                head ? HttpMethod.Head : HttpMethod.Get, target.Url);

            if (target.Headers is { Count: > 0 })
            {
                // The exact headers yt-dlp uses for this stream.
                foreach (KeyValuePair<string, string> h in target.Headers)
                {
                    if (!h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }
            else
            {
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            }

            // Seeking: pass the byte range on.
            string? range = context.Request.Headers["Range"];

            if (!string.IsNullOrEmpty(range))
                request.Headers.TryAddWithoutValidation("Range", range);

            using HttpResponseMessage response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, stop.Token);

            HttpListenerResponse output = context.Response;

            output.StatusCode = (int)response.StatusCode;
            output.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/mp4";
            output.AddHeader("Accept-Ranges", "bytes");

            if (response.Content.Headers.ContentRange != null)
                output.AddHeader("Content-Range", response.Content.Headers.ContentRange.ToString());

            long? length = response.Content.Headers.ContentLength;

            if (length != null)
                output.ContentLength64 = length.Value;
            else
                output.SendChunked = true;

            if (head)
            {
                output.Close();
                return;
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(stop.Token);

            // Straight through, in small pieces: nothing is kept.
            byte[] buffer = new byte[64 * 1024];
            int read;

            while ((read = await body.ReadAsync(buffer, stop.Token)) > 0)
                await output.OutputStream.WriteAsync(buffer.AsMemory(0, read), stop.Token);

            output.Close();
        }
        catch (Exception ex)
        {
            Note("proxy: request ended: " + ex.GetType().Name + " " + ex.Message);

            // The player closes the connection whenever you seek or stop:
            // that is normal, not an error.
            try
            {
                context.Response.Abort();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    // ------------------------------------------------------------
    // CHUNKED LOADING
    // YouTube slows down one long download of a big file and starts
    // slowly. Asking for it piece by piece (the way yt-dlp does) starts
    // at once and stays fast, and the next piece is already on its way
    // while the current one is being played.
    // ------------------------------------------------------------

    private const long FirstChunk = 768 * 1024;
    private const long ChunkSize = 4 * 1024 * 1024;

    // The size of the file, when the address says it (googlevideo "clen").
    private static long? ChunkedLength(string url)
    {
        if (url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        System.Text.RegularExpressions.Match m =
            System.Text.RegularExpressions.Regex.Match(url, @"[?&]clen=(\d+)");

        return m.Success && long.TryParse(m.Groups[1].Value, out long n) && n > 0
            ? n
            : null;
    }

    private static string MimeFromUrl(string url)
    {
        System.Text.RegularExpressions.Match m =
            System.Text.RegularExpressions.Regex.Match(url, @"[?&]mime=([^&]+)");

        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "video/mp4";
    }

    // The first piece of a stream, fetched before anyone asks for it, so
    // playback can start without waiting for the network at all.
    private static readonly ConcurrentDictionary<string, (Task<byte[]> Data, DateTime Made)> Heads = new();

    /// <summary>Starts downloading the beginning of these streams now.</summary>
    public static void Warm(IEnumerable<(string Url, Dictionary<string, string>? Headers)> inputs)
    {
        foreach ((string url, Dictionary<string, string>? headers) in inputs)
        {
            if (ChunkedLength(url) == null)
                continue;

            if (Heads.TryGetValue(url, out var existing) &&
                DateTime.UtcNow - existing.Made < TimeSpan.FromMinutes(30) &&
                !existing.Data.IsFaulted)
            {
                continue;
            }

            // Keep it small: a handful of pictures worth of memory at most.
            if (Heads.Count > 24)
            {
                foreach (string old in Heads.OrderBy(h => h.Value.Made).Take(8).Select(h => h.Key).ToList())
                    Heads.TryRemove(old, out _);
            }

            Target target = new(url, headers, null);

            Heads[url] = (FetchChunk(target, 0, FirstChunk - 1, SharedHttp, CancellationToken.None), DateTime.UtcNow);
        }
    }

    private Task<byte[]> FetchChunk(Target target, long from, long to) =>
        FetchChunk(target, from, to, http, stop.Token);

    private static async Task<byte[]> FetchChunk(
        Target target, long from, long to, HttpClient client, CancellationToken token)
    {
        // The warmed-up beginning, if it is ready (or on its way).
        if (from == 0 && to == FirstChunk - 1 &&
            Heads.TryGetValue(target.Url, out var head) && !head.Data.IsFaulted)
        {
            try
            {
                byte[] cached = await head.Data;

                if (cached.Length > 0)
                    return cached;
            }
            catch
            {
                // Fall through to a normal download.
            }
        }

        string url = target.Url + (target.Url.Contains('?') ? "&" : "?") + $"range={from}-{to}";

        Exception? last = null;

        // A brand-new YouTube address answers "403" for the first ~2.5 s;
        // keep asking (4 times a second) until it opens, but give up after 25 s.
        System.Diagnostics.Stopwatch waited = System.Diagnostics.Stopwatch.StartNew();

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);

                if (target.Headers is { Count: > 0 })
                {
                    foreach (KeyValuePair<string, string> h in target.Headers)
                    {
                        if (!h.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                            request.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                }

                // A request that gets no answer must not hang the picture forever:
                // give each try a time limit, then ask again.
                // (Waiting for the answer is limited; so is a download that stops
                // moving. A slow but steady download is left alone.)
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(RequestLimitMs);

                System.Diagnostics.Stopwatch attemptClock = System.Diagnostics.Stopwatch.StartNew();

                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

                response.EnsureSuccessStatusCode();

                await using Stream body = await response.Content.ReadAsStreamAsync(limit.Token);
                using MemoryStream collected = new();
                byte[] piece = new byte[128 * 1024];

                while (true)
                {
                    using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(RequestLimitMs);

                    int n = await body.ReadAsync(piece, idle.Token);

                    if (n == 0)
                        break;

                    collected.Write(piece, 0, n);

                    // Under the ceiling while you are in another program.
                    await NetworkLimiter.ThrottleAsync(n, token);
                }

                byte[] data = collected.ToArray();

                // Speed of this successful request (retries and waits excluded).
                if (data.Length >= 1_500_000)
                    NoteSpeed(data.Length, attemptClock.Elapsed.TotalSeconds);
                Note($"chunk {from / 1024}K+{data.Length / 1024}K ({MimeFromUrl(target.Url)})");
                return data;
            }
            catch (Exception ex) when (
                ex is HttpRequestException or IOException ||
                (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                last = ex;

                Note($"chunk {from / 1024}K: {(ex is OperationCanceledException ? "no answer in time" : ex.Message)}, asking again");

                if (waited.ElapsedMilliseconds > 45000)
                    throw;

                await Task.Delay(250, token);
            }
        }
    }

    private async Task ServeChunked(
        HttpListenerContext context, Target target, long total, bool head)
    {
        long start = 0;
        long end = total - 1;
        bool partial = false;

        string? range = context.Request.Headers["Range"];

        if (!string.IsNullOrEmpty(range))
        {
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d*)-(\d*)");

            if (m.Success)
            {
                partial = true;

                if (m.Groups[1].Value.Length == 0)
                {
                    // "the last N bytes"
                    long.TryParse(m.Groups[2].Value, out long tail);
                    start = Math.Max(0, total - tail);
                }
                else
                {
                    start = long.Parse(m.Groups[1].Value);

                    if (m.Groups[2].Value.Length > 0)
                        end = Math.Min(end, long.Parse(m.Groups[2].Value));
                }
            }
        }

        if (start > end || start >= total)
        {
            context.Response.StatusCode = 416;
            context.Response.AddHeader("Content-Range", $"bytes */{total}");
            context.Response.Close();
            return;
        }

        HttpListenerResponse output = context.Response;

        output.StatusCode = partial ? 206 : 200;
        output.ContentType = MimeFromUrl(target.Url);
        output.AddHeader("Accept-Ranges", "bytes");

        if (partial)
            output.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");

        output.ContentLength64 = end - start + 1;

        if (head)
        {
            output.Close();
            return;
        }

        long position = start;
        long ChunkEnd(long from) =>
            Math.Min(end, from + (from == start ? FirstChunk : ChunkSize) - 1);

        Task<byte[]>? next = FetchChunk(target, position, ChunkEnd(position));
        string kind = MimeFromUrl(target.Url);
        Note($"serving {kind} from {start / 1024}K of {total / 1024}K");

        while (position <= end)
        {
            long thisEnd = ChunkEnd(position);
            byte[] data = await next;

            position = thisEnd + 1;

            // The next piece starts downloading while this one is played.
            next = position <= end
                ? FetchChunk(target, position, ChunkEnd(position))
                : null;

            System.Diagnostics.Stopwatch writing = System.Diagnostics.Stopwatch.StartNew();
            await output.OutputStream.WriteAsync(data, stop.Token);

            // The converter takes a piece in well under a second; longer means it stopped reading.
            if (writing.ElapsedMilliseconds > 2500)
                Note($"{kind}: the converter took {writing.Elapsed.TotalSeconds:0.0}s to accept the piece ending at {thisEnd / 1024}K");
            else
                Note($"{kind}: piece ending at {thisEnd / 1024}K accepted in {writing.ElapsedMilliseconds} ms");
        }

        Note($"{kind}: all of it was handed over");

        output.Close();
    }

    // How much converted video may wait in memory for the player (a few minutes).
    internal static long ReadAheadBytes = 150L * 1024 * 1024;

    // ---- how fast is YouTube answering on this connection? (megabits per second) ----

    private static double recentMbps;
    private static readonly object SpeedLock = new();

    /// <summary>0 until a few downloads have been timed.</summary>
    public static double RecentMbps
    {
        get
        {
            lock (SpeedLock)
                return recentMbps;
        }
        internal set
        {
            lock (SpeedLock)
                recentMbps = value;
        }
    }

    internal static void NoteSpeed(long bytes, double seconds)
    {
        if (seconds <= 0.02)
            return;

        double mbps = bytes * 8 / 1_000_000.0 / seconds;

        lock (SpeedLock)
            recentMbps = recentMbps <= 0 ? mbps : recentMbps * 0.6 + mbps * 0.4;
    }

    /// <summary>A picture stalled: the connection is not as good as it looked.</summary>
    internal static void NoteStall()
    {
        lock (SpeedLock)
        {
            if (recentMbps > 6)
                recentMbps = 6;
        }
    }

    // No answer (or no data) for this long: ask again.
    private const int RequestLimitMs = 9000;

    // Bridges started ahead of time (a quality switch is prepared while the
    // old picture keeps playing), waiting for the player to ask for them.
    private readonly ConcurrentDictionary<string, BridgeRun> prepared = new();

    /// <summary>
    /// Like RegisterBridge, but the conversion starts right now and keeps
    /// downloading into a buffer, so the player can switch to it instantly.
    /// </summary>
    public string PrepareBridge(BridgeSpec spec)
    {
        string uri = RegisterBridge(spec);
        string token = Path.GetFileNameWithoutExtension(new Uri(uri).AbsolutePath);

        if (targets.TryGetValue(token, out Target? target) && target.Bridge != null)
        {
            BridgeRun? run = BridgeRun.Start(target.Bridge, stop.Token);

            if (run != null)
            {
                prepared[token] = run;

                // Never asked for (the player moved on): clean it up.
                _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ =>
                {
                    if (prepared.TryRemove(token, out BridgeRun? unused))
                        unused.Dispose();
                });
            }
        }

        return uri;
    }

    /// <summary>True once a prepared bridge has its first pictures ready.</summary>
    public async Task<bool> WaitReadyAsync(string uri, int timeoutMs)
    {
        string token = Path.GetFileNameWithoutExtension(new Uri(uri).AbsolutePath);

        if (!prepared.TryGetValue(token, out BridgeRun? run))
            return false;

        Task finished = await Task.WhenAny(run.FirstOutput.Task, Task.Delay(timeoutMs));

        return finished == run.FirstOutput.Task && run.FirstOutput.Task.Result;
    }

    private static readonly ConcurrentDictionary<int, Process> RunningBridges = new();

    private static long lastBridgeActivity;

    /// <summary>
    /// A video is being loaded for playback right now (it started, or it is still reading ahead).
    /// Saving videos for offline use waits for this to end, so it does not take the connection
    /// from the picture the user is watching.
    /// </summary>
    internal static bool StreamIsLoading =>
        Environment.TickCount64 < Interlocked.Read(ref startingUntil) ||
        (!RunningBridges.IsEmpty && Environment.TickCount64 - Interlocked.Read(ref lastBridgeActivity) < 3000);

    private static long startingUntil;

    /// <summary>
    /// A video is about to be streamed (the address is still being looked up): saving other
    /// videos steps aside for the first seconds, which are the ones that decide if it pauses.
    /// </summary>
    internal static void NoteStarting() =>
        Interlocked.Exchange(ref startingUntil, Environment.TickCount64 + 25000);

    private static void NoteBridgeActivity() => Interlocked.Exchange(ref lastBridgeActivity, Environment.TickCount64);

    /// <summary>Tests: stops the converters this app started (and only those).</summary>
    internal static int KillBridgesForTest()
    {
        int n = 0;

        foreach (Process process in RunningBridges.Values.ToList())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    n++;
                }
            }
            catch
            {
                // Already gone.
            }
        }

        return n;
    }

    // One ffmpeg conversion whose output is read ahead into a queue.
    private sealed class BridgeRun : IDisposable
    {
        private readonly CancellationTokenSource cts;
        private readonly Process ffmpeg;
        private long buffered;
        private long outMicros;
        private int complete;
        private long lastProgressLog;

        private static readonly System.Text.RegularExpressions.Regex ProgressLine = new(
            @"^(frame|fps|stream_\d+_\d+_q|bitrate|total_size|out_time_ms|out_time|dup_frames|drop_frames|speed|progress)=",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>How many seconds of the video the conversion has produced so far.</summary>
        public double OutSeconds => Interlocked.Read(ref outMicros) / 1_000_000.0;

        /// <summary>The conversion reached the end of the video.</summary>
        public bool Complete => Volatile.Read(ref complete) == 1;

        private string lastFields = "";

        public void NoteField(string line)
        {
            if (line.StartsWith("total_size=") || line.StartsWith("speed=") || line.StartsWith("frame="))
                lastFields = (lastFields.Length > 120 ? "" : lastFields + " ") + line;
        }

        public void NoteProgress(long micros)
        {
            Interlocked.Exchange(ref outMicros, micros);
            NoteBridgeActivity();

            long now = Environment.TickCount64;

            if (now - lastProgressLog > 10000)
            {
                lastProgressLog = now;
                string cpu = "";

                try { cpu = $", ffmpeg cpu {ffmpeg.TotalProcessorTime.TotalSeconds:0.0}s"; } catch { }

                StreamLog.Write("bridge", $"converted {micros / 1_000_000.0:0.0}s so far, {Interlocked.Read(ref buffered) / 1048576.0:0.0} MB waiting for the player{cpu} [{lastFields}]");
            }
        }

        public System.Threading.Channels.Channel<byte[]> Queue { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public TaskCompletionSource<bool> FirstOutput { get; } = new();

        private readonly int processId;

        private readonly string outputFile;

        private BridgeRun(Process process, CancellationToken outer, string outputFile)
        {
            this.outputFile = outputFile;
            ffmpeg = process;
            processId = process.Id;
            cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        }

        public static BridgeRun? Start(BridgeSpec spec, CancellationToken outer)
        {
            string? exe = FfmpegRecorder.FindFfmpeg();

            if (exe == null)
                return null;

            ProcessStartInfo info = new(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            // The converter writes to a file at full speed (nothing the player does can slow it
            // down) and the player is fed from that file as it grows.
            string folder = Path.Combine(Path.GetTempPath(), "UtilMaster3000-stream");
            Directory.CreateDirectory(folder);
            CleanOldFiles(folder);
            string outputFile = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp4");

            foreach (string a in spec.BuildArguments(outputFile))
                info.ArgumentList.Add(a);

            Process process = Process.Start(info)!;
            ChildJob.Add(process);
            RunningBridges[process.Id] = process;
            NoteBridgeActivity();
            Note("bridge: ffmpeg started");

            try
            {
                // Converting the picture (not just copying it) must keep ahead of playback,
                // so it gets a normal share of the processor; copying is light work.
                process.PriorityClass = spec.CopyVideo ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
            }
            catch
            {
                // Not important.
            }

            BridgeRun run = new(process, outer, outputFile);

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                // "-progress" lines: how far the conversion has got.
                if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
                {
                    if (long.TryParse(e.Data.AsSpan(12), out long micros) && micros >= 0)
                        run.NoteProgress(micros);

                    return;
                }

                if (ProgressLine.IsMatch(e.Data))
                {
                    run.NoteField(e.Data);
                    return;
                }

                Note("ffmpeg: " + e.Data);
            };
            process.BeginErrorReadLine();

            _ = Task.Run(run.Produce);
            return run;
        }

        // ffmpeg downloads at full network speed into the queue while the
        // player takes what it needs: a slow moment on the network is then
        // invisible, like in YouTube's own player.
        private async Task Produce()
        {
            try
            {
                byte[] buffer = new byte[128 * 1024];
                int read;
                bool first = true;
                FileStream? body = null;

                // The converter creates the file a moment after it starts.
                while (body == null)
                {
                    try
                    {
                        body = new FileStream(outputFile, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                    }
                    catch (Exception ex) when (ex is IOException && !cts.IsCancellationRequested && !ffmpeg.HasExited)
                    {
                        await Task.Delay(30, cts.Token);
                    }
                }

                using (body)
                {
                    while (true)
                    {
                        read = await body.ReadAsync(buffer, cts.Token);

                        if (read == 0)
                        {
                            // Caught up with the converter: wait for more, or finish if it is done.
                            if (!ffmpeg.HasExited)
                            {
                                await Task.Delay(30, cts.Token);
                                continue;
                            }

                            read = await body.ReadAsync(buffer, cts.Token);

                            if (read == 0)
                                break;
                        }

                        NoteBridgeActivity();

                        if (first)
                        {
                            first = false;
                            Note("bridge: first output bytes");
                            FirstOutput.TrySetResult(true);
                        }

                        // Far enough ahead: wait for the player to catch up.
                        while (Interlocked.Read(ref buffered) > ReadAheadBytes)
                            await Task.Delay(100, cts.Token);

                        Interlocked.Add(ref buffered, read);
                        await Queue.Writer.WriteAsync(buffer.AsMemory(0, read).ToArray(), cts.Token);
                    }
                }
            }
            catch
            {
                // Ends when the player leaves.
            }
            finally
            {
                FirstOutput.TrySetResult(false);
                Queue.Writer.TryComplete();
                LogEnd();
            }
        }

        // Says how the conversion ended: finished, or stopped early (and why).
        private void LogEnd()
        {
            try
            {
                if (cts.IsCancellationRequested)
                {
                    StreamLog.Write("bridge", $"stopped at {OutSeconds:0.0}s (the player moved on)");
                    return;
                }

                bool exited = ffmpeg.WaitForExit(3000);
                int code = exited ? ffmpeg.ExitCode : -1;

                if (code == 0)
                    Interlocked.Exchange(ref complete, 1);

                StreamLog.Write("bridge", code == 0
                    ? $"conversion finished ({OutSeconds:0.0}s of video)"
                    : $"CONVERSION ENDED EARLY at {OutSeconds:0.0}s (ffmpeg exit code {(exited ? code.ToString() : "still running")})");
            }
            catch
            {
                // Already cleaned up.
            }
        }

        // Converted files of earlier runs (a crash, a forced close) are not kept.
        private static void CleanOldFiles(string folder)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder, "*.mp4"))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(3))
                        File.Delete(file);
                }
            }
            catch
            {
                // Not important.
            }
        }

        public void Taken(int bytes) => Interlocked.Add(ref buffered, -bytes);

        public CancellationToken Token => cts.Token;

        public void Dispose()
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
                // Already gone.
            }

            try
            {
                if (!ffmpeg.HasExited)
                    ffmpeg.Kill(true);
            }
            catch
            {
                // Already finished.
            }

            RunningBridges.TryRemove(processId, out _);
            ffmpeg.Dispose();

            try
            {
                File.Delete(outputFile);
            }
            catch
            {
                // Still open for a moment: removed by the next cleanup.
            }
        }
    }

    private volatile BridgeRun? currentRun;

    /// <summary>A new stream is about to start: what the last one had loaded no longer applies.</summary>
    public void ResetLoaded() => currentRun = null;

    /// <summary>
    /// How far into the video (seconds from where this conversion started) the picture is
    /// already converted and waiting; null when nothing is being served.
    /// </summary>
    public (double Seconds, bool Complete)? Loaded
    {
        get
        {
            BridgeRun? run = currentRun;
            return run == null ? null : (run.OutSeconds, run.Complete);
        }
    }

    private async Task ServeBridge(HttpListenerContext context, string token, BridgeSpec spec)
    {
        HttpListenerResponse output = context.Response;
        BridgeRun? run = null;

        try
        {
            if (context.Request.HttpMethod == "HEAD" || FfmpegRecorder.FindFfmpeg() == null)
            {
                output.StatusCode = FfmpegRecorder.FindFfmpeg() == null ? 500 : 200;
                output.ContentType = "video/mp4";
                output.Close();
                return;
            }

            // A prepared one is already running (and buffered); otherwise start now.
            if (!prepared.TryRemove(token, out run))
                run = BridgeRun.Start(spec, stop.Token);

            if (run == null)
            {
                output.StatusCode = 500;
                output.Close();
                return;
            }

            currentRun = run;

            output.StatusCode = 200;
            output.ContentType = "video/mp4";
            output.SendChunked = true;

            await foreach (byte[] piece in run.Queue.Reader.ReadAllAsync(run.Token))
            {
                run.Taken(piece.Length);
                await output.OutputStream.WriteAsync(piece, run.Token);
                await output.OutputStream.FlushAsync(run.Token);
            }

            output.Close();
        }
        catch
        {
            try
            {
                output.Abort();
            }
            catch
            {
                // Gone.
            }
        }
        finally
        {
            // The player left (seek, stop, close): end the conversion. (How far it got is
            // still reported until the next stream starts, for the loaded part of the time bar.)
            run?.Dispose();
        }
    }

    public void Dispose()
    {
        stop.Cancel();

        foreach (BridgeRun run in prepared.Values)
            run.Dispose();

        try
        {
            listener.Close();
        }
        catch
        {
            // Ignore.
        }

        stop.Dispose();
    }
}

/// <summary>One thing for ffmpeg to read: an https address or a file.</summary>
internal sealed record BridgeInput(string Source, Dictionary<string, string>? Headers);

/// <summary>What the ffmpeg bridge should convert (see StreamProxy.RegisterBridge).</summary>
internal sealed record BridgeSpec(
    List<BridgeInput> Inputs, double StartSeconds, bool CopyVideo)
{
    public IEnumerable<string> BuildArguments(string outputFile)
    {
        List<string> a = new()
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            // How far the conversion has got (read by the time bar's loaded display).
            "-progress", "pipe:2", "-stats_period", "0.5"
        };

        foreach (BridgeInput input in Inputs)
        {
            if (input.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (input.Headers != null)
                {
                    string ua = "";
                    System.Text.StringBuilder rest = new();

                    foreach (KeyValuePair<string, string> h in input.Headers)
                    {
                        if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                            ua = h.Value;
                        else
                            rest.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
                    }

                    if (ua.Length > 0)
                    {
                        a.Add("-user_agent");
                        a.Add(ua);
                    }

                    if (rest.Length > 0)
                    {
                        a.Add("-headers");
                        a.Add(rest.ToString());
                    }
                }

                a.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5" });

                // Look at a little less before starting: faster to the first frame.
                a.AddRange(new[] { "-probesize", "1500000", "-analyzeduration", "1500000" });
            }

            if (StartSeconds > 0.5)
            {
                a.Add("-ss");
                a.Add(StartSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            }

            a.Add("-i");
            a.Add(input.Source);
        }

        if (Inputs.Count >= 2)
            a.AddRange(new[] { "-map", "0:v:0", "-map", "1:a:0" });
        else
            a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });

        if (CopyVideo)
        {
            a.AddRange(new[] { "-c:v", "copy" });
        }
        else
        {
            a.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                "-pix_fmt", "yuv420p", "-vf", "scale=min(1920\\,iw):-2"
            });
        }

        a.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2" });
        a.AddRange(new[]
        {
            "-sn", "-dn", "-flush_packets", "1",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-y", "-f", "mp4", outputFile
        });

        return a;
    }
}
