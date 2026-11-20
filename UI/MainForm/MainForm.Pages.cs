using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UtilMaster3000;

public sealed partial class MainForm
{
    private static readonly (string Name, string Url)[] SearchEngines =
    {
        ("Google", "https://www.google.com/search?q="),
        ("DuckDuckGo", "https://duckduckgo.com/?q="),
        ("Bing", "https://www.bing.com/search?q=")
    };

    private static readonly int[] HoldChoices = { 0, 300, 450, 700 };

    // Anything that looks like a website address (not a file name).
    private static readonly Regex UrlLike = new(
        @"^(https?://\S+|ftp://\S+|www\.\S+" +
        @"|localhost(:\d+)?([/?#]\S*)?" +
        @"|\d{1,3}(\.\d{1,3}){3}(:\d+)?([/?#]\S*)?" +
        @"|[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)*\.[a-z]{2,24}(:\d+)?([/?#]\S*)?)$",
        RegexOptions.IgnoreCase);

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "csv", "rtf", "md",
        "png", "jpg", "jpeg", "gif", "bmp", "svg", "ico", "webp",
        "mp3", "wav", "mp4", "mkv", "avi", "mov", "zip", "rar", "7z", "iso",
        "exe", "msi", "dll", "bat", "cmd", "ps1", "sh", "lnk",
        "cs", "py", "js", "ts", "json", "xml", "yml", "yaml", "ini", "log", "cfg",
        "c", "h", "cpp", "java", "kt", "go", "rs", "sln", "csproj", "html", "css", "sql"
    };

    private static bool LooksLikeUrl(string text)
    {
        if (text.Contains(' ') || !UrlLike.IsMatch(text))
            return false;

        // "report.pdf" or "main.cs" are files, not websites.
        if (!text.Contains("://") &&
            !text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains('/'))
        {
            string ext = Path.GetExtension(text).TrimStart('.');

            if (FileExtensions.Contains(ext))
                return false;
        }

        return true;
    }

    private static string ToUrl(string text)
    {
        if (text.Contains("://"))
            return text;

        bool local = text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                     Regex.IsMatch(text, @"^\d{1,3}(\.\d{1,3}){3}");

        return (local ? "http://" : "https://") + text;
    }

    private static bool IsYouTubeWord(string word)
    {
        word = word.ToLowerInvariant();

        return word is "yt" or "ytube" ||
               (word.Length >= 4 && "youtube".StartsWith(word));
    }

    private readonly List<ClipItem> clipboardHistory = new();
    private long ignoreClipboardUntil;
    private bool clipboardPaused;

    private bool keepAwake;

    private System.Windows.Forms.Timer? countdown;
    private DateTime countdownEnd;

    // --------------------------------------------------
    // ENTRY HELPERS
    // --------------------------------------------------

    private static MenuEntry Act(string icon, string label, Action run) =>
        new(0, icon, label, run: run);

    private static MenuEntry ActIcon(
        string icon, string label, string? iconPath, Action run) =>
        new(0, icon, label, run: run, iconPath: iconPath);

    private static MenuEntry Info(string text) =>
        new(-2, "", text);

    private static MenuEntry BackEntry() =>
        new(-1, "", "Back", command: "back");

    private static List<MenuEntry> Renumber(IEnumerable<MenuEntry> entries)
    {
        int i = 0;

        return entries
            .Select(e => e.Index < 0 ? e : e with { Index = i++ })
            .ToList();
    }

    private static string Trunc(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private void Toast(string text, Color? dot = null) =>
        notification.ShowMessage(text, dot ?? AccentColor);

    private void After(int milliseconds, Action action)
    {
        System.Windows.Forms.Timer timer = new() { Interval = milliseconds };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            action();
        };

        timer.Start();
    }

    private void Launch(Action action, bool hide = true)
    {
        try
        {
            action();

            if (hide)
                HideToTray();
        }
        catch (Exception ex)
        {
            Toast("Failed: " + Trunc(ex.Message, 60), OffColor);
        }
    }

    private void OpenTarget(string file, string? args = null) =>
        Launch(() => Process.Start(new ProcessStartInfo(file, args ?? "")
        {
            UseShellExecute = true
        }));

    private string? GetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private bool SetClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true, 5, 100);
            return true;
        }
        catch (ExternalException)
        {
            Toast("Clipboard is busy, try again", OffColor);
            return false;
        }
    }

    // --------------------------------------------------
    // PAGES
    // --------------------------------------------------

    private void BuildPages()
    {
        pages = new Dictionary<string, List<MenuEntry>>
        {
            ["Learn"] = Renumber(new[]
            {
                Act("", "How it works", () => Say("How it works",
                    "The translator converts Latin characters into Cyrillic as you type, in any application.")),

                Act("", "Examples", () => Say("Examples",
                    "privet -> привет\n\nshchuka -> щука\n\nzhuk -> жук\n\nyu -> ю")),

                Act("", "Using the translator", () => Say("Using the translator",
                    $"Tap {FormatHotkey(toggleKey)} to turn live transliteration on or off.\n\n" +
                    $"Hold it ({HoldLabel()}) to open this menu without toggling.\n\n" +
                    $"{FormatHotkey(launcherKey)} also opens the menu.")),

                Act("", "Search tips", () => Say("Search tips",
                    "On the home screen just start typing:\n\n" +
                    "firefox        launch an app\n" +
                    "2+2*3, 5^2     calculator (Enter copies)\n" +
                    "10 km to mi    unit conversion\n" +
                    "72 f to c      temperature\n" +
                    "> ipconfig     run a command\n" +
                    "github.com     open a website\n" +
                    "anything else  windows, files, web search")),

                Act("", "Alphabet", () => Say("Alphabet",
                    Transliterator.DescribeAlphabet())),

                BackEntry()
            }),

            ["Style"] = BuildStylePage(),
            ["StyleColours"] = BuildColoursPage(),
            ["StyleLayout"] = BuildLayoutPage(),
            ["StyleText"] = BuildFontPage(),
            ["StyleBackground"] = BuildBackgroundPage(),
            ["SkinAssets"] = BuildSkinAssetsPage(),

            ["Setup"] = Renumber(new[]
            {
                new MenuEntry(0, "", "Shortcuts...", targetPage: "Shortcuts"),
                new MenuEntry(0, "", $"Hold to open menu: {HoldLabel()}", command: "hold"),
                new MenuEntry(0, "", $"Live conversion: {OnOff(translator.Enabled)}", command: "toggle"),
                new MenuEntry(0, "", $"Hide on focus loss: {OnOff(settings.HideOnFocusLoss)}", command: "focus-hide"),
                new MenuEntry(0, "\uE700", $"Slide-out menus on hover: {OnOff(settings.HoverSubmenus)}",
                    run: () =>
                    {
                        settings.HoverSubmenus = !settings.HoverSubmenus;
                        SaveSettings();
                        CloseFlyout();
                        RefreshPage();
                    },
                    expandText: $"Slide-out menus on hover: {OnOff(settings.HoverSubmenus)}" + NL +
                                "Rest the mouse on a row that opens a menu and that menu slides out beside the launcher." + NL +
                                "Click what you want straight away. Turn this off to open menus by clicking only."),
                new MenuEntry(0, "", $"Start with Windows: {OnOff(StartupManager.IsEnabled())}", command: "startup"),
                new MenuEntry(0, "", $"Web search: {SearchEngines[EngineIndex()].Name}", command: "engine"),
                new MenuEntry(0, "", "Reset settings", command: "reset"),
                BackEntry()
            }),

            ["About"] = Renumber(new[]
            {
                Act("", "UtilMaster3000", () => Say("UtilMaster3000",
                    "A lightweight offline launcher with live Latin-to-Russian transliteration and a pile of everyday utilities.")),

                Act("", "Version", () => Say("Version",
                    $"Version {Application.ProductVersion.Split('+')[0]}\n\nThis is a local build; there is no online update source configured.")),

                Act("", "Build instructions", () => Say("Build instructions",
                    "Open the project folder in VS Code and run:\n\ndotnet build\n\ndotnet run")),

                new MenuEntry(0, "", "Open settings folder", command: "open-settings"),
                BackEntry()
            }),

            ["System"] = Renumber(new[]
            {
                new MenuEntry(0, "", "Hide to tray", command: "hide"),

                Act("", "Lock", () => Launch(() => Native.LockWorkStation())),

                Act("", "Sleep", () => Launch(() =>
                    Native.SetSuspendState(false, false, false))),

                Act("", "Sign out", () =>
                    ConfirmPower("Sign out", "Sign out of Windows?", "/l")),

                Act("", "Restart", () =>
                    ConfirmPower("Restart", "Restart Windows now?", "/r /t 0")),

                Act("", "Shut down :(", () =>
                    ConfirmPower("Shut down :(", "Are you sure you want to shut down Windows?", "/s /t 0")),

                new MenuEntry(0, "", "Close app", command: "exit"),
                BackEntry()
            }),

            ["Text"] = BuildTextPage(),
            ["Capture"] = BuildCapturePage(),
            ["Tools"] = BuildToolsPage(),
            ["Windows"] = BuildWindowsPage()
        };
    }

    private static void Say(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private int EngineIndex() =>
        Math.Clamp(settings.SearchEngine, 0, SearchEngines.Length - 1);

    private string HoldLabel() =>
        settings.HoldToOpenMs <= 0 ? "Off" : $"{settings.HoldToOpenMs} ms";

    // ---- Text page: works on the clipboard ----

    private MenuEntry TextAction(
        string icon, string label, Func<string, string> transform) =>
        Act(icon, label, () => OpenTextTool(label, transform));

    // The tool you picked on the Text page, and the text it works on.
    private string textToolName = "";
    private Func<string, string> textToolFunc = s => s;
    private string? textToolClipboard;

    private void OpenTextTool(string name, Func<string, string> transform)
    {
        textToolName = name;
        textToolFunc = transform;

        // If the clipboard has text it is ready to use: Enter converts it.
        textToolClipboard = GetClipboardText();

        if (string.IsNullOrEmpty(textToolClipboard))
            textToolClipboard = null;

        ShowPage("TextTool");
    }

    private List<MenuEntry> TextToolPage(string query)
    {
        List<MenuEntry> list = new();

        // What you typed wins; otherwise the clipboard text.
        string input = query.Length > 0 ? searchBox.Text : textToolClipboard ?? "";

        if (input.Length == 0)
        {
            list.Add(Info($"{textToolName}: type or paste text above"));
        }
        else
        {
            string? result = null;

            try
            {
                result = textToolFunc(input);
            }
            catch
            {
                // Handled below.
            }

            if (result == null)
            {
                list.Add(Info("Couldn't convert that text"));
            }
            else
            {
                string captured = result;
                string preview = Trunc(Regex.Replace(result.Trim(), @"\s+", " "), 160);

                // Enter copies the result; hovering unfolds all of it.
                list.Add(new MenuEntry(
                    0, "\uE8C8", preview.Length > 0 ? preview : "(empty result)",
                    run: () =>
                    {
                        if (SetClipboard(captured))
                            Toast($"{textToolName}: copied ({captured.Length} chars)", OnColor);
                    },
                    expandText: Trunc(result, 900)));

                list.Add(Act("\uE77F", "Paste the result into the window you came from", () =>
                {
                    if (!SetClipboard(captured))
                        return;

                    HideToTray();
                    PasteIntoPrevious();
                }));
            }
        }

        string? clip = GetClipboardText();

        if (!string.IsNullOrEmpty(clip))
        {
            list.Add(Act("\uE8C8",
                "Use the clipboard text: " + Trunc(Regex.Replace(clip.Trim(), @"\s+", " "), 60),
                () =>
                {
                    textToolClipboard = clip;
                    searchBox.Clear();
                    RefreshPage();
                }));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    // ---- Calculator page: type a calculation, Enter copies the answer ----

    private const string CalcHelp =
        "5squared   6cubed   2^10   17%5   5!" + NL +
        "3root(27)   root(27,3)   sqrt(2)   √9" + NL +
        "50% of 80   100+10%   20 percent of 150" + NL +
        "sin(30deg)   cos(pi)   ln(e)   log(1000)   log(8,2)" + NL +
        "2pi   3(4+5)   2 to the power of 8" + NL +
        "5 plus 3 times 2   10 divided by 4" + NL +
        "sum(1,2,3)   avg(4,8)   gcd(12,18)   10 choose 3" + NL +
        "x=5 then x*2   ans = the last answer" + NL +
        "0xFF   0b1010   1e3";

    private const string NL = "\n";

    private List<MenuEntry> CalcPage(string query)
    {
        List<MenuEntry> list = new();
        Calc.Degrees = settings.CalcDegrees;

        string input = searchBox.Text.Trim();

        if (input.Length == 0)
        {
            list.Add(Info("Type a calculation above, for example 5squared or 3root(27)"));

            if (Calc.History.Count > 0)
                list.Add(Info("Recent"));

            foreach ((string expression, string result) in Calc.History.Take(8))
            {
                string captured = result;

                list.Add(new MenuEntry(
                    0, "", $"{expression} = {result}",
                    run: () => CopyValue(captured),
                    expandText: expression + NL + "= " + result));
            }
        }
        else
        {
            double value = Calc.Evaluate(input, out string? error, out string? assigned);

            if (error != null)
            {
                list.Add(Info(error));
            }
            else
            {
                string text = Calc.Format(value);
                string shown = assigned != null ? $"{assigned} = {text}" : "= " + text;

                list.Add(new MenuEntry(
                    0, "", shown,
                    run: () =>
                    {
                        Calc.Remember(input, value);
                        CopyValue(text, "Copied: " + text);
                    },
                    expandText: input + NL + "= " + text));

                foreach (string extra in Calc.Extras(value))
                {
                    string copy = extra[(extra.IndexOf(": ", StringComparison.Ordinal) + 2)..];

                    list.Add(Act("", extra, () =>
                    {
                        Calc.Remember(input, value);
                        CopyValue(copy);
                    }));
                }

                list.Add(Act("", "Paste the answer into the window you came from", () =>
                {
                    Calc.Remember(input, value);

                    if (!SetClipboard(text))
                        return;

                    HideToTray();
                    PasteIntoPrevious();
                }));
            }
        }

        list.Add(Act("",
            $"Trig angles: {(settings.CalcDegrees ? "degrees" : "radians")}", () =>
            {
                settings.CalcDegrees = !settings.CalcDegrees;
                Calc.Degrees = settings.CalcDegrees;
                SaveSettings();
                RefreshPage();
            }));

        string vars = Calc.VariablesText();

        if (vars.Length > 0)
            list.Add(Info("Variables:  " + vars));

        if (Calc.History.Count > 0 && input.Length == 0)
            list.Add(Act("", "Clear history", () =>
            {
                Calc.ClearHistory();
                RefreshPage();
            }));

        list.Add(Act("", "What can I type?", () => Say("Calculator", CalcHelp)));
        list.Add(BackEntry());

        return Renumber(list);
    }

    private List<MenuEntry> BuildTextPage() => Renumber(new[]
    {
        TextAction("", "Latin → Russian", Transliterator.Convert),
        TextAction("", "Russian → Latin", Transliterator.ToLatin),
        TextAction("", "UPPERCASE", TextTools.Upper),
        TextAction("", "lowercase", TextTools.Lower),
        TextAction("", "Title Case", TextTools.Title),
        TextAction("", "Plain text (strip formatting)", s => s),
        TextAction("", "Tidy whitespace", TextTools.Tidy),
        TextAction("", "Remove line breaks", TextTools.OneLine),
        TextAction("", "Sort lines", TextTools.SortLines),
        TextAction("", "Remove duplicate lines", TextTools.DedupeLines),
        TextAction("", "Reverse text", TextTools.Reverse),
        TextAction("", "Base64 encode", TextTools.Base64Encode),
        TextAction("", "Base64 decode", TextTools.Base64Decode),
        TextAction("", "URL encode", TextTools.UrlEncode),
        TextAction("", "URL decode", TextTools.UrlDecode),
        TextAction("", "JSON prettify", TextTools.JsonPretty),
        TextAction("", "JSON minify", TextTools.JsonMinify),

        TextAction("\uE8C8", "Count words / characters", TextTools.Stats),

        BackEntry()
    });

    // ---- Tools page ----

    private List<MenuEntry> BuildToolsPage()
    {
        List<MenuEntry> list = new()
        {
            Act("\uE714", "UtilTube  (YouTube, offline)", () => OpenTube(null)),

            new MenuEntry(0, "\uE896", "Download by link", targetPage: "Grab"),
            new MenuEntry(0, "\uE8EF", "Calculator", targetPage: "Calc"),
            new MenuEntry(0, "\uE962", "Autoclicker", targetPage: "Auto"),
            new MenuEntry(0, "", "Macros", targetPage: "Macros"),

            Act("", "Pin last window on top", PinLastWindow),

            Act("", $"Keep PC awake: {OnOff(keepAwake)}", () =>
            {
                keepAwake = !keepAwake;
                SystemTools.SetKeepAwake(keepAwake);
                Toast(keepAwake ? "PC will stay awake" : "Sleep allowed again",
                    keepAwake ? OnColor : OffColor);
                RefreshPage();
            }),

            Act("", "Pick colour (click to pick)", () =>
            {
                HideToTray();
                After(350, PickColorOverlay);
            }),

            Act("", "Timer: 5 minutes", () => StartCountdown(5)),
            Act("", "Timer: 10 minutes", () => StartCountdown(10)),
            Act("", "Timer: 25 minutes", () => StartCountdown(25))
        };

        if (countdown != null)
        {
            list.Add(Act("",
                $"Cancel timer ({Remaining()} left)", CancelCountdown));
        }

        list.AddRange(new[]
        {
            Act("", "New GUID", () => CopyValue(Guid.NewGuid().ToString())),

            Act("", "Random password (20)", () =>
                CopyValue(SystemTools.RandomPassword(20), "Password copied")),

            Act("", "Timestamp (date + time)", () =>
                CopyValue(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))),

            Act("", "Unix time", () =>
                CopyValue(DateTimeOffset.Now.ToUnixTimeSeconds().ToString())),

            Act("", "Quick note", OpenQuickNote),

            Act("", "System info", () =>
            {
                string info = SystemTools.SystemInfo();
                SetClipboard(info);
                Say("System info (copied)", info);
            }),

            BackEntry()
        });

        return Renumber(list);
    }

    // ---- Windows page ----

    private List<MenuEntry> BuildWindowsPage() => Renumber(new[]
    {
        Act("", "Settings", () => OpenTarget("ms-settings:")),
        Act("", "Task Manager", () => OpenTarget("taskmgr.exe")),
        Act("", "Control Panel", () => OpenTarget("control.exe")),
        Act("", "Device Manager", () => OpenTarget("devmgmt.msc")),
        Act("", "Services", () => OpenTarget("services.msc")),
        Act("", "Disk Management", () => OpenTarget("diskmgmt.msc")),
        Act("", "Environment variables", () =>
            OpenTarget("rundll32.exe", "sysdm.cpl,EditEnvironmentVariables")),

        Act("", "Terminal", () => Launch(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
            }
            catch
            {
                Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true });
            }
        })),

        Act("", "Calculator", () => OpenTarget("calc.exe")),
        Act("", "Notepad", () => OpenTarget("notepad.exe")),
        Act("", "Snipping tool", () => OpenTarget("ms-screenclip:")),

        Act("", $"Dark mode: {OnOff(SystemTools.IsDarkMode())}", () =>
        {
            Safe(() => SystemTools.SetDarkMode(!SystemTools.IsDarkMode()));
            RefreshPage();
        }),

        Act("", $"Show file extensions: {OnOff(SystemTools.ShowExtensions())}", () =>
        {
            Safe(() => SystemTools.SetShowExtensions(!SystemTools.ShowExtensions()));
            Toast("Press F5 in open Explorer windows");
            RefreshPage();
        }),

        Act("", $"Show hidden files: {OnOff(SystemTools.ShowHiddenFiles())}", () =>
        {
            Safe(() => SystemTools.SetShowHiddenFiles(!SystemTools.ShowHiddenFiles()));
            Toast("Press F5 in open Explorer windows");
            RefreshPage();
        }),

        Act("", "Empty Recycle Bin", () =>
        {
            DialogResult answer = MessageBox.Show(
                "Permanently delete everything in the Recycle Bin?\n\nThis cannot be undone.",
                "Empty Recycle Bin",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (answer != DialogResult.Yes)
                return;

            Safe(SystemTools.EmptyRecycleBin);
            Toast("Recycle Bin emptied", OnColor);
        }),

        BackEntry()
    });

    // --------------------------------------------------
    // UTILITY ACTIONS
    // --------------------------------------------------

    private void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Toast("Failed: " + Trunc(ex.Message, 60), OffColor);
        }
    }

    private void CopyValue(string value, string? message = null)
    {
        if (!SetClipboard(value))
            return;

        Toast(message ?? "Copied: " + Trunc(value, 40), OnColor);
    }

    private void PinLastWindow()
    {
        bool? pinned = SystemTools.TogglePin(lastExternalWindow);

        if (pinned == null)
        {
            Toast("No window to pin", OffColor);
            return;
        }

        string title = Trunc(SystemTools.WindowTitle(lastExternalWindow), 30);

        Toast(
            (pinned.Value ? "Pinned: " : "Unpinned: ") + title,
            pinned.Value ? OnColor : OffColor
        );
    }

    private void OpenQuickNote()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UtilMaster3000"
        );

        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, "notes.txt");

        if (!File.Exists(path))
            File.WriteAllText(path, "");

        OpenTarget(path);
    }

    private void StartCountdown(int minutes)
    {
        countdown?.Stop();
        countdown?.Dispose();

        countdownEnd = DateTime.Now.AddMinutes(minutes);

        countdown = new System.Windows.Forms.Timer { Interval = 1000 };

        countdown.Tick += (_, _) =>
        {
            if (DateTime.Now < countdownEnd)
                return;

            countdown?.Stop();
            countdown?.Dispose();
            countdown = null;

            Toast($"Timer finished ({minutes} min)", OnColor);
            System.Media.SystemSounds.Exclamation.Play();

            if (currentPage == "Tools")
                RefreshPage();
        };

        countdown.Start();

        Toast($"Timer set: {minutes} min", OnColor);
        RefreshPage();
    }

    private void CancelCountdown()
    {
        countdown?.Stop();
        countdown?.Dispose();
        countdown = null;

        Toast("Timer cancelled", OffColor);
        RefreshPage();
    }

    private string Remaining()
    {
        TimeSpan left = countdownEnd - DateTime.Now;

        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;

        return $"{(int)left.TotalMinutes}:{left.Seconds:00}";
    }

    private void ConfirmPower(string title, string question, string args)
    {
        DialogResult result = MessageBox.Show(
            question,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning
        );

        if (result != DialogResult.Yes)
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false
        });
    }

    // --------------------------------------------------
    // CLIPBOARD HISTORY (in memory only, never written to disk)
    // --------------------------------------------------

    private sealed class ClipItem
    {
        public string Kind = "text";          // text | image | files
        public string Key = "";
        public string Text = "";
        public byte[]? Png;
        public Size PixelSize;
        public Bitmap? Preview;               // shown when the row unfolds
        public Bitmap? Thumb;                 // small row icon
        public string[] Files = Array.Empty<string>();
        public string Search = "";
    }

    private const int MaxClipItems = 30;
    private const int MaxClipImages = 8;
    private const int MaxImageBytes = 12 * 1024 * 1024;

    private void RecordClipboard()
    {
        if (clipboardPaused || IsDisposed ||
            Environment.TickCount64 < ignoreClipboardUntil)
        {
            return;
        }

        try
        {
            IDataObject? data = Clipboard.GetDataObject();

            // Password managers flag their copies; never record those.
            if (data == null ||
                data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"))
            {
                return;
            }

            ClipItem? item = null;

            if (Clipboard.ContainsFileDropList())
                item = ClipFromFiles(Clipboard.GetFileDropList());
            else if (Clipboard.ContainsText())
                item = ClipFromText(Clipboard.GetText());
            else if (Clipboard.ContainsImage())
                item = ClipFromImage(Clipboard.GetImage());

            if (item == null)
                return;

            clipboardHistory.RemoveAll(c => c.Key == item.Key);
            clipboardHistory.Insert(0, item);

            if (clipboardHistory.Count > MaxClipItems)
{
                clipboardHistory.RemoveRange(
                    MaxClipItems, clipboardHistory.Count - MaxClipItems);
            }

            // Pictures are heavy: keep only the newest few.
            int images = 0;

            for (int i = 0; i < clipboardHistory.Count; i++)
            {
                if (clipboardHistory[i].Kind == "image" && ++images > MaxClipImages)
                    clipboardHistory.RemoveAt(i--);
            }

            if (currentPage == "Clipboard" && Visible)
                RefreshPage();
        }
        catch (Exception ex) when (
            ex is ExternalException or ArgumentException or
                OutOfMemoryException or InvalidOperationException or
                System.Threading.ThreadStateException)
        {
            // Another app holds the clipboard, or it held something odd.
        }
    }

    private static ClipItem? ClipFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 20000)
            return null;

        return new ClipItem
        {
            Kind = "text",
            Key = "txt:" + text,
            Text = text,
            Search = text
        };
    }

    private static ClipItem? ClipFromImage(Image? source)
    {
        if (source == null)
            return null;

        using (source)
        {
            using MemoryStream stream = new();
            source.Save(stream, System.Drawing.Imaging.ImageFormat.Png);

            if (stream.Length > MaxImageBytes)
                return null;

            byte[] png = stream.ToArray();

            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(png));

            return new ClipItem
            {
                Kind = "image",
                Key = "img:" + hash,
                Png = png,
                PixelSize = source.Size,
                Preview = FitImage(source, 420, 170, false),
                Thumb = FitImage(source, 22, 22, true),
                Search = "image picture photo screenshot"
            };
        }
    }

    private ClipItem? ClipFromFiles(System.Collections.Specialized.StringCollection list)
    {
        string[] files = list.Cast<string?>()
            .Where(f => !string.IsNullOrEmpty(f))
            .Select(f => f!)
            .Take(200)
            .ToArray();

        if (files.Length == 0)
            return null;

        ClipItem item = new()
        {
            Kind = "files",
            Key = "files:" + string.Join("|", files),
            Files = files,
            Search = string.Join(" ", files.Select(Path.GetFileName)) + " " +
                     string.Join(" ", files)
        };

        if (files.Length == 1 && File.Exists(files[0]))
        {
            string file = files[0];

            if (LocalMedia.IsVideoFile(file))
            {
                // One frame from the video, made in the background.
                string id = LocalMedia.ToVideo(file).Id;

                _ = ThumbCache.EnsureAsync(id).ContinueWith(_ =>
                {
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            Bitmap? frame = ThumbCache.TryGet(id);

                            if (frame == null)
                                return;

                            item.Preview = FitImage(frame, 420, 170, false);
                            item.Thumb = FitImage(frame, 22, 22, true);

                            if (currentPage == "Clipboard" && Visible)
                                RefreshPage();
                        }));
                    }
                    catch (InvalidOperationException)
                    {
                        // Closing.
                    }
                });
            }
            else if (IsPictureFile(file))
            {
                try
                {
                    if (new FileInfo(file).Length < 30 * 1024 * 1024)
                    {
                        using FileStream fs = File.OpenRead(file);
                        using Image img = Image.FromStream(fs, false, false);

                        item.Preview = FitImage(img, 420, 170, false);
                        item.Thumb = FitImage(img, 22, 22, true);
                    }
                }
                catch (Exception ex) when (
                    ex is ArgumentException or OutOfMemoryException or IOException)
                {
                    // Not a picture after all.
                }
            }
        }

        return item;
    }

    private static bool IsPictureFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".ico" or ".tif" or ".tiff";

    // Scales a picture to fit a box. `square` pads it to exactly that box.
    private static Bitmap FitImage(Image source, int maxW, int maxH, bool square)
    {
        double scale = Math.Min(
            maxW / (double)source.Width, maxH / (double)source.Height);

        if (!square)
            scale = Math.Min(scale, 1.0);

        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));

        Bitmap result = new(square ? maxW : w, square ? maxH : h);

        using Graphics g = Graphics.FromImage(result);

        g.InterpolationMode =
            System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        g.DrawImage(
            source,
            new Rectangle((result.Width - w) / 2, (result.Height - h) / 2, w, h));

        return result;
    }

    // Puts a history item back on the clipboard so it can be pasted.
    private bool RestoreClip(ClipItem item)
    {
        try
        {
            switch (item.Kind)
            {
                case "image":
                {
                    if (item.Png == null)
                        return false;

                    using MemoryStream stream = new(item.Png);
                    using Image loaded = Image.FromStream(stream);
                    using Bitmap copy = new(loaded);

                    DataObject data = new();
                    data.SetImage(copy);

                    ignoreClipboardUntil = Environment.TickCount64 + 900;
                    Clipboard.SetDataObject(data, true, 5, 100);
                    return true;
                }

                case "files":
                {
                    var existing = new System.Collections.Specialized.StringCollection();

                    foreach (string file in item.Files)
                    {
                        if (File.Exists(file) || Directory.Exists(file))
                            existing.Add(file);
                    }

                    if (existing.Count == 0)
                    {
                        Toast("Those files no longer exist", OffColor);
                        return false;
                    }

                    DataObject data = new();
                    data.SetFileDropList(existing);

                    ignoreClipboardUntil = Environment.TickCount64 + 900;
                    Clipboard.SetDataObject(data, true, 5, 100);
                    return true;
                }

                default:
                    return SetClipboard(item.Text);
            }
        }
        catch (Exception ex) when (
            ex is ExternalException or ArgumentException or OutOfMemoryException)
        {
            Toast("Clipboard is busy, try again", OffColor);
            return false;
        }
    }

    // The file to show for a picture item (a clipboard picture is saved to
    // a temporary file first). Null when the item is not a picture.
    private static string? PicturePathFor(ClipItem item)
    {
        if (item.Kind == "files" && item.Files.Length == 1 &&
            File.Exists(item.Files[0]) &&
            (LocalMedia.IsImageFile(item.Files[0]) || LocalMedia.IsVideoFile(item.Files[0])))
        {
            return item.Files[0];
        }

        if (item.Kind != "image" || item.Png == null)
            return null;

        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "UtilMaster3000", "clipboard");
            Directory.CreateDirectory(folder);

            // Old pictures from earlier days are not kept.
            foreach (string old in Directory.GetFiles(folder))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1))
                {
                    try { File.Delete(old); } catch (IOException) { }
                }
            }

            string name = $"Clipboard picture {item.PixelSize.Width}x{item.PixelSize.Height} {item.Key[^8..]}.png";
            string path = Path.Combine(folder, name);

            if (!File.Exists(path))
                File.WriteAllBytes(path, item.Png);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private MenuEntry ClipEntry(ClipItem item)
    {
        string glyph;
        string label;
        string? iconPath = null;
        string? unfold = null;

        switch (item.Kind)
        {
            case "image":
                glyph = "\uEB9F";
                label = $"Image   {item.PixelSize.Width} x {item.PixelSize.Height}";
                break;

            case "files":
            {
                string[] names = item.Files
                    .Select(f => Path.GetFileName(f.TrimEnd('\\', '/')))
                    .ToArray();

                bool video = item.Files.Length == 1 &&
                             LocalMedia.IsVideoFile(item.Files[0]);

                glyph = video ? "\uE714" : "\uE8A5";
                iconPath = item.Files[0];

                if (names.Length == 1)
                {
                    label = names[0];
                }
                else
                {
                    label = $"{names.Length} files:  " + string.Join(", ", names);

                    unfold = string.Join("\n", names.Take(10));

                    if (names.Length > 10)
                        unfold += $"\n+ {names.Length - 10} more";
                }

                break;
            }

            default:
                glyph = "\uE8C8";
                label = Trunc(Regex.Replace(item.Text.Trim(), @"\s+", " "), 160);
                unfold = Trunc(item.Text.Trim(), 900);
                break;
        }

        ClipItem captured = item;

        void Paste()
        {
            if (!RestoreClip(captured))
                return;

            HideToTray();
            PasteIntoPrevious();
        }

        void OpenInPlayer()
        {
            // A picture or a video opens in the built-in player.
            string? media = PicturePathFor(captured);

            if (media == null)
            {
                Paste();
                return;
            }

            HideToTray();
            VideoPlayerForm.OpenFiles(new[] { media });
        }

        bool isMedia = captured.Kind == "image" ||
            (captured.Kind == "files" && captured.Files.Length == 1 &&
             File.Exists(captured.Files[0]) &&
             (LocalMedia.IsImageFile(captured.Files[0]) ||
              LocalMedia.IsVideoFile(captured.Files[0])));

        bool leftOpens = settings.ClipboardLeftOpens && isMedia;

        return new MenuEntry(
            0, glyph, label,
            run: leftOpens ? OpenInPlayer : Paste,
            runAlt: isMedia ? (leftOpens ? Paste : OpenInPlayer) : null,
            iconPath: iconPath,
            expandText: unfold,
            expandImage: item.Preview,
            iconBitmap: item.Thumb);
    }

    private void PasteIntoPrevious()
    {
        After(260, () =>
        {
            if (lastExternalWindow != IntPtr.Zero &&
                Native.IsWindow(lastExternalWindow))
            {
                Native.SetForegroundWindow(lastExternalWindow);
            }

            After(80, SystemTools.SendPaste);
        });
    }

    // --------------------------------------------------
    // ENTRY SOURCES (what each page shows for a given search)
    // --------------------------------------------------

    private List<MenuEntry> GetEntries(string query)
    {
        List<MenuEntry> list = currentPage switch
        {
            "home" => query.Length == 0 ? mainEntries : HomeSearch(query),
            "Apps" => AppsPage(query),
            "Find" => FindPage(query),
            "Clipboard" => ClipboardPage(query),
            "Shortcuts" => ShortcutsPage(query),
            "TextTool" => TextToolPage(query),
            "Calc" => CalcPage(query),
            "Auto" => AutoClickPage(query),
            "Grab" => GrabPage(query),
            "Macros" => MacrosPage(query),
            "Notifications" => NotificationsPage(query),
            "MacroEntry" => MacroEntryPage(query),
            "MacroStep" => MacroStepPage(query),
            "Vault" => VaultPage(query),
            "VaultEntry" => VaultEntryPage(),
            _ => FilterStatic(query)
        };

        return list.Count == 0
            ? new List<MenuEntry> { Info("No results") }
            : list;
    }

    private List<MenuEntry> FilterStatic(string query)
    {
        if (!pages.TryGetValue(currentPage, out List<MenuEntry>? list))
            return new List<MenuEntry>();

        return list
            .Where(e => query.Length == 0 ||
                e.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private MenuEntry AppRow(AppEntry app) =>
        ActIcon("", app.Name,
            app.IsStoreApp ? "shell:AppsFolder\\" + app.Target : app.Target,
            () => LaunchApp(app));

    private void LaunchApp(AppEntry app)
    {
        Launch(() => AppIndex.Launch(app));

        settings.RecentApps.RemoveAll(n =>
            n.Equals(app.Name, StringComparison.OrdinalIgnoreCase));

        settings.RecentApps.Insert(0, app.Name);

        if (settings.RecentApps.Count > 8)
            settings.RecentApps.RemoveRange(8, settings.RecentApps.Count - 8);

        SaveSettings();
    }

    private MenuEntry WindowRow(WindowInfo window) =>
        ActIcon("", window.Title, WindowList.GetProcessPath(window.Handle), () =>
        {
            WindowList.Activate(window.Handle);
            HideToTray();
        });

    private MenuEntry FileRow(FileEntry file) =>
        ActIcon(file.IsFolder ? "" : "", file.Name,
            file.IsFolder ? "dir:" : file.Path,
            () =>
            {
                if (!file.IsFolder && settings.VideosInPlayer && LocalMedia.IsVideoFile(file.Path))
                {
                    Launch(() => VideoPlayerForm.OpenFiles(new[] { file.Path }));
                    return;
                }

                OpenTarget(file.Path);
            });

    private MenuEntry WebRow(string query)
    {
        var engine = SearchEngines[EngineIndex()];

        return Act("", $"{engine.Name}: {query}", () =>
            OpenTarget(engine.Url + Uri.EscapeDataString(query)));
    }

    private static bool MatchesAll(string text, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    private List<MenuEntry> HomeSearch(string query)
    {
        List<MenuEntry> list = new();

        // "> command" runs in a console.
        if (query.StartsWith('>'))
        {
            string command = query[1..].Trim();

            if (command.Length > 0)
            {
                list.Add(Act("", "Run: " + command, () =>
                    Launch(() => Process.Start(new ProcessStartInfo(
                        "cmd.exe", "/k " + command)
                    {
                        UseShellExecute = true
                    }))));
            }

            return Renumber(list);
        }

        // A website typed straight into the search opens in the browser.
        if (LooksLikeUrl(query))
        {
            string url = ToUrl(query);

            list.Add(ActIcon("\uE774", "Open " + query, null, () => OpenTarget(url)));
        }

        // "youtube ..." opens UtilTube, the offline YouTube client.
        string[] words = query.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > 0 && IsYouTubeWord(words[0]))
        {
            string rest = words.Length > 1 ? words[1].Trim() : "";

            list.Add(ActIcon("\uE714",
                rest.Length > 0
                    ? $"UtilTube: search \"{rest}\""
                    : "UtilTube  (YouTube, offline)",
                null, () => OpenTube(rest)));

            list.Add(ActIcon("\uE774", "Open youtube.com in the browser", null, () =>
                OpenTarget(rest.Length > 0
                    ? "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(rest)
                    : "https://www.youtube.com")));
        }
        else if (YtDlp.ExtractVideoId(query) != null)
        {
            list.Add(ActIcon("\uE714", "Save this video for offline viewing (UtilTube)",
                null, () => OpenTube(query)));
        }

        if (Calc.TryEvaluate(query, out string calc))
        {
            list.Add(Act("", "= " + calc, () =>
            {
                CopyValue(calc);
                HideToTray();
            }));
        }
        else if (UnitConverter.TryConvert(query, out string converted))
        {
            list.Add(Act("", converted, () =>
            {
                CopyValue(converted);
                HideToTray();
            }));
        }

        if (LinkDownloader.LooksLikeLink(query) && YtDlp.ExtractVideoId(query) == null)
        {
            string linkSite = LinkDownloader.SiteName(query);
            string linkText = query.Trim();

            list.Add(ActIcon("\uE896", $"Download this link ({linkSite}) and copy the file", null, () => StartGrab(linkText)));
        }

        list.AddRange(AppIndex.Search(query, 5).Select(AppRow));

        if (pages.TryGetValue("Tools", out List<MenuEntry>? toolEntries))
            list.AddRange(toolEntries.Where(e => e.targetPage != null &&
                e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)));

        list.AddRange(mainEntries.Where(e =>
            e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)));

        list.AddRange(WindowList.GetOpenWindows()
            .Where(w => MatchesAll(w.Title, query))
            .Take(3)
            .Select(WindowRow));

        if (query.Length >= 2)
            list.AddRange(FileIndex.Search(query, 4).Select(FileRow));

        list.Add(WebRow(query));

        return Renumber(list);
    }

    private List<MenuEntry> AppsPage(string query)
    {
        List<MenuEntry> list = new();

        if (query.Length == 0)
        {
            list.AddRange(settings.RecentApps
                .Select(AppIndex.Find)
                .Where(a => a != null)
                .Select(a => AppRow(a!)));

            if (list.Count == 0)
            {
                list.Add(Info(AppIndex.Ready
                    ? "Type to search your apps"
                    : "Indexing apps..."));
            }

            list.Add(BackEntry());
        }
        else
        {
            list.AddRange(AppIndex.Search(query, 40).Select(AppRow));

            if (list.Count == 0)
            {
                list.Add(Info(AppIndex.Ready
                    ? "No apps found"
                    : "Indexing apps..."));
            }
        }

        return Renumber(list);
    }

    private List<MenuEntry> FindPage(string query)
    {
        List<MenuEntry> list = new();

        if (query.Length == 0)
        {
            // Empty search = window switcher.
            list.AddRange(WindowList.GetOpenWindows()
                .Take(30)
                .Select(WindowRow));

            if (list.Count == 0)
                list.Add(Info("Type to search files and windows"));

            list.Add(BackEntry());
        }
        else
        {
            list.AddRange(WindowList.GetOpenWindows()
                .Where(w => MatchesAll(w.Title, query))
                .Take(5)
                .Select(WindowRow));

            list.AddRange(FileIndex.Search(query, 40).Select(FileRow));

            if (list.Count == 0)
            {
                list.Add(Info(FileIndex.Ready
                    ? "No matches"
                    : "Indexing files..."));
            }
        }

        return Renumber(list);
    }

    private List<MenuEntry> ClipboardPage(string query)
    {
        List<MenuEntry> list = new();

        foreach (ClipItem item in clipboardHistory)
        {
            if (query.Length > 0 &&
                !item.Search.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(ClipEntry(item));
        }

        if (query.Length == 0)
        {
            if (list.Count == 0)
                list.Add(Info("Copy text, a picture or files and they appear here"));

            list.Add(Act("\uE713",
                $"History recording: {OnOff(!clipboardPaused)}", () =>
                {
                    clipboardPaused = !clipboardPaused;
                    RefreshPage();
                }));

            list.Add(Act("\uE713",
                $"{KeyLabel("clipboard")} opens this: {OnOff(settings.ClipboardWinV)}", () =>
                {
                    settings.ClipboardWinV = !settings.ClipboardWinV;

                    SaveSettings();
                    RefreshPage();
                }));

            list.Add(Act("\uE713",
                settings.ClipboardLeftOpens
                    ? "Left click: open in player  (right click pastes)"
                    : "Left click: paste  (right click opens in player)", () =>
                {
                    settings.ClipboardLeftOpens = !settings.ClipboardLeftOpens;
                    SaveSettings();
                    RefreshPage();
                }));

            list.Add(Act("\uE74D", "Clear history", () =>
            {
                clipboardHistory.Clear();
                Toast("Clipboard history cleared", OffColor);
                RefreshPage();
            }));

            list.Add(BackEntry());
        }

        return Renumber(list);
    }

    // --------------------------------------------------
    // MENU ACTIONS (older string commands)
    // --------------------------------------------------

    private void ActivateEntry(MenuEntry entry)
    {
        if (entry.run != null)
        {
            entry.run();
            return;
        }

        if (entry.targetPage != null)
        {
            if (entry.targetPage == "Vault" && !VaultUnlocked)
                EnsureUnlocked();

            ShowPage(entry.targetPage);
            return;
        }

        switch (entry.command)
        {
            case "back":
                if (currentPage != "home")
                    ShowPage(ParentPage());
                break;

            case "toggle":
                translator.Toggle();
                RefreshPage();
                break;

            case "status":
                Say("Translator status",
                    translator.Enabled
                        ? "The translator is currently ON."
                        : "The translator is currently OFF.");
                break;

            case "shortcut":
                Say("Keyboard shortcuts",
                    $"Translator: {FormatHotkey(toggleKey)}  (hold: {HoldLabel()})\n" +
                    $"Launcher: {FormatHotkey(launcherKey)}");
                break;

            case "text-back":
                ShowPage("Text");
                break;

            case "bind":
                BeginHotkeyBinding(toggleKey);
                break;

            case "bind-launcher":
                BeginHotkeyBinding(launcherKey);
                break;

            case "hold":
            {
                int next = (Array.IndexOf(HoldChoices, settings.HoldToOpenMs) + 1)
                    % HoldChoices.Length;

                settings.HoldToOpenMs = HoldChoices[next];
                SaveSettings();
                RefreshPage();
                break;
            }

            case "engine":
                settings.SearchEngine = (EngineIndex() + 1) % SearchEngines.Length;
                SaveSettings();
                RefreshPage();
                break;

            case "opacity":
                notification.CycleOpacity();
                SaveSettings();
                notification.Preview(translator.Enabled);
                RefreshPage();
                break;

            case "position":
                notification.CyclePosition();
                SaveSettings();
                notification.Preview(translator.Enabled);
                RefreshPage();
                break;

            case "notify-toggle":
                notification.Muted = !notification.Muted;
                SaveSettings();

                if (!notification.Muted)
                    notification.Preview(translator.Enabled);

                RefreshPage();
                break;

            case "focus-hide":
                settings.HideOnFocusLoss = !settings.HideOnFocusLoss;
                SaveSettings();
                RefreshPage();
                break;

            case "startup":
                if (!StartupManager.SetEnabled(!StartupManager.IsEnabled()))
                {
                    MessageBox.Show(
                        "Could not change the Windows startup entry.",
                        "Setup",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }

                RefreshPage();
                break;

            case "reset":
                ResetSettings();
                break;

            case "open-settings":
                OpenSettingsFolder();
                break;

            case "hide":
                HideToTray();
                break;

            case "exit":
                ExitApplication();
                break;
        }
    }

    private void OpenSettingsFolder()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "UtilMaster3000"
        );

        Directory.CreateDirectory(folder);

        OpenTarget(folder);
    }

    private void ResetSettings()
    {
        DialogResult result = MessageBox.Show(
            "Reset shortcuts, notification and launcher settings to their defaults?",
            "Reset settings",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );

        if (result != DialogResult.Yes)
            return;

        settings = new AppSettings();
        ApplyNotificationSettings();

        SetHotkey(toggleKey, settings.HotkeyModifiers, (Keys)settings.HotkeyKey);
        SetHotkey(launcherKey, settings.LauncherHotkeyModifiers, (Keys)settings.LauncherHotkeyKey);
        ApplyShortcuts();

        SaveSettings();
        RefreshPage();
    }
}
