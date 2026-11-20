using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// NOTIFICATIONS PAGE  (Style -> Notifications...)
// Everything about the small pop-ups: where, how big, how long,
// how see-through, which kinds appear, and the "what is running"
// banner. Click a row for the next value, right-click for the previous.
// ======================================================

public sealed partial class MainForm
{
    private MenuEntry NotifyCycle<T>(
        string icon, string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> format, string tip, Action? preview = null)
    {
        void Step(int direction)
        {
            int i = Array.IndexOf(values, get());

            if (i < 0)
                i = 0;

            i = (i + direction + values.Length) % values.Length;

            set(values[i]);
            ApplyNotificationSettings();
            SaveSettings();
            RefreshPage();
            preview?.Invoke();
        }

        string text = $"{label}: {format(get())}";

        return new MenuEntry(0, icon, text,
            run: () => Step(1), runAlt: () => Step(-1),
            expandText: text + NL + tip + NL + "Click = next value, right-click = previous.");
    }

    private void PreviewToast() =>
        notification.ShowMessage("Notification preview", Theme.Accent);

    // The banner shows a sample for a few seconds while you tweak it.
    private void PreviewBanner()
    {
        bannerPreviewUntil = DateTime.UtcNow.AddSeconds(4);
        OverlayTick();
    }

    private DateTime bannerPreviewUntil = DateTime.MinValue;

    private List<MenuEntry> NotificationsPage(string query)
    {
        List<MenuEntry> list = new();
        NotificationPosition[] spots = Enum.GetValues<NotificationPosition>();

        list.Add(Info("Pop-ups"));

        list.Add(NotifyCycle("", "Translator on/off pop-up", new[] { true, false },
            () => settings.ShowNotification, v => settings.ShowNotification = v, v => OnOff(v),
            "The pop-up that says TRANSLATOR: ON / OFF.", () => notification.Preview(translator.Enabled)));

        list.Add(NotifyCycle("", "Small messages (Copied, Saved...)", new[] { true, false },
            () => settings.ShowMessages, v => settings.ShowMessages = v, v => OnOff(v),
            "The short pop-ups the tools show, like \"Copied\" or \"Saved\".", PreviewToast));

        list.Add(NotifyCycle("", "Position", spots,
            () => settings.NotificationPosition, v => settings.NotificationPosition = v, Pretty,
            "Which corner or edge of the screen pop-ups use.", PreviewToast));

        list.Add(NotifyCycle("", "Size", new[] { 70, 85, 100, 125, 150, 200 },
            () => settings.NotificationScale, v => settings.NotificationScale = v, v => v + "%",
            "How big the pop-ups are.", PreviewToast));

        list.Add(NotifyCycle("", "Stays on screen for", new[] { 500, 900, 1500, 2500, 4000, 8000 },
            () => settings.NotificationHoldMs, v => settings.NotificationHoldMs = v,
            v => v < 1000 ? v + " ms" : v / 1000.0 + " s",
            "How long a pop-up is shown before it fades.", PreviewToast));

        list.Add(NotifyCycle("", "See-through", new[] { 0.4, 0.55, 0.68, 0.78, 0.88, 1.0 },
            () => Math.Round(settings.NotificationOpacity, 2), v => settings.NotificationOpacity = v,
            v => $"{v:P0} visible",
            "Lower = more see-through.", PreviewToast));

        list.Add(Info("The \"what is running\" banner"));

        list.Add(NotifyCycle("", "Banner", new[] { true, false },
            () => settings.ShowRunningBanner, v => settings.ShowRunningBanner = v, v => OnOff(v),
            "Shows at the top of the screen while the autoclicker runs or a macro / the screen records, and names the key that stops it.",
            PreviewBanner));

        list.Add(NotifyCycle("", "Banner position", spots,
            () => settings.BannerPosition, v => settings.BannerPosition = v, Pretty,
            "Where the banner sits.", PreviewBanner));

        list.Add(NotifyCycle("", "Banner size", new[] { 70, 85, 100, 125, 150, 200 },
            () => settings.BannerScale, v => settings.BannerScale = v, v => v + "%",
            "How big the banner is.", PreviewBanner));

        list.Add(NotifyCycle("", "Banner see-through", new[] { 40, 55, 70, 85, 94, 100 },
            () => settings.BannerOpacityPercent, v => settings.BannerOpacityPercent = v,
            v => v + "% visible", "Lower = more see-through.", PreviewBanner));

        list.Add(Act("", "Show both now (preview)", () =>
        {
            PreviewToast();
            PreviewBanner();
        }));

        list.Add(Act("", "Reset the notification settings", () =>
        {
            settings.ShowNotification = true;
            settings.ShowMessages = true;
            settings.NotificationPosition = NotificationPosition.BottomCenter;
            settings.NotificationScale = 100;
            settings.NotificationHoldMs = 900;
            settings.NotificationOpacity = 0.78;
            settings.ShowRunningBanner = true;
            settings.BannerPosition = NotificationPosition.TopCenter;
            settings.BannerScale = 100;
            settings.BannerOpacityPercent = 94;

            ApplyNotificationSettings();
            SaveSettings();
            RefreshPage();
        }));

        list.Add(BackEntry());

        return Renumber(list);
    }
}
