using System.Drawing;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// UTILTUBE'S OWN LOOK
// UtilTube (its window, settings and video player) has colours of its own, set in
// UtilTube -> Settings -> Look. "Same as the launcher" makes it follow the menu's
// style like before. Everything in UtilTube reads its colours from here.
// ======================================================

internal static class TubeTheme
{
    /// <summary>A named palette (hex colours); preset 0 follows the launcher.</summary>
    public sealed record Preset(
        string Name, string Background, string Panel, string Row,
        string Accent, string Text, string Strong);

    public static readonly Preset[] Presets =
    {
        new("Same as the launcher", "", "", "", "", "", ""),
        new("Dark",         "#0f0f0f", "#1b1b1b", "#272727", "#ff3d3d", "#aaaaaa", "#ffffff"),
        new("Midnight",     "#0b1020", "#131a2e", "#1d2744", "#5b8def", "#9aa7c7", "#f3f6ff"),
        new("Deep purple",  "#120d1c", "#1b1428", "#291d3d", "#b07cff", "#b5a8cc", "#faf6ff"),
        new("Forest",       "#0c1510", "#14211a", "#1f3428", "#3ddc84", "#9db8a8", "#f2fff7"),
        new("Warm",         "#1a1410", "#261d17", "#37291f", "#ffb454", "#c4b19a", "#fff7ec"),
        new("Black (OLED)", "#000000", "#0b0b0b", "#171717", "#ff5252", "#9a9a9a", "#ffffff"),
        new("Light",        "#f6f6f7", "#ffffff", "#e6e8ee", "#d32f2f", "#555b66", "#111317")
    };

    /// <summary>Raised after the look changed: pairs of (old colour, new colour) so open windows can follow.</summary>
    public static event Action<List<(Color Old, Color New)>>? Changed;

    private static int preset;
    // Colours picked by hand (Empty = from the preset).
    private static Color ownBackground = Color.Empty;
    private static Color ownPanel = Color.Empty;
    private static Color ownText = Color.Empty;
    private static Color ownAccent = Color.Empty;

    private static Color background, panel, row, accent, text, strong;
    private static bool ready;

    // ---- the colours everything uses ----

    public static Color Background => Value(() => Theme.Background, background);
    public static Color Search => Value(() => Theme.Search, panel);       // cards, bars, inputs
    public static Color ActiveRow => Value(() => Theme.ActiveRow, row);   // selection, hover
    public static Color Accent => Value(() => Theme.Accent, accent);
    public static Color Text => Value(() => Theme.Text, text);
    public static Color Strong => Value(() => Theme.Strong, strong);
    public static Color On => Theme.On;
    public static Color Off => Theme.Off;
    public static Color Number => Mix(Text, Background, 0.45f);

    public static Color Mix(Color a, Color b, float t) => Theme.Mix(a, b, t);

    /// <summary>
    /// A flat button's hover and pressed colours: its own colour moved a little toward the text
    /// colour. (Windows works them out itself, and for a see-through button that is white, and
    /// for a light accent button a washed-out near-white.)
    /// </summary>
    public static void StyleHover(Button button)
    {
        if (button.FlatStyle != FlatStyle.Flat)
            return;

        Color own = button.BackColor.A < 255 ? Background : button.BackColor;

        button.FlatAppearance.MouseOverBackColor = Mix(own, Strong, 0.14f);
        button.FlatAppearance.MouseDownBackColor = Mix(own, Strong, 0.24f);
    }

    /// <summary>The same for every flat button in a window.</summary>
    public static void StyleHoverTree(Control root)
    {
        if (root is Button button)
            StyleHover(button);

        foreach (Control child in root.Controls)
            StyleHoverTree(child);
    }

    /// <summary>
    /// The window's border, like the menu's: the launcher's border colour and thickness
    /// (a colour of UtilTube's own look when it has one).
    /// </summary>
    public static Color FrameColor => preset <= 0 ? Theme.BorderColor : Mix(Background, Accent, 0.55f);

    public static int FrameWidth => Math.Max(2, Theme.BorderThickness);

    /// <summary>
    /// Text in the menu's font (Consolas by default, the one chosen in Style -> Text and
    /// font), a little smaller to make up for it being wider than UtilTube's old font.
    /// </summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular) =>
        Theme.CreateMenuFont(size * 0.92f, style);

    public static bool FollowsLauncher => preset <= 0;

    private static Color Value(Func<Color> launcher, Color mine)
    {
        if (!ready)
            Apply(TubeLibrary.Instance.Settings, notify: false);

        return preset <= 0 ? launcher() : mine;
    }

    // ---- applying the settings ----

    public static void Apply(TubeSettings s, bool notify = true)
    {
        List<(Color, Color)> before = Snapshot();

        preset = Math.Clamp(s.UtilTubeLook, 0, Presets.Length - 1);
        ownBackground = Parse(s.LookBackground);
        ownPanel = Parse(s.LookPanel);
        ownText = Parse(s.LookText);
        ownAccent = Parse(s.LookAccent);

        Preset p = Presets[Math.Max(1, preset)];

        background = ownBackground.IsEmpty ? Parse(p.Background) : ownBackground;
        accent = ownAccent.IsEmpty ? Parse(p.Accent) : ownAccent;
        text = ownText.IsEmpty ? Parse(p.Text) : ownText;

        // Colours picked by hand carry the rest of the palette with them.
        bool light = Brightness(background) > 0.55;
        Color ink = light ? Color.Black : Color.White;

        panel = !ownPanel.IsEmpty ? ownPanel
            : ownBackground.IsEmpty ? Parse(p.Panel)
            : Mix(background, ink, 0.07f);

        row = ownBackground.IsEmpty && ownPanel.IsEmpty
            ? Parse(p.Row)
            : Mix(panel, ink, 0.09f);

        strong = ownText.IsEmpty ? Parse(p.Strong) : Mix(text, ink, 0.55f);

        ready = true;

        if (!notify)
            return;

        // Old -> new, for every slot, so windows that were coloured when they were built follow.
        List<(Color Old, Color New)> map = new();
        List<(Color, Color)> after = Snapshot();

        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Item1 != after[i].Item1)
                map.Add((before[i].Item1, after[i].Item1));
        }

        Changed?.Invoke(map);
    }

    // The six slots as they are now (each as an (own colour, same) pair, for diffing).
    private static List<(Color, Color)> Snapshot()
    {
        if (!ready)
            return new List<(Color, Color)>();

        Color[] now = { Background, Search, ActiveRow, Accent, Text, Strong };
        return now.Select(c => (c, c)).ToList();
    }

    // ---- helpers used by the settings page ----

    /// <summary>The colour a slot has right now (what a picker should start from).</summary>
    public static Color Current(string slot) => slot switch
    {
        "background" => Background,
        "panel" => Search,
        "text" => Text,
        _ => Accent
    };

    public static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static Color Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Color.Empty;

        try
        {
            return ColorTranslator.FromHtml(hex.Trim());
        }
        catch (Exception)
        {
            return Color.Empty;
        }
    }

    private static double Brightness(Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
}
