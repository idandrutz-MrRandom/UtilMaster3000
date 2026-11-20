using System;
using System.IO;

namespace UtilMaster3000;

// ======================================================
// STREAM LOG
//
// A short text log of what the player and the stream bridge did (restarts,
// stalls, slow downloads, how far ahead the video was loaded). When a video
// freezes, this file says why. It lives next to the UtilTube library as
// stream.log and starts over when it grows past a few hundred KB.
// ======================================================

internal static class StreamLog
{
    private static readonly object Gate = new();
    private static string? path;
    private const long MaxBytes = 400 * 1024;

    public static string? FilePath
    {
        get
        {
            lock (Gate)
                return path;
        }
    }

    public static void Write(string area, string message)
    {
        try
        {
            lock (Gate)
            {
                if (path == null)
                {
                    Directory.CreateDirectory(TubeLibrary.DataFolder);
                    path = Path.Combine(TubeLibrary.DataFolder, "stream.log");

                    FileInfo info = new(path);

                    // One older generation is kept, so a freeze just before a restart is not lost.
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = path + ".old";

                        if (File.Exists(old))
                            File.Delete(old);

                        File.Move(path, old);
                    }

                    File.AppendAllText(path, $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} session start ----\n");
                }

                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message}\n");
            }
        }
        catch
        {
            // A log must never break playback.
        }
    }
}
