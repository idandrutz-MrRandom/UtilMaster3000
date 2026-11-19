using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UtilMaster3000;

internal enum VideoState
{
    None,
    Queued,
    Downloading,
    Done,
    Failed
}

internal sealed class TubeVideo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public int Duration { get; set; }

    public VideoState State { get; set; }
    public string? File { get; set; }
    public long Size { get; set; }

    public bool Watched { get; set; }
    public double Position { get; set; }

    // When it was finished: the auto-delete clock starts here.
    public DateTime? WatchedAt { get; set; }

    // "Keep": never delete this video automatically.
    public bool Keep { get; set; }

    public DateTime Added { get; set; } = DateTime.Now;
    public DateTime? Downloaded { get; set; }

    public string? SourceId { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }

    // Live state, not saved.
    [JsonIgnore] public double Progress { get; set; }
    [JsonIgnore] public string Speed { get; set; } = "";
    [JsonIgnore] public string Eta { get; set; } = "";
    [JsonIgnore] public DateTime RetryAt { get; set; }
    [JsonIgnore] public long Order { get; set; }

    // Extra line shown in lists such as History ("Watched 2 h ago").
    [JsonIgnore] public string? Note { get; set; }

    public static TubeVideo From(VideoInfo info) => new()
    {
        Id = info.Id,
        Title = info.Title,
        Channel = info.Channel,
        Duration = info.Duration
    };

    public string Url => "https://www.youtube.com/watch?v=" + Id;
}

internal sealed class HistoryEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public int Duration { get; set; }
    public DateTime WatchedAt { get; set; } = DateTime.Now;
}

internal sealed class TubeSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>"channel", "playlist" or "search".</summary>
    public string Kind { get; set; } = "channel";

    public string Value { get; set; } = "";
    public string Title { get; set; } = "";
    public int Count { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public DateTime? LastRefresh { get; set; }
    public string? LastError { get; set; }
}

internal sealed class TubeSettings
{
    // Highest resolution to download (H.264 / MP4).
    public int Quality { get; set; } = 720;

    // How video fills the player: 0 = auto, 1 = fill (crop), 2 = fit (bars).
    public int VideoFit { get; set; }

    // Simultaneous downloads. One keeps memory, CPU and network light.
    public int Parallel { get; set; } = 1;

    // Offline library size limit; watched videos are removed first.
    public int MaxGb { get; set; } = 20;

    // Download speed limit in KB/s (0 = unlimited).
    public int RateKb { get; set; }

    // How often subscribed sources are checked for new videos.
    public int RefreshMinutes { get; set; } = 60;

    public bool AutoDownload { get; set; } = true;

    // Signed in: how many of your recommended / subscription videos are
    // saved offline in the background at every refresh (0 = none).
    public int RecommendedToSave { get; set; } = 8;
    public int SubscriptionsToSave { get; set; } = 5;

    public string Folder { get; set; } = "";
    public bool NoticeAccepted { get; set; }

    // ---- player ----
    public bool KeepHistory { get; set; } = true;
    public bool RememberChannelSpeed { get; set; } = true;
    public bool Autoplay { get; set; } = true;

    // When downloads are busy, saving a video offers to watch it online now.
    public bool AskOnlineWhenBusy { get; set; } = true;

    // Seconds the "Up next" screen waits before continuing on its own
    // (0 = continue at once).
    public int AutoplayDelay { get; set; } = 6;
    public int DefaultVolume { get; set; } = 80;

    // Saved videos are removed from this PC this many hours after you
    // finish watching them (0 = never). Videos marked "Keep" stay.
    public int AutoDeleteHours { get; set; } = 2;

    // Saved videos nobody opened for this many days are removed (0 = never).
    public int UnwatchedDeleteDays { get; set; } = 30;
}

internal sealed class TubeData
{
    public List<TubeVideo> Videos { get; set; } = new();
    public List<TubeSource> Sources { get; set; } = new();
    public TubeSettings Settings { get; set; } = new();

    // Every video id ever queued automatically, so removed videos
    // do not come back at the next refresh.
    public List<string> Seen { get; set; } = new();

    // Last loaded account feeds, so Home opens instantly.
    public List<VideoInfo> HomeFeed { get; set; } = new();
    public List<VideoInfo> SubsFeed { get; set; } = new();
    public DateTime? HomeUpdated { get; set; }
    public DateTime? SubsUpdated { get; set; }

