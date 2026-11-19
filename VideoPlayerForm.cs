using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using WpfControls = System.Windows.Controls;
using WpfImaging = System.Windows.Media.Imaging;
using WpfInterop = System.Windows.Interop;
using WpfMedia = System.Windows.Media;

namespace UtilMaster3000;

// ======================================================
// UTILTUBE PLAYER
// Plays saved MP4 files offline in full quality, or streams any other
// video instantly. Uses the built-in Windows media engine (no extra
// software) and streams from disk / network, so memory stays small.
//
// Things YouTube does not give you:
//   * loop a video, the whole playlist, or just a section (A-B)
//   * any speed from 0.1x to 4x, remembered per channel
//   * audio boost up to 3x for quiet saved videos
//   * frame stepping, snapshots, jump to any % with the number keys
//   * always-on-top mini player, sleep timer, resume where you left off
//   Press F1 or use the ... button for the full list of shortcuts.
// ======================================================

internal enum RepeatMode
{
    Off,
    One,
    Playlist
}

internal sealed class VideoPlayerForm : Form
{
    private static double lastVolume = -1;
    private static double lastSpeed = 1.0;

    private static VideoPlayerForm? current;

    private readonly List<TubeVideo> playlist;
    private int index;

    private readonly WpfControls.MediaElement media = new()
    {
        LoadedBehavior = WpfControls.MediaState.Manual,
        UnloadedBehavior = WpfControls.MediaState.Manual,
        Stretch = WpfMedia.Stretch.Uniform,
        ScrubbingEnabled = true
    };

