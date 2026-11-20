using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace UtilMaster3000;

internal static class Program
{
    private const string InstanceName = @"Local\UtilMaster3000.SingleInstance";
    private const string ShowSignalName = @"Local\UtilMaster3000.ShowLauncher";

    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000",
        "crash.log");

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Whatever goes wrong, say so and leave a log instead of vanishing.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), fatal: true);

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Report(ex, fatal: true);
        }
    }

    private static void Run(string[] args)
    {
        // "Open with UtilMaster3000" (or a path on the command line):
        // just the video player, no launcher.
        List<string> videos = args
            .Where(a => LocalMedia.IsVideoFile(a) && File.Exists(a))
            .ToList();

        if (videos.Count > 0)
        {
            Application.Run(new PlayerOnlyContext(videos));
            return;
        }

        // One launcher at a time. A second copy would fight the first for the
        // hotkeys (and, while building, for the program file itself).
        using Mutex instance = new(true, InstanceName, out bool first);

        if (!first)
        {
            // Started again by hand: show the running one. (Started by
            // Windows at login: nothing to show.)
            bool atLogin = args.Any(a =>
                a.Equals("--startup", StringComparison.OrdinalIgnoreCase));

            if (!atLogin)
            {
                try
                {
                    using EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch (Exception)
                {
                    // The other copy is still starting; nothing more to do.
                }
            }

            return;
        }

        using EventWaitHandle show = new(false, EventResetMode.AutoReset, ShowSignalName);
        MainForm form = new();

        Thread listener = new(() =>
        {
            while (show.WaitOne())
                form.ShowFromAnotherStart();
        })
        {
            IsBackground = true,
            Name = "Second start listener"
        };

        listener.Start();

        Application.Run(form);

        try
        {
            instance.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread any more: the process is ending anyway.
        }
    }

    private static int reported;

    private static void Report(Exception ex, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);

            File.AppendAllText(
                CrashLogPath,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(fatal ? "FATAL" : "error")}{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nowhere to write: still show the message below.
        }

        // One message per run for errors the program survives; always for fatal ones.
        if (!fatal && Interlocked.Increment(ref reported) > 1)
            return;

        try
        {
            MessageBox.Show(
                (fatal
                    ? "UtilMaster3000 could not continue.\n\n"
                    : "Something went wrong, but UtilMaster3000 is still running.\n\n") +
                ex.Message + "\n\nDetails were saved to:\n" + CrashLogPath,
                "UtilMaster3000",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Not even a message box is possible.
        }
    }
}

/// <summary>Runs only the player; the program ends when its window closes.</summary>
internal sealed class PlayerOnlyContext : ApplicationContext
{
    public PlayerOnlyContext(List<string> files)
    {
        VideoPlayerForm? player = VideoPlayerForm.OpenFiles(files);

        if (player == null)
        {
            ExitThread();
            return;
        }

        player.FormClosed += (_, _) => ExitThread();
    }
}
