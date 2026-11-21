using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// TYPING CONVERTER  (Setup -> Typing mode...)
// What happens to the letters you type, anywhere in Windows:
// Russian (the translator), Weird text (restyled letters), or nothing.
// ======================================================

public sealed partial class MainForm
{
    private static readonly string[] ModeNames = { "Letter conversion En -> Ru", "Weird text", "Off" };

    private const string WeirdSentence = "The quick brown fox jumps over the lazy dog";

    // The shortcut / tray / menu all end up here.
    private void ToggleTyping()
    {
        if (translator.Mode == TypingMode.Off)
        {
            Toast("Letter conversion is off. Turn it on in Setup > Typing mode.", OffColor);
            return;
        }

        translator.Toggle();
    }

    private void SetTypingMode(TypingMode mode)
    {
        settings.TypingMode = (int)mode;
        SaveSettings();
        translator.SetMode(mode);
    }

    private void SetWeirdStyle(int style)
    {
        settings.WeirdStyle = style;
        translator.WeirdStyle = style;
        SaveSettings();
    }

    // The mode changed (always on the interface thread).
    private void OnTypingModeChanged(TypingMode mode)
    {
        notification.Label = translator.ModeLabel;
        UpdateStatus(translator.Enabled);

        if (currentPage is "Setup" or "Typing" or "TypingStyles")
            RefreshPage();
    }

    private List<MenuEntry> BuildTypingPage()
    {
        TypingMode mode = translator.Mode;

        void Step(int direction) =>
            SetTypingMode((TypingMode)(((int)mode + direction + 3) % 3));

        string modeText = $"Mode: {ModeNames[(int)mode]}";

        List<MenuEntry> list = new()
        {
            new MenuEntry(0, "\uE765", modeText,
                run: () => Step(1), runAlt: () => Step(-1),
                expandText: modeText + NL +
                    "Letter conversion En -> Ru: English letters become Russian letters as you type (privet -> привет)." + NL +
                    "Weird text: your letters are restyled, like random case, other fonts or symbols." + NL +
                    "Off: the converter is disabled completely and no keyboard hook runs." + NL +
                    "Click = next, right-click = previous.")
        };

        if (mode == TypingMode.Off)
        {
            list.Add(Info("The converter is off. Pick a mode to use it."));
        }
        else
        {
            string now = $"Right now: {OnOff(translator.Enabled)}  (click to switch)";

            list.Add(new MenuEntry(0, "\uE7E8", now,
                run: () =>
                {
                    ToggleTyping();
                    RefreshPage();
                },
                expandText: now + NL + $"The shortcut is {FormatHotkey(toggleKey)} (tap it)."));
        }

        if (mode == TypingMode.Weird)
        {
            int style = Math.Clamp(settings.WeirdStyle, 0, TextStyler.StyleCount - 1);
            string sample = TextStyler.Sample(style);

            list.Add(new MenuEntry(0, "\uE8D2", $"Style: {TextStyler.StyleNames[style]}...",
                targetPage: "TypingStyles",
                expandText: $"Style: {TextStyler.StyleNames[style]}" + NL +
                    TextStyler.Apply(WeirdSentence, style, 4242) + NL +
                    "Click to pick another style."));

            list.Add(Info(sample));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }

    private List<MenuEntry> BuildTypingStylesPage()
    {
        int current = Math.Clamp(settings.WeirdStyle, 0, TextStyler.StyleCount - 1);
        List<MenuEntry> list = new();

        for (int i = 0; i < TextStyler.StyleCount; i++)
        {
            int style = i;
            bool active = style == current && translator.Mode == TypingMode.Weird;

            string label = $"{(active ? "● " : "")}{TextStyler.StyleNames[style]}   {TextStyler.Sample(style, "Hello")}";

            list.Add(new MenuEntry(0, "\uE8D2", label,
                run: () =>
                {
                    SetWeirdStyle(style);

                    if (translator.Mode != TypingMode.Weird)
                        SetTypingMode(TypingMode.Weird);
                    else
                        RefreshPage();

                    Toast($"Weird text: {TextStyler.StyleNames[style]}", OnColor);
                },
                expandText: TextStyler.StyleNames[style] + NL +
                    TextStyler.Apply(WeirdSentence, style, 4242) + NL +
                    "Click to use this style."));
        }

        list.Add(BackEntry());

        return Renumber(list);
    }
}
