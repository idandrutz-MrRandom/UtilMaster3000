using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// MACROS PAGES
//   Macros      record, play, repeat / speed, the saved list
//   MacroEntry  one macro: play, rename, delete, its steps
//   MacroStep   one step: move the click, change the wait, delete
// ======================================================

public sealed partial class MainForm
{
    private readonly MacroRecorder macroRecorder = new();
    private readonly MacroPlayer macroPlayer = new();
    private bool macroEventsHooked;

    private Macro? openMacro;
    private MacroStep? openStep;

    // One thing the macro does, made of one or more raw events.
    private sealed record MacroStep(int[] Indexes, string Kind, string Text);

    private static string ButtonName(int b) => b == 1 ? "right" : b == 2 ? "middle" : "left";

    private static List<MacroStep> StepsOf(Macro m, bool includePaths)
    {
        List<MacroStep> steps = new();
        List<MacroEvent> ev = m.Events;
        HashSet<int> used = new();

        for (int i = 0; i < ev.Count; i++)
        {
            if (used.Contains(i))
                continue;

            MacroEvent e = ev[i];
            string when = $"{e.T / 1000.0:0.0}s  ";

            switch (e.K)
            {
                case MacroKind.Move:
                {
                    int j = i;

                    while (j + 1 < ev.Count && ev[j + 1].K == MacroKind.Move)
                        j++;

                    if (includePaths)
                    {
                        steps.Add(new MacroStep(
                            Enumerable.Range(i, j - i + 1).ToArray(), "path",
                            $"{when}Mouse path ({j - i + 1} points, ends at {ev[j].X}, {ev[j].Y})"));
                    }

                    i = j;
                    break;
                }

                case MacroKind.Down:
                {
                    int up = -1;

                    for (int k = i + 1; k < ev.Count; k++)
                    {
                        if (ev[k].K == MacroKind.Down && ev[k].A == e.A)
                            break;

                        if (ev[k].K == MacroKind.Up && ev[k].A == e.A)
                        {
                            up = k;
                            break;
                        }
                    }

                    if (up >= 0)
                    {
                        used.Add(up);
                        bool drag = Math.Abs(ev[up].X - e.X) > 12 || Math.Abs(ev[up].Y - e.Y) > 12;

                        steps.Add(new MacroStep(new[] { i, up }, "click",
                            drag
                                ? $"{when}Drag {ButtonName(e.A)} from ({e.X}, {e.Y}) to ({ev[up].X}, {ev[up].Y})"
                                : $"{when}Click {ButtonName(e.A)} at ({e.X}, {e.Y})"));
                    }
                    else
                    {
                        steps.Add(new MacroStep(new[] { i }, "click",
                            $"{when}Hold {ButtonName(e.A)} at ({e.X}, {e.Y})"));
                    }

                    break;
                }

                case MacroKind.Wheel:
                    steps.Add(new MacroStep(new[] { i }, "wheel",
                        $"{when}Scroll {(e.A > 0 ? "up" : "down")} at ({e.X}, {e.Y})"));
                    break;

                case MacroKind.KeyDown:
                {
                    int up = -1;

                    for (int k = i + 1; k < ev.Count; k++)
                    {
                        if (ev[k].K == MacroKind.KeyUp && ev[k].A == e.A)
                        {
                            up = k;
                            break;
                        }
                    }

                    if (up >= 0)
                        used.Add(up);

                    steps.Add(new MacroStep(up >= 0 ? new[] { i, up } : new[] { i }, "key",
                        $"{when}Key {(Keys)e.A}"));
                    break;
                }
            }
        }

        return steps;
    }

    private static string StepPositionText(Macro m, MacroStep s)
    {
        MacroEvent e = m.Events[s.Indexes[0]];
        return $"({e.X}, {e.Y})";
    }

    // ---------- recording and playing ----------

