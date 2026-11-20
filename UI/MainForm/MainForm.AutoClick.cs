using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// AUTOCLICKER PAGE
// Click a row to step through its values, right-click to step back,
// type a number to set the speed exactly (500, 2.5s, 0.1s...).
// ======================================================

public sealed partial class MainForm
{
    private readonly AutoClicker autoClicker = new();
    private bool autoClickEventsHooked;

    private static readonly int[] AcIntervals =
        { 1, 2, 5, 10, 20, 50, 100, 200, 250, 500, 1000, 2000, 5000, 10000, 60000 };

    private static string AcIntervalText(int ms)
    {
        string rate = ms < 1000
            ? $"{1000.0 / ms:0.#} clicks/s"
            : ms % 1000 == 0 ? $"every {ms / 1000} s" : $"every {ms / 1000.0:0.##} s";

        return $"{ms} ms  ({rate})";
    }

    private AutoClickOptions AutoClickSettings() => new(
        settings.AcIntervalMs,
        settings.AcJitterPercent,
        settings.AcButton,
        settings.AcDouble,
        settings.AcFixedPoint,
        settings.AcX,
        settings.AcY,
        settings.AcRepeat,
        settings.AcStartDelayMs);

    private void HookAutoClickEvents()
    {
        if (autoClickEventsHooked)
            return;

        autoClickEventsHooked = true;

        autoClicker.Stopped += (reason, count) =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Toast($"Autoclicker: {reason}  ({count} clicks)", OffColor);

                    if (currentPage == "Auto" && Visible)
                        RefreshPage();
                }));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        };
    }

    /// <summary>Hotkey / row: start clicking, or stop if it is running.</summary>
    private void ToggleAutoClick()
    {
        HookAutoClickEvents();

        if (autoClicker.Running)
        {
            autoClicker.Stop();
            return;
        }

        AutoClickOptions options = AutoClickSettings();

        // The launcher must not be under the mouse when clicking starts.
        bool wasOpen = Visible && !hiding;

        if (wasOpen)
            HideToTray();

        int delay = options.StartDelayMs;

        Toast(delay > 0
            ? $"Autoclicker starts in {delay / 1000.0:0.#} s  ({KeyLabel("autoclick")} stops it)"
            : $"Autoclicker on  ({KeyLabel("autoclick")} stops it)", OnColor);

        // Let focus go back to the window you were in first.
        After(wasOpen ? 350 : 0, () => autoClicker.Start(options with
        {
            StartDelayMs = options.StartDelayMs
        }));
    }

    private MenuEntry AcCycle(
        string icon, string label, int[] presets, Func<int> get, Action<int> set,
        Func<int, string> format, string tip)
    {
        void Step(int direction)
        {
            int[] all = presets.Contains(get())
                ? presets
                : presets.Append(get()).OrderBy(x => x).ToArray();

            int i = Array.IndexOf(all, get());
            i = (i + direction + all.Length) % all.Length;

            set(all[i]);
            SaveSettings();
            RefreshPage();
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(
            0, icon, text,
            run: () => Step(1),
            runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next value, right-click = previous.");
    }

    // "500", "250ms", "2.5s", "1 min" -> milliseconds
    private static bool TryParseInterval(string text, out int ms)
    {
        ms = 0;

        Match m = Regex.Match(text.Trim(),
            @"^(\d+(?:[.,]\d+)?)\s*(ms|msec|s|sec|secs|m|min|mins)?$", RegexOptions.IgnoreCase);

        if (!m.Success)
            return false;

        double value = double.Parse(m.Groups[1].Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture);

        string unit = m.Groups[2].Value.ToLowerInvariant();

        double result = unit switch
        {
            "s" or "sec" or "secs" => value * 1000,
            "m" or "min" or "mins" => value * 60000,
            _ => value
        };

        if (result < 1 || result > 3_600_000)
            return false;

        ms = (int)Math.Round(result);
        return true;
    }

    private List<MenuEntry> AutoClickPage(string query)
    {
        List<MenuEntry> list = new();
        string key = KeyLabel("autoclick");

        // Typing a number sets the speed exactly.
        if (TryParseInterval(query, out int typed))
        {
            list.Add(Act("", $"Click every {AcIntervalText(typed)}", () =>
            {
                settings.AcIntervalMs = typed;
                SaveSettings();
                searchBox.Clear();
                RefreshPage();
            }));
        }

        string state = autoClicker.Running
            ? $"RUNNING  ({autoClicker.Clicks} clicks)  -  press {key} or Enter to stop"
            : $"Off  -  press {key} or Enter to start";

        list.Add(new MenuEntry(
            0, autoClicker.Running ? "" : "", "Autoclicker: " + state,
            run: ToggleAutoClick,
            expandText: "Autoclicker: " + state + NL +
                        "Starting hides this menu, then clicks where the mouse is (or at the fixed point)." + NL +
                        $"Stop with {key}, or by moving the mouse into the top-left corner of the screen."));

        list.Add(AcCycle("", "Click interval", AcIntervals,
            () => settings.AcIntervalMs, v => settings.AcIntervalMs = v, AcIntervalText,
            "Time between clicks. Or type a number above: 250, 0.5s, 2 min."));

        list.Add(AcCycle("", "Random variation", new[] { 0, 5, 10, 25, 50 },
            () => settings.AcJitterPercent, v => settings.AcJitterPercent = v,
            v => v == 0 ? "off" : "+/- " + v + "%",
            "Varies each wait a little so the clicks are not perfectly even."));

        list.Add(AcCycle("", "Mouse button", new[] { 0, 1, 2 },
            () => settings.AcButton, v => settings.AcButton = v, v => AutoClicker.ButtonNames[v],
            "Which button is clicked."));

        list.Add(AcCycle("", "Click type", new[] { 0, 1 },
            () => settings.AcDouble ? 1 : 0, v => settings.AcDouble = v == 1,
            v => v == 1 ? "double click" : "single click",
            "A double click sends two clicks each time."));

        list.Add(AcCycle("", "Repeat", new[] { 0, 1, 5, 10, 25, 50, 100, 500, 1000, 10000 },
            () => settings.AcRepeat, v => settings.AcRepeat = v,
            v => v == 0 ? "until I stop it" : v + " times",
            "How many times to click before stopping by itself."));

        list.Add(AcCycle("", "Start delay", new[] { 0, 1000, 2000, 3000, 5000, 10000 },
            () => settings.AcStartDelayMs, v => settings.AcStartDelayMs = v,
            v => v == 0 ? "none" : v / 1000 + " s",
            "Time to wait after starting, so you can put the mouse where it should click."));

        list.Add(AcCycle("", "Where to click", new[] { 0, 1 },
            () => settings.AcFixedPoint ? 1 : 0, v => settings.AcFixedPoint = v == 1,
            v => v == 1 ? $"fixed point ({settings.AcX}, {settings.AcY})" : "wherever the mouse is",
            "Follow the mouse, or always click the same spot on screen."));

        list.Add(new MenuEntry(
            0, "", $"Pick the fixed point ({settings.AcX}, {settings.AcY})",
            run: () =>
            {
                HideToTray();
                Toast("Move the mouse to the spot you want clicked... 3 seconds", AccentColor);

                After(3200, () =>
                {
                    Point p = Cursor.Position;
                    settings.AcX = p.X;
                    settings.AcY = p.Y;
                    settings.AcFixedPoint = true;
                    SaveSettings();

                    ShowPage("Auto");
                    OpenLauncher();
                    Toast($"Click point set: ({p.X}, {p.Y})", OnColor);
                });
            },
            expandText: "Pick the fixed point" + NL +
                        "Hides this menu, waits 3 seconds while you put the mouse on the spot, then remembers it."));

        list.Add(Act("", $"Start / stop key: {key}", () => BeginShortcutBinding("autoclick")));

        list.Add(Act("", "Reset the autoclicker settings", () =>
        {
            settings.AcIntervalMs = 100;
            settings.AcJitterPercent = 0;
            settings.AcButton = 0;
            settings.AcDouble = false;
            settings.AcFixedPoint = false;
            settings.AcRepeat = 0;
            settings.AcStartDelayMs = 0;
            SaveSettings();
            RefreshPage();
        }));

        list.Add(BackEntry());

        return Renumber(list);
    }
}
