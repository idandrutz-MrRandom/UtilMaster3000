using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace UtilMaster3000;

// ======================================================
// YOUR GOOGLE ACCOUNT (for recommendations)
//
//  * You sign in yourself, on Google's own page inside a sign-in
//    window. UtilTube never sees your password.
//  * Only the resulting session cookies are kept, encrypted with
//    Windows DPAPI (readable only by your Windows user on this PC).
//  * They are used ONLY to read your Home recommendations and
//    Subscriptions. Videos themselves are downloaded and streamed
//    without your account.
//  * "Sign out" deletes them.
//
// STAYING SIGNED IN
//  1. The sign-in browser profile is kept (not thrown away), so Google
//     keeps recognising this PC and the session keeps renewing itself.
//  2. After every use, the cookies yt-dlp refreshed are saved back.
//  3. When Google rejects the session, or every 12 hours, a hidden
//     browser quietly visits YouTube so the cookies are renewed, then
//     closes again. You only ever sign in again if Google ends the
//     session itself (password change, "sign out everywhere", ...).
// ======================================================

internal sealed class AuthExpiredException : Exception
{
    public AuthExpiredException()
        : base("Your Google session expired. Please sign in again.")
    {
    }
}

internal static class YtAccount
{
    private static string FilePath => Path.Combine(TubeLibrary.DataFolder, "account.dat");

    // Extra protection on top of DPAPI: only this app's entropy decrypts it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UtilMaster3000.UtilTube.account");

    public static bool SignedIn => File.Exists(FilePath);

    /// <summary>The browser profile that holds the Google session.</summary>
    public static string ProfileFolder => Path.Combine(TubeLibrary.DataFolder, "webview");

    private static SynchronizationContext? uiContext;
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static DateTime lastRefreshAttempt = DateTime.MinValue;

    /// <summary>True while the visible sign-in window is open.</summary>
    public static bool SignInOpen { get; set; }

    /// <summary>Call once on the UI thread (the silent refresh needs it).</summary>
    public static void Init()
    {
        if (SynchronizationContext.Current == null)
        {
            SynchronizationContext.SetSynchronizationContext(
                new WindowsFormsSynchronizationContext());
        }

        uiContext ??= SynchronizationContext.Current;

        // A browser profile left behind after signing out (the browser was
        // still shutting down) is removed now.
        if (!SignedIn && Directory.Exists(ProfileFolder))
            DeleteProfile();
    }

    // ---- storing ----

