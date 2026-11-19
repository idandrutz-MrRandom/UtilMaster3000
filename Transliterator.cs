
using System;
using System.Collections.Generic;
using System.Text;

namespace UtilMaster3000;

public static class Transliterator
{
    private static readonly (string Latin, string Cyrillic)[] Rules =
    {
        ("shch", "щ"),
        ("yo", "ё"),
        ("zh", "ж"),
        ("kh", "х"),
        ("ts", "ц"),
        ("ch", "ч"),
        ("sh", "ш"),
        ("yu", "ю"),
        ("ya", "я"),

        ("a", "а"), ("b", "б"), ("v", "в"),
        ("g", "г"), ("d", "д"), ("e", "е"),
        ("z", "з"), ("i", "и"), ("j", "й"),
        ("k", "к"), ("l", "л"), ("m", "м"),
        ("n", "н"), ("o", "о"), ("p", "п"),
        ("r", "р"), ("s", "с"), ("t", "т"),
        ("u", "у"), ("f", "ф"), ("h", "х"),
        ("c", "ц"), ("y", "ы"), ("w", "в"),
        ("x", "кс"), ("q", "к")
    };

    private static readonly Dictionary<char, string> ReverseMap = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['й'] = "j", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    /// <summary>Cyrillic -> Latin (inverse of <see cref="Convert"/>).</summary>
    public static string ToLatin(string input)
    {
        StringBuilder result = new();

        foreach (char c in input)
        {
            char lower = char.ToLowerInvariant(c);

            if (!ReverseMap.TryGetValue(lower, out string? latin))
            {
                result.Append(c);
                continue;
            }

            if (char.IsUpper(c) && latin.Length > 0)
                latin = char.ToUpperInvariant(latin[0]) + latin[1..];

            result.Append(latin);
        }

        return result.ToString();
    }

    /// <summary>Human-readable table of the Latin -> Cyrillic rules.</summary>
    public static string DescribeAlphabet()
    {
        return string.Join(
            "   ",
            Array.ConvertAll(Rules, r => $"{r.Latin}={r.Cyrillic}")
        );
    }

    public static string Convert(string input)
    {
        StringBuilder result = new();

        int i = 0;

        while (i < input.Length)
        {
            bool matched = false;

            foreach (var rule in Rules)
            {
                if (i + rule.Latin.Length > input.Length)
                    continue;

                string part = input.Substring(i, rule.Latin.Length);

                if (!part.Equals(rule.Latin, StringComparison.OrdinalIgnoreCase))
                    continue;

                string replacement = rule.Cyrillic;

                if (char.IsUpper(input[i]))
                    replacement = char.ToUpper(replacement[0]) + replacement[1..];

                result.Append(replacement);

                i += rule.Latin.Length;
                matched = true;
                break;
            }

            if (!matched)
            {
                result.Append(input[i]);
                i++;
            }
        }

        return result.ToString();
    }
}
