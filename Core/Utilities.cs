using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UtilMaster3000;

// ======================================================
// NATIVE METHODS
// ======================================================

internal static class Native
{
    public delegate bool EnumWindowsProc(IntPtr window, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    public static extern void keybd_event(
        byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, UIntPtr wParam, string? lParam,
        uint flags, uint timeout, out UIntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    // Changes with every change of the clipboard's content.
    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr window, string? root, uint flags);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(
        int eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr window, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeRect(
        IntPtr window, int attribute, out RECT rect, int size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(
        uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(
        IntPtr process, int flags, StringBuilder text, ref int size);

    [DllImport("powrprof.dll")]
    public static extern bool SetSuspendState(
        bool hibernate, bool force, bool disableWakeEvents);
}

// ======================================================
// UNIT CONVERTER  ("10 km to mi", "72 f to c", "5 gb in mb")
// ======================================================

internal static class UnitConverter
{
    private static readonly Regex Pattern = new(
        @"^\s*(-?\d+(?:\.\d+)?)\s*([a-zA-Z°/]+)\s+(?:to|in|as)\s+([a-zA-Z°/]+)\s*$",
        RegexOptions.IgnoreCase
    );

    private static readonly Dictionary<string, double>[] Tables =
    {
        // length (metres)
        New(("m", 1), ("km", 1000), ("cm", 0.01), ("mm", 0.001),
            ("mi", 1609.344), ("ft", 0.3048), ("in", 0.0254), ("yd", 0.9144)),

        // mass (kilograms)
        New(("kg", 1), ("g", 0.001), ("mg", 0.000001),
            ("lb", 0.45359237), ("oz", 0.028349523125), ("t", 1000)),

        // volume (litres)
        New(("l", 1), ("ml", 0.001), ("gal", 3.785411784),
            ("qt", 0.946352946), ("floz", 0.0295735295625), ("cup", 0.2365882365)),

        // data (bytes)
        New(("b", 1), ("kb", 1024), ("mb", 1048576),
            ("gb", 1073741824), ("tb", 1099511627776)),

        // speed (m/s)
        New(("ms", 1), ("kmh", 1 / 3.6), ("mph", 0.44704), ("kn", 0.514444)),

        // time (seconds)
        New(("s", 1), ("min", 60), ("h", 3600), ("d", 86400), ("wk", 604800))
    };

    private static Dictionary<string, double> New(
        params (string Unit, double Factor)[] items)
    {
        return items.ToDictionary(
            i => i.Unit,
            i => i.Factor,
            StringComparer.OrdinalIgnoreCase
        );
    }

    public static bool TryConvert(string input, out string result)
    {
        result = "";

        Match m = Pattern.Match(input);

        if (!m.Success)
            return false;

        double value = double.Parse(
            m.Groups[1].Value,
            CultureInfo.InvariantCulture
        );

        string from = m.Groups[2].Value.Trim('°').ToLowerInvariant();
        string to = m.Groups[3].Value.Trim('°').ToLowerInvariant();

        double? converted = null;

        if (IsTemperature(from) && IsTemperature(to))
        {
            converted = FromCelsius(ToCelsius(value, from), to);
        }
        else
        {
            foreach (var table in Tables)
            {
                if (table.TryGetValue(from, out double a) &&
                    table.TryGetValue(to, out double b))
                {
                    converted = value * a / b;
                    break;
                }
            }
        }

        if (converted is null)
            return false;

        result = $"{Format(value)} {from} = {Format(converted.Value)} {to}";

        return true;
    }

    private static string Format(double v) =>
        Math.Round(v, 6).ToString("G10", CultureInfo.InvariantCulture);

    private static bool IsTemperature(string u) =>
        u is "c" or "f" or "k";

    private static double ToCelsius(double v, string u) => u switch
    {
        "f" => (v - 32) * 5 / 9,
        "k" => v - 273.15,
        _ => v
    };

    private static double FromCelsius(double c, string u) => u switch
    {
        "f" => c * 9 / 5 + 32,
        "k" => c + 273.15,
        _ => c
    };
}

// ======================================================
// TEXT TOOLS
// ======================================================

internal static class TextTools
{
    private static string[] Lines(string s) =>
        s.Replace("\r\n", "\n").Split('\n');

    private static string Join(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines);

    public static string Upper(string s) => s.ToUpperInvariant();

    public static string Lower(string s) => s.ToLowerInvariant();

    public static string Title(string s) =>
        CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower());

    public static string Tidy(string s) =>
        Join(Lines(s).Select(l => Regex.Replace(l.Trim(), @"\s+", " ")));

    public static string OneLine(string s) =>
        Regex.Replace(s, @"\s*[\r\n]+\s*", " ").Trim();

    public static string SortLines(string s) =>
        Join(Lines(s).OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase));

    public static string DedupeLines(string s) =>
        Join(Lines(s).Distinct());

    public static string Reverse(string s)
    {
        var elements = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);

        while (e.MoveNext())
            elements.Add((string)e.Current);

        elements.Reverse();
        return string.Concat(elements);
    }

    public static string Base64Encode(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public static string Base64Decode(string s) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim()));

