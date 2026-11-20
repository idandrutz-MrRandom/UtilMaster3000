using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace UtilMaster3000;

// ======================================================
// ICON CACHE
// Real icons for apps / windows / files, loaded on a
// background STA thread so the list never waits for them.
//
// Path forms:
//   "C:\...\app.exe"            -> that file's own icon
//   "C:\...\report.pdf"         -> the icon for the .pdf type
//   "dir:"                      -> generic folder icon
//   "shell:AppsFolder\<AppID>"  -> a Store / packaged app icon
// ======================================================

internal static class IconCache
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGFI_PIDL = 0x8;

    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // These types carry their own icon, so they are cached per file.
    private static readonly HashSet<string> OwnIcon = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".msc", ".cpl", ".scr", ".appref-ms"
    };

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly BlockingCollection<(string Path, Action<Bitmap?> Done)>
        Queue = new();

    static IconCache()
    {
        Thread worker = new(Worker)
        {
            IsBackground = true,
            Name = "IconLoader"
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static string KeyFor(string path)
    {
        if (path == "dir:" ||
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        string ext = Path.GetExtension(path);

        return OwnIcon.Contains(ext)
            ? path
            : "ext:" + ext.ToLowerInvariant();
    }

    /// <summary>True when already loaded (bitmap may be null = no icon).</summary>
    public static bool TryGet(string path, out Bitmap? bitmap) =>
        Cache.TryGetValue(KeyFor(path), out bitmap);

    /// <summary>Loads in the background; <paramref name="done"/> runs on the worker thread.</summary>
    public static void Request(string path, Action<Bitmap?> done) =>
        Queue.Add((path, done));

    private static void Worker()
    {
        foreach (var (path, done) in Queue.GetConsumingEnumerable())
        {
            string key = KeyFor(path);

            if (!Cache.TryGetValue(key, out Bitmap? bitmap))
            {
                try
                {
                    bitmap = Load(path);
                }
                catch
                {
                    bitmap = null;
                }

                Cache[key] = bitmap;
            }

            try
            {
                done(bitmap);
            }
            catch
            {
                // The row may be gone already.
            }
        }
    }

    public static Bitmap? Load(string path)
    {
        SHFILEINFO info = new();
        uint size = (uint)Marshal.SizeOf<SHFILEINFO>();

        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 ||
                pidl == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                SHGetFileInfoPidl(
                    pidl, 0, ref info, size, SHGFI_PIDL | SHGFI_ICON);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        else if (path == "dir:")
        {
            SHGetFileInfo(
                "folder", FILE_ATTRIBUTE_DIRECTORY, ref info, size,
                SHGFI_USEFILEATTRIBUTES | SHGFI_ICON);
        }
        else if (OwnIcon.Contains(Path.GetExtension(path)))
        {
            SHGetFileInfo(path, 0, ref info, size, SHGFI_ICON);
        }
        else
        {
            // Type icon only: does not touch the disk.
            SHGetFileInfo(
                "file" + Path.GetExtension(path), FILE_ATTRIBUTE_NORMAL,
                ref info, size, SHGFI_USEFILEATTRIBUTES | SHGFI_ICON);
        }

        return FromHandle(info.hIcon);
    }

    private static Bitmap? FromHandle(IntPtr icon)
    {
        if (icon == IntPtr.Zero)
            return null;

        try
        {
            using Icon managed = Icon.FromHandle(icon);
            using Bitmap converted = managed.ToBitmap();

            return new Bitmap(converted);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(
        string path, uint attributes, ref SHFILEINFO info,
        uint size, uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfoPidl(
        IntPtr pidl, uint attributes, ref SHFILEINFO info,
        uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindContext, out IntPtr pidl,
        uint attributesIn, out uint attributesOut);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
