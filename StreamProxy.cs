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
                await ServeBridge(context, target.Bridge);
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
            Trace?.Invoke("proxy: request ended: " + ex.GetType().Name + " " + ex.Message);

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

                using HttpResponseMessage response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);

                response.EnsureSuccessStatusCode();

                byte[] data = await response.Content.ReadAsByteArrayAsync(token);
                Trace?.Invoke($"chunk {from / 1024}K+{data.Length / 1024}K ({MimeFromUrl(target.Url)})");
                return data;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                last = ex;

                if (waited.ElapsedMilliseconds > 25000)
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

        while (position <= end)
        {
            long thisEnd = ChunkEnd(position);
            byte[] data = await next;

            position = thisEnd + 1;

            // The next piece starts downloading while this one is played.
            next = position <= end
                ? FetchChunk(target, position, ChunkEnd(position))
                : null;

            await output.OutputStream.WriteAsync(data, stop.Token);
        }

        output.Close();
    }

    private async Task ServeBridge(HttpListenerContext context, BridgeSpec spec)
    {
        HttpListenerResponse output = context.Response;
        Process? ffmpeg = null;

        try
        {
            string? exe = FfmpegRecorder.FindFfmpeg();

            if (exe == null || context.Request.HttpMethod == "HEAD")
            {
                output.StatusCode = exe == null ? 500 : 200;
                output.ContentType = "video/mp4";
                output.Close();
                return;
            }

            ProcessStartInfo info = new(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string a in spec.BuildArguments())
                info.ArgumentList.Add(a);

            ffmpeg = Process.Start(info)!;
            ChildJob.Add(ffmpeg);
            Trace?.Invoke("bridge: ffmpeg started");

            try
            {
                ffmpeg.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // Not important.
            }

            ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    Trace?.Invoke("ffmpeg: " + e.Data);
            };
            ffmpeg.BeginErrorReadLine();

            output.StatusCode = 200;
            output.ContentType = "video/mp4";
            output.SendChunked = true;

            Stream body = ffmpeg.StandardOutput.BaseStream;
            byte[] buffer = new byte[64 * 1024];
            int read;

            bool firstOut = true;

            while ((read = await body.ReadAsync(buffer, stop.Token)) > 0)
            {
                if (firstOut)
                {
                    firstOut = false;
                    Trace?.Invoke("bridge: first output bytes");
                }

                await output.OutputStream.WriteAsync(buffer.AsMemory(0, read), stop.Token);
                await output.OutputStream.FlushAsync(stop.Token);
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
            // The player left (seek, stop, close): end the conversion.
            if (ffmpeg != null)
            {
                try
                {
                    if (!ffmpeg.HasExited)
                        ffmpeg.Kill(true);
                }
                catch
                {
                    // Already finished.
                }

                ffmpeg.Dispose();
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();

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
    public IEnumerable<string> BuildArguments()
    {
        List<string> a = new() { "-hide_banner", "-loglevel", "error", "-nostdin" };

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
            "-f", "mp4", "pipe:1"
        });

        return a;
    }
}
