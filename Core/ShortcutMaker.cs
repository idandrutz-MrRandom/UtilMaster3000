using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.IO;

namespace UtilMaster3000;

// ======================================================
// SHORTCUT MAKER  (Tools > Create shortcut)
// Puts a shortcut (.lnk) to an app from the app index on the Desktop (or any folder).
//  * Programs with a Start Menu shortcut: that shortcut is copied, so icon, options and
//    "run as" settings stay exactly as the installer made them.
//  * Store apps: a new shortcut that opens the app through Explorer, with the app's icon.
// ======================================================

internal static class ShortcutMaker
{
    public static string DesktopFolder =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Creates the shortcut and returns its path. Never overwrites: a number is added instead.</summary>
    public static string Create(AppEntry app, string? folder = null)
    {
        folder ??= DesktopFolder;
        Directory.CreateDirectory(folder);

        string path = FreeName(folder, CleanName(app.Name));

        if (!app.IsStoreApp)
        {
            if (!File.Exists(app.Target))
                throw new FileNotFoundException("the app's own shortcut is gone: " + app.Target);

            File.Copy(app.Target, path);
            return path;
        }

        string explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        string? icon = SaveIcon(app);

        MakeLink(path, explorer, "shell:AppsFolder\\" + app.Target, icon != null ? icon + ",0" : explorer + ",0");

        return path;
    }

    /// <summary>A shortcut that runs a program (used by the tests too).</summary>
    public static void MakeLink(string path, string target, string arguments, string iconLocation)
    {
        Type? type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows' shortcut maker is not available");

        dynamic shell = Activator.CreateInstance(type)!;

        try
        {
            dynamic link = shell.CreateShortcut(path);

            link.TargetPath = target;
            link.Arguments = arguments;
            link.IconLocation = iconLocation;
            link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static string CleanName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');

        name = name.Trim().TrimEnd('.');

        return name.Length == 0 ? "Shortcut" : name;
    }

    private static string FreeName(string folder, string name)
    {
        string path = Path.Combine(folder, name + ".lnk");

        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}).lnk");

        return path;
    }

    // The app's icon as a .ico (a PNG inside an ICO file, which Windows reads fine).
    private static string? SaveIcon(AppEntry app)
    {
        try
        {
            using Bitmap? bitmap = IconCache.Load("shell:AppsFolder\\" + app.Target);

            if (bitmap == null)
                return null;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UtilMaster3000", "shortcut-icons");

            Directory.CreateDirectory(folder);

            string file = Path.Combine(folder, CleanName(app.Name) + ".ico");

            using MemoryStream png = new();
            bitmap.Save(png, ImageFormat.Png);

            using FileStream stream = File.Create(file);
            using BinaryWriter w = new(stream);

            w.Write((short)0);                                   // reserved
            w.Write((short)1);                                   // type: icon
            w.Write((short)1);                                   // one image
            w.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            w.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);                                   // planes
            w.Write((short)32);                                  // bits per pixel
            w.Write((int)png.Length);
            w.Write(22);                                         // where the image starts
            w.Write(png.ToArray());

            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException)
        {
            return null;
        }
    }
}
