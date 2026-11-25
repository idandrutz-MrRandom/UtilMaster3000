using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace UtilMaster3000;

/// <summary>
/// A smooth fade-out when a window is closed, instead of it vanishing in one frame.
/// The window is photographed, the real one is hidden, and the photograph is faded as a light
/// see-through overlay that Windows composes on the graphics card. (Fading the real window with
/// Opacity made it lag: a big window with video in it was redrawn on every frame.)
/// Follows the menu's animation speed; "no animation" there turns it off. Only closing by the user
/// (title bar, Esc, Close()) is animated, never Windows shutting down.
/// </summary>
internal static class CloseAnimation
{
    private const int BaseMs = 190;

    /// <summary>Tests: how many frames the last fade drew.</summary>
    public static int LastFrames { get; private set; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> Immediate = new();

    /// <summary>Closes a window at once (for code that replaces it with another one).</summary>
    public static void CloseNow(Form form)
    {
        Immediate.AddOrUpdate(form, new object());
        form.Close();
    }

    /// <param name="onStart">Anything that must go away at once (a floating bar owned by the window).</param>
    public static void Attach(Form form, Action? onStart = null)
    {
        bool running = false;
        bool finished = false;

        form.FormClosing += (_, e) =>
        {
            if (running && !finished)
            {
                e.Cancel = true;                         // already fading: ignore a second close request
                return;
            }

            if (finished || e.Cancel || Immediate.TryGetValue(form, out _))
                return;

            if (e.CloseReason != CloseReason.UserClosing || !form.IsHandleCreated || !form.Visible ||
                form.WindowState == FormWindowState.Minimized)
            {
                return;
            }

            // Follows the menu's speed, but never so fast that it looks like a snap.
            if (Theme.AnimationScale >= 30)
                return;                                  // animations are off: close at once

            int ms = (int)Math.Clamp(BaseMs / Math.Max(0.2, Theme.AnimationScale), 200, 420);

            FadeGhost? ghost = FadeGhost.TryFromScreen(form.Bounds);

            if (ghost == null)
                return;                                  // could not photograph it: close at once

            e.Cancel = true;
            running = true;

            // The overlay is up: now the real window can go (it and anything it owns).
            onStart?.Invoke();
            form.Hide();

            Stopwatch clock = Stopwatch.StartNew();
            int frames = 0;

            System.Windows.Forms.Timer timer = new() { Interval = 8 };

            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1.0, clock.ElapsedMilliseconds / (double)ms);
                double eased = t * t * (3 - 2 * t);      // smooth in and out

                ghost.SetAlpha((byte)Math.Round(255 * (1 - eased)));
                frames++;

                if (t < 1.0)
                    return;

                timer.Stop();
                timer.Dispose();
                ghost.Dispose();

                LastFrames = frames;
                finished = true;

                if (!form.IsDisposed)
                    form.Close();
            };

            timer.Start();
        };
    }
}