    private void HookMacroEvents()
    {
        if (macroEventsHooked)
            return;

        macroEventsHooked = true;

        macroPlayer.Finished += reason =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Toast("Macro: " + reason, OffColor);

                    if (Visible && currentPage.StartsWith("Macro"))
                        RefreshPage();
                }));
            }
            catch (InvalidOperationException)
            {
                // Closing.
            }
        };
    }

    private void ToggleMacroRecording()
    {
        HookMacroEvents();

        if (macroPlayer.Playing)
            macroPlayer.Stop();

        if (macroRecorder.Recording)
        {
            Macro macro = macroRecorder.Stop(Macro.NextName());

            if (macro.Events.Count < 2)
            {
                Toast("Macro: nothing was recorded", OffColor);
            }
            else
            {
                macro.Save();
                settings.LastMacro = macro.Name;
                SaveSettings();

                Toast($"Saved \"{macro.Name}\" ({macro.Summary}).  {KeyLabel("macro-play")} plays it", OnColor);
            }

            if (Visible && currentPage.StartsWith("Macro"))
                RefreshPage();

            return;
        }

        if (Visible && !hiding)
            HideToTray();

        macroRecorder.RecordMoves = settings.MacroRecordMoves;
        macroRecorder.Ignore(new[]
        {
            ShortcutCatalog.Get(settings, ShortcutCatalog.All.First(i => i.Id == "macro-record")).Key,
            ShortcutCatalog.Get(settings, ShortcutCatalog.All.First(i => i.Id == "macro-play")).Key
        });

        macroRecorder.Start();
        Toast($"Recording...  {KeyLabel("macro-record")} stops and saves", AccentColor);
    }

    private Macro? CurrentMacro()
    {
        List<Macro> all = Macro.LoadAll();
        return all.FirstOrDefault(m => m.Name == settings.LastMacro) ?? all.LastOrDefault();
    }

    private void PlayMacro(Macro? macro)
    {
        HookMacroEvents();

        if (macroRecorder.Recording)
            return;

        if (macroPlayer.Playing)
        {
            macroPlayer.Stop();
            return;
        }

        macro ??= CurrentMacro();

        if (macro == null)
        {
            Toast("No macro yet: record one first", OffColor);
            return;
        }

        settings.LastMacro = macro.Name;
        SaveSettings();

        bool wasOpen = Visible && !hiding;

        if (wasOpen)
            HideToTray();

        string times = settings.MacroRepeat == 0 ? "until stopped" : settings.MacroRepeat + "x";
        Toast($"Playing \"{macro.Name}\" ({times}, {settings.MacroSpeedPercent}%).  {KeyLabel("macro-play")} stops it", OnColor);

        Macro toPlay = macro;
        After(wasOpen ? 350 : 0, () =>
            macroPlayer.Play(toPlay, settings.MacroRepeat, settings.MacroSpeedPercent / 100.0));
    }

    // ---------- helpers ----------

    private MenuEntry MacroCycle(
        string icon, string label, int[] presets, Func<int> get, Action<int> set,
        Func<int, string> format, string tip)
    {
        void Step(int direction)
        {
            int[] all = presets.Contains(get()) ? presets : presets.Append(get()).ToArray();
            int i = Array.IndexOf(all, get());
            i = (i + direction + all.Length) % all.Length;
            set(all[i]);
            SaveSettings();
            RefreshPage();
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(0, icon, text, run: () => Step(1), runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next value, right-click = previous.");
    }

    private void SaveOpenMacro()
    {
        openMacro?.Save();
    }

    // ---------- the Macros page ----------

    private List<MenuEntry> MacrosPage(string query)
    {
        List<MenuEntry> list = new();

        string recKey = KeyLabel("macro-record");
        string playKey = KeyLabel("macro-play");

        if (macroRecorder.Recording)
        {
            list.Add(new MenuEntry(0, "", $"RECORDING...  {macroRecorder.Count} events  -  Enter or {recKey} stops",
                run: ToggleMacroRecording));
        }
        else
        {
            list.Add(new MenuEntry(0, "", $"Record a new macro  ({recKey})",
                run: ToggleMacroRecording,
                expandText: "Record a new macro" + NL +
                            "Hides this menu and records everything you do with the mouse and keyboard." + NL +
                            $"Press {recKey} again to stop and save it. Then open it here to adjust the clicks."));
        }

        Macro? current = CurrentMacro();

        list.Add(new MenuEntry(0, macroPlayer.Playing ? "" : "",
            macroPlayer.Playing
                ? $"PLAYING...  Enter or {playKey} stops"
                : current == null ? "Play the last macro  (none yet)" : $"Play \"{current.Name}\"  ({playKey})",
            run: () => PlayMacro(null),
            expandText: "Plays the macro you used last: " + (current?.Name ?? "none") + NL +
                        $"{playKey} starts and stops it from anywhere. Slamming the mouse into the top-left corner also stops it."));

        list.Add(MacroCycle("", "Repeat", new[] { 1, 2, 3, 5, 10, 25, 50, 100, 1000, 0 },
            () => settings.MacroRepeat, v => settings.MacroRepeat = v,
            v => v == 0 ? "until I stop it" : v + (v == 1 ? " time" : " times"),
            "How many times a macro plays."));

        list.Add(MacroCycle("", "Speed", new[] { 25, 50, 100, 150, 200, 400, 800 },
            () => settings.MacroSpeedPercent, v => settings.MacroSpeedPercent = v,
            v => v + "%", "100% = the speed you recorded it at."));

        list.Add(MacroCycle("", "Record the mouse path", new[] { 1, 0 },
            () => settings.MacroRecordMoves ? 1 : 0, v => settings.MacroRecordMoves = v == 1,
            v => v == 1 ? "yes, every movement" : "no, only clicks and keys",
            "Off: smaller macros that jump straight to each click."));

        list.Add(MacroCycle("", "Macro hotkeys", new[] { 1, 0 },
            () => settings.MacroHotkeysOn ? 1 : 0, v => settings.MacroHotkeysOn = v == 1,
            v => v == 1 ? "on" : "off", "Switch the record and play keys on or off."));

        list.Add(Act("", $"Record key: {recKey}", () => BeginShortcutBinding("macro-record")));
        list.Add(Act("", $"Play key: {playKey}", () => BeginShortcutBinding("macro-play")));

        List<Macro> macros = Macro.LoadAll();

        if (macros.Count > 0)
            list.Add(Info("Saved macros  -  click to open and edit, right-click to play"));

        foreach (Macro m in macros.Where(m =>
                     query.Length == 0 || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            Macro captured = m;

            list.Add(new MenuEntry(0, "",
                (m.Name == settings.LastMacro ? "* " : "") + $"{m.Name}  -  {m.Summary}",
                run: () =>
                {
                    openMacro = captured;
                    ShowPage("MacroEntry");
                },
                runAlt: () => PlayMacro(captured),
                expandText: $"{m.Name}  ({m.Summary})" + NL + "Click to open and adjust it, right-click to play it now."));
        }

        if (macros.Count == 0)
            list.Add(Info("No saved macros yet"));

        list.Add(BackEntry());

        return Renumber(list);
    }

    // ---------- one macro ----------

    private List<MenuEntry> MacroEntryPage(string query)
    {
        List<MenuEntry> list = new();

        if (openMacro == null)
        {
            list.Add(BackEntry());
            return Renumber(list);
        }

        Macro m = openMacro;

        // Typing a name offers to rename.
        if (query.Length > 0 && !m.Name.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(query, @"^\d"))
        {
            string newName = query.Trim();

            list.Add(Act("", $"Rename to \"{newName}\"", () =>
            {
                m.Delete();
                bool wasLast = settings.LastMacro == m.Name;
                m.Name = newName;
                m.Save();

                if (wasLast)
                {
                    settings.LastMacro = newName;
                    SaveSettings();
                }

                searchBox.Clear();
                RefreshPage();
            }));
        }

        list.Add(Act("", $"Play this macro  ({settings.MacroRepeat}x, {settings.MacroSpeedPercent}%)",
            () => PlayMacro(m)));

        list.Add(Info($"{m.Name}  -  {m.Summary}.  Type a new name to rename it."));

        // Move every click by the same amount (the window moved).
        void ShiftAll(int dx, int dy)
        {
            foreach (MacroEvent e in m.Events)
            {
                e.X += dx;
                e.Y += dy;
            }

            SaveOpenMacro();
            RefreshPage();
        }

        list.Add(Act("", "Shift the whole macro right 20 px", () => ShiftAll(20, 0)));
        list.Add(Act("", "Shift the whole macro left 20 px", () => ShiftAll(-20, 0)));
        list.Add(Act("", "Shift the whole macro down 20 px", () => ShiftAll(0, 20)));
        list.Add(Act("", "Shift the whole macro up 20 px", () => ShiftAll(0, -20)));

        bool hasMoves = m.Events.Any(e => e.K == MacroKind.Move);

        if (hasMoves)
        {
            list.Add(Act("", "Remove the mouse paths (keep clicks and keys)", () =>
            {
                m.Events.RemoveAll(e => e.K == MacroKind.Move);
                SaveOpenMacro();
                RefreshPage();
            }));
        }

        list.Add(Act("", "Delete this macro", () =>
        {
            m.Delete();

            if (settings.LastMacro == m.Name)
            {
                settings.LastMacro = "";
                SaveSettings();
            }

            openMacro = null;
            ShowPage("Macros");
            Toast("Macro deleted", OffColor);
        }));

        // The steps (mouse paths are hidden unless asked for).
        bool showPaths = query.StartsWith("path", StringComparison.OrdinalIgnoreCase);
        List<MacroStep> steps = StepsOf(m, showPaths);

        string filter = showPaths ? "" : query;

        if (steps.Count > 0)
            list.Add(Info("Steps  -  click one to adjust it (type \"paths\" to see mouse paths too)"));

        foreach (MacroStep s in steps.Where(s =>
                     filter.Length == 0 || Regex.IsMatch(filter, @"^\d") ||
                     s.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(60))
        {
            MacroStep captured = s;

            list.Add(new MenuEntry(0, s.Kind == "click" ? "" : s.Kind == "key" ? "" : "",
                s.Text,
                run: () =>
                {
                    openStep = captured;
                    ShowPage("MacroStep");
                }));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    // ---------- one step ----------

    private List<MenuEntry> MacroStepPage(string query)
    {
        List<MenuEntry> list = new();

        if (openMacro == null || openStep == null || openStep.Indexes.Any(i => i >= openMacro.Events.Count))
        {
            list.Add(BackEntry());
            return Renumber(list);
        }

        Macro m = openMacro;
        MacroStep s = openStep;
        int first = s.Indexes[0];
        bool positional = s.Kind != "key";

        // Refresh the description after edits.
        MacroStep? fresh = StepsOf(m, true).FirstOrDefault(x => x.Indexes[0] == first);
        string text = fresh?.Text ?? s.Text;

        // Typed "x,y" sets the position; a time sets the wait before the step.
        Match xy = Regex.Match(query, @"^\s*(-?\d+)\s*[, ]\s*(-?\d+)\s*$");

        if (positional && xy.Success)
        {
            int nx = int.Parse(xy.Groups[1].Value), ny = int.Parse(xy.Groups[2].Value);

            list.Add(Act("", $"Set the position to ({nx}, {ny})", () =>
            {
                SetStepPosition(m, s, nx, ny);
                searchBox.Clear();
                RefreshPage();
            }));
        }
        else if (TryParseInterval(query, out int typedWait))
        {
            list.Add(Act("", $"Wait {typedWait} ms before this step", () =>
            {
                ChangeWait(m, first, typedWait - GapBefore(m, first));
                searchBox.Clear();
                RefreshPage();
            }));
        }

        list.Add(Info(text));

        if (positional)
        {
            void Nudge(int dx, int dy)
            {
                foreach (int i in s.Indexes)
                {
                    m.Events[i].X += dx;
                    m.Events[i].Y += dy;
                }

                SaveOpenMacro();
                RefreshPage();
            }

            MacroEvent firstEvent = m.Events[first];

            list.Add(Info($"Position ({firstEvent.X}, {firstEvent.Y})  -  or type  x,y  to set it exactly"));

            foreach ((string name, int dx, int dy, int by) in new[]
                     {
                         ("right", 1, 0, 1), ("left", -1, 0, 1), ("down", 0, 1, 1), ("up", 0, -1, 1),
                         ("right", 10, 0, 10), ("left", -10, 0, 10), ("down", 0, 10, 10), ("up", 0, -10, 10)
                     })
            {
                int ddx = dx, ddy = dy;
                list.Add(Act("", $"Move {by} px {name}", () => Nudge(ddx, ddy)));
            }

            list.Add(Act("", "Pick the position with the mouse (3 seconds)", () =>
            {
                HideToTray();
                Toast("Move the mouse to the new spot... 3 seconds", AccentColor);

                After(3200, () =>
                {
                    Point p = Cursor.Position;
                    SetStepPosition(m, s, p.X, p.Y);

                    ShowPage("MacroStep");
                    OpenLauncher();
                    Toast($"Position set: ({p.X}, {p.Y})", OnColor);
                });
            }));

            list.Add(Act("", "Show where it is (moves the mouse there)", () =>
            {
                HideToTray();
                After(300, () => Cursor.Position = new Point(m.Events[first].X, m.Events[first].Y));
            }));
        }

        int gap = GapBefore(m, first);

        list.Add(new MenuEntry(0, "", $"Wait before this step: {gap} ms",
            run: () => ChangeWait(m, first, gap < 1000 ? 100 : 500, refresh: true),
            runAlt: () => ChangeWait(m, first, gap < 1000 ? -100 : -500, refresh: true),
            expandText: $"Wait before this step: {gap} ms" + NL +
                        "Click = longer, right-click = shorter. Or type a time like 250 or 1.5s." + NL +
                        "Later steps move along with it."));

        list.Add(Act("", "Delete this step", () =>
        {
            foreach (int i in s.Indexes.OrderByDescending(i => i))
                m.Events.RemoveAt(i);

            SaveOpenMacro();
            openStep = null;
            ShowPage("MacroEntry");
        }));

        list.Add(BackEntry());

        return Renumber(list);
    }

    private static int GapBefore(Macro m, int index) =>
        index == 0 ? m.Events[0].T : m.Events[index].T - m.Events[index - 1].T;

    // Longer / shorter wait before an event; everything after it moves too.
    private void ChangeWait(Macro m, int index, int delta, bool refresh = false)
    {
        int gap = GapBefore(m, index);
        delta = Math.Max(delta, -gap);

        for (int k = index; k < m.Events.Count; k++)
            m.Events[k].T += delta;

        SaveOpenMacro();

        if (refresh)
            RefreshPage();
    }

    private void SetStepPosition(Macro m, MacroStep s, int x, int y)
    {
        int dx = x - m.Events[s.Indexes[0]].X;
        int dy = y - m.Events[s.Indexes[0]].Y;

        // The whole step moves together (a drag keeps its length).
        foreach (int i in s.Indexes)
        {
            m.Events[i].X += dx;
            m.Events[i].Y += dy;
        }

        SaveOpenMacro();
    }
}
