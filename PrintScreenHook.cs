using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// REBINDABLE SHORTCUTS (screenshots, recording, clipboard)
//
// A low-level hook (not RegisterHotKey) so these win over the
// Snipping Tool (Print Screen) and Windows (Windows + V), and so
// a shortcut may be a single key such as Print Screen or F9.
// ======================================================

internal sealed class ShortcutBinding
{
    public uint Mods { get; set; }
    public int Key { get; set; }

    public ShortcutBinding() { }

    public ShortcutBinding(uint mods, int key)
    {
        Mods = mods;
        Key = key;
    }
}

internal static class ShortcutCatalog
{
    // Same values as RegisterHotKey's modifiers.
    public const uint Alt = 1;
    public const uint Ctrl = 2;
    public const uint Shift = 4;
    public const uint Win = 8;

    public sealed record Info(string Id, string Name, uint Mods, int Key);

    private const int PrintScreen = 0x2C;

    public static readonly Info[] All =
    {
        new("shot-area", "Screenshot: choose area", 0, PrintScreen),
        new("shot-full", "Screenshot: full screen", Ctrl, PrintScreen),
        new("shot-window", "Screenshot: active window", Alt, PrintScreen),
        new("rec-area", "Record: choose area", Shift, PrintScreen),
        new("rec-full", "Record: full screen", Ctrl | Shift, PrintScreen),
        new("clipboard", "Clipboard history", Win, 0x56)
    };

    public static ShortcutBinding Get(AppSettings settings, Info info) =>
        settings.Shortcuts.TryGetValue(info.Id, out ShortcutBinding? b)
            ? b
            : new ShortcutBinding(info.Mods, info.Key);

    public static string Describe(ShortcutBinding binding) =>
        Describe(binding.Mods, binding.Key);

    public static string Describe(uint mods, int key)
    {
        if (key == 0)
            return "not set";

        List<string> parts = new();

        if ((mods & Win) != 0) parts.Add("Windows");
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if ((mods & Shift) != 0) parts.Add("Shift");

        parts.Add(KeyName(key));

        return string.Join(" + ", parts);
    }

    private static string KeyName(int key)
    {
        Keys k = (Keys)key;

        return k switch
        {
            Keys.Snapshot => "Print Screen",
            Keys.Return => "Enter",
            Keys.Next => "Page Down",
            Keys.Prior => "Page Up",
            Keys.Scroll => "Scroll Lock",
            Keys.Capital => "Caps Lock",
            Keys.Oemtilde => "`",
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (k - Keys.D0))).ToString(),
            _ => k.ToString()
        };
    }

    /// <summary>Single keys that are safe to bind without a modifier.</summary>
    public static bool AllowedWithoutModifier(int key)
    {
        Keys k = (Keys)key;

        return (k >= Keys.F1 && k <= Keys.F24) ||
               k == Keys.Snapshot || k == Keys.Pause ||
               k == Keys.Scroll || k == Keys.Insert;
    }
}

