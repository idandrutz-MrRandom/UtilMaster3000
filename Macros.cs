using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace UtilMaster3000;

// ======================================================
// MACROS (like TinyTask)
// Record what you do with the mouse and keyboard, then play it
// back: once, a number of times, or until stopped, at any speed.
// ======================================================

internal static class MacroKind
{
    public const byte Move = 0;
    public const byte Down = 1;     // mouse button down   (A = button 0 left / 1 right / 2 middle)
    public const byte Up = 2;
    public const byte Wheel = 3;    // A = delta
    public const byte KeyDown = 4;  // A = virtual key, B = scan code | (extended ? 0x100 : 0)
    public const byte KeyUp = 5;
}

internal sealed class MacroEvent
{
    public int T { get; set; }       // milliseconds since the start
    public byte K { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int A { get; set; }
    public int B { get; set; }
}

internal sealed class Macro
{
    public string Name { get; set; } = "Macro";
    public List<MacroEvent> Events { get; set; } = new();

    public int DurationMs => Events.Count == 0 ? 0 : Events[^1].T;

    public string Summary =>
        $"{DurationMs / 1000.0:0.0} s, {Events.Count} events" +
        (Events.Any(e => e.K == MacroKind.KeyDown) ? ", keys" : "") +
        (Events.Any(e => e.K == MacroKind.Down) ? ", clicks" : "");

    // ---- storage ----

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000", "macros");

    private static string FileFor(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return Path.Combine(Folder, (safe.Length == 0 ? "macro" : safe) + ".json");
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileFor(Name), JsonSerializer.Serialize(this));
    }

    public void Delete()
    {
        try
        {
            File.Delete(FileFor(Name));
        }
        catch
        {
            // Already gone.
        }
    }

    public static List<Macro> LoadAll()
    {
        List<Macro> list = new();

        if (!Directory.Exists(Folder))
            return list;

        foreach (string file in Directory.GetFiles(Folder, "*.json").OrderBy(f => File.GetLastWriteTime(f)))
        {
            try
            {
                Macro? m = JsonSerializer.Deserialize<Macro>(File.ReadAllText(file));

                if (m != null && m.Events.Count > 0)
                    list.Add(m);
            }
            catch
            {
                // A damaged file is skipped.
            }
        }

        return list;
    }

    public static string NextName()
    {
        HashSet<string> used = LoadAll().Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; ; i++)
        {
            if (!used.Contains("Macro " + i))
                return "Macro " + i;
        }
    }
}

// ------------------------------------------------------
// RECORDER: low-level mouse and keyboard hooks while recording
// ------------------------------------------------------