    public static string UrlEncode(string s) => Uri.EscapeDataString(s);

    public static string UrlDecode(string s) => Uri.UnescapeDataString(s);

    public static string JsonPretty(string s)
    {
        using JsonDocument doc = JsonDocument.Parse(s);

        return JsonSerializer.Serialize(
            doc.RootElement,
            new JsonSerializerOptions { WriteIndented = true }
        );
    }

    public static string JsonMinify(string s)
    {
        using JsonDocument doc = JsonDocument.Parse(s);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    public static string Stats(string s)
    {
        int words = s.Split(
            new[] { ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries
        ).Length;

        return $"Characters: {s.Length}\n" +
               $"Words: {words}\n" +
               $"Lines: {Lines(s).Length}";
    }
}

// ======================================================
// APP INDEX  (Start Menu shortcuts + Store apps)
// ======================================================

internal sealed record AppEntry(string Name, string Target, bool IsStoreApp);

internal static class AppIndex
{
    private static volatile AppEntry[] apps = Array.Empty<AppEntry>();

    public static AppEntry[] All => apps;
    public static bool Ready { get; private set; }

    public static void BuildAsync() => Task.Run(Build);

    private static void Build()
    {
        try
        {
            var map = new Dictionary<string, AppEntry>(
                StringComparer.OrdinalIgnoreCase
            );

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs)
            };

            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    continue;

                foreach (string file in Directory.EnumerateFiles(
                             root, "*.lnk", options))
                {
                    string name = Path.GetFileNameWithoutExtension(file);

                    if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                        continue;

                    map.TryAdd(name, new AppEntry(name, file, false));
                }
            }

            // Searchable straight away; Store apps are merged in below.
            apps = map.Values.OrderBy(a => a.Name).ToArray();

            foreach (AppEntry store in LoadStartApps())
                map.TryAdd(store.Name, store);

            apps = map.Values.OrderBy(a => a.Name).ToArray();
        }
        catch
        {
            // Keep whatever was indexed so far.
        }
        finally
        {
            Ready = true;
        }
    }

    private static List<AppEntry> LoadStartApps()
    {
        List<AppEntry> list = new();

        try
        {
            ProcessStartInfo info = new()
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command " +
                    "\"[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                    "Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"",
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(info);

            if (process == null)
                return list;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);

            if (string.IsNullOrWhiteSpace(output))
                return list;

            using JsonDocument doc = JsonDocument.Parse(output);

            IEnumerable<JsonElement> items =
                doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : new List<JsonElement> { doc.RootElement };

            foreach (JsonElement item in items)
            {
                string? name = item.GetProperty("Name").GetString();
                string? id = item.GetProperty("AppID").GetString();

                if (!string.IsNullOrWhiteSpace(name) &&
                    !string.IsNullOrWhiteSpace(id) &&
                    !name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new AppEntry(name, id, true));
                }
            }
        }
        catch
        {
            // PowerShell missing or blocked: Start Menu shortcuts still work.
        }

        return list;
    }

    public static IEnumerable<AppEntry> Search(string query, int max)
    {
        return Matching.Filter(apps, a => a.Name, query)
            .Take(max);
    }

    public static AppEntry? Find(string name) =>
        apps.FirstOrDefault(a =>
            a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void Launch(AppEntry app)
    {
        if (app.IsStoreApp)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{app.Target}",
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = app.Target,
                UseShellExecute = true
            });
        }
    }
}

// ======================================================
// FILE INDEX  (Desktop, Documents, Downloads, Pictures ...)
// ======================================================

internal sealed record FileEntry(string Name, string Path, bool IsFolder);

internal static class FileIndex
{
    // Paths only (folders end with a backslash): about 10 MB at the maximum.
    private const int MaxEntries = 60_000;