    // Last time the Google session was renewed in the background.
    public DateTime? AccountRefreshed { get; set; }

    // Local lists (no account needed).
    public List<HistoryEntry> History { get; set; } = new();
    public List<VideoInfo> WatchLater { get; set; } = new();

    // Playback speed remembered per channel.
    public Dictionary<string, double> ChannelSpeeds { get; set; } = new();
}

// ======================================================
// LIBRARY + BACKGROUND MANAGER
// Downloads run in separate yt-dlp processes that stream straight to
// disk, so memory use here stays tiny whatever the library size.
// ======================================================

internal sealed class TubeLibrary
{
    private const int MaxWorkers = 3;
    private const long MinFreeBytes = 2L * 1024 * 1024 * 1024;

    public static string DataFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000", "tube");

    private static TubeLibrary? instance;
    public static TubeLibrary Instance => instance ??= new TubeLibrary();

    private readonly object gate = new();
    private readonly Dictionary<string, CancellationTokenSource> running = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    private TubeData data = new();
    private HashSet<string> seen = new();
    private System.Threading.Timer? saveTimer;
    private long orderCounter;
    private int version;
    private bool started;

    public bool Paused { get; set; }
    public bool StorageFull { get; private set; }
    public bool LowDisk { get; private set; }
    public string Activity { get; private set; } = "";

    // ---- account feeds ----

    private readonly SemaphoreSlim feedGate = new(1, 1);

    public bool FeedBusy { get; private set; }
    public string? FeedError { get; private set; }

    /// <summary>True when Google rejected the saved session (sign in again).</summary>
    public bool AuthExpired { get; set; }

    public List<VideoInfo> HomeFeed
    {
        get { lock (gate) return data.HomeFeed.ToList(); }
    }

    public List<VideoInfo> SubsFeed
    {
        get { lock (gate) return data.SubsFeed.ToList(); }
    }

    public DateTime? HomeUpdated => data.HomeUpdated;
    public DateTime? SubsUpdated => data.SubsUpdated;

    /// <summary>Changes whenever something the UI shows has changed.</summary>
    public int Version => Volatile.Read(ref version);

    public TubeSettings Settings => data.Settings;

    private static string FilePath => Path.Combine(DataFolder, "library.json");

