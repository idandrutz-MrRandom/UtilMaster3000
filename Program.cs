using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace UtilMaster3000;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

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

        Application.Run(new MainForm());
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
