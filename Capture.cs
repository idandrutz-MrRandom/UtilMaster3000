using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// COLOUR PICKER
// Freezes the screen, shows a magnifier at the cursor and
// picks the pixel under it when you CLICK (no click-through).
//   click = pick      Esc / right-click = cancel
//   arrow keys nudge the cursor one pixel
// ======================================================

internal sealed class ColorPickerForm : Form
{
    private const int Grid = 15;   // pixels shown in the loupe (odd)
    private const int Zoom = 10;   // size of each pixel in the loupe

    private readonly Bitmap shot;
    private Point mouse;
    private Rectangle lastLoupe = Rectangle.Empty;

    public Color PickedColor { get; private set; }

    private ColorPickerForm(Bitmap screenshot, Rectangle bounds)
    {
        shot = screenshot;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;

        mouse = PointToClient(Cursor.Position);
    }

    public static Color? Pick(Bitmap screenshot, Rectangle bounds)
    {
        using ColorPickerForm form = new(screenshot, bounds);

        return form.ShowDialog() == DialogResult.OK
            ? form.PickedColor
            : null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    private Rectangle LoupeRect(Point at)
    {
        int width = Grid * Zoom;
        int height = Grid * Zoom + 30;

        int x = at.X + 24;
        int y = at.Y + 24;

        if (x + width > ClientSize.Width - 4)
            x = at.X - 24 - width;

        if (y + height > ClientSize.Height - 4)
            y = at.Y - 24 - height;

        return new Rectangle(x, y, width, height);
    }

    private Color PixelUnder(Point at)
    {
        int x = Math.Clamp(at.X, 0, shot.Width - 1);
        int y = Math.Clamp(at.Y, 0, shot.Height - 1);

        return shot.GetPixel(x, y);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        mouse = e.Location;

        Rectangle now = LoupeRect(mouse);

        // Repaint only the old and new loupe areas.
        Invalidate(Rectangle.Inflate(lastLoupe, 6, 6));
        Invalidate(Rectangle.Inflate(now, 6, 6));

        lastLoupe = now;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            PickedColor = PixelUnder(e.Location);
            DialogResult = DialogResult.OK;
        }
        else
        {
            DialogResult = DialogResult.Cancel;
        }

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        Point p = Cursor.Position;

        switch (e.KeyCode)
        {
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                Close();
                break;

            case Keys.Left: Cursor.Position = new Point(p.X - 1, p.Y); break;
            case Keys.Right: Cursor.Position = new Point(p.X + 1, p.Y); break;
            case Keys.Up: Cursor.Position = new Point(p.X, p.Y - 1); break;
            case Keys.Down: Cursor.Position = new Point(p.X, p.Y + 1); break;

            case Keys.Enter:
                PickedColor = PixelUnder(PointToClient(Cursor.Position));
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        g.DrawImageUnscaled(shot, 0, 0);

        Rectangle loupe = LoupeRect(mouse);
        Rectangle magnifier = new(loupe.X, loupe.Y, Grid * Zoom, Grid * Zoom);

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        g.FillRectangle(Brushes.Black, Rectangle.Inflate(loupe, 2, 2));

        g.DrawImage(
            shot,
            magnifier,
            new Rectangle(mouse.X - Grid / 2, mouse.Y - Grid / 2, Grid, Grid),
            GraphicsUnit.Pixel
        );

        // Mark the pixel that will be picked.
        Rectangle center = new(
            magnifier.X + Grid / 2 * Zoom,
            magnifier.Y + Grid / 2 * Zoom,
            Zoom, Zoom
        );

        g.DrawRectangle(Pens.Black, Rectangle.Inflate(center, 1, 1));
        g.DrawRectangle(Pens.White, center);

        Color color = PixelUnder(mouse);
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        Rectangle bar = new(loupe.X, magnifier.Bottom, loupe.Width, 30);

        using (SolidBrush barBrush = new(Color.FromArgb(26, 27, 38)))
            g.FillRectangle(barBrush, bar);

        using (SolidBrush swatch = new(color))
            g.FillRectangle(swatch, bar.X + 6, bar.Y + 6, 18, 18);

        g.DrawRectangle(Pens.White, bar.X + 6, bar.Y + 6, 18, 18);

        TextRenderer.DrawText(
            g,
            $"{hex}   click to pick",
            Font,
            new Rectangle(bar.X + 30, bar.Y, bar.Width - 34, bar.Height),
            Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left
        );
    }
}

// ======================================================
// REGION SELECTOR
// Freezes the screen dimmed; drag a rectangle to choose.
//   release = confirm   Esc / right-click = cancel
// ======================================================

internal sealed class RegionSelector : Form
{
    private readonly Bitmap shot;
    private readonly string hint;

