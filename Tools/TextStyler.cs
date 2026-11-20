using System;
using System.Collections.Generic;
using System.Text;

namespace UtilMaster3000;

/// <summary>What the typing converter does with the letters you type.</summary>
public enum TypingMode
{
    /// <summary>Latin letters become Russian (the original translator).</summary>
    Russian,

    /// <summary>Letters are restyled: random case, other fonts, look-alike symbols.</summary>
    Weird,

    /// <summary>The converter is switched off completely (no keyboard hook at all).</summary>
    Off
}

// ======================================================
// WEIRD TEXT
// Letter-by-letter restyling. Every style maps one typed letter to
// the same output each time (random case uses a per-word seed), so
// re-rendering a word after a backspace gives the same look.
// ======================================================

public static class TextStyler
{
    private sealed record Style(string Name, Func<char, int, string?> Map);

    private static readonly Style[] Styles =
    {
        new("Random case", (c, h) => (h & 1) == 0 ? char.ToUpperInvariant(c).ToString() : char.ToLowerInvariant(c).ToString()),
        new("Alternating case", (c, h) => (h & 0x100) == 0 ? char.ToUpperInvariant(c).ToString() : char.ToLowerInvariant(c).ToString()),
        new("Bold", (c, _) => Offset(c, 0x1D400, 0x1D41A)),
        new("Italic", (c, _) => c == 'h' ? "ℎ" : Offset(c, 0x1D434, 0x1D44E)),
        new("Bold italic", (c, _) => Offset(c, 0x1D468, 0x1D482)),
        new("Script", (c, _) => Offset(c, 0x1D4D0, 0x1D4EA)),
        new("Gothic", (c, _) => Offset(c, 0x1D56C, 0x1D586)),
        new("Double-struck", (c, _) => DoubleStruck(c)),
        new("Sans bold", (c, _) => Offset(c, 0x1D5D4, 0x1D5EE)),
        new("Monospace", (c, _) => Offset(c, 0x1D670, 0x1D68A)),
        new("Circled", (c, _) => Offset(c, 0x24B6, 0x24D0)),
        new("Wide", (c, _) => Offset(c, 0xFF21, 0xFF41)),
        new("Small caps", (c, _) => SmallCaps(c)),
        new("Leet speak", (c, _) => Leet(c)),
        new("Look-alike symbols", (c, _) => Symbols(c)),
        new("Chaos (every letter different)", Chaos)
    };

    public static string[] StyleNames { get; } = Array.ConvertAll(Styles, s => s.Name);

    public static int StyleCount => Styles.Length;

    /// <summary>Restyles a typed word. <paramref name="seed"/> keeps random choices stable for the word.</summary>
    public static string Apply(string word, int style, int seed)
    {
        Style chosen = Styles[Math.Clamp(style, 0, Styles.Length - 1)];
        StringBuilder result = new(word.Length * 2);

        for (int i = 0; i < word.Length; i++)
        {
            char c = word[i];
            int hash = Hash(seed, i);

            result.Append(char.IsAsciiLetter(c) ? chosen.Map(c, hash) ?? c.ToString() : c.ToString());
        }

        return result.ToString();
    }

    /// <summary>A short sample line for menus.</summary>
    public static string Sample(int style, string text = "The quick fox") =>
        Apply(text, style, 12345);

    private static int Hash(int seed, int index)
    {
        unchecked
        {
            uint x = (uint)seed * 2654435761u + (uint)(index + 1) * 40503u;
            x ^= x >> 15;
            x *= 2246822519u;
            x ^= x >> 13;
            return (int)(x & 0x7FFFFFFF);
        }
    }

    // ---- mappings ----

    // Letters sit in two runs of 26: capitals first, then small ones.
    private static string Offset(char c, int upperStart, int lowerStart)
    {
        int code = char.IsUpper(c)
            ? upperStart + (c - 'A')
            : lowerStart + (c - 'a');

        return char.ConvertFromUtf32(code);
    }

    private static string DoubleStruck(char c)
    {
        // A few capitals live elsewhere in Unicode.
        switch (c)
        {
            case 'C': return "ℂ";
            case 'H': return "ℍ";
            case 'N': return "ℕ";
            case 'P': return "ℙ";
            case 'Q': return "ℚ";
            case 'R': return "ℝ";
            case 'Z': return "ℤ";
        }

        return Offset(c, 0x1D538, 0x1D552);
    }

    private const string SmallCapsLetters = "ᴀʙᴄᴅᴇꜰɢʜɪᴊᴋʟᴍɴᴏᴘǫʀꜱᴛᴜᴠᴡxʏᴢ";

    private static string SmallCaps(char c) =>
        SmallCapsLetters[char.ToLowerInvariant(c) - 'a'].ToString();

    private static string? Leet(char c) => char.ToLowerInvariant(c) switch
    {
        'a' => "4", 'b' => "8", 'e' => "3", 'g' => "6", 'i' => "1",
        'l' => "1", 'o' => "0", 's' => "5", 't' => "7", 'z' => "2",
        _ => char.ToUpperInvariant(c).ToString()
    };

    // One look-alike per letter, same for both cases where there is no good pair.
    private static readonly Dictionary<char, string> SymbolLetters = new()
    {
        ['a'] = "ä", ['b'] = "ß", ['c'] = "¢", ['d'] = "ð", ['e'] = "ε", ['f'] = "ƒ",
        ['g'] = "ğ", ['h'] = "ħ", ['i'] = "ï", ['j'] = "ĵ", ['k'] = "ĸ", ['l'] = "ł",
        ['m'] = "ʍ", ['n'] = "ñ", ['o'] = "ø", ['p'] = "þ", ['q'] = "ǫ", ['r'] = "я",
        ['s'] = "§", ['t'] = "†", ['u'] = "ü", ['v'] = "ν", ['w'] = "ω", ['x'] = "×",
        ['y'] = "¥", ['z'] = "ƶ"
    };

    private static readonly Dictionary<char, string> SymbolCapitals = new()
    {
        ['A'] = "Λ", ['B'] = "ß", ['C'] = "Ĉ", ['D'] = "Ð", ['E'] = "Σ", ['F'] = "Ғ",
        ['G'] = "Ǥ", ['H'] = "Ħ", ['I'] = "Ї", ['J'] = "Ĵ", ['K'] = "Ҝ", ['L'] = "Ł",
        ['M'] = "ʍ", ['N'] = "Ñ", ['O'] = "Ø", ['P'] = "Þ", ['Q'] = "Ǫ", ['R'] = "Я",
        ['S'] = "§", ['T'] = "Ŧ", ['U'] = "Ü", ['V'] = "Ɣ", ['W'] = "Ш", ['X'] = "Ж",
        ['Y'] = "¥", ['Z'] = "Ƶ"
    };

    private static string? Symbols(char c) =>
        (char.IsUpper(c) ? SymbolCapitals : SymbolLetters).TryGetValue(c, out string? s) ? s : null;

    // Every letter gets a style of its own, and a random case on top.
    private static string? Chaos(char c, int hash)
    {
        // Styles that make sense to mix (everything except leet, caps tricks and chaos itself).
        int[] pool = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14 };

        char cased = (hash >> 3 & 1) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
        Style pick = Styles[pool[(hash >> 5) % pool.Length]];

        return pick.Map(cased, hash);
    }
}