    public static void Save(string netscapeCookies)
    {
        byte[] plain = Encoding.UTF8.GetBytes(netscapeCookies);

        try
        {
            byte[] protectedBytes = ProtectedData.Protect(
                plain, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(TubeLibrary.DataFolder);
            File.WriteAllBytes(FilePath, protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void SignOut()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // Retried next time.
        }

        DeleteProfile();
    }

    // The browser may still be shutting down: keep trying for a while.
    private static void DeleteProfile()
    {
        _ = Task.Run(async () =>
        {
            // Let go of any browser objects that are waiting to be collected:
            // the browser process only exits once they are gone.
            GC.Collect();

            for (int i = 0; i < 45; i++)
            {
                try
                {
                    if (!Directory.Exists(ProfileFolder))
                        return;

                    Directory.Delete(ProfileFolder, true);
                    return;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    private static string? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);

            string text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);

            return text;
        }
        catch (CryptographicException)
        {
            // Different Windows user / damaged file: treat as signed out.
            return null;
        }
    }

    /// <summary>Imports a cookies.txt exported from a browser (an alternative to the sign-in window).</summary>
    public static void ImportFile(string path)
    {
        string text = File.ReadAllText(path);

        if (!LooksValid(text))
            throw new InvalidDataException("That file has no YouTube / Google cookies.");

        Save(text);
    }

    private static bool LooksValid(string netscape) =>
        netscape.Contains("youtube.com") &&
        (netscape.Contains("SAPISID") || netscape.Contains("__Secure-3PSID") || netscape.Contains("LOGIN_INFO"));

    // ---- using ----

    /// <summary>
    /// Writes the cookies to a short-lived file for yt-dlp and wipes it
    /// again as soon as the action finishes.
    /// </summary>
    public static async Task<T> WithCookieFile<T>(Func<string, Task<T>> action)
    {
        string? cookies = Load();

        if (cookies == null)
            throw new AuthExpiredException();

        string folder = Path.Combine(Path.GetTempPath(), "UtilMaster3000");
        Directory.CreateDirectory(folder);

        string file = Path.Combine(folder, "c_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            await File.WriteAllTextAsync(file, cookies);
            return await action(file);
        }
        finally
        {
            // yt-dlp renews the session cookies while it runs; keep the
            // renewed copy so the sign-in does not slowly go stale.
            try
            {
                if (File.Exists(file))
                {
                    string updated = File.ReadAllText(file);

                    if (updated != cookies && LooksValid(updated))
                        Save(updated);
                }
            }
            catch
            {
                // Keep the previous copy.
            }

            Wipe(file);
        }
    }

    private static void Wipe(string file)
    {
        try
        {
            if (!File.Exists(file))
                return;

            long length = new FileInfo(file).Length;

            using (FileStream stream = new(file, FileMode.Open, FileAccess.Write))
                stream.Write(new byte[length], 0, (int)length);

            File.Delete(file);
        }
        catch
        {
            // Best effort.
        }
    }

    // ---- silent session renewal ----

    /// <summary>
    /// Opens a hidden browser on the saved profile, lets YouTube renew its
    /// cookies, saves them and closes the browser again.
    /// Returns false when the session is really gone (sign in again).
    /// </summary>
    public static async Task<bool> RefreshSessionAsync(bool force)
    {
        if (!SignedIn || uiContext == null || SignInOpen)
            return false;

        // Not more than once an hour unless forced.
        if (!force && DateTime.Now - lastRefreshAttempt < TimeSpan.FromHours(1))
            return false;

        await RefreshGate.WaitAsync();

        try
        {
            lastRefreshAttempt = DateTime.Now;

            TaskCompletionSource<bool> done = new();

            uiContext.Post(async _ =>
            {
                try
                {
                    done.TrySetResult(await RenewOnUiThread());
                }
                catch
                {
                    done.TrySetResult(false);
                }
            }, null);

            return await done.Task;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static async Task<bool> RenewOnUiThread()
    {
        using Form host = new()
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-4000, -4000),
            Size = new Size(480, 320)
        };

        WebView2 web = new() { Dock = DockStyle.Fill };
        host.Controls.Add(web);
        host.Show();

        bool ok = false;

        try
        {
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(null, ProfileFolder);

            await web.EnsureCoreWebView2Async(environment);

            TaskCompletionSource<bool> loaded = new();

            web.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
            web.CoreWebView2.Navigate("https://www.youtube.com/");

            await Task.WhenAny(loaded.Task, Task.Delay(25000));

            // Give the page time to renew its cookies, like a normal visit.
            await Task.Delay(6000);

            List<CoreWebView2Cookie> cookies =
                await web.CoreWebView2.CookieManager.GetCookiesAsync(null);

            ok = HasLoginCookies(cookies);

            if (ok)
                Save(ToNetscape(cookies));
        }
        finally
        {
            // Quiet the page first and give pending browser messages time to
            // drain, so the window is not torn down mid-message.
            try
            {
                if (web.CoreWebView2 != null)
                {
                    web.CoreWebView2.Navigate("about:blank");
                    await Task.Delay(500);
                }
            }
            catch
            {
                // Ignore.
            }

            try
            {
                web.Dispose();
            }
            catch
            {
                // Ignore.
            }

            await Task.Delay(400);

            host.Close();
            host.Dispose();

            MemoryTrimmer.TrimSoon(6000);
        }

        return ok;
    }

    // ---- exporting WebView2 cookies ----

    public static string ToNetscape(IEnumerable<CoreWebView2Cookie> cookies)
    {
        StringBuilder sb = new();

        sb.AppendLine("# Netscape HTTP Cookie File");

        foreach (CoreWebView2Cookie c in cookies)
        {
            string domain = c.Domain;

            if (!domain.Contains("youtube.com") && !domain.Contains("google."))
                continue;

            // Session cookies get a far-future expiry so yt-dlp keeps them.
            long expires = c.IsSession
                ? DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds()
                : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds();

            sb.Append(c.IsHttpOnly ? "#HttpOnly_" : "")
              .Append(domain).Append('\t')
              .Append(domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Path).Append('\t')
              .Append(c.IsSecure ? "TRUE" : "FALSE").Append('\t')
              .Append(expires).Append('\t')
              .Append(c.Name).Append('\t')
              .Append(c.Value).Append('\n');
        }

        return sb.ToString();
    }

    public static bool HasLoginCookies(IEnumerable<CoreWebView2Cookie> cookies) =>
        cookies.Any(c => c.Domain.Contains("youtube.com") &&
            (c.Name is "SAPISID" or "__Secure-3PAPISID" or "LOGIN_INFO"));
}

// ======================================================
// SIGN-IN WINDOW
// A private browser window (WebView2) with a throw-away profile.
// ======================================================

internal sealed class GoogleSignInForm : Form
{
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2F";

    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Label hint = new();
    private readonly Button finish = new();

    // Kept between sessions: this is what keeps you signed in.
    private readonly string profileFolder = YtAccount.ProfileFolder;

    private bool done;

    public bool Success { get; private set; }

    private GoogleSignInForm()
    {
        Text = "Sign in to YouTube";
        ClientSize = new Size(520, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = TubeTheme.Background;
        ForeColor = TubeTheme.Strong;
        Font = TubeTheme.Font(9.5f);

        Panel bar = new() { Dock = DockStyle.Top, Height = 46, BackColor = TubeTheme.Search };

        hint.Text = "Sign in with your Google account, then wait for the YouTube home page.";
        hint.AutoSize = false;
        hint.Dock = DockStyle.Fill;
        hint.TextAlign = ContentAlignment.MiddleLeft;
        hint.Padding = new Padding(12, 0, 0, 0);
        hint.ForeColor = TubeTheme.Text;

        finish.Text = "I'm signed in";
        finish.Dock = DockStyle.Right;
        finish.Width = 120;
        finish.FlatStyle = FlatStyle.Flat;
        finish.BackColor = TubeTheme.Accent;
        finish.ForeColor = TubeTheme.Background;
        finish.FlatAppearance.BorderSize = 0;
        TubeTheme.StyleHover(finish);
        finish.Click += async (_, _) => await Finish();

        bar.Controls.Add(hint);
        bar.Controls.Add(finish);

        Controls.Add(web);
        Controls.Add(bar);

        Shown += async (_, _) => await StartAsync();
    }

    /// <summary>Shows the sign-in window; true when the account was connected.</summary>
    public static bool Run(IWin32Window owner)
    {
        // The WebView2 runtime ships with Windows 11 and recent Edge.
        string? version = null;

        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            // Not installed.
        }

        if (version == null)
        {
            MessageBox.Show(
                owner,
                "Signing in needs the Microsoft Edge WebView2 runtime, which was not found.\n\n" +
                "Install it with:\n\nwinget install Microsoft.EdgeWebView2Runtime",
                "UtilTube",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
        }

        YtAccount.SignInOpen = true;

        try
        {
            using GoogleSignInForm form = new();
            form.ShowDialog(owner);

            return form.Success;
        }
        finally
        {
            YtAccount.SignInOpen = false;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(null, profileFolder);

            await web.EnsureCoreWebView2Async(environment);

            web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckSignedIn();
            web.CoreWebView2.Navigate(SignInUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not start the sign-in window:\n\n" + ex.Message,
                "UtilTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Close();
        }
    }

    // As soon as YouTube's own page loads with login cookies, finish.
    private async Task CheckSignedIn()
    {
        if (done || web.CoreWebView2 == null)
            return;

        Uri? uri = null;

        try
        {
            uri = new Uri(web.CoreWebView2.Source);
        }
        catch
        {
            return;
        }

        if (!uri.Host.EndsWith("youtube.com") || uri.AbsolutePath.StartsWith("/robots"))
            return;

        List<CoreWebView2Cookie> cookies =
            await web.CoreWebView2.CookieManager.GetCookiesAsync(null);

        if (YtAccount.HasLoginCookies(cookies))
            await Finish();
    }

    private async Task Finish()
    {
        if (done || web.CoreWebView2 == null)
            return;

        done = true;
        finish.Enabled = false;
        hint.Text = "Finishing...";

        try
        {
            // Let the signed-in page finish setting its cookies.
            await Task.Delay(3000);

            List<CoreWebView2Cookie> cookies =
                await web.CoreWebView2.CookieManager.GetCookiesAsync(null);

            if (!YtAccount.HasLoginCookies(cookies))
            {
                done = false;
                finish.Enabled = true;
                hint.Text = "No sign-in found yet. Finish signing in first.";
                return;
            }

            YtAccount.Save(YtAccount.ToNetscape(cookies));
            Success = true;

            Close();
        }
        catch (Exception ex)
        {
            done = false;
            finish.Enabled = true;
            hint.Text = "Failed: " + ex.Message;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        // Close the browser but KEEP its profile: it holds the session.
        try
        {
            web.Dispose();
        }
        catch
        {
            // Ignore.
        }

        MemoryTrimmer.TrimSoon(4000);
    }
}
