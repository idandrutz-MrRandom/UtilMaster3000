using System.IO;
using System.Text.Json;

namespace UtilMaster3000;

// ======================================================
// MODULES
// Your own entries in the launcher, with no programming and no rebuild:
// drop a small JSON file into %APPDATA%\UtilMaster3000\modules and press
// "Reload modules". Each file is one module (a group of entries); each entry
// opens something, runs a command or script, copies or pastes text, or shows a
// message. See Modules -> "How to write a module" in the launcher, or the README.
// ======================================================

/// <summary>One entry of a module.</summary>
internal sealed class ModuleItem
{
    /// <summary>What the row says.</summary>
    public string Label { get; set; } = "";

    /// <summary>open | url | command | script | copy | paste | message</summary>
    public string Type { get; set; } = "open";

    /// <summary>File, folder, address, app or command (depending on the type).</summary>
    public string? Target { get; set; }

    /// <summary>Extra arguments (open, command, script).</summary>
    public string? Args { get; set; }

    /// <summary>The text for copy, paste and message.</summary>
    public string? Text { get; set; }

    /// <summary>Folder the program starts in (default: the module's folder).</summary>
    public string? Folder { get; set; }

    /// <summary>command / script: keep the window open when it finishes.</summary>
    public bool Keep { get; set; }

    /// <summary>command / script: run without any window.</summary>
    public bool Hidden { get; set; }

    /// <summary>Shown when you rest on the row.</summary>
    public string? Info { get; set; }

    /// <summary>A Segoe MDL2 Assets glyph for the row, like "" (optional).</summary>
    public string? Icon { get; set; }
}

/// <summary>The contents of one module file.</summary>
internal sealed class ModuleFile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Icon { get; set; }
    public List<ModuleItem> Items { get; set; } = new();
}

/// <summary>A module found on disk (or the reason it could not be read).</summary>
internal sealed class LoadedModule
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Icon { get; init; }
    public string Folder { get; init; } = "";
    public string File { get; init; } = "";
    public List<ModuleItem> Items { get; init; } = new();
    public string? Error { get; init; }
}

internal static class Modules
{
    /// <summary>Tests: look somewhere else (a throw-away folder).</summary>
    public static string? FolderOverride { get; set; }

