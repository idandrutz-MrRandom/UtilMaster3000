using System;
using System.Collections.Generic;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// MENU MODES  (asked on the first start; Setup -> Menu mode...)
//   Simple    a short home menu of the everyday things
//   Advanced  most of the important things
//   Master    everything, as the menu always was
// Nothing is ever removed: whatever is not on the home page is under "More...".
// ======================================================

public sealed partial class MainForm
{
    private const int MenuModeSimple = 0, MenuModeAdvanced = 1, MenuModeMaster = 2;

    private static readonly string[] MenuModeNames = { "Simple", "Advanced", "Master" };

    // The home menu with every row, in the user's order (the visible list is made from it).
    private List<MenuEntry> fullMainEntries = new();

    // The rows each mode shows on the home page (null = all of them).
    private static HashSet<string>? MenuModeRows(int mode) => mode switch
    {
        MenuModeSimple => new HashSet<string> { "Apps", "Find", "Capture", "Clipboard", "Functions", "Setup" },
        MenuModeAdvanced => new HashSet<string>
        {
            "Functions", "Apps", "Find", "Capture", "Tools", "Text", "Clipboard", "Vault", "Windows", "Style", "Setup", "System"
        },
        _ => null
    };

    private int CurrentMenuMode => Math.Clamp(settings.UiMode, MenuModeSimple, MenuModeMaster);

    // The home page's rows for the chosen mode, plus "More..." with the rest.
    private void ApplyMenuMode()
    {
        HashSet<string>? shown = MenuModeRows(CurrentMenuMode);

        List<MenuEntry> list = shown == null
            ? fullMainEntries.ToList()
            : fullMainEntries.Where(e => shown.Contains(e.Label)).ToList();

        if (shown != null && list.Count < fullMainEntries.Count)
        {
            list.Add(new MenuEntry(0, "", "More...", targetPage: "More",
                expandText: "Everything that is not on the home menu in this mode: every feature is still here. " +
                            "Change the mode in Setup > Menu mode."));
        }

        List<MenuEntry> numbered = Renumber(list);

        mainEntries.Clear();
        mainEntries.AddRange(numbered);
    }

    // "More...": the rows the mode leaves off the home page, and the way to change the mode.
    private List<MenuEntry> BuildMorePage()
    {
        HashSet<string>? shown = MenuModeRows(CurrentMenuMode);

        List<MenuEntry> list = new()
        {
            Info($"Everything else ({MenuModeNames[CurrentMenuMode]} mode)")
        };

        foreach (MenuEntry e in fullMainEntries)
        {
            if (shown != null && shown.Contains(e.Label))
                continue;

            list.Add(new MenuEntry(0, e.Icon, e.Label, targetPage: e.targetPage, command: e.command, run: e.run,
                iconPath: e.iconPath, expandText: e.expandText));
        }

        list.Add(new MenuEntry(0, "", $"Menu mode: {MenuModeNames[CurrentMenuMode]}...", targetPage: "Mode"));
        list.Add(BackEntry());

        return Renumber(list);
    }

    // True on the very first start: the mode is asked before anything else.
    private bool FirstRunMenuModeChoice => !settings.ModeChosen && !settings.GuideShown;

    private List<MenuEntry> BuildMenuModePage()
    {
        bool first = FirstRunMenuModeChoice;
        List<MenuEntry> list = new();

        list.Add(Info(first
            ? "Welcome! How much do you want on the menu?"
            : "How much of the menu is on the home page?"));

        MenuEntry Choice(int mode, string title, string tagline, string description)
        {
            bool on = !first && CurrentMenuMode == mode;

            return new MenuEntry(0, on ? "" : "", $"{(on ? "●  " : "")}{title}: {tagline}",
                run: () => ChooseMenuMode(mode),
                expandText: title + NL + description);
        }

        list.Add(Choice(MenuModeSimple, "Simple", "just the basics",
            "A short menu: Apps, Find, Capture, Clipboard, Functions and Setup. Everything else is under More..."));

        list.Add(Choice(MenuModeAdvanced, "Advanced", "most things",
            "Most of the important things on the home page. Only the set-up guide, Modules, Learn and About are under More..."));

        list.Add(Choice(MenuModeMaster, "Master", "everything",
            "Everything on the home page, with nothing tucked away."));

        list.Add(Info("You can change this any time: Setup > Menu mode. Nothing is removed in any mode."));

        if (first)
        {
            list.Add(Act("", "Decide later (show everything)", () =>
            {
                settings.UiMode = MenuModeMaster;
                FinishMenuModeChoice(chosen: false);
            }));
        }
        else
        {
            list.Add(BackEntry());
        }

        return Renumber(list);
    }

    // The Setup row: click = next mode, right-click = previous. Applies at once.
    private void CycleMenuMode(int direction)
    {
        settings.UiMode = (CurrentMenuMode + direction + MenuModeNames.Length) % MenuModeNames.Length;
        settings.ModeChosen = true;

        ApplyMenuMode();
        SaveSettings();
        Toast($"{MenuModeNames[CurrentMenuMode]} mode", OnColor);

        // The pages that list the rows (More, Setup) are rebuilt for the new mode.
        RefreshPage();
    }

    private void ChooseMenuMode(int mode)
    {
        settings.UiMode = Math.Clamp(mode, MenuModeSimple, MenuModeMaster);
        FinishMenuModeChoice(chosen: true);
    }

    private void FinishMenuModeChoice(bool chosen)
    {
        bool first = FirstRunMenuModeChoice;

        if (chosen)
            settings.ModeChosen = true;

        ApplyMenuMode();
        BuildPages();

        if (first)
        {
            // Then the set-up guide, as the first start always did.
            settings.GuideShown = true;
            SaveSettings();
            ShowPage("Guide");
            return;
        }

        SaveSettings();
        Toast($"{MenuModeNames[CurrentMenuMode]} mode", OnColor);
        ShowPage("home");
    }
}
