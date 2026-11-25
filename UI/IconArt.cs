using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace UtilMaster3000;

internal enum IconKind
{
    /// <summary>UtilMaster3000: a multi-tool (pliers, blade, pivot screw) as line art.</summary>
    App,

    /// <summary>UtilTube: a video screen with a screw for the progress knob, as line art.</summary>
    Tube
}

/// <summary>The colours an icon is drawn in (taken from the menu's theme).</summary>
internal readonly record struct IconPalette(Color Tile, Color Dark, Color Muted, Color Accent, Color Strong);

/// <summary>
/// Draws the program's icons in the launcher's look: a flat dark panel with a thick solid border and
/// hard 45-degree corners, thin line-art glyphs in the accent colour, and a flat tinted fill like the
/// menu's highlighted row. No gradients, no shadows. Only System.Drawing is used so the same code can
/// also make the .ico built into the exe.
/// </summary>
internal static class IconArt
{
    private static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>A rectangle with its corners cut at 45 degrees.</summary>
    private static GraphicsPath Chamfer(RectangleF r, float cut)
    {
        cut = Math.Min(cut, Math.Min(r.Width, r.Height) / 2.2f);

        GraphicsPath path = new();

        path.AddPolygon(new[]
        {
            new PointF(r.Left + cut, r.Top), new PointF(r.Right - cut, r.Top),
            new PointF(r.Right, r.Top + cut), new PointF(r.Right, r.Bottom - cut),
            new PointF(r.Right - cut, r.Bottom), new PointF(r.Left + cut, r.Bottom),
            new PointF(r.Left, r.Bottom - cut), new PointF(r.Left, r.Top + cut)
        });

        return path;
    }

    /// <summary>The icon as a square picture of the given size.</summary>
    public static Bitmap Render(IconKind kind, int size, IconPalette p)
    {
        // Drawn large and scaled down, so small sizes stay smooth.
        int big = Math.Max(512, size * 2);
        float u = big / 100f;

        using Bitmap canvas = new(big, big, PixelFormat.Format32bppPArgb);

        using (Graphics g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            bool tiny = size <= 24;

            DrawTile(g, u, p);

            if (kind == IconKind.App)
                DrawToolkit(g, u, p, tiny);
            else
                DrawTube(g, u, p, tiny);
        }

        Bitmap result = new(size, size, PixelFormat.Format32bppPArgb);

        using (Graphics g = Graphics.FromImage(result))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(canvas, 0, 0, size, size);
        }

