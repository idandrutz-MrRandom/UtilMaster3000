using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace UtilMaster3000;

// ======================================================
// AUTOCLICKER ENGINE
// Clicks the mouse by itself: any speed from 1 ms up, left /
// right / middle button, single or double clicks, at the mouse
// or at a fixed point, a set number of times or until stopped.
// Safety: moving the mouse into the top-left corner of the
// screen stops it at once.
// ======================================================

internal sealed record AutoClickOptions(
    int IntervalMs,
    int JitterPercent,
    int Button,          // 0 left, 1 right, 2 middle
    bool Double,
    bool FixedPoint,
    int X,
    int Y,
    int Repeat,          // 0 = until stopped
    int StartDelayMs);

internal sealed class AutoClicker : IDisposable
{
    public static readonly string[] ButtonNames = { "Left", "Right", "Middle" };

    private Thread? worker;
    private volatile bool stopRequested;
    private long clicks;
    private readonly Random random = new();

    /// <summary>Raised on a background thread when it ends (with the reason).</summary>
    public event Action<string, long>? Stopped;

    public bool Running { get; private set; }

    public long Clicks => Interlocked.Read(ref clicks);

    /// <summary>The click that was just made (tests and counters use it).</summary>
    public event Action? Clicked;

    public void Start(AutoClickOptions options)
    {
        if (Running)
            return;

        Running = true;
        stopRequested = false;
        Interlocked.Exchange(ref clicks, 0);

        worker = new Thread(() => Run(options))
        {
            IsBackground = true,
            Name = "Autoclicker"
        };

        worker.Start();
    }

    public void Stop() => stopRequested = true;

    private void Run(AutoClickOptions o)
    {
        string reason = "Stopped";

        // Finer timer resolution so short intervals are accurate.
        timeBeginPeriod(1);

        try
        {
            Stopwatch clock = Stopwatch.StartNew();

            // Start delay (so you can move the mouse where it should click).
            while (clock.ElapsedMilliseconds < o.StartDelayMs)
            {
                if (stopRequested || InCorner())
                {
                    reason = stopRequested ? "Stopped" : "Stopped: mouse in the corner";
                    return;
                }

                Thread.Sleep(10);
            }

            double next = 0;
            bool clickedOnce = false;
            Stopwatch run = Stopwatch.StartNew();
            int interval = Math.Max(1, o.IntervalMs);

            while (!stopRequested)
            {
                if (InCorner())
                {
                    reason = "Stopped: mouse in the corner";
                    return;
                }

                if (o.FixedPoint)
                {
                    // You took the mouse away from the spot: stop.
                    if (clickedOnce && GetCursorPos(out POINT now) &&
                        (Math.Abs(now.X - o.X) > 12 || Math.Abs(now.Y - o.Y) > 12))
                    {
                        reason = "Stopped: you moved the mouse";
                        return;
                    }

                    SetCursorPos(o.X, o.Y);
                }

                clickedOnce = true;

                Click(o.Button);

                if (o.Double)
                    Click(o.Button);

                long done = Interlocked.Increment(ref clicks);
                Clicked?.Invoke();

                if (o.Repeat > 0 && done >= o.Repeat)
                {
                    reason = $"Finished: {done} clicks";
                    return;
                }

                // Next click: the interval, varied a little if asked.
                double wait = interval;

                if (o.JitterPercent > 0)
                {
                    double spread = interval * o.JitterPercent / 100.0;
                    wait = Math.Max(1, interval + (random.NextDouble() * 2 - 1) * spread);
                }

                next += wait;

                // Sleep most of the wait, spin the last bit.
                while (!stopRequested)
                {
                    double left = next - run.Elapsed.TotalMilliseconds;

                    if (left <= 0)
                        break;

                    if (left > 3)
                        Thread.Sleep((int)Math.Min(left - 2, 50));
                    else
                        Thread.SpinWait(200);
                }

                // Fell far behind (the PC was busy): do not fire a burst.
                if (run.Elapsed.TotalMilliseconds - next > 500)
                    next = run.Elapsed.TotalMilliseconds;
            }
        }
        catch (Exception ex)
        {
            reason = "Stopped: " + ex.Message;
        }
        finally
        {
            timeEndPeriod(1);
            Running = false;
            Stopped?.Invoke(reason, Interlocked.Read(ref clicks));
        }
    }

    private static bool InCorner() =>
        GetCursorPos(out POINT p) && p.X <= 0 && p.Y <= 0;

    private static void Click(int button)
    {
        uint down = button switch { 1 => 0x0008u, 2 => 0x0020u, _ => 0x0002u };
        uint up = button switch { 1 => 0x0010u, 2 => 0x0040u, _ => 0x0004u };

        INPUT[] inputs = new INPUT[2];
        inputs[0].type = 0;
        inputs[0].mi.dwFlags = down;
        inputs[1].type = 0;
        inputs[1].mi.dwFlags = up;

        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose() => Stop();

    // ---- native ----

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