    private static volatile string[] items = Array.Empty<string>();

    public static bool Ready { get; private set; }

    public static void BuildAsync() =>
        Task.Factory.StartNew(Build, TaskCreationOptions.LongRunning);

    private static void Build()
    {
        // Indexing is background work: never compete with what you are doing.
        System.Threading.Thread.CurrentThread.Priority =
            System.Threading.ThreadPriority.BelowNormal;

        try
        {
            string profile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                System.IO.Path.Combine(profile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            };

            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 6,
                AttributesToSkip =
                    FileAttributes.Hidden |
                    FileAttributes.System |
                    FileAttributes.ReparsePoint
            };

            List<string> list = new();

            foreach (string root in roots.Distinct(
                         StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    continue;

                foreach (string path in Directory.EnumerateFileSystemEntries(
                             root, "*", options))
                {
                    if (path.Contains("\\node_modules\\") ||
                        path.Contains("\\.git\\"))
                    {
                        continue;
                    }

                    list.Add(Directory.Exists(path) ? path + "\\" : path);

                    if (list.Count >= MaxEntries)
                        break;
                }

                if (list.Count >= MaxEntries)
                    break;
            }

            items = list.ToArray();
        }
        catch
        {
            // Partial or empty index is fine.
        }
        finally
        {
            Ready = true;
        }
    }

    private static string NameOf(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd('\\'));

    public static IEnumerable<FileEntry> Search(string query, int max)
    {
        return Matching.Filter(items, NameOf, query)
            .Take(max)
            .Select(p => new FileEntry(
                NameOf(p), p.TrimEnd('\\'), p.EndsWith('\\')));
    }
}

// ======================================================
// MATCHING  (all words must appear, prefix matches first)
// ======================================================

internal static class Matching
{
    public static IEnumerable<T> Filter<T>(
        IEnumerable<T> source,
        Func<T, string> name,
        string query)
    {
        string[] tokens = query.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return Enumerable.Empty<T>();

        return source
            .Select(item => (item, text: name(item)))
            .Where(x => tokens.All(t =>
                x.text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => Rank(x.text, tokens[0]))
            .ThenBy(x => x.text.Length)
            .Select(x => x.item);
    }

    private static int Rank(string text, string token)
    {
        int i = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);

        if (i == 0)
            return 0;

        return i > 0 && !char.IsLetterOrDigit(text[i - 1]) ? 1 : 2;
    }
}

// ======================================================
// OPEN WINDOWS
// ======================================================

internal sealed record WindowInfo(IntPtr Handle, string Title);

