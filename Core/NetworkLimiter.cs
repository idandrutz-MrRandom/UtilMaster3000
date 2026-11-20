using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

// ======================================================
// NETWORK LIMITER
// While you are in another program, the app keeps its network use under a
// ceiling (20 Mbps by default) so it does not hog your Wi-Fi. The moment you
// are back in the app (the launcher, UtilTube, the player...) it goes at full
// speed again. Set it in UtilTube -> Settings.
// ======================================================

internal static class NetworkLimiter
{
    /// <summary>
    /// The ceiling in megabits per second while unfocused (0 = no limit,
    /// negative = saving videos waits until you are back in the app).
    /// </summary>
    public static int LimitMbps { get; set; } = 20;

    /// <summary>Tests: pretend the app is (true) or is not (false) in front.</summary>
    public static bool? ForceForeground { get; set; }

    /// <summary>Bytes the app has downloaded through the limiter (tests, statistics).</summary>
    public static long BytesSeen;

    private static long nextFree;
    private static readonly object Gate = new();

    private static long checkedAt;
    private static bool foreground = true;
    private static bool otherFullscreen;

    /// <summary>
    /// Saving videos stops while another program fills the whole screen (a game, a
    /// film) and carries on afterwards. A download keeps a line busy, and that is
    /// what pushes your ping up in a game.
    /// </summary>
    public static bool PauseInFullscreen { get; set; } = true;

    /// <summary>
    /// True while downloads must wait: you chose "pause" for other programs, or a
    /// fullscreen program is in front. (Video you are watching is never held.)
    /// </summary>
    public static bool DownloadsHeld
    {
        get
        {
            if (AppInFront)
                return false;

            return LimitMbps < 0 || (PauseInFullscreen && otherFullscreen);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    // A window with no title bar that covers its whole screen, and is not the desktop.
    private static bool IsFullscreen(IntPtr window)
    {
        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0xC00000;

        if (!GetWindowRect(window, out Rect r))
            return false;

        if ((GetWindowLong(window, GWL_STYLE) & WS_CAPTION) == WS_CAPTION)
            return false;

        System.Text.StringBuilder name = new(64);
        GetClassName(window, name, name.Capacity);

        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;

        System.Drawing.Rectangle screen = System.Windows.Forms.Screen.FromHandle(window).Bounds;

        return r.Left <= screen.Left && r.Top <= screen.Top &&
               r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    /// <summary>True while one of the app's own windows is in front.</summary>
    public static bool AppInFront
    {
        get
        {
            if (ForceForeground != null)
                return ForceForeground.Value;

            long now = Environment.TickCount64;

            // Asked a lot while downloading: look at most 4 times a second.
            if (now - checkedAt < 250)
                return foreground;

            checkedAt = now;

            try
            {
                IntPtr window = GetForegroundWindow();

                if (window == IntPtr.Zero)
                {
                    foreground = false;
                    otherFullscreen = false;
                }
                else
                {
                    GetWindowThreadProcessId(window, out uint pid);
                    foreground = pid == (uint)Environment.ProcessId;
                    otherFullscreen = !foreground && IsFullscreen(window);
                }
            }
            catch
            {
                foreground = true;
                otherFullscreen = false;
            }

            return foreground;
        }
    }

    /// <summary>True when the ceiling applies right now.</summary>
    public static bool Limiting => LimitMbps > 0 && !AppInFront;

    /// <summary>
    /// Called after each piece of data arrives: waits as long as needed to stay
    /// under the ceiling (no wait while the app is in front).
    /// </summary>
    public static async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        Interlocked.Add(ref BytesSeen, bytes);

        if (!Limiting)
            return;

        double bytesPerSecond = LimitMbps * 125_000.0;
        long cost = (long)(bytes / bytesPerSecond * Stopwatch.Frequency);
        long wait;

        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();

            // No saved-up credit: a pause is not a licence to burst.
            if (nextFree < now)
                nextFree = now;

            nextFree += cost;

            // A quarter second of slack keeps it smooth.
            wait = nextFree - now - Stopwatch.Frequency / 4;
        }

        if (wait > 0)
            await Task.Delay((int)Math.Min(2000, wait * 1000 / Stopwatch.Frequency), ct);
    }

    /// <summary>
    /// yt-dlp's speed limit (KB/s) for a download that starts now: the user's own
    /// limit, lowered to the ceiling when the app is not in front. 0 = unlimited.
    /// </summary>
    public static int DownloadLimitKb(int usersOwnKb)
    {
        if (!Limiting)
            return usersOwnKb;

        int ceiling = Math.Max(50, LimitMbps * 125);   // 20 Mbps = 2500 KB/s

        return usersOwnKb > 0 ? Math.Min(usersOwnKb, ceiling) : ceiling;
    }
}