    public static string Folder => FolderOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000", "modules");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Every module: a folder with a module.json inside, or a loose .json file in the
    /// modules folder. A broken file shows up with its error instead of being skipped.
    /// </summary>
    public static List<LoadedModule> LoadAll()
    {
        List<LoadedModule> found = new();

        try
        {
            if (!Directory.Exists(Folder))
                return found;

            foreach (string dir in Directory.GetDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string file = Path.Combine(dir, "module.json");

                if (File.Exists(file))
                    found.Add(Load(file, dir, Path.GetFileName(dir)));
            }

            foreach (string file in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                found.Add(Load(file, Folder, Path.GetFileNameWithoutExtension(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be read: no modules.
        }

        return found;
    }

    private static LoadedModule Load(string file, string folder, string id)
    {
        try
        {
            ModuleFile data = JsonSerializer.Deserialize<ModuleFile>(File.ReadAllText(file), Options)
                ?? throw new JsonException("the file is empty");

            List<ModuleItem> items = data.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Label))
                .ToList();

            return new LoadedModule
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(data.Name) ? id : data.Name.Trim(),
                Description = data.Description ?? "",
                Icon = data.Icon,
                Folder = folder,
                File = file,
                Items = items
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LoadedModule
            {
                Id = id,
                Name = id,
                Folder = folder,
                File = file,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// The documented template. It lives next to (not inside) the modules folder, so it is
    /// never loaded as a module itself.
    /// </summary>
    public static string TemplatePath => Path.Combine(
        Path.GetDirectoryName(Folder.TrimEnd('\\', '/'))!, "module-template.json");

    /// <summary>Writes the template (fresh each time) and returns where it is.</summary>
    public static string WriteTemplate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
        File.WriteAllText(TemplatePath, TemplateText);
        return TemplatePath;
    }

    private const string TemplateText = """
        // =====================================================================
        //  UtilMaster3000 MODULE TEMPLATE
        //  A module adds your own entries to the launcher. No programming.
        //
        //  This file is written again every time you open it from the launcher,
        //  so COPY IT before you edit it.
        //
        //  HOW TO USE IT
        //   1. In the launcher open  Modules > Open the modules folder.
        //   2. Copy this file in there and rename it, for example  my-tools.json
        //      (or make a folder with any name and call the file  module.json
        //      inside it: then scripts can sit next to it).
        //   3. Keep the entries you want, delete the rest, change the words.
        //   4. In the launcher choose  Modules > Reload modules.
        //
        //  Lines starting with // are notes: they are ignored. Everything else
        //  must be valid JSON ("quotes" around words, commas between entries).
        //  A file with a mistake still shows up under Modules, with the reason.
        // =====================================================================
        {
          // The name shown under Modules, and a note for yourself (shown when you rest on it).
          "name": "My tools",
          "description": "What this module is for.",

          // Optional: a symbol for the module, written like "" (Segoe MDL2 Assets).
          // "icon": "",

          "items": [

            // ---------------------------------------------------------------
            //  EVERY ENTRY HAS
            //    "label"  what the row says (required)
            //    "type"   what it does (see below)
            //    "info"   optional: shown when you rest the pointer on the row
            //    "icon"   optional: a symbol for this row only
            //
            //  THE TYPES, AND THE FIELDS EACH ONE USES
            //    "open"     "target"  a folder, file, website or program to open
            //               "args"    optional extra arguments for a program
            //    "url"      "target"  a web address (same as open)
            //    "command"  "target"  a command, run in a console
            //               "keep"    true = the window stays open when it finishes
            //               "hidden"  true = no window at all
            //    "script"   "target"  a PowerShell file next to this module
            //               "args", "keep", "hidden" as for command
            //    "copy"     "text"    put this text on the clipboard
            //    "paste"    "text"    type this text into the window you came from
            //    "message"  "text"    show this text in a box
            //
            //  EXTRA FIELD FOR open, command AND script
            //    "folder"   the folder the program starts in (default: this module's folder)
            //
            //  WORDS YOU CAN USE INSIDE "target", "args", "folder" AND "text"
            //    {clipboard}      what is on the clipboard now
            //    {clipboard-url}  the same, ready to go inside a web address
            //    {date}           today, like 2026-11-21
            //    {time}           now, like 14:05
            //    {folder}         the folder this module is in
            //    %USERPROFILE%    any Windows variable, like %APPDATA% or %TEMP%
            // ---------------------------------------------------------------

            {
              "label": "Open a folder",
              "type": "open",
              "target": "%USERPROFILE%\\Documents",
              "info": "Opens the folder in Explorer. A file, a program or a website works too."
            },
            {
              "label": "Open a program with a file",
              "type": "open",
              "target": "notepad.exe",
              "args": "%USERPROFILE%\\notes.txt"
            },
            {
              "label": "Search the web for what I copied",
              "type": "url",
              "target": "https://www.google.com/search?q={clipboard-url}"
            },
            {
              "label": "Show my IP configuration",
              "type": "command",
              "target": "ipconfig /all",
              "keep": true
            },
            {
              "label": "Run a script from this module's folder",
              "type": "script",
              "target": "my-script.ps1",
              "args": "first second",
              "keep": true
            },
            {
              "label": "Copy today's date",
              "type": "copy",
              "text": "{date}"
            },
            {
              "label": "Paste my signature",
              "type": "paste",
              "text": "Best regards,\nYour Name"
            },
            {
              "label": "Show a reminder",
              "type": "message",
              "text": "Drink some water."
            }
          ]
        }
        """;

    /// <summary>Writes a small working example (and a script it uses) and returns its folder.</summary>
    public static string CreateExample()
    {
        string folder = Path.Combine(Folder, "example");
        Directory.CreateDirectory(folder);

        string manifest = Path.Combine(folder, "module.json");

        if (!File.Exists(manifest))
            File.WriteAllText(manifest, ExampleManifest);

        string script = Path.Combine(folder, "hello.ps1");

        if (!File.Exists(script))
            File.WriteAllText(script, ExampleScript);

        return folder;
    }

    private const string ExampleScript =
        "# A script a module can run (type \"script\"). Arguments arrive as $args.\r\n" +
        "Write-Host \"Hello from a UtilMaster3000 module!\"\r\n" +
        "Write-Host \"Today is $(Get-Date -Format D)\"\r\n" +
        "Write-Host \"Arguments: $args\"\r\n";

    private const string ExampleManifest = """
        {
          // The name shown in the launcher, and what it is for.
          "name": "Example module",
          "description": "Shows every kind of entry. Edit this file, then press Modules -> Reload modules.",

          "items": [
            {
              // open: a folder, file, website or program
              "label": "Open my Downloads folder",
              "type": "open",
              "target": "%USERPROFILE%\\Downloads",
              "info": "Opens the folder in Explorer."
            },
            {
              // url: {clipboard-url} becomes whatever you copied, ready for a web address
              "label": "Search the web for the clipboard",
              "type": "url",
              "target": "https://www.google.com/search?q={clipboard-url}"
            },
            {
              // command: runs in a console; keep leaves the window open
              "label": "Show my IP configuration",
              "type": "command",
              "target": "ipconfig /all",
              "keep": true
            },
            {
              // script: a PowerShell file next to this one
              "label": "Run the hello script",
              "type": "script",
              "target": "hello.ps1",
              "args": "from the example",
              "keep": true
            },
            {
              // copy: puts text on the clipboard. {date} and {time} fill themselves in
              "label": "Copy today's date",
              "type": "copy",
              "text": "{date}"
            },
            {
              // paste: types the text into the window you came from
              "label": "Paste my email signature",
              "type": "paste",
              "text": "Best regards,\nYour Name"
            },
            {
              // message: shows a note
              "label": "Show a reminder",
              "type": "message",
              "text": "Drink some water."
            }
          ]
        }
        """;
}
