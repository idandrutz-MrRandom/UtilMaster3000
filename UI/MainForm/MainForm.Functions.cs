using System;
using System.Collections.Generic;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// FUNCTION SEARCH
// Every action and setting in the launcher, found by name from one place:
//  * home -> Functions, a page that searches all of them
//  * the home search also lists matching functions under the usual results
// Picking one does it, or opens its page (same as clicking it where it lives).
// ======================================================

public sealed partial class MainForm
{
    // The pages searched, and how each is named in the results.
    private static readonly Dictionary<string, string> FunctionPages = new()
    {
        ["Capture"] = "Capture",
        ["CaptureQuality"] = "Capture > Quality and output",
        ["Tools"] = "Tools",
        ["Text"] = "Text",
        ["Windows"] = "Windows",
        ["Learn"] = "Learn",
        ["Guide"] = "Guide",
        ["Modules"] = "Modules",
        ["Style"] = "Style",
        ["StyleColours"] = "Style > Colours",
        ["StyleLayout"] = "Style > Layout",
        ["StyleText"] = "Style > Font",
        ["StyleBackground"] = "Style > Background",
        ["SkinAssets"] = "Style > Skins",
        ["Notifications"] = "Style > Notifications",
        ["Setup"] = "Setup",
        ["Typing"] = "Setup > Typing",
        ["TypingStyles"] = "Setup > Typing > Styles",
        ["Shortcuts"] = "Setup > Shortcuts",
        ["Auto"] = "Tools > Autoclicker",
        ["About"] = "About",
        ["Updates"] = "About > Updates",
        ["UpdateLog"] = "About > Updates > Update log",
        ["System"] = "System"
    };

    // Other words people use for what a row is called.
    private static readonly Dictionary<string, string> FunctionSynonyms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["hotkey"] = "shortcut",
            ["hotkeys"] = "shortcut",
            ["keybind"] = "shortcut",
            ["keybinds"] = "shortcut",
            ["color"] = "colour",
            ["colors"] = "colours",
            ["autostart"] = "start with windows",
            ["startup"] = "start with windows",
            ["screenrecord"] = "record",
            ["wallpaper"] = "background",
            ["translate"] = "letter conversion",
            ["translator"] = "letter conversion",
            ["russian"] = "letter conversion"
        };

    // Every function with the place it lives in.
    private List<(string Path, MenuEntry Entry)> FunctionIndex()
    {
        List<(string, MenuEntry)> all = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void Add(string path, IEnumerable<MenuEntry> entries)
        {
            foreach (MenuEntry e in entries)
            {
                // Not rows that only show text, "Back", or do nothing.
                if (e.Index < 0 || e.command == "back" || string.IsNullOrWhiteSpace(e.Label))
                    continue;

                if (e.run == null && e.targetPage == null && e.command == null)
                    continue;

                if (seen.Add(path + "|" + e.Label))
                    all.Add((path, e));
            }
        }

        foreach ((string page, List<MenuEntry> entries) in pages)
        {
            if (FunctionPages.TryGetValue(page, out string? title))
            {
                Add(title, entries);
            }
            else if (page.StartsWith(ModulePrefix, StringComparison.Ordinal))
            {
                // Your own modules: "Modules > My tools > Open project folder".
                string name = modules.FirstOrDefault(m => ModulePrefix + m.Id == page)?.Name
                    ?? page[ModulePrefix.Length..];

                Add("Modules > " + name, entries);
            }
        }

        // These pages are built when opened.
        foreach ((string page, Func<List<MenuEntry>> build) in new (string, Func<List<MenuEntry>>)[]
                 {
                     ("Notifications", () => NotificationsPage("")),
                     ("Auto", () => AutoClickPage("")),
                     ("Shortcuts", () => ShortcutsPage(""))
                 })
        {
            try
            {
                Add(FunctionPages[page], build());
            }
            catch (Exception)
            {
                // A page that cannot be built just is not searched.
            }
        }

        return all;
    }

    // The functions that match what was typed: names first, then descriptions.
    // Their labels carry the place ("Setup > Slide-out side: Automatic").
    private List<MenuEntry> FunctionMatches(string query, int limit, ICollection<string?>? alreadyShown = null)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => FunctionSynonyms.TryGetValue(t, out string? same) ? same : t)
            .ToArray();

        if (terms.Length == 0)
            return new List<MenuEntry>();

        // 0 = the word starts with the term, 1 = it is only inside a word ("ping" in
        // "typing"), -1 = not there.
        static int Fit(string text, string term)
        {
            int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            int best = -1;

            while (at >= 0)
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                    return 0;

                best = 1;
                at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase);
            }

            return best;
        }

        List<(int Rank, string Path, MenuEntry Entry)> hits = new();

        foreach ((string path, MenuEntry e) in FunctionIndex())
        {
            // A page that is already in the results needs no second row.
            if (e.targetPage != null && alreadyShown != null && alreadyShown.Contains(e.targetPage))
                continue;

            string name = path + " " + e.Label;
            int[] inName = terms.Select(t => Fit(name, t)).ToArray();

            int rank;

            if (inName.All(f => f >= 0))
            {
                rank = e.Label.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : inName.All(f => f == 0) ? 1
                    : 2;
            }
            else if (e.expandText != null)
            {
                string full = name + " " + e.expandText;

                if (!terms.All(t => Fit(full, t) == 0))
                    continue;

                rank = 3;
            }
            else
            {
                continue;
            }

            hits.Add((rank, path, e));
        }

        return hits
            .OrderBy(h => h.Rank)
            .Take(limit)
            .Select(h => h.Entry with { Label = h.Path + " > " + h.Entry.Label })
            .ToList();
    }

    // home -> Functions: search everything.
    private List<MenuEntry> FunctionsPage(string query)
    {
        List<MenuEntry> list = new();

        if (query.Length == 0)
        {
            list.Add(Info("Type what you want to do: screenshot, dark mode, slide-out, ffmpeg..."));
        }
        else
        {
            List<MenuEntry> matches = FunctionMatches(query, 80);

            if (matches.Count == 0)
                list.Add(Info("No function matches that"));
            else
                list.AddRange(matches);
        }

        list.Add(BackEntry());

        return Renumber(list);
    }
}