    public string VideoFolder => string.IsNullOrWhiteSpace(Settings.Folder)
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "UtilMaster3000", "YouTube")
        : Settings.Folder;

    private TubeLibrary()
    {
        Load();
    }

    // ---- persistence ----

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                data = JsonSerializer.Deserialize<TubeData>(
                    File.ReadAllText(FilePath)) ?? new TubeData();
            }
        }
        catch
        {
            data = new TubeData();
        }

        seen = new HashSet<string>(data.Seen);

        foreach (TubeVideo v in data.Videos)
        {
            v.Order = ++orderCounter;

            // Interrupted downloads resume (yt-dlp continues the .part file).
            if (v.State == VideoState.Downloading)
                v.State = VideoState.Queued;

            // The user deleted the file by hand: forget it.
            if (v.State == VideoState.Done &&
                (v.File == null || !File.Exists(v.File)))
            {
                v.State = VideoState.None;
                v.File = null;
            }
        }

        data.Videos.RemoveAll(v => v.State == VideoState.None);
    }

    private void Save()
    {
        string json;

        lock (gate)
        {
            data.Seen = seen.ToList();
            json = JsonSerializer.Serialize(data);
        }

        try
        {
            Directory.CreateDirectory(DataFolder);

            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // Library state is rebuilt from disk if this fails.
        }
    }

    // Saves a moment after the last change, not on every progress tick.
    private void Changed()
    {
        Bump();

        saveTimer ??= new System.Threading.Timer(_ => Save(), null,
            Timeout.Infinite, Timeout.Infinite);

        saveTimer.Change(1500, Timeout.Infinite);
    }

    public void Bump() => Interlocked.Increment(ref version);

    public void SaveNow() => Save();

    // ---- reading ----

    public List<TubeVideo> Snapshot()
    {
        lock (gate)
            return data.Videos.ToList();
    }

    public List<TubeSource> Sources()
    {
        lock (gate)
            return data.Sources.ToList();
    }

    public TubeVideo? Get(string id)
    {
        lock (gate)
            return data.Videos.FirstOrDefault(v => v.Id == id);
    }

    /// <summary>
    /// True when a video saved now would have to wait: every download slot is
    /// in use, downloads are paused, or there is no room to save.
    /// </summary>
    public bool DownloadsBusy(out int waiting, out string reason)
    {
        int active;

        lock (gate)
        {
            active = data.Videos.Count(v => v.State == VideoState.Downloading);
            waiting = data.Videos.Count(v => v.State == VideoState.Queued);
        }

        if (Paused)
        {
            reason = "Downloads are paused";
            return true;
        }

        if (StorageFull)
        {
            reason = "The offline library is full";
            return true;
        }

        if (LowDisk)
        {
            reason = "There is little free disk space";
            return true;
        }

        if (active >= Settings.Parallel)
        {
            reason = waiting > 0
                ? $"Offline downloads are busy ({active} saving, {waiting} waiting)"
                : "An offline download is in progress";
            return true;
        }

        reason = "";
        return false;
    }

    public long UsedBytes()
    {
        lock (gate)
            return data.Videos.Where(v => v.State == VideoState.Done).Sum(v => v.Size);
    }

    // ---- videos ----

    /// <summary>Diagnostics hook (tests): called for every video that gets queued.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Adds the video to the library (queued for download).</summary>
    public TubeVideo Enqueue(TubeVideo video, bool priority = false)
    {
        if (Trace != null)
        {
            System.Diagnostics.StackTrace stack = new(1, false);

            string chain = string.Join(" <- ", Enumerable.Range(0, Math.Min(5, stack.FrameCount))
                .Select(i => stack.GetFrame(i)?.GetMethod()?.Name ?? "?"));

            Trace($"Enqueue {video.Id} '{video.Title}' via {chain}");
        }

        lock (gate)
        {
            TubeVideo? existing = data.Videos.FirstOrDefault(v => v.Id == video.Id);

            if (existing != null)
                video = existing;
            else
                data.Videos.Add(video);

            if (video.State is VideoState.None or VideoState.Failed)
            {
                video.State = VideoState.Queued;
                video.Attempts = 0;
                video.Error = null;
                video.RetryAt = DateTime.MinValue;
            }

            if (video.State == VideoState.Queued)
            {
                video.Order = priority
                    ? -(++orderCounter)    // jump the queue
                    : ++orderCounter;
            }

            seen.Add(video.Id);
        }

        Changed();
        return video;
    }

    public void Cancel(TubeVideo video)
    {
        lock (gate)
        {
            if (running.TryGetValue(video.Id, out CancellationTokenSource? cts))
                cts.Cancel();

            if (video.State is VideoState.Queued or VideoState.Failed)
                data.Videos.Remove(video);
        }

        Changed();
    }

    public void Retry(TubeVideo video)
    {
        if (video.State == VideoState.Failed)
            Enqueue(video, priority: true);
    }

    /// <summary>Removes the video and deletes its file.</summary>
    public void Delete(TubeVideo video)
    {
        lock (gate)
        {
            if (running.TryGetValue(video.Id, out CancellationTokenSource? cts))
                cts.Cancel();

            data.Videos.Remove(video);
        }

        DeleteFiles(video);
        Changed();
    }

    private void DeleteFiles(TubeVideo video)
    {
        try
        {
            if (video.File != null && File.Exists(video.File))
                File.Delete(video.File);

            // Leftovers of an unfinished download.
            foreach (string file in Directory.GetFiles(VideoFolder, video.Id + ".*")
                         .Concat(Directory.GetFiles(VideoFolder, "* [" + video.Id + "].*")))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // A file in use is removed next time.
        }

        ThumbCache.Remove(video.Id);
    }

    public void SetWatched(TubeVideo video, bool watched)
    {
        // The auto-delete clock starts the first time it counts as watched.
        if (watched && !video.Watched)
            video.WatchedAt = DateTime.Now;

        if (!watched)
            video.WatchedAt = null;

        video.Watched = watched;

        if (watched)
            video.Position = 0;

        Changed();
        EnforceCap();
    }

    public void SetKeep(TubeVideo video, bool keep)
    {
        video.Keep = keep;
        Changed();
    }

    // ---- automatic deletion of watched videos ----

    /// <summary>The video currently open in the player is never deleted.</summary>
    public static string? NowPlayingId { get; set; }

    /// <summary>How many watched videos were removed automatically so far.</summary>
    public int PurgedTotal { get; private set; }

    private async Task PurgeLoop()
    {
        try
        {
            await Task.Delay(20000, lifetime.Token);

            while (!lifetime.IsCancellationRequested)
            {
                PurgeWatched();
                await Task.Delay(60000, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Deletes saved videos that were finished at least AutoDeleteHours ago.
    /// Only files this app downloaded are touched; videos marked Keep and the
    /// one that is playing are skipped.
    /// </summary>
    public void PurgeWatched()
    {
        int hours = Settings.AutoDeleteHours;
        int days = Settings.UnwatchedDeleteDays;

        if (hours <= 0 && days <= 0)
            return;

        List<TubeVideo> remove = new();

        lock (gate)
        {
            // Saved but never finished and not opened for a long time.
            if (days > 0)
            {
                Dictionary<string, DateTime> lastOpened = new();

                foreach (HistoryEntry h in data.History)
                    lastOpened[h.Id] = h.WatchedAt;

                foreach (TubeVideo v in data.Videos.Where(v =>
                             v.State == VideoState.Done && !v.Watched && !v.Keep && v.Id != NowPlayingId))
                {
                    DateTime since = v.Downloaded ?? DateTime.MaxValue;

                    if (lastOpened.TryGetValue(v.Id, out DateTime opened) && opened > since)
                        since = opened;

                    if (DateTime.Now - since >= TimeSpan.FromDays(days))
                        remove.Add(v);
                }
            }

            foreach (TubeVideo v in hours <= 0 ? Enumerable.Empty<TubeVideo>() : data.Videos.Where(v =>
                         v.State == VideoState.Done && v.Watched && !v.Keep))
            {
                // Watched before this feature existed: start its clock now
                // instead of deleting it immediately.
                if (v.WatchedAt == null)
                {
                    v.WatchedAt = DateTime.Now;
                    continue;
                }

                if (v.Id == NowPlayingId)
                    continue;

                if (DateTime.Now - v.WatchedAt >= TimeSpan.FromHours(hours) && !remove.Contains(v))
                    remove.Add(v);
            }

            foreach (TubeVideo v in remove)
                data.Videos.Remove(v);
        }

        if (remove.Count == 0)
            return;

        foreach (TubeVideo v in remove)
            DeleteFiles(v);

        PurgedTotal += remove.Count;
        Changed();
    }

    /// <summary>How long until a watched video is deleted (null = not scheduled).</summary>
    public TimeSpan? DeletesIn(TubeVideo video)
    {
        int hours = Settings.AutoDeleteHours;

        if (hours <= 0 || !video.Watched || video.Keep || video.State != VideoState.Done)
            return null;

        DateTime started = video.WatchedAt ?? DateTime.Now;
        TimeSpan left = started + TimeSpan.FromHours(hours) - DateTime.Now;

        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    public void SetPosition(TubeVideo video, double seconds)
    {
        video.Position = seconds;
        Changed();
    }

    // ---- history ----

    public void RecordWatch(TubeVideo video)
    {
        if (!Settings.KeepHistory)
            return;

        lock (gate)
        {
            data.History.RemoveAll(h => h.Id == video.Id);

            data.History.Insert(0, new HistoryEntry
            {
                Id = video.Id,
                Title = video.Title,
                Channel = video.Channel,
                Duration = video.Duration,
                WatchedAt = DateTime.Now
            });

            if (data.History.Count > 500)
                data.History.RemoveRange(500, data.History.Count - 500);
        }

        Changed();
    }

    public List<HistoryEntry> History()
    {
        lock (gate)
            return data.History.ToList();
    }

    public void RemoveFromHistory(string id)
    {
        lock (gate)
            data.History.RemoveAll(h => h.Id == id);

        Changed();
    }

    public void ClearHistory()
    {
        lock (gate)
            data.History.Clear();

        Changed();
    }

    // ---- watch later (a local list, no account needed) ----

    public List<VideoInfo> WatchLater()
    {
        lock (gate)
            return data.WatchLater.ToList();
    }

    public bool IsWatchLater(string id)
    {
        lock (gate)
            return data.WatchLater.Any(v => v.Id == id);
    }

    public void ToggleWatchLater(TubeVideo video)
    {
        lock (gate)
        {
            if (data.WatchLater.RemoveAll(v => v.Id == video.Id) == 0)
            {
                data.WatchLater.Insert(0, new VideoInfo(
                    video.Id, video.Title, video.Channel, video.Duration));
            }
        }

        Changed();
    }

    public void RemoveWatchLater(string id)
    {
        lock (gate)
            data.WatchLater.RemoveAll(v => v.Id == id);

        Changed();
    }

    // ---- playback speed remembered per channel ----

    public double? ChannelSpeed(string channel)
    {
        lock (gate)
            return channel.Length > 0 && data.ChannelSpeeds.TryGetValue(channel, out double speed)
                ? speed
                : null;
    }

    public void SetChannelSpeed(string channel, double speed)
    {
        if (channel.Length == 0)
            return;

        lock (gate)
        {
            if (Math.Abs(speed - 1.0) < 0.001)
                data.ChannelSpeeds.Remove(channel);
            else
                data.ChannelSpeeds[channel] = Math.Round(speed, 2);
        }

        Changed();
    }

    // ---- sources ----

    public TubeSource AddSource(string kind, string value, int count)
    {
        TubeSource source = new()
        {
            Kind = kind,
            Value = value.Trim(),
            Title = value.Trim(),
            Count = Math.Clamp(count, 1, 50)
        };

        lock (gate)
            data.Sources.Add(source);

        Changed();
        return source;
    }

    public void RemoveSource(TubeSource source)
    {
        lock (gate)
            data.Sources.Remove(source);

        Changed();
    }

    public void ToggleSource(TubeSource source)
    {
        source.Enabled = !source.Enabled;
        Changed();
    }

    /// <summary>Check every source on the next pass of the refresh loop.</summary>
    public void RefreshNow()
    {
        lock (gate)
        {
            foreach (TubeSource s in data.Sources)
                s.LastRefresh = null;
        }

        Changed();
    }

    // ---- account feeds ----

    public void ClearFeeds()
    {
        lock (gate)
        {
            data.HomeFeed = new List<VideoInfo>();
            data.SubsFeed = new List<VideoInfo>();
            data.HomeUpdated = null;
            data.SubsUpdated = null;
        }

        FeedError = null;
        AuthExpired = false;
        Changed();
    }

    /// <summary>Loads your Home recommendations or Subscriptions.</summary>
    public async Task RefreshFeedAsync(bool subscriptions)
    {
        if (!YtAccount.SignedIn || !YtDlp.IsInstalled)
            return;

        await feedGate.WaitAsync();

        try
        {
            FeedBusy = true;
            FeedError = null;
            Activity = subscriptions
                ? "Loading your subscriptions..."
                : "Loading your recommendations...";
            Bump();

            List<VideoInfo> videos;

            try
            {
                videos = await LoadFeed(subscriptions);
            }
            catch (AuthExpiredException)
            {
                // Quietly renew the Google session in the background and
                // try once more before asking you to sign in again.
                if (!await YtAccount.RefreshSessionAsync(force: true))
                    throw;

                videos = await LoadFeed(subscriptions);
            }

            lock (gate)
            {
                if (subscriptions)
                {
                    data.SubsFeed = videos;
                    data.SubsUpdated = DateTime.Now;
                }
                else
                {
                    data.HomeFeed = videos;
                    data.HomeUpdated = DateTime.Now;
                }
            }

            AuthExpired = false;

            int count = subscriptions
                ? Settings.SubscriptionsToSave
                : Settings.RecommendedToSave;

            if (Settings.AutoDownload && count > 0)
                SaveFromFeed(videos, count);
        }
        catch (AuthExpiredException)
        {
            AuthExpired = true;
            FeedError = "Your Google session expired. Sign in again.";
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            FeedError = ex.Message;

            // Do not retry for a while.
            lock (gate)
            {
                DateTime retry = DateTime.Now.AddMinutes(
                    -Math.Max(1, Settings.RefreshMinutes - 10));

                if (subscriptions)
                    data.SubsUpdated = retry;
                else
                    data.HomeUpdated = retry;
            }
        }
        finally
        {
            FeedBusy = false;
            Activity = "";
            feedGate.Release();
            Changed();
        }
    }

    private Task<List<VideoInfo>> LoadFeed(bool subscriptions) =>
        YtAccount.WithCookieFile(file =>
            YtDlp.FeedAsync(
                subscriptions ? ":ytsubs" : ":ytrec",
                subscriptions ? 40 : 60,
                file,
                lifetime.Token));

    /// <summary>Call after a successful sign-in so the renewal clock starts.</summary>
    public void NoteSignedIn()
    {
        lock (gate)
            data.AccountRefreshed = DateTime.Now;

        AuthExpired = false;
        Changed();
    }

    // Visits YouTube in a hidden browser now and then, like a real browser
    // would, so the session keeps renewing and never goes stale.
    private async Task KeepSessionAlive()
    {
        bool due;

        lock (gate)
            due = data.AccountRefreshed == null ||
                  DateTime.Now - data.AccountRefreshed >= TimeSpan.FromHours(12);

        if (!due)
            return;

        Activity = "Renewing your Google session...";
        Bump();

        bool ok = await YtAccount.RefreshSessionAsync(force: true);

        lock (gate)
            data.AccountRefreshed = DateTime.Now;

        // Only a definite "no" from Google means the session is gone.
        if (!ok)
            AuthExpired = true;

        Activity = "";
        Changed();
    }

    // Queues the first few new videos of a feed for offline viewing.
    private void SaveFromFeed(List<VideoInfo> videos, int count)
    {
        int saved = 0;

        foreach (VideoInfo info in videos)
        {
            if (saved >= count)
                break;

            // Skip live streams and very long videos (hours of disk).
            if (info.Duration <= 0 || info.Duration > 7200)
                continue;

            bool isNew;

            lock (gate)
                isNew = !seen.Contains(info.Id) &&
                        !data.Videos.Any(v => v.Id == info.Id);

            if (!isNew)
                continue;

            Enqueue(TubeVideo.From(info));
            saved++;
        }
    }

    private bool FeedDue(DateTime? updated) =>
        updated == null ||
        DateTime.Now - updated >= TimeSpan.FromMinutes(Settings.RefreshMinutes);

    // ---- settings ----

    public void SettingsChanged()
    {
        Changed();
        EnforceCap();
    }

    // ---- background work ----

    public void Start()
    {
        if (started)
            return;

        started = true;

        for (int i = 0; i < MaxWorkers; i++)
        {
            int index = i;
            Task.Run(() => WorkerLoop(index));
        }

        Task.Run(RefreshLoop);
        Task.Run(PurgeLoop);
    }

    public void Stop()
    {
        lifetime.Cancel();
        Save();
    }

    private async Task WorkerLoop(int index)
    {
        while (!lifetime.IsCancellationRequested)
        {
            TubeVideo? video = null;

            if (index < Settings.Parallel && !Paused && YtDlp.IsInstalled)
                video = TakeNext();

            if (video == null)
            {
                try
                {
                    await Task.Delay(1000, lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            await DownloadOne(video);
        }
    }

    private TubeVideo? TakeNext()
    {
        if (StorageFull)
            return null;

        // Never fill the disk completely.
        LowDisk = FreeSpace(VideoFolder) < MinFreeBytes;

        if (LowDisk)
            return null;

        lock (gate)
        {
            TubeVideo? next = data.Videos
                .Where(v => v.State == VideoState.Queued && v.RetryAt <= DateTime.Now)
                .OrderBy(v => v.Order)
                .FirstOrDefault();

            if (next == null)
                return null;

            next.State = VideoState.Downloading;
            next.Progress = 0;
            next.Speed = "";
            next.Eta = "";

            running[next.Id] = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);

            Bump();
            return next;
        }
    }

    private async Task DownloadOne(TubeVideo video)
    {
        CancellationTokenSource cts;

        lock (gate)
            cts = running[video.Id];

        try
        {
            // Thumbnail first so the video looks right in the library.
            await ThumbCache.EnsureAsync(video.Id);

            string file = await YtDlp.DownloadAsync(
                video.Id, VideoFolder, Settings.Quality, Settings.RateKb,
                p =>
                {
                    video.Progress = p.Percent;
                    video.Speed = p.Speed;
                    video.Eta = p.Eta;
                    Bump();
                },
                cts.Token);

            lock (gate)
            {
                video.File = file;
                video.Size = new FileInfo(file).Length;
                video.State = VideoState.Done;
                video.Downloaded = DateTime.Now;
                video.Progress = 100;
                video.Error = null;
            }

            EnforceCap();
        }
        catch (OperationCanceledException)
        {
            lock (gate)
            {
                // App closing: resume next time. User cancel: forget it.
                if (video.State == VideoState.Downloading)
                {
                    video.State = lifetime.IsCancellationRequested
                        ? VideoState.Queued
                        : VideoState.None;
                }

                if (video.State == VideoState.None)
                    data.Videos.Remove(video);
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                video.Attempts++;
                video.Error = ex.Message;

                if (video.Attempts < 3)
                {
                    video.State = VideoState.Queued;
                    video.RetryAt = DateTime.Now.AddMinutes(2 * video.Attempts);
                }
                else
                {
                    video.State = VideoState.Failed;
                }
            }
        }
        finally
        {
            lock (gate)
            {
                if (running.Remove(video.Id, out CancellationTokenSource? done))
                    done.Dispose();
            }

            Changed();
        }
    }

    private static long FreeSpace(string folder)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(folder))!;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return long.MaxValue;
        }
    }

    // Keeps the library under its size limit by removing the oldest
    // *watched* videos. Unwatched videos are never deleted automatically:
    // when they alone exceed the limit, downloading simply pauses.
    private void EnforceCap()
    {
        long cap = (long)Settings.MaxGb * 1024 * 1024 * 1024;
        List<TubeVideo> remove = new();

        lock (gate)
        {
            long used = data.Videos
                .Where(v => v.State == VideoState.Done)
                .Sum(v => v.Size);

            if (used > cap)
            {
                foreach (TubeVideo v in data.Videos
                             .Where(v => v.State == VideoState.Done && v.Watched)
                             .OrderBy(v => v.Downloaded))
                {
                    remove.Add(v);
                    used -= v.Size;

                    if (used <= cap)
                        break;
                }

                foreach (TubeVideo v in remove)
                    data.Videos.Remove(v);
            }

            StorageFull = used > cap;
        }

        foreach (TubeVideo v in remove)
            DeleteFiles(v);

        if (remove.Count > 0)
            Changed();
    }

    // ---- subscribed sources ----

    private async Task RefreshLoop()
    {
        // Give the app a moment to start before touching the network.
        try
        {
            await Task.Delay(8000, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!lifetime.IsCancellationRequested)
        {
            if (Settings.AutoDownload && !Paused && YtDlp.IsInstalled)
            {
                List<TubeSource> due;

                lock (gate)
                {
                    due = data.Sources
                        .Where(s => s.Enabled &&
                            (s.LastRefresh == null ||
                             DateTime.Now - s.LastRefresh >=
                                TimeSpan.FromMinutes(Settings.RefreshMinutes)))
                        .ToList();
                }

                foreach (TubeSource source in due)
                {
                    if (lifetime.IsCancellationRequested)
                        return;

                    await RefreshSource(source);
                }

                // Signed in: keep your recommendations / subscriptions fresh
                // and save a few of their newest videos for offline viewing.
                if (YtAccount.SignedIn && !AuthExpired)
                {
                    if (Settings.RecommendedToSave > 0 && FeedDue(data.HomeUpdated))
                        await RefreshFeedAsync(false);

                    if (Settings.SubscriptionsToSave > 0 && FeedDue(data.SubsUpdated))
                        await RefreshFeedAsync(true);

                    await KeepSessionAlive();
                }
            }

            try
            {
                await Task.Delay(15000, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshSource(TubeSource source)
    {
        await refreshGate.WaitAsync(lifetime.Token);

        try
        {
            Activity = "Checking " + source.Title + "...";
            Bump();

            List<VideoInfo> found = source.Kind == "search"
                ? await YtDlp.SearchAsync(source.Value, source.Count, true, lifetime.Token)
                : await YtDlp.ListAsync(
                    YtDlp.NormalizeSource(source.Kind, source.Value),
                    source.Count, lifetime.Token);

            int added = 0;

            foreach (VideoInfo info in found)
            {
                bool isNew;

                lock (gate)
                    isNew = !seen.Contains(info.Id) &&
                            !data.Videos.Any(v => v.Id == info.Id);

                if (!isNew)
                    continue;

                TubeVideo video = TubeVideo.From(info);
                video.SourceId = source.Id;

                // Flat channel listings carry no channel name: use the source's.
                if (video.Channel.Length == 0 && source.Kind == "channel")
                    video.Channel = source.Title;

                Enqueue(video);
                added++;
            }

            if (source.Kind == "channel" &&
                source.Title == source.Value &&
                found.Count > 0 &&
                found[0].Channel.Length > 0)
            {
                source.Title = found[0].Channel;
            }

            source.LastRefresh = DateTime.Now;
            source.LastError = null;
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            source.LastError = ex.Message;

            // Try again in about ten minutes rather than hammering.
            int wait = Math.Max(1, Settings.RefreshMinutes - 10);
            source.LastRefresh = DateTime.Now.AddMinutes(-wait);
        }
        finally
        {
            Activity = "";
            refreshGate.Release();
            Changed();
        }
    }

    /// <summary>Refreshes one source right now (used by the UI).</summary>
    public Task RefreshSourceNow(TubeSource source) => RefreshSource(source);
}

// ======================================================
// THUMBNAILS
// Small JPEGs on disk; only a handful of tiny bitmaps in memory.
// ======================================================

internal static class ThumbCache
{
    private const int MaxInMemory = 48;
    private const int Width = 240;
    private const int Height = 135;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> Memory = new();
    private static readonly LinkedList<string> Recent = new();
    private static readonly HashSet<string> Pending = new();
    private static readonly HashSet<string> Failed = new();
    private static readonly SemaphoreSlim Limit = new(2, 2);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string Folder => Path.Combine(TubeLibrary.DataFolder, "thumbs");

    private static string PathFor(string id) => Path.Combine(Folder, id + ".jpg");

    /// <summary>The thumbnail if it is available now; otherwise starts loading it.</summary>
    public static Bitmap? TryGet(string id)
    {
        lock (Gate)
        {
            if (Memory.TryGetValue(id, out Bitmap? cached))
            {
                Recent.Remove(id);
                Recent.AddFirst(id);
                return cached;
            }
        }

        string file = PathFor(id);

        if (File.Exists(file))
        {
            Bitmap? small = LoadSmall(file);

            if (small != null)
            {
                lock (Gate)
                {
                    Memory[id] = small;
                    Recent.AddFirst(id);

                    // Drop the least recently used (GC reclaims them).
                    while (Recent.Count > MaxInMemory)
                    {
                        string oldest = Recent.Last!.Value;
                        Recent.RemoveLast();
                        Memory.Remove(oldest);
                    }
                }

                return small;
            }
        }

        Request(id);
        return null;
    }

    private static Bitmap? LoadSmall(string file)
    {
        try
        {
            // Read fully so the file is not locked, then shrink.
            byte[] bytes = File.ReadAllBytes(file);

            using MemoryStream stream = new(bytes);
            using Image original = Image.FromStream(stream);

            Bitmap small = new(Width, Height);

            using Graphics g = Graphics.FromImage(small);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(original, 0, 0, Width, Height);

            return small;
        }
        catch
        {
            return null;
        }
    }

    private static void Request(string id)
    {
        lock (Gate)
        {
            if (Failed.Contains(id) || !Pending.Add(id))
                return;
        }

        Task.Run(async () =>
        {
            try
            {
                await EnsureAsync(id);
                TubeLibrary.Instance.Bump();
            }
            catch
            {
                lock (Gate)
                    Failed.Add(id);
            }
            finally
            {
                lock (Gate)
                    Pending.Remove(id);
            }
        });
    }

    public static async Task EnsureAsync(string id)
    {
        string file = PathFor(id);

        if (File.Exists(file))
            return;

        // A local video file: one frame made with ffmpeg.
        if (LocalMedia.IsLocalId(id))
        {
            await Limit.WaitAsync();

            try
            {
                await Task.Run(() => LocalMedia.MakeThumbnail(id, file));
            }
            finally
            {
                Limit.Release();
            }

            return;
        }

        await Limit.WaitAsync();

        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(
                $"https://i.ytimg.com/vi/{id}/mqdefault.jpg");

            Directory.CreateDirectory(Folder);
            await File.WriteAllBytesAsync(file, bytes);
        }
        finally
        {
            Limit.Release();
        }
    }

    public static void Remove(string id)
    {
        lock (Gate)
        {
            Memory.Remove(id);
            Recent.Remove(id);
        }

        try
        {
            File.Delete(PathFor(id));
        }
        catch
        {
            // Not important.
        }
    }

    /// <summary>Drops every in-memory bitmap (called when the client closes).</summary>
    public static void TrimMemory()
    {
        lock (Gate)
        {
            Memory.Clear();
            Recent.Clear();
        }
    }
}
