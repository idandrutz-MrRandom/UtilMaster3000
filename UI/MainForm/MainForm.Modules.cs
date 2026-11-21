using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// MODULES PAGE  (home -> Modules)
// The user's own entries (see Core/Modules.cs). Every module is a page of its
// own; its entries also turn up in the function search.
// ======================================================

public sealed partial class MainForm
{
    private List<LoadedModule> modules = new();

    private const string ModulePrefix = "Module:";

    private void LoadModules() => modules = Modules.LoadAll();

    // The Modules page, and one page per module (called from BuildPages).
    private void AddModulePages(Dictionary<string, List<MenuEntry>> target)
    {
        target["Modules"] = BuildModulesPage();

        foreach (LoadedModule m in modules)
            target[ModulePrefix + m.Id] = BuildModulePage(m);
    }

    private List<MenuEntry> BuildModulesPage()
    {
        List<MenuEntry> list = new()
        {
            Info("Your own tools. Only add modules you trust: they run what their author wrote.")
        };

        foreach (LoadedModule m in modules)
        {
            string count = m.Error != null ? "has an error" : $"{m.Items.Count} entries";

            list.Add(new MenuEntry(0, m.Icon ?? "", $"{m.Name}  ({count})",
                targetPage: ModulePrefix + m.Id,
                expandText: (m.Description.Length > 0 ? m.Description + NL : "") + m.File));
        }

        if (modules.Count == 0)
            list.Add(Info("No modules yet. Create the example below, then edit it."));

        list.Add(Act("", "See module template", OpenModuleTemplate));
        list.Add(Act("", "Create an example module", CreateExampleModule));
        list.Add(Act("", "Open the modules folder", OpenModulesFolder));
        list.Add(Act("", "Reload modules", ReloadModules));
        list.Add(Act("", "How to write a module", ShowModuleHelp));
        list.Add(BackEntry());

        return Renumber(list);
    }

    private List<MenuEntry> BuildModulePage(LoadedModule m)
    {
        List<MenuEntry> list = new();

        if (m.Error != null)
        {
            list.Add(Info("This module could not be read:"));
            list.Add(Info(Trunc(m.Error, 120)));
            list.Add(Act("", "Open its file", () => OpenFolder(m.Folder)));
        }

        foreach (ModuleItem item in m.Items)
        {
            ModuleItem captured = item;

            list.Add(new MenuEntry(0, item.Icon ?? ModuleGlyph(item), item.Label,
                run: () => RunModuleItem(m, captured),
                expandText: item.Info));
        }

        if (m.Error == null && m.Items.Count == 0)
            list.Add(Info("This module has no entries yet."));

        list.Add(BackEntry());

        return Renumber(list);
    }

    // A glyph that fits what the entry does.
    private static string ModuleGlyph(ModuleItem item) =>
        item.Type.ToLowerInvariant() switch
        {
            "url" => "",
            "command" => "",
            "script" => "",
            "copy" => "",
            "paste" => "",
            "message" => "",
            _ => ""
        };

    // {clipboard}, {clipboard-url}, {date}, {time}, {folder} and %ENV% variables.
    private string FillModuleText(string? text, LoadedModule m)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        string result = text;

        if (result.Contains("{clipboard"))
        {
            string clip = GetClipboardText() ?? "";

            result = result
                .Replace("{clipboard-url}", Uri.EscapeDataString(clip.Trim()))
                .Replace("{clipboard}", clip);
        }