    private Point? anchor;
    private Rectangle selection = Rectangle.Empty;

    public Rectangle Result { get; private set; }

    private RegionSelector(Bitmap screenshot, Rectangle bounds, string hint)
    {
        shot = screenshot;
        this.hint = hint;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        BackColor = Color.Black;
    }

    /// <summary>Returns the chosen area in screenshot coordinates, or null.</summary>
    public static Rectangle? Select(
        Bitmap screenshot, Rectangle bounds, string hint)
    {
        using RegionSelector form = new(screenshot, bounds, hint);

        return form.ShowDialog() == DialogResult.OK
            ? form.Result
            : null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    private static Rectangle Normalize(Point a, Point b) =>
        Rectangle.FromLTRB(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            anchor = e.Location;
            selection = Rectangle.Empty;
        }
        else
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (anchor == null)
            return;

        Rectangle old = selection;
        selection = Normalize(anchor.Value, e.Location);

        Invalidate(Rectangle.Inflate(old, 50, 50));
        Invalidate(Rectangle.Inflate(selection, 50, 50));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button != MouseButtons.Left || anchor == null)
            return;

        anchor = null;

        if (selection.Width >= 4 && selection.Height >= 4)
        {
            Result = selection;
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            // A plain click: start over.
            selection = Rectangle.Empty;
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        g.DrawImageUnscaled(shot, 0, 0);

        using (SolidBrush dim = new(Color.FromArgb(140, 0, 0, 0)))
            g.FillRectangle(dim, ClientRectangle);

        if (selection.Width > 0 && selection.Height > 0)
        {
            // Show the chosen area at full brightness.
            g.SetClip(selection);
            g.DrawImageUnscaled(shot, 0, 0);
            g.ResetClip();

            using Pen border = new(Color.White, 2f);
            g.DrawRectangle(border, selection);

            string size = $"{selection.Width} × {selection.Height}";

            int labelY = selection.Top > 28 ? selection.Top - 26 : selection.Bottom + 6;

            Rectangle label = new(selection.Left, labelY, 120, 20);

            using SolidBrush back = new(Color.FromArgb(220, 26, 27, 38));
            g.FillRectangle(back, label);

            TextRenderer.DrawText(
                g, size, Font, label, Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
        else
        {
            TextRenderer.DrawText(
                g, hint, Font,
                new Rectangle(0, 40, ClientSize.Width, 30),
                Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

// ======================================================
// GIF RECORDER  (built in, no extra software needed)
// Records a screen region to an animated GIF, writing each
// frame as it is captured so memory stays small.
// ======================================================

internal sealed class GifRecorder
{
    private const int MaxWidth = 720;

    private readonly Rectangle region;
    private readonly int outWidth;
    private readonly int outHeight;
    private readonly ImageCodecInfo codec;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private Bitmap? first;
    private long lastFrameMs;

    public string Path { get; }
    public int Frames { get; private set; }
    public int MaxSeconds { get; set; } = 30;

    /// <summary>Raised when the time limit is reached.</summary>
    public event Action? AutoStopped;

    public GifRecorder(Rectangle region, string path)
    {
        this.region = region;
        Path = path;

        double scale = Math.Min(1.0, MaxWidth / (double)region.Width);

        outWidth = Math.Max(2, (int)(region.Width * scale));
        outHeight = Math.Max(2, (int)(region.Height * scale));

        codec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Gif.Guid);
    }

    public void Start()
    {
        timer.Tick += (_, _) =>
        {
            try
            {
                CaptureFrame();
            }
            catch
            {
                AutoStopped?.Invoke();
                return;
            }

            if (clock.Elapsed.TotalSeconds >= MaxSeconds)
                AutoStopped?.Invoke();
        };

        CaptureFrame();
        timer.Start();
    }

    private static PropertyItem MakeItem(int id, short type, byte[] value)
    {
        // PropertyItem has no public constructor.
        PropertyItem item = (PropertyItem)RuntimeHelpers
            .GetUninitializedObject(typeof(PropertyItem));

        item.Id = id;
        item.Type = type;
        item.Len = value.Length;
        item.Value = value;

        return item;
    }

    /// <summary>Captures and appends one frame (called by the timer).</summary>
    public void CaptureFrame()
    {
        long now = clock.ElapsedMilliseconds;

        // Real time since the previous frame, in 1/100 s, so playback
        // speed matches even when encoding is slower than the timer.
        int delay = Frames == 0
            ? 10
            : Math.Clamp((int)((now - lastFrameMs) / 10), 2, 200);

        lastFrameMs = now;

        using Bitmap raw = new(
            region.Width, region.Height, PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(raw))
        {
            g.CopyFromScreen(region.Location, Point.Empty, region.Size);
        }

        using Bitmap frame = new(
            outWidth, outHeight, PixelFormat.Format24bppRgb);

        using (Graphics g = Graphics.FromImage(frame))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(raw, 0, 0, outWidth, outHeight);
        }

        frame.SetPropertyItem(
            MakeItem(0x5100, 4, BitConverter.GetBytes(delay)));

        if (first == null)
        {
            first = (Bitmap)frame.Clone();

            // Loop forever.
            first.SetPropertyItem(MakeItem(0x5101, 3, new byte[] { 0, 0 }));

            using EncoderParameters start = new(1);

            start.Param[0] = new EncoderParameter(
                Encoder.SaveFlag, (long)EncoderValue.MultiFrame);

            first.Save(Path, codec, start);
        }
        else
        {
            using EncoderParameters add = new(1);

            add.Param[0] = new EncoderParameter(
                Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);

            first.SaveAdd(frame, add);
        }

        Frames++;
    }

    public string Stop()
    {
        timer.Stop();
        timer.Dispose();

        if (first != null)
        {
            using EncoderParameters flush = new(1);

            flush.Param[0] = new EncoderParameter(
                Encoder.SaveFlag, (long)EncoderValue.Flush);

            first.SaveAdd(flush);
            first.Dispose();
            first = null;
        }

        return Path;
    }
}

// ======================================================
// MP4 RECORDER  (uses ffmpeg when it is installed)
// ======================================================

internal sealed class FfmpegRecorder
{
    private static string? cachedPath;
    private static bool searched;

    private readonly Process process;
    private SystemAudioRecorder? audio;
    private string? audioFile;

    public string Path { get; }

    /// <summary>True when the PC's sound is being recorded too.</summary>
    public bool HasSound => audio is { Working: true };

    private FfmpegRecorder(Process process, string path)
    {
        this.process = process;
        Path = path;
    }

    public static string? FindFfmpeg()
    {
        if (searched)
            return cachedPath;

        searched = true;

        try
        {
            string[] extra =
            {
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WinGet", "Links"),
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "ffmpeg", "bin"),
                @"C:\ffmpeg\bin"
            };

            string[] fromPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(System.IO.Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries);

            foreach (string folder in fromPath.Concat(extra))
            {
                string candidate = System.IO.Path.Combine(
                    folder.Trim('"'), "ffmpeg.exe");

                if (File.Exists(candidate))
                {
                    cachedPath = candidate;
                    break;
                }
            }
        }
        catch
        {
            cachedPath = null;
        }

        return cachedPath;
    }

    /// <summary>
    /// Starts an H.264 recording.
    ///  * crf: quality (lower = better; 23 standard, 18 high, 14 max)
    ///  * fragmented MP4 with a keyframe every 2 s, so even if the PC
    ///    crashes or the disk fills up, everything but the last couple
    ///    of seconds is still a playable file. Length is unlimited.
    /// </summary>
    public static FfmpegRecorder Start(
        string ffmpeg, Rectangle region, string path, int fps, int crf,
        bool sound = true)
    {
        // H.264 wants even dimensions.
        int width = region.Width & ~1;
        int height = region.Height & ~1;

        string args = string.Create(CultureInfo.InvariantCulture,
            $"-y -hide_banner -f gdigrab -framerate {fps} -draw_mouse 1 " +
            $"-offset_x {region.X} -offset_y {region.Y} " +
            $"-video_size {width}x{height} -i desktop " +
            $"-c:v libx264 -preset veryfast -crf {crf} -pix_fmt yuv420p " +
            $"-g {fps * 2} -movflags +frag_keyframe+empty_moov+default_base_moof " +
            $"\"{path}\"");

        ProcessStartInfo info = new(ffmpeg, args)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process process = Process.Start(info)
            ?? throw new InvalidOperationException("ffmpeg did not start");

        ChildJob.Add(process);

        // Keep the tail of ffmpeg's log: it explains any failure.
        FfmpegRecorder recorder = new(process, path);

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                recorder.LastLog = e.Data;
        };

        process.BeginErrorReadLine();

        if (sound)
            recorder.StartSound();

        return recorder;
    }

    // The PC's sound goes to a WAV next to the video; it is joined into the
    // video when the recording stops.
    private void StartSound()
    {
        try
        {
            audioFile = System.IO.Path.ChangeExtension(Path, ".sound.wav");

            SystemAudioRecorder recorder = new(audioFile);

            // Begin with the picture: wait for the first video data.
            for (int i = 0; i < 60 && !File.Exists(Path) && !process.HasExited; i++)
                System.Threading.Thread.Sleep(50);

            if (recorder.Start())
            {
                audio = recorder;
            }
            else
            {
                recorder.Dispose();
                TryDelete(audioFile);
                audioFile = null;
            }
        }
        catch
        {
            audio = null;
        }
    }

    private static void TryDelete(string? file)
    {
        try
        {
            if (file != null && File.Exists(file))
                File.Delete(file);
        }
        catch
        {
            // Leftover temp file; harmless.
        }
    }

    // Joins picture and sound (the picture is copied, not re-encoded).
    private void MergeSound()
    {
        if (audioFile == null || !File.Exists(audioFile) || !File.Exists(Path))
            return;

        string? ffmpeg = FindFfmpeg();

        if (ffmpeg == null)
            return;

        string merged = System.IO.Path.ChangeExtension(Path, ".merged.mp4");

        try
        {
            ProcessStartInfo info = new(ffmpeg,
                $"-y -hide_banner -i \"{Path}\" -i \"{audioFile}\" " +
                "-map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -ac 2 -ar 48000 -b:a 192k -shortest " +
                $"-movflags +faststart \"{merged}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using Process mux = Process.Start(info)!;
            ChildJob.Add(mux);
            mux.ErrorDataReceived += (_, _) => { };
            mux.BeginErrorReadLine();

            if (!mux.WaitForExit(180_000))
            {
                mux.Kill();
                return;
            }

            if (mux.ExitCode == 0 && File.Exists(merged) && new FileInfo(merged).Length > 0)
                File.Move(merged, Path, overwrite: true);
        }
        catch
        {
            // Keep the silent video rather than lose it.
        }
        finally
        {
            TryDelete(merged);
        }
    }

    public string LastLog { get; private set; } = "";

    public bool HasExited => process.HasExited;

    public string Stop()
    {
        try
        {
            if (!process.HasExited)
            {
                // "q" makes ffmpeg finish the file properly.
                process.StandardInput.WriteLine("q");
                process.StandardInput.Flush();

                // The sound ends together with the picture.
                audio?.Stop();

                if (!process.WaitForExit(6000))
                    process.Kill();
            }
        }
        catch
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }

        if (audio != null)
        {
            audio.Stop();
            MergeSound();
        }

        TryDelete(audioFile);

        return Path;
    }
}
