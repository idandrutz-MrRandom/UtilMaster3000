using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UtilMaster3000;

// ======================================================
// CREATE SHORTCUT PAGE  (Tools > Create shortcut)
// Search an app, click it, and its shortcut appears on the Desktop.
// ======================================================

public sealed partial class MainForm
{
    private List<MenuEntry> MakeShortcutPage(string query)
    {
        List<MenuEntry> list = new();

        if (query.Length == 0)
        {
            list.Add(Info("Type an app's name, then click it to put its shortcut on your Desktop."));

            if (!AppIndex.Ready)
                list.Add(Info("Indexing apps..."));

            list.Add(new MenuEntry(0, "", "Open the Desktop folder",
                run: () => OpenFolder(ShortcutMaker.DesktopFolder)));

            list.Add(BackEntry());
        }
        else
        {
            list.AddRange(AppIndex.Search(query, 40).Select(ShortcutRow));

            if (list.Count == 0)
                list.Add(Info(AppIndex.Ready ? "No apps found" : "Indexing apps..."));
        }

        return Renumber(list);
    }

    private MenuEntry ShortcutRow(AppEntry app) =>
        ActIcon("", app.Name,
            app.IsStoreApp ? "shell:AppsFolder\\" + app.Target : app.Target,
            () => CreateAppShortcut(app));

    private void CreateAppShortcut(AppEntry app)
    {
        try
        {
            string path = ShortcutMaker.Create(app);

            Toast($"Shortcut created on the Desktop: {Path.GetFileNameWithoutExtension(path)}", OnColor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException
                                       or System.Runtime.InteropServices.COMException)
        {
            Toast("Could not create the shortcut: " + Trunc(ex.Message, 50), OffColor);
        }
    }
}
