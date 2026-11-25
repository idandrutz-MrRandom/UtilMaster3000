using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UtilMaster3000;

// ======================================================
// UPDATES
// Asks GitHub (the project's public releases, no login, no token) whether a newer
// version exists, shows its notes, and can download it, check its SHA-256 and swap it in.
//
//  * Nothing about you is sent: one request to api.github.com with a plain
//    "UtilMaster3000" user agent. (GitHub sees the connection's address, like any web site.)
//  * Only files from this project's release downloads are accepted, and only when GitHub
//    published a checksum for them and the download matches it.
//  * Never a token inside the app: the repository is public, so none is needed.
// ======================================================

/// <summary>One published release.</summary>
internal sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string Title,
    string Notes,
    DateTime Published,
    string PageUrl,
    string? ExeUrl,
    long ExeSize,
    string? ExeSha256,
    bool Prerelease);

internal static class Updater
{
    public const string Repo = "idandrutz-MrRandom/UtilMaster3000";

    /// <summary>Tests: pretend this version is installed.</summary>
    public static Version? CurrentOverride { get; set; }

    /// <summary>Tests: keep downloads somewhere else.</summary>
    public static string? FolderOverride { get; set; }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UtilMaster3000-update-check");
        return client;
    }

    public static string UpdateFolder => FolderOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000", "update");

    // ---- versions ----

    /// <summary>The version that is running (the build's version, without the commit id).</summary>
    public static Version Current
    {
        get
        {
            if (CurrentOverride != null)
                return Normalize(CurrentOverride);

            Assembly? entry = Assembly.GetEntryAssembly();
            string? info = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (info != null && TryParseVersion(info, out Version v))
                return v;

            return Normalize(entry?.GetName().Version ?? new Version(1, 0, 0, 0));
        }
    }

    // 1.0.2 and 1.0.2.0 are the same version (System.Version says otherwise).
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>"1.0.2", "v1.0.2" and "1.0.2+abc123" all mean version 1.0.2.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        text = text.Trim().TrimStart('v', 'V');

        int cut = text.IndexOfAny(new[] { '+', '-', ' ' });

        if (cut >= 0)
            text = text[..cut];

        string[] parts = text.Split('.');

        if (parts.Length < 2 || parts.Length > 4)
            return false;

        int[] n = new int[4];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out n[i]) || n[i] < 0)
                return false;
        }

        version = new Version(n[0], n[1], n[2], n[3]);
        return true;
    }

    /// <summary>"1.0.2" (not "1.0.2.0").</summary>
    public static string Pretty(Version v) =>
        v.Revision > 0 ? v.ToString() : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    // ---- asking GitHub ----

    /// <summary>The latest published releases, newest first by version.</summary>
    public static async Task<List<ReleaseInfo>> FetchReleasesAsync(CancellationToken ct)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=20");

        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await Http.SendAsync(request, ct);

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "GitHub says this connection has checked too often; try again in an hour");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("no releases found for this project");

        response.EnsureSuccessStatusCode();

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Turns GitHub's answer into releases (separate so it can be tested on its own).</summary>
    public static List<ReleaseInfo> Parse(string json)
    {
        List<ReleaseInfo> list = new();

        using JsonDocument doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        foreach (JsonElement r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
                continue;

            string tag = Str(r, "tag_name");

            if (!TryParseVersion(tag, out Version version))
                continue;

            string? exeUrl = null, sha = null;
            long size = 0;

            if (r.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in assets.EnumerateArray())
                {
                    if (!Str(a, "name").Equals("UtilMaster3000.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    exeUrl = Str(a, "browser_download_url");
                    size = a.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;

                    string digest = Str(a, "digest");

                    if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..];
                }
            }

            DateTime.TryParse(Str(r, "published_at"), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime published);

            list.Add(new ReleaseInfo(
                tag, version,
                Str(r, "name"),
                Str(r, "body"),
                published,
                Str(r, "html_url"),
                string.IsNullOrEmpty(exeUrl) ? null : exeUrl,
                size,
                sha,
                r.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean()));
        }

        return list.OrderByDescending(x => x.Version).ToList();
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>The newest real (not pre-)release that is newer than what is running.</summary>
    public static ReleaseInfo? Newer(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => !r.Prerelease && r.Version > Current)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();

    // ---- downloading and installing ----

    /// <summary>
    /// True for the one-file build (the exe from a release): that file can be replaced. A
    /// build run from the source folder (bin\Debug) is not replaced, only pointed at the page.
    /// </summary>
    public static bool CanSelfInstall
    {
        get
        {
            string? path = Environment.ProcessPath;

            return path != null &&
                   Path.GetFileName(path).Equals("UtilMaster3000.exe", StringComparison.OrdinalIgnoreCase) &&
                   !File.Exists(Path.Combine(AppContext.BaseDirectory, "UtilMaster3000.dll"));
        }
    }

    /// <summary>
    /// Downloads the release's exe into the update folder and checks it against the checksum
    /// GitHub published for it. Refuses anything it cannot verify.
    /// </summary>
    public static async Task<string> DownloadAsync(
        ReleaseInfo release, Action<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        if (release.ExeUrl == null)
            throw new InvalidOperationException("this release has no UtilMaster3000.exe file");

        if (!release.ExeUrl.StartsWith(
                $"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "the download address is not one of this project's release files, so it was refused");
        }

        if (release.ExeSha256 == null)
            throw new InvalidOperationException(
                "GitHub gave no checksum for this file, so it cannot be checked; use the release page");

        return await DownloadVerifiedAsync(
            release.ExeUrl, release.ExeSha256,
            Path.Combine(UpdateFolder, $"UtilMaster3000-{Pretty(release.Version)}.exe"),
            progress, ct);
    }

    /// <summary>Downloads a file and keeps it only if its SHA-256 is the expected one.</summary>
    public static async Task<string> DownloadVerifiedAsync(
        string url, string expectedSha256, string target,
        Action<ToolDownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        string temp = target + ".download";

        try
        {
            await Downloader.DownloadAsync(url, temp, progress, ct, 0, 98);

            progress?.Invoke(new ToolDownloadProgress(99, 0, 0, 0, 0));

            await using (FileStream stream = File.OpenRead(temp))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));

                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "the download did not match its published checksum, so it was thrown away");
                }
            }

            File.Move(temp, target, true);
            progress?.Invoke(new ToolDownloadProgress(100, 0, 0, 0, 0));

            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Hands over to a small script that waits for this program to end, keeps a copy of the old
    /// exe (update\previous.exe), puts the new one in its place and starts it. The caller then
    /// closes the app. <paramref name="restart"/> is false only in tests.
    /// </summary>
    public static void InstallAndRestart(string newExe, string? target = null, bool restart = true, int waitForProcess = -1)
    {
        target ??= Environment.ProcessPath
            ?? throw new InvalidOperationException("the program's own file could not be found");

        Directory.CreateDirectory(UpdateFolder);

        string script = Path.Combine(UpdateFolder, "apply-update.ps1");
        string previous = Path.Combine(UpdateFolder, "previous.exe");

        File.WriteAllText(script, ApplyScript, new UTF8Encoding(true));

        string processId = (waitForProcess >= 0 ? waitForProcess : Environment.ProcessId).ToString();

        ProcessStartInfo info = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        foreach (string arg in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                     "-File", script, "-ProcessId", processId,
                     "-NewFile", newExe, "-Target", target, "-Previous", previous,
                     restart ? "-Restart" : "-NoRestart"
                 })
        {
            info.ArgumentList.Add(arg);
        }

        Process.Start(info);
    }

    // Waits for the app to exit, keeps the old exe, swaps the new one in (retrying while the old
    // one is still locked), and starts it.
    private const string ApplyScript = """
        param(
            [int]$ProcessId,
            [string]$NewFile,
            [string]$Target,
            [string]$Previous,
            [switch]$Restart,
            [switch]$NoRestart
        )

        try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue } catch { }

        $done = $false

        for ($i = 0; $i -lt 60 -and -not $done; $i++) {
            try {
                if (Test-Path -LiteralPath $Target) {
                    Copy-Item -LiteralPath $Target -Destination $Previous -Force -ErrorAction Stop
                }

                Copy-Item -LiteralPath $NewFile -Destination $Target -Force -ErrorAction Stop
                $done = $true
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }

        if ($done) {
            Remove-Item -LiteralPath $NewFile -Force -ErrorAction SilentlyContinue
            if ($Restart) { Start-Process -FilePath $Target }
        }
        """;
}