internal static class WindowList
{
    private static readonly HashSet<string> ShellClasses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow"
    };

    public static bool IsShellWindow(IntPtr window)
    {
        StringBuilder sb = new(128);
        Native.GetClassName(window, sb, sb.Capacity);
        return ShellClasses.Contains(sb.ToString());
    }

    public static bool IsOwnWindow(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public static List<WindowInfo> GetOpenWindows()
    {
        List<WindowInfo> list = new();

        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window))
                return true;

            int length = Native.GetWindowTextLength(window);

            if (length == 0 || IsOwnWindow(window))
                return true;

            int style = Native.GetWindowLong(window, -20);
            bool toolWindow = (style & 0x80) != 0;
            bool appWindow = (style & 0x40000) != 0;

            if (toolWindow && !appWindow)
                return true;

            // Skip hidden UWP shells ("cloaked" windows).
            if (Native.DwmGetWindowAttribute(window, 14, out int cloaked, 4) == 0 &&
                cloaked != 0)
            {
                return true;
            }

            StringBuilder title = new(length + 1);
            Native.GetWindowText(window, title, title.Capacity);

            string text = title.ToString();

            if (text == "Program Manager")
                return true;

            list.Add(new WindowInfo(window, text));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>Path of the exe that owns a window (null if not readable).</summary>
    public static string? GetProcessPath(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);

        // PROCESS_QUERY_LIMITED_INFORMATION
        IntPtr process = Native.OpenProcess(0x1000, false, pid);

        if (process == IntPtr.Zero)
            return null;

        try
        {
            StringBuilder sb = new(1024);
            int size = sb.Capacity;

            return Native.QueryFullProcessImageName(process, 0, sb, ref size)
                ? sb.ToString()
                : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>Visible bounds of a window (without the invisible resize border).</summary>
    public static Rectangle GetBounds(IntPtr window)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS
        if (Native.DwmGetWindowAttributeRect(
                window, 9, out Native.RECT frame, Marshal.SizeOf<Native.RECT>()) == 0)
        {
            return Rectangle.FromLTRB(
                frame.Left, frame.Top, frame.Right, frame.Bottom);
        }

        Native.GetWindowRect(window, out Native.RECT rect);

        return Rectangle.FromLTRB(
            rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    public static void Activate(IntPtr window)
    {
        if (Native.IsIconic(window))
            Native.ShowWindow(window, 9); // SW_RESTORE

        Native.SetForegroundWindow(window);
    }
}

// ======================================================
// SYSTEM TOOLS
// ======================================================

internal static class SystemTools
{
    private const string Personalize =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private const string Advanced =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    // ---- registry helpers ----

    private static int GetDword(string path, string name, int fallback)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private static void SetDword(string path, string name, int value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void Broadcast(string? area)
    {
        Native.SendMessageTimeout(
            (IntPtr)0xFFFF, 0x001A, UIntPtr.Zero, area,
            0x0002, 1000, out _);
    }

    // ---- theme / explorer toggles ----

    public static bool IsDarkMode() =>
        GetDword(Personalize, "AppsUseLightTheme", 1) == 0;

    public static void SetDarkMode(bool dark)
    {
        int value = dark ? 0 : 1;

        SetDword(Personalize, "AppsUseLightTheme", value);
        SetDword(Personalize, "SystemUsesLightTheme", value);

        Broadcast("ImmersiveColorSet");
    }

    public static bool ShowExtensions() =>
        GetDword(Advanced, "HideFileExt", 1) == 0;

    public static void SetShowExtensions(bool show)
    {
        SetDword(Advanced, "HideFileExt", show ? 0 : 1);
        RefreshExplorer();
    }

    public static bool ShowHiddenFiles() =>
        GetDword(Advanced, "Hidden", 2) == 1;

    public static void SetShowHiddenFiles(bool show)
    {
        SetDword(Advanced, "Hidden", show ? 1 : 2);
        RefreshExplorer();
    }

    private static void RefreshExplorer()
    {
        Broadcast(null);
        Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- power / session ----

    public static void EmptyRecycleBin()
    {
        // NOCONFIRMATION | NOPROGRESSUI | NOSOUND
        Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);
    }

    public static void SetKeepAwake(bool on)
    {
        Native.SetThreadExecutionState(
            on
                ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED
                : ES_CONTINUOUS
        );
    }

    // ---- windows ----

    /// <summary>Toggles always-on-top. Returns the new pinned state.</summary>
    public static bool? TogglePin(IntPtr window)
    {
        if (window == IntPtr.Zero || !Native.IsWindow(window))
            return null;

        bool isTopmost = (Native.GetWindowLong(window, -20) & 0x8) != 0;

        IntPtr after = isTopmost ? (IntPtr)(-2) : (IntPtr)(-1);

        // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        bool ok = Native.SetWindowPos(
            window, after, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

        return ok ? !isTopmost : null;
    }

    public static string WindowTitle(IntPtr window)
    {
        StringBuilder sb = new(256);
        Native.GetWindowText(window, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void SendPaste()
    {
        const uint KEYUP = 0x0002;

        Native.keybd_event(0x11, 0, 0, UIntPtr.Zero);      // Ctrl down
        Native.keybd_event(0x56, 0, 0, UIntPtr.Zero);      // V down
        Native.keybd_event(0x56, 0, KEYUP, UIntPtr.Zero);  // V up
        Native.keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);  // Ctrl up
    }

    // ---- capture ----

    public static Bitmap CaptureScreen()
    {
        Rectangle bounds = SystemInformation.VirtualScreen;

        Bitmap bitmap = new(bounds.Width, bounds.Height);

        using Graphics g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        return bitmap;
    }

    /// <summary>Copies a rectangle of the screen (virtual-screen coordinates).</summary>
    public static Bitmap CaptureRegion(Rectangle area)
    {
        Bitmap bitmap = new(area.Width, area.Height);

        using Graphics g = Graphics.FromImage(bitmap);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size);

        return bitmap;
    }

    public static Color PixelAt(Point point)
    {
        using Bitmap bitmap = new(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);

        g.CopyFromScreen(point, Point.Empty, new Size(1, 1));

        return bitmap.GetPixel(0, 0);
    }

    // ---- generators ----

    public static string RandomPassword(int length = 20)
    {
        const string chars =
            "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ" +
            "23456789!@#$%^&*-_=+?";

        StringBuilder sb = new(length);

        for (int i = 0; i < length; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);

        return sb.ToString();
    }

    // ---- info ----

    public static string SystemInfo()
    {
        StringBuilder sb = new();

        sb.AppendLine($"Machine: {Environment.MachineName}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"CPU cores: {Environment.ProcessorCount}");

        double gb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes /
                    1024.0 / 1024.0 / 1024.0;

        sb.AppendLine($"Memory: {gb:0.0} GB");

        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        sb.AppendLine(
            $"Uptime: {(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m");

        try
        {
            foreach (NetworkInterface nic in
                     NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation address in
                         nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily ==
                        AddressFamily.InterNetwork)
                    {
                        sb.AppendLine($"IP ({nic.Name}): {address.Address}");
                    }
                }
            }
        }
        catch
        {
            // Network info is optional.
        }

        return sb.ToString().TrimEnd();
    }
}

// ======================================================
// TYPING INTO OTHER WINDOWS  (password "auto-type")
// ======================================================

internal static class AutoType
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;

    /// <summary>Types text; '\t' presses Tab and '\n' presses Enter.</summary>
    public static void Type(string text)
    {
        List<INPUT> inputs = new(text.Length * 2);

        foreach (char c in text)
        {
            if (c == '\t')
            {
                inputs.Add(Key(VK_TAB, 0, 0));
                inputs.Add(Key(VK_TAB, 0, KEYEVENTF_KEYUP));
            }
            else if (c == '\n')
            {
                inputs.Add(Key(VK_RETURN, 0, 0));
                inputs.Add(Key(VK_RETURN, 0, KEYEVENTF_KEYUP));
            }
            else if (c != '\r')
            {
                inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
                inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
        }

        // Small batches: some apps drop characters from one huge burst.
        const int batch = 40;

        for (int i = 0; i < inputs.Count; i += batch)
        {
            INPUT[] chunk = inputs.Skip(i).Take(batch).ToArray();
            SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<INPUT>());
            System.Threading.Thread.Sleep(8);
        }
    }

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
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
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

// ======================================================
// JOB OBJECT
// Child processes (ffmpeg) added here are killed by Windows
// if this app dies, so a crash can never leave a recorder
// running forever in the background.
// ======================================================

internal static class ChildJob
{
    private static IntPtr job;

    public static void Add(Process process)
    {
        try
        {
            if (job == IntPtr.Zero)
            {
                job = CreateJobObject(IntPtr.Zero, null);

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
                info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE

                SetInformationJobObject(
                    job, 9, ref info,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }

            AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // Best effort.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}

// ======================================================
// MEMORY TRIMMING
// Gives freed memory back to Windows when the app is idle, so it
// sits at a small working set in the tray.
// ======================================================

internal static class MemoryTrimmer
{
    private static System.Threading.Timer? pending;

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static void Trim()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect();

            using Process self = Process.GetCurrentProcess();
            EmptyWorkingSet(self.Handle);
        }
        catch
        {
            // Purely an optimisation.
        }
    }

    /// <summary>Trims a few seconds from now (after windows have closed).</summary>
    public static void TrimSoon(int milliseconds = 3000)
    {
        pending?.Dispose();
        pending = new System.Threading.Timer(_ => Trim(), null, milliseconds, Timeout.Infinite);
    }
}


// ======================================================
// THEMED TITLE BAR
// Makes the Windows title bar match the app (dark, same colour as the
// background) instead of the default white bar.
// ======================================================

internal static class WindowStyle
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    public static void ApplyTitleBar(Form form)
    {
        try
        {
            bool dark = (0.299 * Theme.Background.R +
                         0.587 * Theme.Background.G +
                         0.114 * Theme.Background.B) < 128;

            int mode = dark ? 1 : 0;

            // Dark / light caption buttons (Windows 10 and 11).
            DwmSetWindowAttribute(form.Handle, 20, ref mode, 4);
            DwmSetWindowAttribute(form.Handle, 19, ref mode, 4);

            // Windows 11: caption and border in the app's own colours.
            int caption = ColorRef(Theme.Background);
            int text = ColorRef(Theme.Strong);

            DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);
            DwmSetWindowAttribute(form.Handle, 34, ref caption, 4);
            DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
        }
        catch
        {
            // Older Windows: keep the default title bar.
        }
    }
}
