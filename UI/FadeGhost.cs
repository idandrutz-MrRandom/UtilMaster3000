using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

/// <summary>
/// A picture of a window shown as a click-through overlay whose see-through level is changed cheaply
/// (UpdateLayeredWindow with a new alpha, no redrawing): the basis of the open and close fades.
/// Windows composes it on the graphics card, so even a very large window fades smoothly.
/// </summary>
internal sealed class FadeGhost : Form
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int W, H; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr window, IntPtr screenDc, ref POINT dst, ref SIZE size, IntPtr sourceDc,
        ref POINT source, int colourKey, ref BLENDFUNCTION blend, int flags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    private const int ULW_ALPHA = 2;

    private IntPtr memoryDc, bitmap, oldBitmap;
    private POINT where;
    private SIZE size;

    private FadeGhost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;

            // layered, click-through, never activated, not in Alt+Tab
            cp.ExStyle |= 0x00080000 | 0x00000020 | 0x08000000 | 0x00000080;

            return cp;
        }
    }

    /// <summary>A photograph of what is on screen in the area, shown at full strength.</summary>
    public static FadeGhost? TryFromScreen(Rectangle area)
    {
        try
        {
            if (area.Width < 8 || area.Height < 8)
                return null;

            using Bitmap photo = new(area.Width, area.Height, PixelFormat.Format32bppArgb);

            using (Graphics g = Graphics.FromImage(photo))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);

            return TryFromBitmap(photo, area.Location, 255);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The given picture, shown at the given position and strength (0 = invisible).</summary>
    public static FadeGhost? TryFromBitmap(Bitmap picture, Point at, byte alpha)
    {
        FadeGhost? ghost = null;

        try
        {
            ghost = new FadeGhost();
            ghost.where = new POINT { X = at.X, Y = at.Y };
            ghost.size = new SIZE { W = picture.Width, H = picture.Height };

            IntPtr screen = GetDC(IntPtr.Zero);

            try
            {
                ghost.memoryDc = CreateCompatibleDC(screen);
                ghost.bitmap = picture.GetHbitmap(Color.Black);
                ghost.oldBitmap = SelectObject(ghost.memoryDc, ghost.bitmap);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            _ = ghost.Handle;
            ghost.SetAlpha(alpha);
            ghost.Show();

            return ghost;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            ghost?.Dispose();
            return null;
        }
    }

    public void SetAlpha(byte alpha)
    {
        if (!IsHandleCreated || memoryDc == IntPtr.Zero)
            return;

        POINT source = new();
        BLENDFUNCTION blend = new() { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };

        IntPtr screen = GetDC(IntPtr.Zero);

        try
        {
            UpdateLayeredWindow(Handle, screen, ref where, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (memoryDc != IntPtr.Zero)
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            memoryDc = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }
}
