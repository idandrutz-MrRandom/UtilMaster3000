
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UtilMaster3000;

public sealed class GlobalTypingTranslator : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_BACK = 0x08;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_CAPITAL = 0x14;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private const uint LLKHF_INJECTED = 0x10;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private readonly System.Text.StringBuilder buffer = new();
    private readonly HashSet<int> suppressedKeys = new();

    private readonly HookProc hookCallback;
    private IntPtr hookHandle;

    private int renderedLength;
    private string renderedText = string.Empty;

    private int wordSeed;

    public bool Enabled { get; private set; }
    public bool HookInstalled => hookHandle != IntPtr.Zero;

    /// <summary>What typed letters turn into (Off = no keyboard hook at all).</summary>
    public TypingMode Mode { get; private set; }

    /// <summary>Which look "Weird" mode uses (index into <see cref="TextStyler.StyleNames"/>).</summary>
    public int WeirdStyle { get; set; }

    /// <summary>Short name for pop-ups and the status line.</summary>
    public string ModeLabel => Mode switch
    {
        TypingMode.Weird => "WEIRD TEXT",
        TypingMode.Off => "TYPING OFF",
        _ => "Letter conversion En -> Ru"
    };

    public event Action<bool>? EnabledChanged;
    public event Action<TypingMode>? ModeChanged;

    public GlobalTypingTranslator(TypingMode mode = TypingMode.Russian, int weirdStyle = 0)
    {
        hookCallback = KeyboardCallback;
        Mode = mode;
        WeirdStyle = weirdStyle;

        // "Off" means off: the keyboard hook is not even installed.
        if (mode != TypingMode.Off)
            InstallHook();
    }

    private void InstallHook()
    {
        if (hookHandle != IntPtr.Zero)
            return;

        hookHandle = SetWindowsHookEx(
            WH_KEYBOARD_LL,
            hookCallback,
            GetModuleHandle(null),
            0
        );
    }

    private void RemoveHook()
    {
        if (hookHandle == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(hookHandle);
        hookHandle = IntPtr.Zero;
    }

    public void SetMode(TypingMode mode)
    {
        if (Mode == mode)
            return;

        ClearWord();
        Mode = mode;

        if (mode == TypingMode.Off)
        {
            SetEnabled(false);
            RemoveHook();
        }
        else
        {
            InstallHook();
        }

        ModeChanged?.Invoke(mode);
    }

    public void Toggle()
    {
        SetEnabled(!Enabled);
    }

    public void SetEnabled(bool enabled)
    {
        // Switched off for good: it cannot be turned on until the mode changes.
        if (enabled && Mode == TypingMode.Off)
            return;

        if (Enabled == enabled)
            return;

        ClearWord();

        Enabled = enabled;
        EnabledChanged?.Invoke(Enabled);
    }

    private IntPtr KeyboardCallback(
        int nCode,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);

        try
        {
            int message = wParam.ToInt32();

            bool keyDown =
                message == WM_KEYDOWN ||
                message == WM_SYSKEYDOWN;

            bool keyUp =
                message == WM_KEYUP ||
                message == WM_SYSKEYUP;

            if (!keyDown && !keyUp)
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            KBDLLHOOKSTRUCT key =
                Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            if ((key.flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            int vk = (int)key.vkCode;

            if (keyUp && suppressedKeys.Remove(vk))
                return (IntPtr)1;

            if (!keyDown || !Enabled)
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            // Never translate inside the launcher.
            if (IsOwnWindowForeground())
            {
                ClearWord();
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            // Preserve Ctrl, Alt and Windows shortcuts.
            if (IsModifierPressed())
            {
                ClearWord();
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            if (vk == VK_SHIFT)
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            // Backspace updates the currently visible word.
            if (vk == VK_BACK)
            {
                if (buffer.Length == 0)
                    return CallNextHookEx(hookHandle, nCode, wParam, lParam);

                char removed = buffer[buffer.Length - 1];
                buffer.Length--;

                if (!TryRenderWord())
                {
                    buffer.Append(removed);
                    DisableAfterInjectionFailure();

                    return CallNextHookEx(
                        hookHandle, nCode, wParam, lParam);
                }

                suppressedKeys.Add(vk);
                return (IntPtr)1;
            }

            // Capture English letters.
            if (vk >= 0x41 && vk <= 0x5A)
            {
                bool shift = IsKeyDown(VK_SHIFT);
                bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;

                bool uppercase = shift ^ caps;

                char letter = (char)vk;

                if (!uppercase)
                    letter = char.ToLowerInvariant(letter);

                // A new word gets its own random pattern (stays the same while you type it).
                if (buffer.Length == 0)
                    wordSeed = Random.Shared.Next();

                buffer.Append(letter);

                if (!TryRenderWord())
                {
                    buffer.Length--;
                    DisableAfterInjectionFailure();

                    // Let the original key reach the application.
                    return CallNextHookEx(
                        hookHandle, nCode, wParam, lParam);
                }

                suppressedKeys.Add(vk);
                return (IntPtr)1;
            }

            // The word is already visible. Let delimiters through.
            ClearWord();

            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
        catch
        {
            // Never intentionally swallow a key after an exception.
            DisableAfterInjectionFailure();

            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
    }

    private bool TryRenderWord()
    {
        string previousText = renderedText;

        // The previous text is what is currently visible.
        // We replace it with the new text.
        string converted = Mode == TypingMode.Weird
            ? TextStyler.Apply(buffer.ToString(), WeirdStyle, wordSeed)
            : Transliterator.Convert(buffer.ToString());

        // Only touch the part of the word that actually changed.
        // Typing "k" after "pri" just appends one character;
        // typing "h" after "s" replaces one. Cost no longer grows
        // with word length, and undo history stays small.
        // Compared by whole characters: styled letters are two UTF-16 units
        // but one backspace removes one of them.
        (int backspaces, string toType) = Diff(previousText, converted);

        if (backspaces == 0 && toType.Length == 0)
            return true;

        List<INPUT> inputs = new(backspaces * 2 + toType.Length * 2);

        for (int i = 0; i < backspaces; i++)
        {
            inputs.Add(CreateKeyInput(VK_BACK, 0));
            inputs.Add(CreateKeyInput(VK_BACK, KEYEVENTF_KEYUP));
        }

        foreach (char character in toType)
        {
            inputs.Add(CreateUnicodeInput(character, 0));
            inputs.Add(CreateUnicodeInput(
                character,
                KEYEVENTF_KEYUP
            ));
        }

        INPUT[] array = inputs.ToArray();

        uint sent = SendInput(
            (uint)array.Length,
            array,
            Marshal.SizeOf<INPUT>()
        );

        if (sent == array.Length)
        {
            renderedText = converted;
            renderedLength = converted.Length;
            return true;
        }

        // Repair partially injected input where possible.
        RepairPartialRender(sent, previousText, backspaces, toType);

        return false;
    }

    /// <summary>
    /// What to do to turn the visible text into the new one: how many backspaces,
    /// then what to type. Compared by whole characters (a styled letter is two
    /// UTF-16 units but one backspace removes it).
    /// </summary>
    public static (int Backspaces, string ToType) Diff(string previous, string converted)
    {
        int[] before = CodePoints(previous);
        int[] after = CodePoints(converted);

        int common = 0;
        int maxCommon = Math.Min(before.Length, after.Length);

        while (common < maxCommon && before[common] == after[common])
            common++;

        System.Text.StringBuilder typed = new();

        for (int i = common; i < after.Length; i++)
            typed.Append(char.ConvertFromUtf32(after[i]));

        return (before.Length - common, typed.ToString());
    }

    private static int[] CodePoints(string text)
    {
        List<int> points = new(text.Length);

        foreach (System.Text.Rune rune in text.EnumerateRunes())
            points.Add(rune.Value);

        return points.ToArray();
    }

    private void RepairPartialRender(
        uint sent,
        string previousText,
        int backspaces,
        string typed)
    {
        if (sent == 0)
            return;

        List<INPUT> repair = new();

        int completedPairs = (int)(sent / 2);

        // A half-sent pair leaves its key held down: release it.
        if ((sent & 1) != 0)
        {
            if (completedPairs < backspaces)
            {
                repair.Add(CreateKeyInput(VK_BACK, KEYEVENTF_KEYUP));
            }
            else
            {
                char held = typed[completedPairs - backspaces];
                repair.Add(CreateUnicodeInput(held, KEYEVENTF_KEYUP));
            }
        }

        int erased = Math.Min(completedPairs, backspaces);
        int inserted = Math.Max(0, completedPairs - backspaces);

        // Visible now: previousText minus `erased` trailing chars,
        // plus the first `inserted` chars of `typed`. Undo both.
        for (int i = 0; i < inserted; i++)
        {
            repair.Add(CreateKeyInput(VK_BACK, 0));
            repair.Add(CreateKeyInput(VK_BACK, KEYEVENTF_KEYUP));
        }

        foreach (char character in
                 previousText.Substring(previousText.Length - erased))
        {
            repair.Add(CreateUnicodeInput(character, 0));
            repair.Add(CreateUnicodeInput(
                character,
                KEYEVENTF_KEYUP
            ));
        }

        if (repair.Count > 0)
        {
            INPUT[] repairArray = repair.ToArray();

            SendInput(
                (uint)repairArray.Length,
                repairArray,
                Marshal.SizeOf<INPUT>()
            );
        }
    }

    private void DisableAfterInjectionFailure()
    {
        ClearWord();

        if (!Enabled)
            return;

        Enabled = false;
        EnabledChanged?.Invoke(false);
    }

    private void ClearWord()
    {
        buffer.Clear();
        renderedLength = 0;
        renderedText = string.Empty;
    }

    private bool IsModifierPressed()
    {
        return IsKeyDown(VK_CONTROL) ||
               IsKeyDown(VK_MENU) ||
               IsKeyDown(VK_LWIN) ||
               IsKeyDown(VK_RWIN);
    }

    private static bool IsKeyDown(int key)
    {
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    private bool IsOwnWindowForeground()
    {
        IntPtr window = GetForegroundWindow();

        if (window == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(window, out uint processId);

        return processId == (uint)Environment.ProcessId;
    }

    private static INPUT CreateKeyInput(int key, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)key,
                    wScan = 0,
                    dwFlags = flags,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static INPUT CreateUnicodeInput(char character, uint flags)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = KEYEVENTF_UNICODE | flags,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    public void Dispose()
    {
        ClearWord();

        Enabled = false;

        RemoveHook();

        GC.KeepAlive(hookCallback);
        GC.SuppressFinalize(this);
    }

    private delegate IntPtr HookProc(
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        HookProc callback,
        IntPtr module,
        uint threadId
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int nCode,
        IntPtr wParam,
        IntPtr lParam
    );

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint count,
        INPUT[] inputs,
        int size
    );
}