        return Environment.ExpandEnvironmentVariables(result
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH:mm"))
            .Replace("{folder}", m.Folder));
    }

    private void RunModuleItem(LoadedModule m, ModuleItem item)
    {
        string type = item.Type.Trim().ToLowerInvariant();

        string workDir = FillModuleText(item.Folder, m);

        if (workDir.Length == 0 || !Directory.Exists(workDir))
            workDir = m.Folder;

        switch (type)
        {
            case "open":
            case "url":
                if (string.IsNullOrWhiteSpace(item.Target))
                {
                    Toast("This entry has no \"target\"", OffColor);
                    return;
                }

                Launch(() => Process.Start(new ProcessStartInfo(FillModuleText(item.Target, m))
                {
                    Arguments = FillModuleText(item.Args, m),
                    WorkingDirectory = workDir,
                    UseShellExecute = true
                }));
                break;

            case "command":
                if (string.IsNullOrWhiteSpace(item.Target))
                {
                    Toast("This entry has no \"target\" (the command)", OffColor);
                    return;
                }

                Launch(() => Process.Start(new ProcessStartInfo("cmd.exe",
                    (item.Keep ? "/k " : "/c ") + FillModuleText(item.Target, m) +
                    (string.IsNullOrWhiteSpace(item.Args) ? "" : " " + FillModuleText(item.Args, m)))
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = !item.Hidden,
                    CreateNoWindow = item.Hidden
                }));
                break;

            case "script":
            {
                if (string.IsNullOrWhiteSpace(item.Target))
                {
                    Toast("This entry has no \"target\" (the script file)", OffColor);
                    return;
                }

                string file = Path.IsPathRooted(item.Target)
                    ? item.Target
                    : Path.Combine(m.Folder, FillModuleText(item.Target, m));

                if (!File.Exists(file))
                {
                    Toast("Script not found: " + Path.GetFileName(file), OffColor);
                    return;
                }

                Launch(() => Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass " + (item.Keep ? "-NoExit " : "") +
                    $"-File \"{file}\" " + FillModuleText(item.Args, m))
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = !item.Hidden,
                    CreateNoWindow = item.Hidden
                }));
                break;
            }

            case "copy":
            {
                string text = FillModuleText(item.Text, m);

                if (SetClipboard(text))
                    Toast($"Copied: {Trunc(text.Replace('\n', ' '), 40)}", OnColor);

                break;
            }

            case "paste":
                if (SetClipboard(FillModuleText(item.Text, m)))
                {
                    HideToTray();
                    PasteIntoPrevious();
                }

                break;

            case "message":
                Say(item.Label, FillModuleText(item.Text, m));
                break;

            default:
                Toast($"Unknown type \"{item.Type}\" (use open, url, command, script, copy, paste or message)", OffColor);
                break;
        }
    }

    // Opens the documented template in Notepad: every field, every type, what each does.
    private void OpenModuleTemplate()
    {
        try
        {
            string path = Modules.WriteTemplate();

            Launch(() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast("Could not open the template: " + Trunc(ex.Message, 50), OffColor);
        }
    }

    private void ReloadModules()
    {
        LoadModules();
        RefreshPage();
        Toast($"{modules.Count} module{(modules.Count == 1 ? "" : "s")} loaded", OnColor);
    }

    private void OpenModulesFolder()
    {
        try
        {
            Directory.CreateDirectory(Modules.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast("Could not open the modules folder", OffColor);
            return;
        }

        OpenFolder(Modules.Folder);
    }

    private void CreateExampleModule()
    {
        try
        {
            string folder = Modules.CreateExample();

            LoadModules();
            RefreshPage();
            Toast("Example module created", OnColor);
            OpenFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast("Could not create the example: " + Trunc(ex.Message, 50), OffColor);
        }
    }

    private static void ShowModuleHelp() =>
        Say("How to write a module",
            "A module is a small text file. Put it in the modules folder (Modules > Open the modules folder), " +
            "then choose Modules > Reload modules.\n\n" +
            "Either a folder with a file called module.json inside, or a loose .json file.\n\n" +
            "Each entry has a \"label\" and a \"type\":\n" +
            "  open     a folder, file, website or program (\"target\")\n" +
            "  url      same, for web addresses\n" +
            "  command  runs a command in a console (\"keep\": true leaves it open)\n" +
            "  script   runs a PowerShell file next to the module\n" +
            "  copy     puts \"text\" on the clipboard\n" +
            "  paste    types \"text\" into the window you came from\n" +
            "  message  shows \"text\" in a box\n\n" +
            "Handy words inside the text: {clipboard}, {clipboard-url}, {date}, {time}, {folder} and %USERPROFILE%.\n\n" +
            "\"Create an example module\" writes a file that uses every type. Your entries also show up when you search.\n\n" +
            "Modules run what their author wrote, so only add ones you trust.");
}
