using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UtilMaster3000;

/// <summary>
/// The program's icons, drawn in the menu's current colours (see IconArt). They are redrawn when
/// the theme changes; the file's own icon (Assets/app.ico) is the Tokyo Night version.
/// </summary>
internal static class AppIcon
{
    private static readonly Dictionary<(IconKind, Size, IconPalette), Icon> Cache = new();

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>The menu's colours (a background picture tints the tile).</summary>
    public static IconPalette MenuPalette()
    {
        Color tile = Theme.HasBackground
            ? Theme.AverageBehind(new Rectangle(0, 0, 320, 320), new Size(320, 320))
            : Theme.Background;

        return new IconPalette(tile, Theme.Search, Theme.Text, Theme.Accent, Theme.Strong);
    }

    /// <summary>UtilTube's colours (the menu's, unless UtilTube has a look of its own).</summary>
    public static IconPalette TubePalette()
    {
        Color tile = TubeGlass.Active
            ? Theme.AverageBehind(new Rectangle(0, 0, 320, 320), new Size(320, 320))
            : TubeTheme.Background;

        return new IconPalette(tile, TubeTheme.Search, TubeTheme.Text, TubeTheme.Accent, TubeTheme.Strong);
    }

    public static Icon Get(IconKind kind, Size size)
    {
        IconPalette palette = kind == IconKind.App ? MenuPalette() : TubePalette();

        lock (Cache)
        {
            if (Cache.TryGetValue((kind, size, palette), out Icon? known))
                return known;

            // Only the current colours are kept (old icons may still be in use by a window, so they are
            // left to the garbage collector rather than destroyed).
            Cache.Clear();

            Icon made = Make(kind, size, palette);
            Cache[(kind, size, palette)] = made;

            return made;
        }
    }

    private static Icon Make(IconKind kind, Size size, IconPalette palette)
    {
        try
        {
            using Bitmap picture = IconArt.Render(kind, Math.Max(size.Width, 16), palette);

            IntPtr handle = picture.GetHicon();

            try
            {
                using Icon temporary = Icon.FromHandle(handle);
                return (Icon)temporary.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            return SystemIcons.Application;
        }
    }
}