internal sealed class MacroRecorder : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly HookProc keyProc;
    private readonly HookProc mouseProc;
    private IntPtr keyHook = IntPtr.Zero;
    private IntPtr mouseHook = IntPtr.Zero;

    private readonly Stopwatch clock = new();
    private readonly List<MacroEvent> events = new();
    private readonly HashSet<int> ignoredKeys = new();
    private long lastMoveTick = -100;

    public bool Recording { get; private set; }

    /// <summary>Record the mouse path too (off = only clicks, wheel and keys).</summary>
    public bool RecordMoves { get; set; } = true;

    public int Count => events.Count;

    public MacroRecorder()
    {
        keyProc = KeyCallback;
        mouseProc = MouseCallback;
    }

    /// <summary>Keys that must not end up in the macro (the record / play hotkeys).</summary>
    public void Ignore(IEnumerable<int> keys)
    {
        ignoredKeys.Clear();

        foreach (int k in keys)
            ignoredKeys.Add(k);
    }

    public void Start()
    {
        if (Recording)
            return;

        events.Clear();
        lastMoveTick = -100;
        clock.Restart();

        IntPtr module = GetModuleHandle(null);
        keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, module, 0);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);

        Recording = true;

        // Start from where the mouse is now.
        if (GetCursorPos(out POINT p))
            Add(MacroKind.Move, p.X, p.Y, 0, 0);
    }

    public Macro Stop(string name)
    {
        Recording = false;

        if (keyHook != IntPtr.Zero)
            UnhookWindowsHookEx(keyHook);

        if (mouseHook != IntPtr.Zero)
            UnhookWindowsHookEx(mouseHook);

        keyHook = mouseHook = IntPtr.Zero;
        clock.Stop();

        // Pointer events recorded, then never used until the end are harmless;
        // keep the macro exactly as recorded.
        return new Macro { Name = name, Events = events.ToList() };
    }

    private void Add(byte kind, int x, int y, int a, int b)
    {
        lock (events)
        {
            events.Add(new MacroEvent
            {
                T = (int)clock.ElapsedMilliseconds,
                K = kind,
                X = x,
                Y = y,
                A = a,
                B = b
            });
        }
    }

    private IntPtr KeyCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Recording)
        {
            KBDLLHOOKSTRUCT k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & 0x10) != 0;
            int msg = wParam.ToInt32();

            if (!injected && !ignoredKeys.Contains((int)k.vkCode))
            {
                bool down = msg is 0x100 or 0x104;
                bool up = msg is 0x101 or 0x105;

                if (down || up)
                {
                    GetCursorPos(out POINT p);
                    Add(down ? MacroKind.KeyDown : MacroKind.KeyUp, p.X, p.Y,
                        (int)k.vkCode, (int)k.scanCode | ((k.flags & 1) != 0 ? 0x100 : 0));
                }
            }
        }

        return CallNextHookEx(keyHook, code, wParam, lParam);
    }

    private IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Recording)
        {
            MSLLHOOKSTRUCT m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if ((m.flags & 1) == 0)   // not made by a program (autoclicker, playback)
            {
                switch (wParam.ToInt32())
                {
                    case 0x200:
                        // Plenty of moves per second: keep about one every 8 ms.
                        if (RecordMoves && clock.ElapsedMilliseconds - lastMoveTick >= 8)
                        {
                            lastMoveTick = clock.ElapsedMilliseconds;
                            Add(MacroKind.Move, m.pt.X, m.pt.Y, 0, 0);
                        }

                        break;

                    case 0x201: Add(MacroKind.Down, m.pt.X, m.pt.Y, 0, 0); break;
                    case 0x202: Add(MacroKind.Up, m.pt.X, m.pt.Y, 0, 0); break;
                    case 0x204: Add(MacroKind.Down, m.pt.X, m.pt.Y, 1, 0); break;
                    case 0x205: Add(MacroKind.Up, m.pt.X, m.pt.Y, 1, 0); break;
                    case 0x207: Add(MacroKind.Down, m.pt.X, m.pt.Y, 2, 0); break;
                    case 0x208: Add(MacroKind.Up, m.pt.X, m.pt.Y, 2, 0); break;
                    case 0x20A:
                        Add(MacroKind.Wheel, m.pt.X, m.pt.Y, (short)((m.mouseData >> 16) & 0xFFFF), 0);
                        break;
                }
            }
        }

        return CallNextHookEx(mouseHook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (Recording)
            Stop("");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}

// ------------------------------------------------------
// PLAYER
// ------------------------------------------------------

internal sealed class MacroPlayer
{
    private volatile bool stopRequested;
    private Thread? worker;

    public bool Playing { get; private set; }

    /// <summary>Raised on a background thread when playback ends (with why).</summary>
    public event Action<string>? Finished;

    /// <summary>repeat 0 = until stopped. speed 1.0 = as recorded.</summary>
    public void Play(Macro macro, int repeat, double speed)
    {
        if (Playing || macro.Events.Count == 0)
            return;

        Playing = true;
        stopRequested = false;

        worker = new Thread(() => Run(macro, repeat, Math.Clamp(speed, 0.1, 20)))
        {
            IsBackground = true,
            Name = "Macro player"
        };

        worker.Start();
    }

    public void Stop() => stopRequested = true;