    private readonly WpfControls.TextBlock osd = new()
    {
        Foreground = WpfMedia.Brushes.White,
        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(185, 0, 0, 0)),
        FontSize = 22,
        Padding = new System.Windows.Thickness(14, 6, 14, 6),
        Margin = new System.Windows.Thickness(22),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        VerticalAlignment = System.Windows.VerticalAlignment.Top,
        Visibility = System.Windows.Visibility.Collapsed,
        IsHitTestVisible = false
    };

    // Subtitles, drawn over the bottom of the picture.
    private readonly WpfControls.TextBlock subtitleText = new()
    {
        Foreground = WpfMedia.Brushes.White,
        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(170, 0, 0, 0)),
        FontSize = 28,
        FontWeight = System.Windows.FontWeights.SemiBold,
        TextAlignment = System.Windows.TextAlignment.Center,
        TextWrapping = System.Windows.TextWrapping.Wrap,
        Padding = new System.Windows.Thickness(14, 4, 14, 5),
        Margin = new System.Windows.Thickness(0, 0, 0, 36),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
        Visibility = System.Windows.Visibility.Collapsed,
        IsHitTestVisible = false
    };

    // A picture (opened from the clipboard or as a file) instead of a video.
    private readonly WpfControls.Image picture = new()
    {
        Stretch = WpfMedia.Stretch.Uniform,
        Visibility = System.Windows.Visibility.Collapsed,
        RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
        RenderTransform = new WpfMedia.ScaleTransform(1, 1)
    };

    private bool imageMode;
    private double pictureZoom = 1;

    private readonly ElementHost host = new() { Dock = DockStyle.Fill, TabStop = false };

    // "Up next" screen shown when a video ends.
    private readonly WpfControls.Grid endScreen = new()
    {
        Visibility = System.Windows.Visibility.Collapsed,
        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(238, 0, 0, 0))
    };

    private readonly WpfControls.TextBlock endTitle = new();
    // 6 suggestions: 3 per row, 2 rows.
    private readonly WpfControls.Primitives.UniformGrid endCards = new() { Columns = 3 };
    private readonly WpfControls.TextBlock endSubtitle = new();
    private readonly System.Windows.Forms.Timer endTimer = new() { Interval = 1000 };
    private readonly List<(WpfControls.Image Image, TubeVideo Video)> endThumbs = new();
    private List<TubeVideo> endChoices = new();
    private int endRemaining;
    private bool endCounting;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private readonly Panel bar = new();
    private readonly Button prev = new();
    private readonly Button play = new();
    private readonly Button next = new();
    private readonly Button speedButton = new();
    private readonly Button repeatButton = new();
    private readonly Button muteButton = new();
    private readonly Button moreButton = new();
    private readonly Button fullButton = new();
    private readonly Label time = new();
    private readonly SeekBar seek = new();
    private readonly SeekBar volumeBar = new();

    private readonly System.Windows.Forms.Timer tick = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer osdTimer = new() { Interval = 1500 };

    private CancellationTokenSource? resolveCts;

    // The media engine only plays plain "http": YouTube's https streams go
    // through this local loopback proxy (see StreamProxy.cs).
    private StreamProxy? proxy;

    // Anything that touches the video / controls after an "await" must run
    // on the window's own thread; this makes that explicit and safe.
    private void OnUi(Action action)
    {
        if (IsDisposed)
            return;

        try
        {
            if (InvokeRequired)
                Invoke(action);
            else
                action();
        }
        catch (ObjectDisposedException)
        {
            // Window closed meanwhile.
        }
    }

    private double duration;
    private double pendingResume;
    private bool playing;
    private bool muted;
    private bool recorded;

    // ---- converted playback (ffmpeg bridge) ----
    // Formats Windows cannot play (mkv, webm, avi, HEVC...) and streams that
    // YouTube hands out in separate parts are converted on the fly. That
    // stream has no seek index, so a seek restarts it at the new time and
    // the position is "start offset + time since the restart".
    private bool bridged;
    private int streamRetries;

    // Stall watchdog: if a streamed picture stops moving, reconnect.
    private double watchPosition = -1;
    private long watchSince;
    private int stallRestarts;
    private BridgeSpec? beforeUpgrade;
    private long upgradedAt;
    private long restartedAt;
    private double bridgeOffset;
    private double bridgeDuration;
    private BridgeSpec? bridgeBase;
    private Func<bool>? fallback;
    private long lastSave;

    private double speed = 1.0;
    private RepeatMode repeat = RepeatMode.Off;

    // A-B loop (seconds); b < 0 while only A is set.
    private double abA = -1;
    private double abB = -1;

    // Audio boost: 1.0 = normal. Boosted copies are made with ffmpeg.
    private double boost = 1.0;
    private bool boosting;

    private DateTime? sleepAt;

    // After you seek, show the thumb at the target until the video really
    // gets there (a streamed seek takes a moment). Otherwise the thumb
    // would snap back to the old position and then jump forward again.
    // The media engine can report "ended" more than once for the same
    // play-through; only the first one may advance to another video.
    private bool endedHandled;

    private double seekTarget = -1;
    private long seekHoldUntil;
    private long lastSeekApplied;
    private double lastSeekValue = -1;

    // fullscreen
    private bool fullscreen;
    private FormBorderStyle savedBorder;
    private Rectangle savedBounds;
    private FormWindowState savedState;
    private Point lastMouse;
    private long lastMove;

    private static readonly double[] SpeedPresets =
        { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 };

    private static readonly double[] BoostLevels = { 1.0, 1.5, 2.0, 3.0 };

    /// <summary>Opens the player (one window at a time) at the given item.</summary>
    public static VideoPlayerForm Play(List<TubeVideo> playlist, int index)
    {
        current?.Close();

        current = new VideoPlayerForm(playlist, index);
        current.FormClosed += (_, _) => current = null;
        current.Show();

        return current;
    }

    /// <summary>Plays video files. One file also lines up the others in its folder.</summary>
    public static VideoPlayerForm? OpenFiles(IEnumerable<string> paths)
    {
        List<string> files = paths
            .Where(p => File.Exists(p) && (LocalMedia.IsVideoFile(p) || LocalMedia.IsImageFile(p)))
            .ToList();

        if (files.Count == 0)
            return null;

        int start = 0;

        if (files.Count == 1 && LocalMedia.IsVideoFile(files[0]))
        {
            // Everything else in the folder follows (Up next).
            List<string> siblings = LocalMedia.Siblings(files[0]);
            int at = siblings.FindIndex(f =>
                string.Equals(f, Path.GetFullPath(files[0]), StringComparison.OrdinalIgnoreCase));

            if (at >= 0)
            {
                files = siblings;
                start = at;
            }
        }

        List<TubeVideo> list = files.Select(LocalMedia.ToVideo).ToList();

        return Play(list, start);
    }

    private void OpenFileDialog()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Open video",
            Filter = LocalMedia.DialogFilter,
            Multiselect = true
        };

        SetPlaying(false);

        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenFiles(dialog.FileNames);
        else
            SetPlaying(true);
    }

    // ---- state the tests (and other code) can read ----
    public bool Opened { get; private set; }
    public double DurationSeconds => duration;
    public string? Failure { get; private set; }
    public double Speed => speed;
    public RepeatMode Repeat => repeat;
    public double LoopA => abA;
    public double LoopB => abB;
    public double Boost => boost;
    public bool Converting => bridged;
    public double PositionSeconds => Pos;
    public double VolumeLevel => media.Volume;
    public string LastOsd { get; private set; } = "";

    /// <summary>Diagnostics hook (tests): player events in order.</summary>
    public static Action<string>? DebugLog { get; set; }

    /// <summary>Tests: always convert streams instead of playing them directly.</summary>
    public static bool ForceBridge { get; set; }
    public static bool NoUpgrade { get; set; }

    private VideoPlayerForm(List<TubeVideo> playlist, int index)
    {
        this.playlist = playlist;

        Text = "UtilTube";
        BackColor = Color.Black;
        ForeColor = Theme.Strong;
        ClientSize = new Size(960, 592);
        MinimumSize = new Size(520, 340);
        StartPosition = FormStartPosition.CenterScreen;

        // "Full windowed": fills the screen but keeps the title bar and
        // taskbar, so nobody has to press the maximize square. (F switches
        // to true fullscreen; Z decides how the picture fills the window.)
        WindowState = FormWindowState.Maximized;

        KeyPreview = true;

        AllowDrop = true;

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };

        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
                OpenFiles(dropped);
        };
        DoubleBuffered = true;

        if (lastVolume < 0)
            lastVolume = Math.Clamp(TubeLibrary.Instance.Settings.DefaultVolume, 0, 100) / 100.0;

        // The video surface: a black grid so letterboxing stays black.
        WpfControls.Grid surface = new() { Background = WpfMedia.Brushes.Black };
        surface.Children.Add(media);
        surface.Children.Add(picture);
        BuildEndScreen();
        surface.Children.Add(endScreen);
        surface.Children.Add(osd);
        surface.Children.Add(subtitleText);
        host.Child = surface;

        // Click = play / pause, double click = fullscreen, wheel = volume.
        surface.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
                ToggleFullscreen();
            else
                TogglePlay();
        };

        surface.MouseWheel += (_, e) =>
        {
            if (imageMode)
                SetPictureZoom(pictureZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
            else
                SetVolume(media.Volume + (e.Delta > 0 ? 0.05 : -0.05));
        };

        surface.MouseRightButtonUp += (_, _) => ShowMenu(Cursor.Position);

        media.Volume = lastVolume;
        media.MediaOpened += (_, _) => OnOpened();
        media.MediaEnded += (_, _) => OnEnded();
        media.MediaFailed += (_, e) => OnFailed(e.ErrorException);

        BuildBar();

        Controls.Add(host);
        Controls.Add(bar);

        host.Resize += (_, _) => ApplyFit();

        tick.Tick += (_, _) => OnTick();
        tick.Start();

        osdTimer.Tick += (_, _) =>
        {
            osdTimer.Stop();
            osd.Visibility = System.Windows.Visibility.Collapsed;
        };

        speed = lastSpeed;

        Open(index);
    }

    // --------------------------------------------------
    // ON-SCREEN MESSAGE
    // --------------------------------------------------

    private void Say(string text)
    {
        LastOsd = text;
        osd.Text = text;
        osd.Visibility = System.Windows.Visibility.Visible;

        osdTimer.Stop();
        osdTimer.Start();
    }

    // --------------------------------------------------
    // CONTROLS
    // --------------------------------------------------

    private void IconButton(Button button, string glyph, string tip, int size = 11)
    {
        button.Text = glyph;
        button.Font = new Font("Segoe MDL2 Assets", size);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.ActiveRow;
        button.FlatAppearance.MouseDownBackColor = Theme.ActiveRow;
        button.BackColor = Theme.Background;
        button.ForeColor = Theme.Strong;
        button.Size = new Size(32, 28);
        button.Padding = Padding.Empty;
        button.TabStop = false;
        button.Cursor = Cursors.Hand;
        bar.Controls.Add(button);

        new ToolTip().SetToolTip(button, tip);
    }

    private void BuildBar()
    {
        // Two slim rows: the seek line on top, the buttons below it.
        bar.Dock = DockStyle.Bottom;
        bar.Height = 52;
        bar.BackColor = Theme.Background;

        IconButton(prev, "\uE892", "Previous  (P)");
        IconButton(play, "\uE769", "Play / pause  (Space)", 12);
        IconButton(next, "\uE893", "Next  (N)");
        IconButton(muteButton, "\uE767", "Mute  (M)");
        IconButton(repeatButton, "\uE8EE", "Repeat  (R)");
        IconButton(moreButton, "\uE712", "More  (F1: shortcuts)");
        IconButton(fullButton, "\uE740", "Fullscreen  (F)");

        speedButton.Text = "1x";
        speedButton.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        speedButton.FlatStyle = FlatStyle.Flat;
        speedButton.FlatAppearance.BorderSize = 0;
        speedButton.FlatAppearance.MouseOverBackColor = Theme.ActiveRow;
        speedButton.FlatAppearance.MouseDownBackColor = Theme.ActiveRow;
        speedButton.BackColor = Theme.Background;
        speedButton.ForeColor = Theme.Strong;
        speedButton.Size = new Size(48, 28);
        speedButton.Padding = Padding.Empty;
        speedButton.TabStop = false;
        speedButton.Cursor = Cursors.Hand;
        bar.Controls.Add(speedButton);

        new ToolTip().SetToolTip(speedButton, "Speed: click for presets, scroll to fine-tune  ([ ] < >)");

        time.AutoSize = false;
        time.Size = new Size(104, 28);
        time.TextAlign = ContentAlignment.MiddleLeft;
        time.Font = new Font("Segoe UI", 9f);
        time.ForeColor = Theme.Text;
        time.Text = "0:00 / 0:00";
        bar.Controls.Add(time);

        seek.Height = 16;
        bar.Controls.Add(seek);

        volumeBar.Height = 28;
        volumeBar.Value = lastVolume;
        bar.Controls.Add(volumeBar);

        prev.Click += (_, _) => Open(index - 1);
        next.Click += (_, _) => Open(index + 1);
        play.Click += (_, _) => TogglePlay();
        fullButton.Click += (_, _) => ToggleFullscreen();
        muteButton.Click += (_, _) => ToggleMute();
        repeatButton.Click += (_, _) => CycleRepeat();
        moreButton.Click += (_, _) => ShowMenu(moreButton.PointToScreen(new Point(0, 0)));
        speedButton.Click += (_, _) => ShowSpeedMenu(speedButton.PointToScreen(new Point(0, 0)));

        speedButton.MouseWheel += (_, e) => SetSpeed(speed + (e.Delta > 0 ? 0.05 : -0.05));

        // While dragging, the thumb follows the mouse at once but the video
        // is only asked to seek a few times a second (and finally on release).
        seek.UserChanged += value =>
        {
            if (duration <= 0)
                return;

            seekTarget = Math.Clamp(value * duration, 0, duration - 0.3);
            seekHoldUntil = Environment.TickCount64 + 8000;

            if (!bridged && Environment.TickCount64 - lastSeekApplied >= 150)
                ApplySeek(value);
        };

        seek.DragCompleted += value =>
        {
            if (duration > 0 && Math.Abs(value - lastSeekValue) > 0.0005)
                ApplySeek(value);
        };

        volumeBar.UserChanged += value => SetVolume(value);

        bar.Resize += (_, _) => LayoutBar();
        LayoutBar();
    }

    private void LayoutBar()
    {
        // top row: seek line across the full width
        seek.SetBounds(6, 2, Math.Max(60, bar.Width - 12), 16);

        // bottom row: left group ...
        int y = 20;
        int x = 6;

        prev.Location = new Point(x, y); x += 32;
        play.Location = new Point(x, y); x += 32;
        next.Location = new Point(x, y); x += 36;
        muteButton.Location = new Point(x, y); x += 32;
        volumeBar.SetBounds(x, y, 70, 28); x += 78;
        time.Location = new Point(x, y);

        // ... and right group
        int right = bar.Width - 6;

        fullButton.Location = new Point(right - 32, y); right -= 32;
        moreButton.Location = new Point(right - 32, y); right -= 32;
        repeatButton.Location = new Point(right - 32, y); right -= 32;
        speedButton.Location = new Point(right - 48, y);
    }

    // --------------------------------------------------
    // OPENING A VIDEO
    // --------------------------------------------------

    private TubeVideo Current => playlist[index];

    private void Open(int i)
    {
        if (i < 0 || i >= playlist.Count)
            return;

        SaveProgress();
        resolveCts?.Cancel();
        HideEndScreen();

        DebugLog?.Invoke($"Open({i}) {playlist[i].Title}  (was index {index})");

        index = i;
        TubeVideo video = Current;

        // Keeps the video you are watching safe from auto-delete.
        TubeLibrary.NowPlayingId = video.Id;

        Text = video.Title;
        Opened = false;
        Failure = null;
        duration = 0;
        bridged = false;
        streamRetries = 0;
        stallRestarts = 0;
        ClearSubtitles();
        watchPosition = -1;
        beforeUpgrade = null;
        bridgeBase = null;
        bridgeOffset = 0;
        fallback = null;
        recorded = false;
        endedHandled = false;
        seekTarget = -1;
        lastSeekValue = -1;
        boost = 1.0;
        abA = abB = -1;
        seek.MarkA = seek.MarkB = -1;

        // Resume where you stopped (only saved videos keep a position).
        TubeVideo? saved = TubeLibrary.Instance.Get(video.Id);
        pendingResume = saved != null && saved.Position > 5 ? saved.Position : 0;

        // The speed you last used for this channel (YouTube forgets it).
        TubeSettings settings = TubeLibrary.Instance.Settings;

        if (settings.RememberChannelSpeed)
        {
            double? remembered = TubeLibrary.Instance.ChannelSpeed(video.Channel);

            if (remembered != null)
                speed = remembered.Value;
        }

        ApplySpeed();

        imageMode = false;
        picture.Visibility = System.Windows.Visibility.Collapsed;
        picture.Source = null;

        if (video.File != null && File.Exists(video.File))
        {
            if (LocalMedia.IsImageFile(video.File))
            {
                ShowPicture(video);
                return;
            }

            StartFile(video, i);
            return;
        }

        // Not saved: stream it right away (360p, instant start).
        _ = StreamAsync(video, i);
    }

    private double Pos =>
        bridged ? bridgeOffset + media.Position.TotalSeconds : media.Position.TotalSeconds;

    private void SetPos(double seconds)
    {
        if (bridged)
            RestartBridge(seconds);
        else
            media.Position = TimeSpan.FromSeconds(seconds);
    }

    private static bool PlaysNatively(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".wmv" or ".3gp";

    // A video file: direct when Windows can play it, converted otherwise.
    private void StartFile(TubeVideo video, int target)
    {
        string file = video.File!;

        if (PlaysNatively(file))
        {
            // If the codec turns out to be unsupported, convert it instead.
            fallback = () =>
            {
                BridgeFile(video, target);
                return true;
            };

            StartSource(new Uri(file));
            return;
        }

        BridgeFile(video, target);
    }

    private void ShowPicture(TubeVideo video)
    {
        imageMode = true;
        playing = false;

        try
        {
            media.Stop();
            media.Source = null;

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(video.File!);
            bitmap.EndInit();
            bitmap.Freeze();

            picture.Source = bitmap;
            FitWindowTo(bitmap.PixelWidth, bitmap.PixelHeight);
            SetPictureZoom(1);
            picture.Visibility = System.Windows.Visibility.Visible;

            Text = video.Title;
            Say("Wheel = zoom,  Ctrl+C = copy");
        }
        catch (Exception)
        {
            Text = "Cannot show this picture";
        }

        UpdateButtons();
    }

    private void SetPictureZoom(double zoom)
    {
        pictureZoom = Math.Clamp(zoom, 1, 12);
        picture.RenderTransform = new WpfMedia.ScaleTransform(pictureZoom, pictureZoom);
    }

    private void CopyPicture()
    {
        try
        {
            string? file = Current.File;

            if (file == null)
                return;

            using System.Drawing.Image loaded = System.Drawing.Image.FromFile(file);
            using System.Drawing.Bitmap copy = new(loaded);

            Clipboard.SetImage(copy);
            Say("Copied");
        }
        catch (Exception)
        {
            Say("Could not copy");
        }
    }

    private void BridgeFile(TubeVideo video, int target)
    {
        string file = video.File!;
        double start = pendingResume;

        pendingResume = 0;
        fallback = null;

        Text = "Preparing...  " + video.Title;

        Task.Run(() => LocalMedia.Probe(file)).ContinueWith(t =>
        {
            if (IsDisposed || index != target)
                return;

            MediaProbe? probe = t.IsCompletedSuccessfully ? t.Result : null;

            double length = probe is { Duration: > 0 } ? probe.Duration : video.Duration;

            if (length > 0 && start > length - 10)
                start = 0;

            OnUi(() =>
            {
                if (IsDisposed || index != target)
                    return;

                Text = video.Title;

                BeginBridge(
                    new BridgeSpec(
                        new List<BridgeInput> { new(file, null) },
                        0,
                        LocalMedia.VideoCanBeCopied(probe)),
                    length,
                    start);
            });
        });
    }

    private void BeginBridge(BridgeSpec spec, double knownDuration, double start)
    {
        proxy ??= new StreamProxy();

        bridged = true;
        bridgeBase = spec;
        bridgeDuration = knownDuration;
        bridgeOffset = start;

        StartSource(new Uri(proxy.RegisterBridge(spec with { StartSeconds = start })));
    }

    private void RestartBridge(double seconds)
    {
        if (bridgeBase == null || proxy == null)
            return;

        bool wasPlaying = playing;

        DebugLog?.Invoke($"RestartBridge({seconds:0.0}) from: " + string.Join(" < ",
            new System.Diagnostics.StackTrace().GetFrames().Skip(1).Take(4).Select(f => f.GetMethod()?.Name)));

        restartedAt = Environment.TickCount64;
        bridgeOffset = Math.Max(0, seconds);
        media.Source = new Uri(proxy.RegisterBridge(bridgeBase with { StartSeconds = bridgeOffset }));
        ApplySpeed();

        if (wasPlaying)
            media.Play();
        else
            media.Pause();
    }

    private void StartSource(Uri uri)
    {
        media.Source = uri;
        ApplySpeed();
        media.Play();

        playing = true;
        UpdateButtons();
    }

    // Once the video plays steadily, look up the better picture and switch
    // to it at the same spot.
    private async Task UpgradeQualityAsync(TubeVideo video, int target, StreamPlan current)
    {
        int wanted = YtDlp.UpgradeHeight;

        if (NoUpgrade || wanted <= 0 || current.Height >= wanted || current.Progressive)
            return;

        try
        {
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);

                if (IsDisposed || index != target || !bridged)
                    return;

                if (Opened && playing && media.Position.TotalSeconds >= 6)
                    break;
            }

            StreamPlan better = await YtDlp.PlanAsync(video.Id, null, wanted, CancellationToken.None);

            if (IsDisposed || index != target || !bridged || bridgeBase == null ||
                better.Progressive || !better.CopyVideo || better.Height <= current.Height || better.Inputs.Count == 0)
            {
                return;
            }

            // Brand-new addresses are refused for ~2.5 s; the first pieces are
            // being fetched meanwhile. Switch only once they are ready.
            await Task.Delay(3500);

            if (IsDisposed || index != target || !bridged || bridgeBase == null)
                return;

            double at = Pos;

            if (duration > 0 && at > duration - 30)
                return;

            // The better stream is started in the background, a few seconds
            // ahead of the picture, and fills its buffer while the current
            // one keeps playing. The switch then costs almost nothing.
            double switchAt = at + 8;

            BridgeSpec upgraded = new(
                better.Inputs.Select(x => new BridgeInput(x.Url, x.Headers)).ToList(),
                switchAt,
                better.CopyVideo);

            proxy ??= new StreamProxy();
            string uri = proxy.PrepareBridge(upgraded);

            DebugLog?.Invoke($"upgrade: preparing {better.Height}p to take over at {switchAt:0.0}s");

            if (!await proxy.WaitReadyAsync(uri, 25000))
            {
                DebugLog?.Invoke("upgrade: the better stream did not start, staying put");
                return;
            }

            // Let the current picture reach the switch point.
            for (int i = 0; i < 250; i++)
            {
                if (IsDisposed || index != target || !bridged || bridgeBase == null)
                    return;

                if (playing && Pos >= switchAt - 0.15)
                    break;

                await Task.Delay(40);
            }

            double now = Pos;

            if (now < switchAt - 1 || now > switchAt + 5)
                return;

            beforeUpgrade = bridgeBase;
            upgradedAt = restartedAt = Environment.TickCount64;

            bridgeBase = upgraded;
            bridgeOffset = switchAt;

            DebugLog?.Invoke($"upgrade: {current.Height}p -> {better.Height}p at {now:0.0}s (copyVideo={better.CopyVideo})");

            media.Source = new Uri(uri);
            ApplySpeed();

            if (playing)
                media.Play();
            else
                media.Pause();

            Say("Quality: " + better.Height + "p");
        }
        catch
        {
            // Stay at the current quality.
        }
    }

    private async Task StreamAsync(TubeVideo video, int target)
    {
        CancellationTokenSource cts = resolveCts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        Text = "Loading...  " + video.Title;
        playing = false;
        UpdateButtons();

        try
        {
            StreamPlan plan;

            try
            {
                plan = await YtDlp.PlanAsync(video.Id, null, YtDlp.StartHeight, token);
            }
            catch (AuthExpiredException) when (YtAccount.SignedIn)
            {
                // Age-restricted: this one video needs your account.
                plan = await YtAccount.WithCookieFile(file =>
                    YtDlp.PlanAsync(video.Id, file, YtDlp.StartHeight, token));
            }

            if (token.IsCancellationRequested || IsDisposed || index != target)
                return;

            OnUi(() =>
            {
                proxy ??= new StreamProxy();

                Text = video.Title;

                double length = plan.Duration > 0 ? plan.Duration : video.Duration;

                BridgeSpec spec = new(
                    plan.Inputs.Select(x => new BridgeInput(x.Url, x.Headers)).ToList(),
                    0,
                    plan.CopyVideo);

                if (plan.Progressive && !ForceBridge)
                {
                    // One file with sound: play it directly (instant start,
                    // real seeking). If Windows refuses it, convert instead.
                    fallback = () =>
                    {
                        BeginBridge(spec, length, 0);
                        return true;
                    };

                    StartSource(new Uri(proxy.Register(plan.Inputs[0].Url, plan.Inputs[0].Headers)));
                }
                else
                {
                    BeginBridge(spec, length, 0);
                    _ = UpgradeQualityAsync(video, target, plan);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Moved on to another video.
        }
        catch (Exception ex)
        {
            if (IsDisposed || index != target)
                return;

            Failure = ex.Message;

            OnUi(() =>
            {
                Text = video.Title;

                DialogResult save = MessageBox.Show(
                    this,
                    "This video could not be streamed:\n\n" + ex.Message +
                    "\n\nSave it for offline viewing instead?",
                    "UtilTube",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (save == DialogResult.Yes)
                    TubeLibrary.Instance.Enqueue(video, priority: true);
            });
        }
    }

    // Sizes the window so the picture fills it exactly: the video's own
    // shape, as large as fits the screen (never bigger than 1.5x its pixels).
    private void FitWindowTo(int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || fullscreen ||
            WindowState != FormWindowState.Normal)
        {
            return;
        }

        Rectangle area = Screen.FromControl(this).WorkingArea;

        int frameW = Width - ClientSize.Width;
        int frameH = Height - ClientSize.Height;

        double maxW = area.Width * 0.97 - frameW;
        double maxH = area.Height * 0.97 - frameH - bar.Height;

        double scale = Math.Min(Math.Min(maxW / pixelWidth, maxH / pixelHeight), 1.5);

        int w = Math.Max(MinimumSize.Width - frameW, (int)Math.Round(pixelWidth * scale));
        int h = Math.Max(MinimumSize.Height - frameH - bar.Height, (int)Math.Round(pixelHeight * scale));

        ClientSize = new Size(w, h + bar.Height);

        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2);
    }

    private static readonly string[] FitNames = { "Auto", "Fill (no black bars)", "Fit (whole picture)" };

    // Decides how the video fills the window. Fill crops the edges so no
    // black bars remain; Fit shows everything; Auto fills when the shapes
    // are close (a small crop) and fits when they are far apart.
    private void ApplyFit()
    {
        int mode = TubeLibrary.Instance.Settings.VideoFit;
        int nw = media.NaturalVideoWidth;
        int nh = media.NaturalVideoHeight;

        bool fill;

        if (mode == 1)
        {
            fill = true;
        }
        else if (mode == 2 || nw <= 0 || nh <= 0 || host.Height <= 0)
        {
            fill = false;
        }
        else
        {
            double videoShape = nw / (double)nh;
            double windowShape = host.Width / (double)host.Height;
            double mismatch = Math.Max(videoShape / windowShape, windowShape / videoShape) - 1;

            fill = mismatch <= 0.08;
        }

        media.Stretch = fill ? WpfMedia.Stretch.UniformToFill : WpfMedia.Stretch.Uniform;
    }

    // ---------- subtitles ----------

    private record struct Cue(double Start, double End, string Text);

    private List<SubtitleTrack>? subTracks;
    private List<Cue> subCues = new();
    private string subLabel = "";
    private int subLoading;
    private int lastCue = -1;

    private static readonly System.Net.Http.HttpClient SubtitleHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private void ClearSubtitles()
    {
        subTracks = null;
        subCues = new List<Cue>();
        subLabel = "";
        lastCue = -1;
        subtitleText.Visibility = System.Windows.Visibility.Collapsed;
    }

    private bool SubtitlesAvailable => !LocalMedia.IsLocalId(Current.Id) && !imageMode;

    private ToolStripMenuItem BuildSubtitleMenu(Point where)
    {
        string state = subCues.Count > 0 ? ": " + subLabel : "";
        ToolStripMenuItem subs = new("Subtitles" + state + "   (C)");

        if (!SubtitlesAvailable)
        {
            subs.DropDownItems.Add(new ToolStripMenuItem("Not available for local files") { Enabled = false });
            return subs;
        }

        if (subTracks == null)
        {
            subs.DropDownItems.Add("Find subtitles for this video", null, async (_, _) =>
            {
                await LoadSubtitleTracksAsync();

                if (!IsDisposed)
                    BeginInvoke(() => ShowMenu(where));
            });

            return subs;
        }

        if (subTracks.Count == 0)
        {
            subs.DropDownItems.Add(new ToolStripMenuItem("This video has no subtitles") { Enabled = false });
            return subs;
        }

        ToolStripMenuItem off = new("Off") { Checked = subCues.Count == 0 };
        off.Click += (_, _) => SubtitlesOff();
        subs.DropDownItems.Add(off);
        subs.DropDownItems.Add(new ToolStripSeparator());

        foreach (SubtitleTrack track in subTracks)
        {
            SubtitleTrack captured = track;

            ToolStripMenuItem item = new(track.Name) { Checked = subCues.Count > 0 && subLabel == track.Name };
            item.Click += async (_, _) => await ChooseSubtitleAsync(captured);
            subs.DropDownItems.Add(item);
        }

        return subs;
    }

    private async Task LoadSubtitleTracksAsync()
    {
        if (!SubtitlesAvailable || subTracks != null || Interlocked.Exchange(ref subLoading, 1) == 1)
            return;

        string id = Current.Id;
        Say("Looking for subtitles...");

        try
        {
            List<SubtitleTrack> tracks = await YtDlp.SubtitlesAsync(id, CancellationToken.None);

            if (IsDisposed || Current.Id != id)
                return;

            subTracks = tracks;
            Say(tracks.Count == 0 ? "No subtitles for this video" : tracks.Count + " subtitle track(s) found");
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke("subtitles failed: " + ex.Message);
            Say("Couldn't look up subtitles");
        }
        finally
        {
            Interlocked.Exchange(ref subLoading, 0);
        }
    }

    // Best track for what you chose before, else English, else the first.
    private SubtitleTrack? PickTrack()
    {
        if (subTracks == null || subTracks.Count == 0)
            return null;

        string wanted = TubeLibrary.Instance.Settings.SubtitleLanguage;

        return subTracks.FirstOrDefault(t => wanted.Length > 0 && t.Language == wanted && !t.Auto)
               ?? subTracks.FirstOrDefault(t => wanted.Length > 0 && t.Language == wanted)
               ?? subTracks.FirstOrDefault(t => t.Language.StartsWith("en") && !t.Auto)
               ?? subTracks.FirstOrDefault(t => !t.Auto)
               ?? subTracks[0];
    }

    private async Task ToggleSubtitlesAsync()
    {
        if (!SubtitlesAvailable)
            return;

        if (subCues.Count > 0)
        {
            SubtitlesOff();
            return;
        }

        await LoadSubtitleTracksAsync();

        SubtitleTrack? track = PickTrack();

        if (track != null && !IsDisposed)
            await ChooseSubtitleAsync(track);
    }

    private void SubtitlesOff()
    {
        subCues = new List<Cue>();
        subLabel = "";
        lastCue = -1;
        subtitleText.Visibility = System.Windows.Visibility.Collapsed;

        TubeLibrary.Instance.Settings.SubtitleLanguage = "";
        TubeLibrary.Instance.SaveNow();
        Say("Subtitles off");
    }

    private async Task ChooseSubtitleAsync(SubtitleTrack track)
    {
        string id = Current.Id;
        Say("Loading subtitles...");

        try
        {
            string text = await SubtitleHttp.GetStringAsync(track.Url);
            List<Cue> cues = track.Ext == "json3" ? ParseJson3(text) : ParseVtt(text);

            if (IsDisposed || Current.Id != id)
                return;

            if (cues.Count == 0)
            {
                Say("That subtitle track is empty");
                return;
            }

            subCues = cues;
            subLabel = track.Name;
            lastCue = -1;

            TubeLibrary.Instance.Settings.SubtitleLanguage = track.Language;
            TubeLibrary.Instance.SaveNow();
            Say("Subtitles: " + track.Name);
        }
        catch (Exception ex)
        {
            DebugLog?.Invoke("subtitle download failed: " + ex.Message);
            Say("Couldn't load those subtitles");
        }
    }

    private static List<Cue> ParseJson3(string json)
    {
        List<Cue> cues = new();

        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("events", out System.Text.Json.JsonElement events))
            return cues;

        foreach (System.Text.Json.JsonElement e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("segs", out System.Text.Json.JsonElement segs) ||
                !e.TryGetProperty("tStartMs", out System.Text.Json.JsonElement start))
            {
                continue;
            }

            string text = string.Concat(segs.EnumerateArray()
                .Select(s => s.TryGetProperty("utf8", out System.Text.Json.JsonElement u) ? u.GetString() : ""))
                .Replace('\n', ' ').Trim();

            if (text.Length == 0)
                continue;

            double from = start.GetDouble() / 1000.0;
            double length = e.TryGetProperty("dDurationMs", out System.Text.Json.JsonElement d) ? d.GetDouble() / 1000.0 : 3;

            cues.Add(new Cue(from, from + Math.Max(0.5, length), text));
        }

        return Tidy(cues);
    }

    private static readonly System.Text.RegularExpressions.Regex VttTime = new(
        @"(?:(\d+):)?(\d\d):(\d\d)\.(\d{3})\s*-->\s*(?:(\d+):)?(\d\d):(\d\d)\.(\d{3})");

    private static List<Cue> ParseVtt(string vtt)
    {
        List<Cue> cues = new();
        string[] lines = vtt.Replace("\r", "").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            System.Text.RegularExpressions.Match m = VttTime.Match(lines[i]);

            if (!m.Success)
                continue;

            double T(int g) =>
                (m.Groups[g].Success ? double.Parse(m.Groups[g].Value) * 3600 : 0) +
                double.Parse(m.Groups[g + 1].Value) * 60 +
                double.Parse(m.Groups[g + 2].Value) +
                double.Parse(m.Groups[g + 3].Value) / 1000.0;

            List<string> body = new();

            for (i++; i < lines.Length && lines[i].Trim().Length > 0; i++)
                body.Add(System.Text.RegularExpressions.Regex.Replace(lines[i], "<[^>]+>", "").Trim());

            string text = string.Join(" ", body.Where(b => b.Length > 0));

            if (text.Length > 0)
                cues.Add(new Cue(T(1), T(5), System.Net.WebUtility.HtmlDecode(text)));
        }

        return Tidy(cues);
    }

    // Sorted, and cut so two cues never overlap on screen.
    private static List<Cue> Tidy(List<Cue> cues)
    {
        cues = cues.OrderBy(c => c.Start).ToList();

        for (int i = 0; i + 1 < cues.Count; i++)
        {
            if (cues[i].End > cues[i + 1].Start)
                cues[i] = cues[i] with { End = cues[i + 1].Start };
        }

        return cues.Where(c => c.End > c.Start).ToList();
    }

    private void UpdateSubtitle(double position)
    {
        if (subCues.Count == 0)
        {
            if (subtitleText.Visibility != System.Windows.Visibility.Collapsed)
                subtitleText.Visibility = System.Windows.Visibility.Collapsed;

            lastCue = -1;
            return;
        }

        int lo = 0, hi = subCues.Count - 1, found = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;

            if (subCues[mid].Start > position)
                hi = mid - 1;
            else if (subCues[mid].End <= position)
                lo = mid + 1;
            else
            {
                found = mid;
                break;
            }
        }

        // Size follows the window; the text only changes when the cue does.
        double size = Math.Clamp(host.Height / 22.0, 18, 56);

        if (Math.Abs(subtitleText.FontSize - size) > 1)
            subtitleText.FontSize = size;

        subtitleText.MaxWidth = Math.Max(200, host.Width * 0.86);

        if (found != lastCue)
        {
            lastCue = found;

            if (found < 0)
            {
                subtitleText.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                subtitleText.Text = subCues[found].Text;
                subtitleText.Visibility = System.Windows.Visibility.Visible;
            }
        }
    }

    private void CycleFit()
    {
        TubeSettings settings = TubeLibrary.Instance.Settings;
        settings.VideoFit = (settings.VideoFit + 1) % FitNames.Length;
        TubeLibrary.Instance.SaveNow();

        ApplyFit();
        Say("Video: " + FitNames[settings.VideoFit]);
    }

    private void OnOpened()
    {
        Opened = true;

        FitWindowTo(media.NaturalVideoWidth, media.NaturalVideoHeight);
        ApplyFit();

        duration = bridged && bridgeDuration > 0
            ? bridgeDuration
            : media.NaturalDuration.HasTimeSpan
                ? media.NaturalDuration.TimeSpan.TotalSeconds
                : 0;

        // Continue where you left off (unless it was nearly finished).
        if (pendingResume > 0 && pendingResume < duration - 10)
        {
            SetPos(pendingResume);
            Say("Resuming at " + Format(pendingResume));
        }

        pendingResume = 0;

        // Count it in the watch history once it really started.
        if (!recorded)
        {
            recorded = true;

            // Local files are not YouTube history.
            if (!LocalMedia.IsLocalId(Current.Id))
                TubeLibrary.Instance.RecordWatch(Current);
        }
    }

    private void OnEnded()
    {
        DebugLog?.Invoke($"MediaEnded on {Current.Title} position={Pos:0.0}/{duration:0.0}");

        if (endedHandled)
        {
            DebugLog?.Invoke("  (duplicate end ignored)");
            return;
        }

        endedHandled = true;

        TubeVideo? inLibrary = TubeLibrary.Instance.Get(Current.Id);

        if (inLibrary != null)
            TubeLibrary.Instance.SetWatched(inLibrary, true);

        if (repeat == RepeatMode.One)
        {
            endedHandled = false;
            SetPos(0);
            media.Play();
            return;
        }

        // Repeating the whole list: wrap around after the last video.
        if (repeat == RepeatMode.Playlist && index + 1 >= playlist.Count && playlist.Count > 0)
        {
            Open(0);
            return;
        }

        SetPlaying(false);

        // Continue automatically, or let you choose what to watch next.
        ShowEndScreen(allowCountdown: true);
    }

    private void OnFailed(Exception? error)
    {
        // Direct playback was refused: convert it instead (once).
        if (fallback != null)
        {
            Func<bool> next = fallback;
            fallback = null;

            DebugLog?.Invoke("direct playback failed (" + error?.Message + "), converting");
            Say("Converting for playback...");

            if (next())
                return;
        }

        // A streamed video whose addresses went bad: look it up again,
        // quietly, before bothering you with an error.
        if (Current.File == null && !LocalMedia.IsLocalId(Current.Id) && streamRetries < 2)
        {
            streamRetries++;
            DebugLog?.Invoke("stream failed (" + error?.Message + "), looking it up again");
            Say("Retrying...");

            YtDlp.Forget(Current.Id);
            resolveCts?.Cancel();
            bridged = false;
            bridgeBase = null;
            _ = StreamAsync(Current, index);
            return;
        }

        Failure = error?.Message ?? "unknown error";

        string tail = Current.File != null
            ? "\n\nOpen it with your default video player instead?"
            : "";

        DialogResult answer = MessageBox.Show(
            this,
            "This video could not be played in the built-in player:\n\n" + Failure +
            (Failure.Contains("0xC00D", StringComparison.OrdinalIgnoreCase)
                ? "\n\nWindows could not decode this format."
                : "") + tail,
            "UtilTube",
            Current.File != null ? MessageBoxButtons.YesNo : MessageBoxButtons.OK,
            MessageBoxIcon.Warning);

        if (answer == DialogResult.Yes && Current.File != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Current.File) { UseShellExecute = true });
            }
            catch
            {
                // Nothing more to try.
            }
        }
    }

    // --------------------------------------------------
    // PLAYBACK CONTROLS
    // --------------------------------------------------

    private void SetPlaying(bool value)
    {
        if (value)
            media.Play();
        else
            media.Pause();

        playing = value;
        UpdateButtons();
    }

    private void TogglePlay()
    {
        if (imageMode)
            return;

        SetPlaying(!playing);
        Say(playing ? "Play" : "Pause");
    }

    private void UpdateButtons()
    {
        play.Text = playing ? "" : "";
        prev.Enabled = index > 0;
        next.Enabled = index + 1 < playlist.Count;
        muteButton.Text = muted || media.Volume <= 0.001 ? "" : "";

        repeatButton.Text = repeat == RepeatMode.One ? "" : "";
        repeatButton.ForeColor = repeat == RepeatMode.Off ? Theme.Text : Theme.Accent;

        speedButton.Text = FormatSpeed(speed);
        speedButton.ForeColor = Math.Abs(speed - 1.0) < 0.001 ? Theme.Strong : Theme.Accent;
    }

    private void ApplySeek(double fraction)
    {
        lastSeekApplied = Environment.TickCount64;
        lastSeekValue = fraction;

        SetPos(Math.Clamp(fraction * duration, 0, duration - 0.3));
    }

    public void SeekTo(double seconds)
    {
        if (duration <= 0)
            return;

        seekTarget = Math.Clamp(seconds, 0, duration - 0.3);
        seekHoldUntil = Environment.TickCount64 + 8000;

        SetPos(seekTarget);
    }

    private void Skip(double seconds)
    {
        SeekTo(Pos + seconds);
        Say((seconds > 0 ? "+" : "") + seconds.ToString("0") + " s");
    }

    private void SetVolume(double value)
    {
        value = Math.Clamp(value, 0, 1);

        lastVolume = value;
        media.Volume = value;
        media.IsMuted = false;
        muted = false;
        volumeBar.Value = value;

        // Remembered at once, for this and every later video.
        TubeLibrary.Instance.Settings.DefaultVolume = (int)Math.Round(value * 100);
        TubeLibrary.Instance.SettingsChanged();

        Say($"Volume {value * 100:0}%");
        UpdateButtons();
    }

    private void ToggleMute()
    {
        muted = !muted;
        media.IsMuted = muted;
        Say(muted ? "Muted" : $"Volume {media.Volume * 100:0}%");
        UpdateButtons();
    }

    // ---- speed ----

    public void SetSpeed(double value)
    {
        speed = Math.Round(Math.Clamp(value, 0.1, 4.0), 2);
        lastSpeed = speed;

        ApplySpeed();
        Say("Speed " + FormatSpeed(speed));

        TubeSettings settings = TubeLibrary.Instance.Settings;

        if (settings.RememberChannelSpeed)
            TubeLibrary.Instance.SetChannelSpeed(Current.Channel, speed);
    }

    private void ApplySpeed()
    {
        media.SpeedRatio = speed;
        UpdateButtons();
    }

    private static string FormatSpeed(double value) =>
        value.ToString("0.##") + "x";

    private void AskCustomSpeed()
    {
        string? answer = TextPrompt.Ask(
            this, "Playback speed",
            "Speed from 0.1 to 4  (for example 1.37):",
            speed.ToString("0.##"));

        if (answer != null &&
            double.TryParse(answer.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value))
        {
            SetSpeed(value);
        }
    }

    // ---- repeat / A-B loop ----

    public void CycleRepeat()
    {
        repeat = repeat switch
        {
            RepeatMode.Off => RepeatMode.One,
            RepeatMode.One => RepeatMode.Playlist,
            _ => RepeatMode.Off
        };

        UpdateButtons();

        Say(repeat switch
        {
            RepeatMode.One => "Repeat: this video",
            RepeatMode.Playlist => "Repeat: whole list",
            _ => "Repeat: off"
        });
    }

    /// <summary>First call sets A, second sets B and starts looping, third clears.</summary>
    public void StepAbLoop()
    {
        double now = Pos;

        if (abA < 0)
        {
            abA = now;
            Say("Loop start (A) set. Press A again for the end.");
        }
        else if (abB < 0)
        {
            if (now <= abA + 0.3)
            {
                Say("Pick an end point after A.");
                return;
            }

            abB = now;
            Say($"Looping {Format(abA)} - {Format(abB)}");
        }
        else
        {
            abA = abB = -1;
            Say("Loop cleared");
        }

        UpdateMarks();
    }

    private void UpdateMarks()
    {
        double a = duration > 0 && abA >= 0 ? abA / duration : -1;
        double b = duration > 0 && abB >= 0 ? abB / duration : -1;

        if (a != seek.MarkA || b != seek.MarkB)
        {
            seek.MarkA = a;
            seek.MarkB = b;
            seek.Invalidate();
        }
    }

    // ---- frame step ----

    private void StepFrame(int direction)
    {
        SetPlaying(false);
        SeekTo(Pos + direction / 30.0);
        Say(direction > 0 ? "Next frame" : "Previous frame");
    }

    // ---- sleep timer ----

    private void SetSleep(int minutes)
    {
        if (minutes <= 0)
        {
            sleepAt = null;
            Say("Sleep timer off");
            return;
        }

        sleepAt = DateTime.Now.AddMinutes(minutes);
        Say($"Sleep timer: pause in {minutes} min");
    }

    // ---- snapshot ----

    public string? Snapshot()
    {
        try
        {
            int w = (int)media.ActualWidth;
            int h = (int)media.ActualHeight;

            if (w < 2 || h < 2)
                return null;

            WpfImaging.RenderTargetBitmap bitmap =
                new(w, h, 96, 96, WpfMedia.PixelFormats.Pbgra32);

            bitmap.Render(media);

            WpfImaging.PngBitmapEncoder encoder = new();
            encoder.Frames.Add(WpfImaging.BitmapFrame.Create(bitmap));

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "UtilMaster3000");

            Directory.CreateDirectory(folder);

            string safe = string.Concat(Current.Title.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

            string path = Path.Combine(
                folder,
                $"{(safe.Length > 40 ? safe[..40] : safe)} {Format(Pos).Replace(':', '-')}.png");

            using FileStream stream = File.Create(path);
            encoder.Save(stream);

            Say("Snapshot saved");
            return path;
        }
        catch (Exception ex)
        {
            Say("Snapshot failed: " + ex.Message);
            return null;
        }
    }

    // ---- audio boost (saved videos only) ----

    public async Task BoostAsync(double level)
    {
        TubeVideo video = Current;

        if (video.File == null || !File.Exists(video.File))
        {
            Say("Boost needs a saved video (press D in the library first)");
            return;
        }

        if (bridged)
        {
            Say("Boost is not available while the video is being converted");
            return;
        }

        if (Math.Abs(level - boost) < 0.01 || boosting)
            return;

        // Back to normal: just play the original file.
        if (Math.Abs(level - 1.0) < 0.01)
        {
            SwapSource(video.File, 1.0);
            return;
        }

        string? ffmpeg = FfmpegRecorder.FindFfmpeg();

        if (ffmpeg == null)
        {
            Say("Boost needs ffmpeg");
            return;
        }

        string target = Path.Combine(
            Path.GetDirectoryName(video.File)!,
            $"{video.Id}.boost{(int)(level * 100)}.mp4");

        if (!File.Exists(target))
        {
            boosting = true;
            Say($"Boosting audio x{level:0.#} ... (this takes a moment)");

            try
            {
                ProcessStartInfo info = new(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                // Video is copied untouched; only the audio is re-encoded.
                // The limiter stops loud parts from distorting.
                foreach (string arg in new[]
                {
                    "-y", "-i", video.File, "-c:v", "copy",
                    "-af", $"volume={level.ToString(System.Globalization.CultureInfo.InvariantCulture)},alimiter=limit=0.97",
                    "-c:a", "aac", "-b:a", "192k", target + ".part.mp4"
                })
                {
                    info.ArgumentList.Add(arg);
                }

                using Process process = Process.Start(info)!;
                ChildJob.Add(process);

                process.ErrorDataReceived += (_, _) => { };
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("ffmpeg could not process this video.");

                File.Move(target + ".part.mp4", target, true);
            }
            catch (Exception ex)
            {
                boosting = false;
                OnUi(() => Say("Boost failed: " + ex.Message));

                try
                {
                    File.Delete(target + ".part.mp4");
                }
                catch
                {
                    // Ignore.
                }

                return;
            }

            boosting = false;
        }

        if (IsDisposed)
            return;

        OnUi(() => SwapSource(target, level));
    }

    // Plays another file of the same video from the same position.
    private void SwapSource(string file, double level)
    {
        double position = Pos;
        bool wasPlaying = playing;

        boost = level;
        pendingResume = position;

        media.Source = new Uri(file);
        ApplySpeed();

        if (wasPlaying)
            media.Play();
        else
            media.Pause();

        Say(level > 1.0 ? $"Audio boost x{level:0.#}" : "Audio boost off");
    }

    private void CycleBoost()
    {
        int next = (Array.IndexOf(BoostLevels, boost) + 1) % BoostLevels.Length;
        _ = BoostAsync(BoostLevels[next]);
    }

    // --------------------------------------------------
    // UP NEXT  (autocontinue + choose the next video)
    // --------------------------------------------------

    public bool EndScreenVisible => endScreen.Visibility == System.Windows.Visibility.Visible;
    public bool EndCounting => endCounting;
    public int EndRemaining => endRemaining;
    public IReadOnlyList<TubeVideo> EndChoices => endChoices;
    public string CurrentId => Current.Id;

    private static WpfMedia.SolidColorBrush Brush(Color c) =>
        new(WpfMedia.Color.FromRgb(c.R, c.G, c.B));

    private void BuildEndScreen()
    {
        WpfControls.StackPanel panel = new()
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };

        endTitle.FontSize = 22;
        endTitle.FontWeight = System.Windows.FontWeights.SemiBold;
        endTitle.Foreground = WpfMedia.Brushes.White;
        endTitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        endTitle.Margin = new System.Windows.Thickness(0, 0, 0, 4);

        endSubtitle.FontSize = 13;
        endSubtitle.Foreground = Brush(Theme.Text);
        endSubtitle.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        endSubtitle.Margin = new System.Windows.Thickness(0, 0, 0, 16);

        endCards.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;

        WpfControls.StackPanel buttons = new()
        {
            Orientation = WpfControls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 18, 0, 0)
        };

        buttons.Children.Add(EndButton("Play now", true, () =>
        {
            if (endChoices.Count > 0)
                PlayChoice(endChoices[0]);
        }));

        buttons.Children.Add(EndButton("Replay", false, ReplayCurrent));

        buttons.Children.Add(EndButton("Stay here", false, CancelEndScreen));

        panel.Children.Add(endTitle);
        panel.Children.Add(endSubtitle);
        panel.Children.Add(endCards);
        panel.Children.Add(buttons);

        // In a small window everything shrinks to fit instead of being cut off.
        WpfControls.Viewbox fit = new()
        {
            Child = panel,
            Stretch = WpfMedia.Stretch.Uniform,
            StretchDirection = WpfControls.StretchDirection.DownOnly,
            Margin = new System.Windows.Thickness(24)
        };

        endScreen.Children.Add(fit);

        endTimer.Tick += (_, _) => OnEndTick();
    }

    private WpfControls.Button EndButton(string text, bool primary, Action click)
    {
        WpfControls.Button button = new()
        {
            Content = text,
            FontSize = 14,
            Padding = new System.Windows.Thickness(20, 8, 20, 8),
            Margin = new System.Windows.Thickness(6, 0, 6, 0),
            BorderThickness = new System.Windows.Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = primary ? Brush(Theme.Background) : WpfMedia.Brushes.White,
            Background = primary ? Brush(Theme.Accent) : Brush(Theme.Search),
            Focusable = false
        };

        button.Click += (_, _) => click();
        return button;
    }

    // True when the first suggestions come from your YouTube recommendations.
    private bool endFromRecommendations;

    // What could play next: your recommendations first (only videos you have
    // not watched), then your subscriptions, then your own lists.
    private List<TubeVideo> BuildChoices(int max)
    {
        List<TubeVideo> result = new();
        TubeLibrary lib = TubeLibrary.Instance;

        // Everything you already watched: recommendations should be new.
        HashSet<string> watched = lib.History().Select(h => h.Id).ToHashSet();

        foreach (TubeVideo v in lib.Snapshot().Where(v => v.Watched))
            watched.Add(v.Id);

        HashSet<string> seen = new() { Current.Id };

        // A local video: what plays next is the rest of its folder.
        if (LocalMedia.IsLocalId(Current.Id))
        {
            endFromRecommendations = false;

            for (int i = 1; i < playlist.Count && result.Count < max; i++)
            {
                TubeVideo v = playlist[(index + i) % playlist.Count];

                if (seen.Add(v.Id))
                    result.Add(v);
            }

            return result;
        }

        void Add(TubeVideo v)
        {
            if (result.Count < max && seen.Add(v.Id))
                result.Add(v);
        }

        TubeVideo Resolve(VideoInfo info) => lib.Get(info.Id) ?? TubeVideo.From(info);

        // 1. recommended for you, not watched yet
        foreach (VideoInfo info in lib.HomeFeed.Where(i => !watched.Contains(i.Id)))
            Add(Resolve(info));

        endFromRecommendations = result.Count > 0;

        // 2. new videos from channels you follow
        foreach (VideoInfo info in lib.SubsFeed.Where(i => !watched.Contains(i.Id)))
            Add(Resolve(info));

        // 3. still not enough (signed out / small feed): what is lined up,
        //    saved videos you have not watched, and your Watch later list
        for (int i = index + 1; i < playlist.Count; i++)
            Add(playlist[i]);

        foreach (TubeVideo v in lib.Snapshot()
                     .Where(v => v.State == VideoState.Done && !v.Watched && v.File != null)
                     .OrderByDescending(v => v.Downloaded))
        {
            Add(v);
        }

        foreach (VideoInfo info in lib.WatchLater())
            Add(Resolve(info));

        return result;
    }

    /// <summary>
    /// Shows the "Up next" screen. With autoplay on it counts down and then
    /// continues by itself; any card can be clicked to pick another video.
    /// </summary>
    public void ShowEndScreen(bool allowCountdown)
    {
        endChoices = BuildChoices(6);

        DebugLog?.Invoke($"ShowEndScreen(allowCountdown={allowCountdown}) autoplay={TubeLibrary.Instance.Settings.Autoplay} choices=[{string.Join(",", endChoices.Select(c => c.Title))}]");

        endCards.Children.Clear();
        endThumbs.Clear();

        if (endChoices.Count == 0)
        {
            // Nothing to suggest: just offer replay.
            endTitle.Text = "That was the last one";
        }

        for (int i = 0; i < endChoices.Count; i++)
            endCards.Children.Add(MakeCard(endChoices[i], i == 0));

        TubeSettings settings = TubeLibrary.Instance.Settings;

        bool auto = allowCountdown && settings.Autoplay && endChoices.Count > 0;

        endScreen.Visibility = System.Windows.Visibility.Visible;

        if (auto && settings.AutoplayDelay <= 0)
        {
            PlayChoice(endChoices[0]);
            return;
        }

        if (auto)
        {
            endRemaining = settings.AutoplayDelay;
            endCounting = true;
        }
        else
        {
            endCounting = false;
        }

        UpdateEndTitle();
        endTimer.Start();
    }

    private WpfControls.Button MakeCard(TubeVideo video, bool first)
    {
        WpfControls.Image image = new()
        {
            Width = 176,
            Height = 99,
            Stretch = WpfMedia.Stretch.UniformToFill
        };

        endThumbs.Add((image, video));
        FillThumb(image, video);

        WpfControls.TextBlock title = new()
        {
            Text = video.Title,
            Foreground = WpfMedia.Brushes.White,
            FontSize = 12,
            FontWeight = System.Windows.FontWeights.SemiBold,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Width = 176,
            Margin = new System.Windows.Thickness(0, 6, 0, 0)
        };

        WpfControls.TextBlock channel = new()
        {
            Text = video.Channel,
            Foreground = Brush(Theme.Text),
            FontSize = 11,
            Width = 176,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis
        };

        WpfControls.StackPanel stack = new();
        stack.Children.Add(image);
        stack.Children.Add(title);
        stack.Children.Add(channel);

        WpfControls.Button card = new()
        {
            Content = stack,
            Padding = new System.Windows.Thickness(6),
            Margin = new System.Windows.Thickness(6),
            Background = WpfMedia.Brushes.Transparent,
            BorderBrush = first ? Brush(Theme.Accent) : WpfMedia.Brushes.Transparent,
            BorderThickness = new System.Windows.Thickness(2),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            Tag = video
        };

        card.Click += (_, _) => PlayChoice(video);
        return card;
    }

    private void FillThumb(WpfControls.Image image, TubeVideo video)
    {
        Bitmap? bitmap = ThumbCache.TryGet(video.Id);

        if (bitmap == null)
            return;

        IntPtr handle = bitmap.GetHbitmap();

        try
        {
            image.Source = WpfInterop.Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    private void UpdateEndTitle()
    {
        endTitle.Text = endCounting && endChoices.Count > 0
            ? $"Up next in {endRemaining}"
            : endChoices.Count > 0 ? "Choose what to watch next" : "That was the last one";

        endSubtitle.Text = endChoices.Count == 0
            ? ""
            : (endFromRecommendations ? "Recommended for you" : "Suggestions")
              + "  -  click one to play it now"
              + (endCounting ? "  -  or wait to play the first" : "");
    }

    private void OnEndTick()
    {
        DebugLog?.Invoke($"endTick counting={endCounting} remaining={endRemaining} visible={EndScreenVisible}");

        // Thumbnails that were not ready yet appear as soon as they load.
        foreach ((WpfControls.Image image, TubeVideo video) in endThumbs)
        {
            if (image.Source == null)
                FillThumb(image, video);
        }

        if (!endCounting)
            return;

        endRemaining--;

        if (endRemaining <= 0)
        {
            endTimer.Stop();
            endCounting = false;

            if (endChoices.Count > 0)
                PlayChoice(endChoices[0]);

            return;
        }

        UpdateEndTitle();
    }

    /// <summary>Plays a video right after this one (or the next in the list).</summary>
    public void PlayChoice(TubeVideo video)
    {
        DebugLog?.Invoke($"PlayChoice {video.Title} (from {Environment.StackTrace.Split('\n')[2].Trim()})");

        int at = playlist.IndexOf(video);

        if (at < 0)
        {
            playlist.Insert(index + 1, video);
            at = index + 1;
        }

        Open(at);
    }

    public void ChooseNext(int choice)
    {
        if (choice >= 0 && choice < endChoices.Count)
            PlayChoice(endChoices[choice]);
    }

    private void HideEndScreen()
    {
        if (EndScreenVisible)
            DebugLog?.Invoke("HideEndScreen from " + Environment.StackTrace.Split('\n')[2].Trim());

        endTimer.Stop();
        endCounting = false;
        endScreen.Visibility = System.Windows.Visibility.Collapsed;
    }

    // Stops the countdown but keeps the choices on screen.
    private void CancelEndScreen()
    {
        if (endCounting)
        {
            endCounting = false;
            UpdateEndTitle();
        }
        else
        {
            HideEndScreen();
        }
    }

    private void ReplayCurrent()
    {
        HideEndScreen();
        endedHandled = false;
        SetPos(0);
        SetPlaying(true);
    }

    // --------------------------------------------------
    // TICK
    // --------------------------------------------------

    private void WatchForStall(double position)
    {
        if (!bridged || bridgeBase == null || !playing || seekTarget >= 0)
        {
            watchPosition = -1;
            return;
        }

        long now = Environment.TickCount64;

        // A new stream is still starting up (seeking inside it takes a few seconds).
        if (now - restartedAt < 12000)
        {
            watchPosition = position;
            watchSince = now;
            return;
        }

        if (watchPosition < 0 || position > watchPosition + 0.05 || position < watchPosition - 0.5)
        {
            watchPosition = position;
            watchSince = now;
            return;
        }

        bool justUpgraded = beforeUpgrade != null && now - upgradedAt < 25000;

        if (now - watchSince < (justUpgraded ? 4500 : 7000) || position >= duration - 3 || stallRestarts >= 4)
            return;

        stallRestarts++;
        watchSince = now;

        // Just after a quality upgrade: the better stream is the suspect.
        if (beforeUpgrade != null && now - upgradedAt < 25000)
        {
            bridgeBase = beforeUpgrade;
            beforeUpgrade = null;
            DebugLog?.Invoke($"stalled after upgrade at {position:0.0}s, going back");
            Say("Back to the smoother quality");
        }
        else
        {
            DebugLog?.Invoke($"stalled at {position:0.0}s, reconnecting");
            Say("Reconnecting...");
        }

        RestartBridge(position);
        watchPosition = -1;
    }

    private void OnTick()
    {
        // Show the controls again when the mouse moves in fullscreen.
        Point mouse = Cursor.Position;

        if (mouse != lastMouse)
        {
            lastMouse = mouse;
            lastMove = Environment.TickCount64;

            if (!bar.Visible)
                bar.Visible = true;
        }
        else if ((fullscreen || playing && Opened) && bar.Visible &&
                 Environment.TickCount64 - lastMove > 2500 &&
                 !bar.RectangleToScreen(bar.ClientRectangle).Contains(mouse))
        {
            // The controls get out of the way while a video plays.
            bar.Visible = false;
        }

        if (sleepAt != null && DateTime.Now >= sleepAt)
        {
            sleepAt = null;
            SetPlaying(false);
            Say("Sleep timer: paused");
        }

        if (!Opened || duration <= 0)
            return;

        double position = Pos;

        WatchForStall(position);
        UpdateSubtitle(position);

        // Moved away from the end (seek, replay): the next end counts again.
        if (endedHandled && position < duration - 2.0)
            endedHandled = false;

        // Loop a section.
        if (abA >= 0 && abB > abA && position >= abB)
        {
            SetPos(abA);
            position = abA;
        }

        // Show the seek target until the video has really arrived there.
        double shown = position;

        if (seekTarget >= 0)
        {
            bool arrived = Math.Abs(position - seekTarget) < 2.5;
            bool expired = Environment.TickCount64 > seekHoldUntil;

            if (arrived || expired)
                seekTarget = -1;
            else
                shown = seekTarget;
        }

        if (!seek.Dragging)
            seek.Value = shown / duration;

        UpdateMarks();

        time.Text = $"{Format(shown)} / {Format(duration)}";

        // Remember the position every few seconds.
        if (playing && Environment.TickCount64 - lastSave > 5000)
            SaveProgress();
    }

    private void SaveProgress()
    {
        lastSave = Environment.TickCount64;

        if (!Opened || duration <= 0)
            return;

        double position = Pos;

        // Nearly finished counts as watched.
        if (position > duration - 10)
            return;

        TubeVideo? tracked = TubeLibrary.Instance.Get(Current.Id);

        if (position > 5 && tracked != null)
            TubeLibrary.Instance.SetPosition(tracked, position);
    }

    private static string Format(double seconds)
    {
        TimeSpan t = TimeSpan.FromSeconds(Math.Max(0, seconds));

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    // --------------------------------------------------
    // MENUS
    // --------------------------------------------------

    private void ShowSpeedMenu(Point where)
    {
        ContextMenuStrip menu = new();

        foreach (double preset in SpeedPresets)
        {
            double value = preset;

            ToolStripMenuItem item = new(FormatSpeed(value) + (value == 1.0 ? "  (normal)" : ""))
            {
                Checked = Math.Abs(speed - value) < 0.001
            };

            item.Click += (_, _) => SetSpeed(value);
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Custom...", null, (_, _) => AskCustomSpeed());

        ToolStripMenuItem remember = new("Remember speed per channel")
        {
            Checked = TubeLibrary.Instance.Settings.RememberChannelSpeed,
            CheckOnClick = true
        };

        remember.Click += (_, _) =>
        {
            TubeLibrary.Instance.Settings.RememberChannelSpeed = remember.Checked;
            TubeLibrary.Instance.SettingsChanged();
        };

        menu.Items.Add(remember);
        menu.Show(where);
    }

    private void ShowMenu(Point where)
    {
        ContextMenuStrip menu = new();
        TubeVideo video = Current;
        bool saved = video.File != null && File.Exists(video.File);

        // repeat
        ToolStripMenuItem repeatMenu = new("Repeat");

        foreach ((RepeatMode mode, string label) in new[]
        {
            (RepeatMode.Off, "Off"),
            (RepeatMode.One, "This video"),
            (RepeatMode.Playlist, "Whole list")
        })
        {
            RepeatMode captured = mode;

            ToolStripMenuItem item = new(label) { Checked = repeat == captured };

            item.Click += (_, _) =>
            {
                repeat = captured;
                UpdateButtons();
                Say("Repeat: " + label.ToLower());
            };

            repeatMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(repeatMenu);

        // A-B
        string abText = abA < 0 ? "Loop a section: set start (A)"
            : abB < 0 ? "Loop a section: set end (B)"
            : "Loop a section: clear";

        menu.Items.Add(abText, null, (_, _) => StepAbLoop());

        // speed
        ToolStripMenuItem speedMenu = new($"Speed ({FormatSpeed(speed)})");

        foreach (double preset in SpeedPresets)
        {
            double value = preset;

            ToolStripMenuItem item = new(FormatSpeed(value)) { Checked = Math.Abs(speed - value) < 0.001 };
            item.Click += (_, _) => SetSpeed(value);
            speedMenu.DropDownItems.Add(item);
        }

        speedMenu.DropDownItems.Add(new ToolStripSeparator());
        speedMenu.DropDownItems.Add("Custom...", null, (_, _) => AskCustomSpeed());
        menu.Items.Add(speedMenu);

        // audio boost
        ToolStripMenuItem boostMenu = new(saved
            ? $"Audio boost ({(boost > 1 ? "x" + boost.ToString("0.#") : "off")})"
            : "Audio boost (needs a saved video)")
        {
            Enabled = saved
        };

        foreach (double level in BoostLevels)
        {
            double value = level;

            ToolStripMenuItem item = new(value == 1.0 ? "Off (normal)" : $"x{value:0.#} louder")
            {
                Checked = Math.Abs(boost - value) < 0.01
            };

            item.Click += (_, _) => _ = BoostAsync(value);
            boostMenu.DropDownItems.Add(item);
        }

        menu.Items.Add(boostMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Choose the next video...   (U)", null, (_, _) =>
        {
            SetPlaying(false);
            ShowEndScreen(allowCountdown: false);
        });

        menu.Items.Add(BuildSubtitleMenu(where));
        menu.Items.Add("Video fills window: " + FitNames[TubeLibrary.Instance.Settings.VideoFit] + "   (Z)", null, (_, _) => CycleFit());
        menu.Items.Add("Open a video file...   (Ctrl+O)", null, (_, _) => OpenFileDialog());
        menu.Items.Add("Skip ahead 85 s   (I)", null, (_, _) => Skip(85));
        menu.Items.Add("Take snapshot   (S)", null, (_, _) => Snapshot());
        menu.Items.Add("Step one frame   (. and ,)", null, (_, _) => StepFrame(1));

        // sleep timer
        ToolStripMenuItem sleepMenu = new("Sleep timer");

        foreach ((int minutes, string label) in new[] { (0, "Off"), (15, "15 minutes"), (30, "30 minutes"), (60, "1 hour") })
        {
            int captured = minutes;
            sleepMenu.DropDownItems.Add(label, null, (_, _) => SetSleep(captured));
        }

        menu.Items.Add(sleepMenu);

        ToolStripMenuItem onTop = new("Always on top   (T)") { Checked = TopMost };
        onTop.Click += (_, _) => ToggleTopMost();
        menu.Items.Add(onTop);

        ToolStripMenuItem auto = new("Play next video automatically")
        {
            Checked = TubeLibrary.Instance.Settings.Autoplay
        };

        auto.Click += (_, _) =>
        {
            TubeLibrary.Instance.Settings.Autoplay = !TubeLibrary.Instance.Settings.Autoplay;
            TubeLibrary.Instance.SettingsChanged();
        };

        menu.Items.Add(auto);
        menu.Items.Add(new ToolStripSeparator());

        if (!saved)
        {
            menu.Items.Add("Save this video offline", null, (_, _) =>
            {
                TubeLibrary.Instance.Enqueue(video, priority: true);
                Say("Saving for offline viewing...");
            });
        }

        menu.Items.Add("Open on YouTube", null, (_, _) =>
            Process.Start(new ProcessStartInfo(video.Url) { UseShellExecute = true }));

        menu.Items.Add("Copy link", null, (_, _) =>
        {
            try
            {
                Clipboard.SetText(video.Url);
                Say("Link copied");
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard busy.
            }
        });

        menu.Items.Add("Keyboard shortcuts   (F1)", null, (_, _) => ShowHelp());

        menu.Show(where);
    }

    private void ShowHelp()
    {
        MessageBox.Show(
            this,
            "Play / pause          Space or K, or click the video\n" +
            "Seek                  Left / Right  -5 / +5 s       J / L  -10 / +10 s\n" +
            "                      Shift+arrows  30 s            Ctrl+arrows  60 s\n" +
            "Jump to a position    0 - 9  (0 = start, 5 = halfway ...)\n" +
            "Skip ahead 85 s       I\n" +
            "Volume                Up / Down, or the mouse wheel over the video\n" +
            "Mute                  M\n" +
            "Speed                 [  ]  fine (0.05)    <  >  coarse (0.25)    Backspace = 1x\n" +
            "Repeat                R   (off, this video, whole list)\n" +
            "Loop a section        A   (press for start, again for end, again to clear)\n" +
            "Audio boost           V   (saved videos only)\n" +
            "Frame step            .  next    ,  previous\n" +
            "Snapshot              S\n" +
            "Open a video file      Ctrl+O   (or drop files on the window)\n" +
            "Fullscreen            F or double-click\n" +
            "Always on top         T\n" +
            "Next / previous       N  /  P\n" +
            "Choose the next video U   (when a video ends: Enter = play, 1-6 = pick, Esc = stay)\n" +
            "Close                 Esc",
            "UtilTube shortcuts",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ToggleTopMost()
    {
        TopMost = !TopMost;
        Say(TopMost ? "Always on top" : "Normal window");
    }

    // --------------------------------------------------
    // FULLSCREEN + KEYS
    // --------------------------------------------------

    private void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            savedBorder = FormBorderStyle;
            savedState = WindowState;
            savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;

            Rectangle screen = Screen.FromControl(this).Bounds;

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = screen;

            fullscreen = true;
            lastMove = Environment.TickCount64;
        }
        else
        {
            fullscreen = false;
            bar.Visible = true;

            // Back to how it was: usually the maximized "full windowed" mode.
            FormBorderStyle = savedBorder;
            Bounds = savedBounds;
            WindowState = savedState;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0;
        bool ctrl = (keyData & Keys.Control) != 0;

        // The "Up next" screen: Enter = play, 1-5 = pick one, Esc = stay.
        if (EndScreenVisible)
        {
            if (keyData == Keys.Enter && endChoices.Count > 0)
            {
                PlayChoice(endChoices[0]);
                return true;
            }

            if (!shift && !ctrl && key >= Keys.D1 && key <= Keys.D6)
            {
                ChooseNext(key - Keys.D1);
                return true;
            }

            if (keyData == Keys.Space)
            {
                ReplayCurrent();
                return true;
            }

            if (keyData == Keys.Escape)
            {
                CancelEndScreen();
                return true;
            }
        }

        // 0 - 9: jump to that tenth of the video.
        if (!shift && !ctrl && key >= Keys.D0 && key <= Keys.D9)
        {
            SeekTo(duration * (key - Keys.D0) / 10.0);
            Say($"{(key - Keys.D0) * 10}%");
            return true;
        }

        switch (keyData)
        {
            case Keys.Space:
            case Keys.K:
                TogglePlay();
                return true;

            case Keys.Control | Keys.O: OpenFileDialog(); return true;

            case Keys.Z: CycleFit(); return true;
            case Keys.C: _ = ToggleSubtitlesAsync(); return true;

            case Keys.Control | Keys.C:
                if (imageMode)
                {
                    CopyPicture();
                    return true;
                }

                break;

            case Keys.Left: Skip(-5); return true;
            case Keys.Right: Skip(5); return true;
            case Keys.J: Skip(-10); return true;
            case Keys.L: Skip(10); return true;
            case Keys.Shift | Keys.Left: Skip(-30); return true;
            case Keys.Shift | Keys.Right: Skip(30); return true;
            case Keys.Control | Keys.Left: Skip(-60); return true;
            case Keys.Control | Keys.Right: Skip(60); return true;
            case Keys.I: Skip(85); return true;
            case Keys.U: SetPlaying(false); ShowEndScreen(allowCountdown: false); return true;

            case Keys.Up: SetVolume(media.Volume + 0.05); return true;
            case Keys.Down: SetVolume(media.Volume - 0.05); return true;
            case Keys.M: ToggleMute(); return true;

            case Keys.OemOpenBrackets: SetSpeed(speed - 0.05); return true;
            case Keys.OemCloseBrackets: SetSpeed(speed + 0.05); return true;
            case Keys.Shift | Keys.Oemcomma: SetSpeed(speed - 0.25); return true;
            case Keys.Shift | Keys.OemPeriod: SetSpeed(speed + 0.25); return true;
            case Keys.Back: SetSpeed(1.0); return true;

            case Keys.R: CycleRepeat(); return true;
            case Keys.A: StepAbLoop(); return true;
            case Keys.V: CycleBoost(); return true;

            case Keys.OemPeriod: StepFrame(1); return true;
            case Keys.Oemcomma: StepFrame(-1); return true;

            case Keys.S: Snapshot(); return true;

            case Keys.F:
            case Keys.F11:
                ToggleFullscreen();
                return true;

            case Keys.T: ToggleTopMost(); return true;
            case Keys.N: Open(index + 1); return true;
            case Keys.P: Open(index - 1); return true;
            case Keys.F1: ShowHelp(); return true;

            case Keys.Escape:
                if (fullscreen)
                    ToggleFullscreen();
                else
                    Close();

                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowStyle.ApplyTitleBar(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tick.Stop();
        tick.Dispose();
        osdTimer.Stop();
        osdTimer.Dispose();
        endTimer.Stop();
        endTimer.Dispose();

        resolveCts?.Cancel();

        SaveProgress();
        TubeLibrary.NowPlayingId = null;

        // Remember the volume for next time.
        if (lastVolume >= 0)
        {
            TubeLibrary.Instance.Settings.DefaultVolume = (int)Math.Round(lastVolume * 100);
            TubeLibrary.Instance.SettingsChanged();
        }

        // Release the decoder and its buffers right away.
        media.Close();
        media.Source = null;
        host.Dispose();

        proxy?.Dispose();
        proxy = null;

        base.OnFormClosed(e);

        MemoryTrimmer.TrimSoon();
    }
}

// ======================================================
// SEEK / VOLUME BAR  (with A-B loop markers)
// ======================================================

internal sealed class SeekBar : Control
{
    private double value;

    public bool Dragging { get; private set; }

    /// <summary>A-B loop markers as fractions (0..1), or -1 when not set.</summary>
    public double MarkA { get; set; } = -1;
    public double MarkB { get; set; } = -1;

    /// <summary>Raised when the user drags or clicks (value 0..1).</summary>
    public event Action<double>? UserChanged;

    /// <summary>Raised once when the mouse button is released.</summary>
    public event Action<double>? DragCompleted;

    public double Value
    {
        get => value;
        set
        {
            double clamped = Math.Clamp(value, 0, 1);

            if (Math.Abs(clamped - this.value) < 0.0005)
                return;

            this.value = clamped;
            Invalidate();
        }
    }

    public SeekBar()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Cursor = Cursors.Hand;
        TabStop = false;
    }

    private void SetFrom(int x)
    {
        int usable = Math.Max(1, Width - 16);
        Value = (x - 8) / (double)usable;
        UserChanged?.Invoke(Value);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        Dragging = true;
        Capture = true;
        SetFrom(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (Dragging)
            SetFrom(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        bool wasDragging = Dragging;

        Dragging = false;
        Capture = false;

        if (wasDragging && e.Button == MouseButtons.Left)
            DragCompleted?.Invoke(Value);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;

        g.Clear(Theme.Background);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        int left = 8;
        int right = Width - 8;
        int mid = Height / 2;
        int span = right - left;

        float thin = Height < 24 ? 3f : 4f;

        using Pen track = new(Theme.Number, thin)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        g.DrawLine(track, left, mid, right, mid);

        // the looped section
        if (MarkA >= 0)
        {
            int a = left + (int)(span * MarkA);
            int b = MarkB >= 0 ? left + (int)(span * MarkB) : a;

            using Pen section = new(Theme.On, thin + 2f);

            if (b > a)
                g.DrawLine(section, a, mid, b, mid);

            using SolidBrush marker = new(Theme.On);
            g.FillRectangle(marker, a - 1, mid - 6, 3, 12);

            if (MarkB >= 0)
                g.FillRectangle(marker, b - 1, mid - 6, 3, 12);
        }

        int x = left + (int)(span * value);

        using Pen fill = new(Theme.Accent, thin)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round
        };

        if (x > left)
            g.DrawLine(fill, left, mid, x, mid);

        using SolidBrush thumb = new(Theme.Strong);
        int radius = Height < 24 ? 5 : 6;
        g.FillEllipse(thumb, x - radius, mid - radius, radius * 2, radius * 2);
    }
}