        return result;
    }

    // ---- the panel: flat, a thick solid border like the menu's, small 45-degree corners ----

    private static void DrawTile(Graphics g, float u, IconPalette p)
    {
        RectangleF bounds = new(3 * u, 3 * u, 94 * u, 94 * u);

        using GraphicsPath tile = Chamfer(bounds, 6.5f * u);

        using (SolidBrush fill = new(p.Tile))
            g.FillPath(fill, tile);

        // The menu draws its border in the accent colour, a little subdued.
        using GraphicsPath inner = Chamfer(RectangleF.Inflate(bounds, -2.2f * u, -2.2f * u), 4.6f * u);

        using Pen border = new(Mix(p.Tile, p.Accent, 0.62f), 4.4f * u) { LineJoin = LineJoin.Miter };
        g.DrawPath(border, inner);
    }

    // ---- line-art helpers: thin strokes with sharp joins, like the menu's glyphs ----

    private static Pen Line(Color colour, float width) => new(colour, width)
    {
        LineJoin = LineJoin.Miter,
        StartCap = LineCap.Flat,
        EndCap = LineCap.Flat,
        MiterLimit = 4f
    };

    private static void Rotated(Graphics g, float u, float px, float py, float angle, Action draw)
    {
        GraphicsState state = g.Save();

        g.TranslateTransform(px * u, py * u);
        g.RotateTransform(angle);
        g.TranslateTransform(-px * u, -py * u);

        draw();

        g.Restore(state);
    }

    // ---- UtilMaster3000: an opened multi-tool, as an outline ----

    private static void DrawToolkit(Graphics g, float u, IconPalette p, bool tiny)
    {
        // Pliers jaws to the top right, two handles to the bottom left, a pivot between them and a
        // blade swinging out, drawn along a straight axis (pivot at 0,0) and then turned and made
        // smaller so it sits comfortably inside the panel.
        GraphicsState whole = g.Save();

        g.TranslateTransform(54 * u, 49 * u);
        g.RotateTransform(-40);
        g.ScaleTransform(0.72f, 0.72f);

        float w = tiny ? 5.2f : 3.6f;                       // stroke, in the scaled drawing
        Color tint = Mix(p.Tile, p.Accent, 0.2f);           // the highlighted row's flat tint
        Color steelTint = Mix(p.Tile, p.Strong, 0.1f);

        using SolidBrush tintBrush = new(tint);
        using SolidBrush steelBrush = new(steelTint);
        using Pen accent = Line(p.Accent, w * u);
        using Pen steel = Line(p.Strong, w * u);

        // The blade first, so the handle covers its root.
        if (!tiny)
        {
            using GraphicsPath blade = new();

            blade.AddPolygon(new[]
            {
                new PointF(-44 * u, -6 * u), new PointF(-44 * u, -22 * u), new PointF(-34 * u, -35 * u),
                new PointF(-34 * u, -6 * u)
            });

            g.FillPath(steelBrush, blade);
            g.DrawPath(steel, blade);
        }

        // Pliers jaws.
        void Jaw(float angle, int side)
        {
            Rotated(g, u, 0, 0, angle, () =>
            {
                using GraphicsPath jaw = new();

                jaw.AddPolygon(new PointF[]
                {
                    new(0, 0), new(34 * u, 0), new(40 * u, side * 2.6f * u), new(34 * u, side * 5.4f * u),
                    new(0, side * 5.4f * u)
                });

                g.FillPath(steelBrush, jaw);
                g.DrawPath(steel, jaw);
            });
        }

        Jaw(-10f, -1);
        Jaw(10f, 1);

        // Two handles, apart like an opened tool.
        void Handle(float angle)
        {
            Rotated(g, u, 0, 0, angle, () =>
            {
                using GraphicsPath handle = Chamfer(new RectangleF(-52 * u, -7 * u, 48 * u, 14 * u), 3.4f * u);

                g.FillPath(tintBrush, handle);
                g.DrawPath(accent, handle);

                if (!tiny)
                {
                    using SolidBrush dot = new(p.Accent);
                    g.FillEllipse(dot, -46.4f * u, -2.4f * u, 4.8f * u, 4.8f * u);
                }
            });
        }

        Handle(-20f);
        Handle(20f);

        // The pivot: a ring with a slotted screw.
        using (SolidBrush solid = new(p.Tile))
            g.FillEllipse(solid, -10.5f * u, -10.5f * u, 21 * u, 21 * u);

        g.DrawEllipse(accent, -10.5f * u, -10.5f * u, 21 * u, 21 * u);

        using (Pen slot = Line(p.Strong, (w * 0.85f) * u))
        {
            slot.StartCap = LineCap.Square;
            slot.EndCap = LineCap.Square;

            Rotated(g, u, 0, 0, 45, () => g.DrawLine(slot, -4.2f * u, 0, 4.2f * u, 0));
        }

        g.Restore(whole);
    }

    // ---- UtilTube: a screen, a play triangle and a screw as the progress knob, as an outline ----

    private static void DrawTube(Graphics g, float u, IconPalette p, bool tiny)
    {
        float w = tiny ? 5.2f : 4f;

        using Pen accent = Line(p.Accent, w * u);
        using Pen faint = Line(Mix(p.Tile, p.Accent, 0.35f), w * u);
        using SolidBrush tint = new(Mix(p.Tile, p.Accent, 0.2f));
        using SolidBrush solidAccent = new(p.Accent);

        // The screen.
        RectangleF screen = new(20 * u, 24 * u, 60 * u, 36 * u);

        using (GraphicsPath box = Chamfer(screen, 4.5f * u))
        {
            g.FillPath(tint, box);
            g.DrawPath(accent, box);
        }

        // The play triangle (flat, solid).
        using (GraphicsPath triangle = new())
        {
            triangle.AddPolygon(new PointF[] { new(44 * u, 33 * u), new(44 * u, 51 * u), new(60 * u, 42 * u) });
            g.FillPath(solidAccent, triangle);
        }

        // The progress bar: a faint track, the part played, and a screw as the knob.
        float y = 72 * u;

        g.DrawLine(faint, 20 * u, y, 80 * u, y);
        g.DrawLine(accent, 20 * u, y, 56 * u, y);

        using (SolidBrush solid = new(p.Tile))
            g.FillEllipse(solid, 48.5f * u, y - 7.5f * u, 15 * u, 15 * u);

        g.DrawEllipse(accent, 48.5f * u, y - 7.5f * u, 15 * u, 15 * u);

        using Pen slot = Line(p.Strong, w * 0.8f * u);
        slot.StartCap = LineCap.Square;
        slot.EndCap = LineCap.Square;

        Rotated(g, u, 56, 72, 45, () => g.DrawLine(slot, 52.8f * u, y, 59.2f * u, y));
    }
}