internal sealed class PrintScreenHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_SNAPSHOT = 0x2C;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const byte VK_UNASSIGNED = 0xE8;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint LLKHF_ALTDOWN = 0x20;

    private readonly HookProc callback;
    private IntPtr hook;

    private readonly Dictionary<string, (uint Mods, int Key)> bindings = new();

    // Keys whose key-down we swallowed (their key-up is swallowed too).
    private readonly HashSet<int> swallowed = new();

    private Action<uint, int>? capture;

    public bool Installed => hook != IntPtr.Zero;

    /// <summary>Asks whether an action is switched on right now.</summary>
    public Func<string, bool>? IsEnabled { get; set; }

    /// <summary>Raised from inside the hook: handlers must return quickly.</summary>
    public event Action<string>? Triggered;

    public PrintScreenHook()
    {
        callback = HookCallback;

        hook = SetWindowsHookEx(
            WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0);
    }

    /// <summary>Binds an action; a key of 0 removes it.</summary>
    public void Set(string id, uint mods, int key)
    {
        if (key == 0)
            bindings.Remove(id);
        else
            bindings[id] = (mods, key);
    }

    /// <summary>
    /// The next key combination pressed is reported (and swallowed) instead
    /// of acting. Also works for combinations Windows keeps for itself.
    /// </summary>
    public void BeginCapture(Action<uint, int> done) => capture = done;

    public void EndCapture() => capture = null;

    public bool Capturing => capture != null;

    /// <summary>While capturing: the modifiers held right now (live).</summary>
    public Action<uint>? CaptureModifiers { get; set; }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                KBDLLHOOKSTRUCT key =
                    Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                int vk = (int)key.vkCode;
                int message = wParam.ToInt32();

                bool down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                bool up = message == WM_KEYUP || message == WM_SYSKEYUP;

                if (capture != null && IsModifierKey(vk) && (down || up))
                {
                    uint bit = vk switch
                    {
                        0x10 or 0xA0 or 0xA1 => ShortcutCatalog.Shift,
                        0x11 or 0xA2 or 0xA3 => ShortcutCatalog.Ctrl,
                        0x12 or 0xA4 or 0xA5 => ShortcutCatalog.Alt,
                        _ => ShortcutCatalog.Win
                    };

                    uint now = CurrentModifiers(key.flags);
                    now = down ? now | bit : now & ~bit;

                    CaptureModifiers?.Invoke(now);
                }

                if (!IsModifierKey(vk))
                {
                    if (down)
                    {
                        uint mods = CurrentModifiers(key.flags);

                        if (capture != null)
                        {
                            Action<uint, int> done = capture;
                            capture = null;

                            swallowed.Add(vk);
                            CancelStartMenuIfNeeded(mods);

                            done(mods, vk);
                            return (IntPtr)1;
                        }

                        string? id = Match(vk, mods);

                        if (id != null)
                        {
                            // Ignore auto-repeat while the key stays down.
                            if (swallowed.Add(vk))
                            {
                                CancelStartMenuIfNeeded(mods);
                                Triggered?.Invoke(id);
                            }

                            return (IntPtr)1;
                        }
                    }
                    else if (up)
                    {
                        if (swallowed.Remove(vk))
                            return (IntPtr)1;

                        // Some keyboards / apps only deliver Print Screen's key-up.
                        if (vk == VK_SNAPSHOT)
                        {
                            string? id = Match(vk, CurrentModifiers(key.flags));

                            if (id != null)
                            {
                                Triggered?.Invoke(id);
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Never break the keyboard because of an error here.
            }
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private string? Match(int vk, uint mods)
    {
        foreach (KeyValuePair<string, (uint Mods, int Key)> b in bindings)
        {
            if (b.Value.Key == vk && b.Value.Mods == mods &&
                (IsEnabled?.Invoke(b.Key) ?? true))
            {
                return b.Key;
            }
        }

        return null;
    }

    private static bool IsModifierKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3
            or 0xA4 or 0xA5 or VK_LWIN or VK_RWIN;

    private static uint CurrentModifiers(uint flags)
    {
        uint mods = 0;

        if (IsDown(VK_MENU) || (flags & LLKHF_ALTDOWN) != 0)
            mods |= ShortcutCatalog.Alt;

        if (IsDown(VK_CONTROL))
            mods |= ShortcutCatalog.Ctrl;

        if (IsDown(VK_SHIFT))
            mods |= ShortcutCatalog.Shift;

        if (IsDown(VK_LWIN) || IsDown(VK_RWIN))
            mods |= ShortcutCatalog.Win;

        return mods;
    }

    // Without a key in between, letting go of Windows opens the Start menu
    // (we swallowed the other key). A harmless unassigned key press cancels that.
    private static void CancelStartMenuIfNeeded(uint mods)
    {
        if ((mods & ShortcutCatalog.Win) == 0)
            return;

        keybd_event(VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static bool IsDown(int vk) =>
        (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        GC.KeepAlive(callback);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