    private void Run(Macro macro, int repeat, double speed)
    {
        string reason = "Finished";
        HashSet<int> keysDown = new();
        HashSet<int> buttonsDown = new();

        timeBeginPeriod(1);

        try
        {
            for (int round = 0; repeat == 0 || round < repeat; round++)
            {
                Stopwatch clock = Stopwatch.StartNew();
                int lastX = int.MinValue, lastY = int.MinValue;

                foreach (MacroEvent e in macro.Events)
                {
                    double due = e.T / speed;

                    while (!stopRequested)
                    {
                        double left = due - clock.Elapsed.TotalMilliseconds;

                        if (left <= 0)
                            break;

                        if (left > 3)
                            Thread.Sleep((int)Math.Min(left - 2, 40));
                        else
                            Thread.SpinWait(200);
                    }

                    if (stopRequested)
                    {
                        reason = "Stopped";
                        return;
                    }

                    // Slammed the mouse into the top-left corner: that is "stop".
                    if (lastX != int.MinValue && lastX > 40 && lastY > 40 &&
                        GetCursorPos(out POINT now) && now.X <= 0 && now.Y <= 0)
                    {
                        reason = "Stopped: mouse in the corner";
                        return;
                    }

                    Execute(e, keysDown, buttonsDown);
                    lastX = e.X;
                    lastY = e.Y;
                }

                // A short breath between repeats.
                if (repeat == 0 || round + 1 < repeat)
                    Thread.Sleep(60);
            }
        }
        catch (Exception ex)
        {
            reason = "Stopped: " + ex.Message;
        }
        finally
        {
            // Never leave a key or button stuck down.
            foreach (int vk in keysDown)
                SendKey(vk, 0, true);

            foreach (int b in buttonsDown)
                SendMouse(ButtonFlags(b).up, 0);

            timeEndPeriod(1);
            Playing = false;
            Finished?.Invoke(reason);
        }
    }

    private static (uint down, uint up) ButtonFlags(int button) => button switch
    {
        1 => (0x0008u, 0x0010u),
        2 => (0x0020u, 0x0040u),
        _ => (0x0002u, 0x0004u)
    };

    private static void Execute(MacroEvent e, HashSet<int> keysDown, HashSet<int> buttonsDown)
    {
        switch (e.K)
        {
            case MacroKind.Move:
                SetCursorPos(e.X, e.Y);
                break;

            case MacroKind.Down:
                SetCursorPos(e.X, e.Y);
                SendMouse(ButtonFlags(e.A).down, 0);
                buttonsDown.Add(e.A);
                break;

            case MacroKind.Up:
                SetCursorPos(e.X, e.Y);
                SendMouse(ButtonFlags(e.A).up, 0);
                buttonsDown.Remove(e.A);
                break;

            case MacroKind.Wheel:
                SendMouse(0x0800, (uint)e.A);
                break;

            case MacroKind.KeyDown:
                SendKey(e.A, e.B, false);
                keysDown.Add(e.A);
                break;

            case MacroKind.KeyUp:
                SendKey(e.A, e.B, true);
                keysDown.Remove(e.A);
                break;
        }
    }

    private static void SendMouse(uint flags, uint data)
    {
        INPUT[] input = new INPUT[1];
        input[0].type = 0;
        input[0].mi.dwFlags = flags;
        input[0].mi.mouseData = data;
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(int vk, int scanAndExtended, bool up)
    {
        KEYINPUT[] input = new KEYINPUT[1];
        input[0].type = 1;
        input[0].ki.wVk = (ushort)vk;
        input[0].ki.wScan = (ushort)(scanAndExtended & 0xFF);
        input[0].ki.dwFlags = (up ? 0x0002u : 0u) | ((scanAndExtended & 0x100) != 0 ? 0x0001u : 0u);
        uint sent = SendInput(1, input, Marshal.SizeOf<KEYINPUT>());
        Trace?.Invoke($"key {(up ? "up" : "down")} vk={vk} sent={sent} err={Marshal.GetLastWin32Error()}");
    }

    /// <summary>Diagnostics (tests).</summary>
    public static Action<string>? Trace { get; set; }

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
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
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

    // Same size as INPUT (40 bytes on 64-bit Windows).
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYINPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        public long padding;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInput(uint count, KEYINPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
