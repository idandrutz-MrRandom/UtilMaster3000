using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UtilMaster3000;

// ======================================================
// UTILTUBE  -  a YouTube client with offline viewing, built on yt-dlp
//
//  Home            your recommendations (sign in with Google once)
//  Subscriptions   newest videos from channels you follow
//  Saved videos    everything on this PC, watch with no internet
//  History         what you watched (local, you can clear it)
//  Watch later     a list that needs no account
//  Downloads       what is saving right now
//  Sources         channels / playlists / topics kept up to date
//  Settings
//
// Click a video to watch at once (streams instantly). The newest
// recommended and subscribed videos are saved in the background.
// ======================================================

internal sealed class TubeClient : Form
{
    private enum View
    {
        Home,
        Subs,
        Search,
        Library,
        History,
        Later,
        Queue,
        Playlists,
        Sources,
        Settings
    }

    private enum LibFilter
    {
        All,
        Unwatched,
        InProgress,
        Watched
    }

    private enum LibSort
    {
        Newest,
        Oldest,
        Title,
        Shortest,
        Longest
    }

    private static TubeClient? current;

    private readonly TubeLibrary library = TubeLibrary.Instance;

    // top bar
    private readonly Panel top = new();
    private readonly Button menuButton = new();
    private readonly Button backButton = new();
    private readonly Button forwardButton = new();
    private readonly Label brand = new();
    private readonly TextBox searchBox = new();
    private readonly Button searchButton = new();
    private readonly Button accountButton = new();

    // sidebar + content
    private readonly SideBar sidebar = new();
    private readonly Panel content = new();
    private readonly Panel header = new();
    private readonly Label titleLabel = new();
    private readonly Label subtitleLabel = new();
    private readonly FlowLayoutPanel actions = new();
    private readonly Panel body = new();

    private readonly VideoGrid grid = new();
    private readonly Label empty = new();
    private readonly Button emptyAction = new();
    private readonly Panel sourcesView = new();
    private readonly ListBox sourceList = new();
    private readonly Panel settingsView = new();

    private readonly Panel statusBar = new();
    private readonly Label statusLeft = new();
    private readonly Label statusRight = new();

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };

    // Videos that are not in the library (feeds, search, history): one object
    // per id so the grid keeps its selection while the lists refresh.
    private readonly Dictionary<string, TubeVideo> temp = new();

    // navigation (back / forward)
    private readonly List<(View View, string? Query)> navStack = new();
    private int navPos = -1;

    private View view = View.Home;
    private int lastVersion = -1;

    // search
    private readonly Dictionary<string, List<VideoInfo>> searchCache = new();
    private string searchQuery = "";
    private List<VideoInfo> searchResults = new();
    private bool searching;
    private CancellationTokenSource? searchCts;

    // library filters
    private LibFilter libFilter = LibFilter.All;
    private LibSort libSort = LibSort.Newest;

    private string message = "";
    private long messageUntil;
    private int lastPurged;

    /// <summary>Opens the client (or brings it forward) and optionally searches.</summary>
    public static TubeClient Open(string? query = null)
    {
        if (current == null || current.IsDisposed)
        {
            current = new TubeClient();
            current.FormClosed += (_, _) => current = null;
            current.Show();
        }
        else
        {
            if (current.WindowState == FormWindowState.Minimized)
                current.WindowState = FormWindowState.Normal;

            current.Activate();
        }

        if (!string.IsNullOrWhiteSpace(query))
            current.BeginSearch(query);

        return current;
    }

    private TubeClient()
    {
        Text = "UtilTube";
        ClientSize = new Size(1240, 760);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;

        // "Full windowed": fills the screen but keeps the title bar and
        // taskbar. The size above is what "Restore" goes back to.
        WindowState = FormWindowState.Maximized;

        BackColor = Theme.Background;
        ForeColor = Theme.Strong;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        KeyPreview = true;

        BuildTop();
        BuildSidebar();
        BuildContent();
        BuildStatus();

        Controls.Add(content);
        Controls.Add(sidebar);
        Controls.Add(top);
        Controls.Add(statusBar);

        timer.Tick += (_, _) => OnTimer();
        timer.Start();

        // Make sure background downloading is running.
        library.Start();

        // Like YouTube: open on your recommendations.
        Go(YtAccount.SignedIn || library.Snapshot().All(v => v.State != VideoState.Done)
            ? View.Home
            : View.Library);

        UpdateAccountButton();
    }

    // --------------------------------------------------
    // TOP BAR
    // --------------------------------------------------

    private Button IconButton(string glyph, string tip, int size = 13)
    {
        Button b = new()
        {
            Text = glyph,
            Font = new Font("Segoe MDL2 Assets", size),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Background,
            ForeColor = Theme.Strong,
            Size = new Size(38, 36),
            Cursor = Cursors.Hand,
            TabStop = false
        };

        b.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    private void BuildTop()
    {
        top.Dock = DockStyle.Top;
        top.Height = 58;
        top.BackColor = Theme.Background;

        menuButton.Text = "";
        StyleIcon(menuButton, "Show / hide the menu");
        menuButton.Click += (_, _) => ToggleSidebar();

        backButton.Text = "";
        StyleIcon(backButton, "Back  (Alt+Left)");
        backButton.Click += (_, _) => GoBack();

        forwardButton.Text = "";
        StyleIcon(forwardButton, "Forward  (Alt+Right)");
        forwardButton.Click += (_, _) => GoForward();

        brand.Text = "UtilTube";
        brand.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        brand.ForeColor = Theme.Accent;
        brand.AutoSize = true;

        searchBox.BackColor = Theme.Search;
        searchBox.ForeColor = Theme.Strong;
        searchBox.BorderStyle = BorderStyle.FixedSingle;
        searchBox.Font = new Font("Segoe UI", 11f);
        searchBox.PlaceholderText = "Search YouTube, filter your lists, or paste a link";
        searchBox.Size = new Size(420, 30);

        // In Saved videos / History / Watch later, typing filters live.
        searchBox.TextChanged += (_, _) =>
        {
            if (view is View.Library or View.History or View.Later)
                RefreshView();
        };

        searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                BeginSearch(searchBox.Text);
            }
            else if (e.KeyCode == Keys.Down && grid.Items.Count > 0)
            {
                grid.Focus();
                grid.SelectFirst();
                e.Handled = true;
            }
        };

        searchButton.Text = "";
        searchButton.Font = new Font("Segoe MDL2 Assets", 12f);
        searchButton.FlatStyle = FlatStyle.Flat;
        searchButton.BackColor = Theme.ActiveRow;
        searchButton.ForeColor = Theme.Strong;
        searchButton.Size = new Size(52, 30);
        searchButton.Cursor = Cursors.Hand;
        searchButton.TabStop = false;
        searchButton.FlatAppearance.BorderSize = 0;
        searchButton.Click += (_, _) => BeginSearch(searchBox.Text);

        accountButton.FlatStyle = FlatStyle.Flat;
        accountButton.Size = new Size(120, 32);
        accountButton.Cursor = Cursors.Hand;
        accountButton.TabStop = false;
        accountButton.FlatAppearance.BorderSize = 0;
        accountButton.Click += (_, _) => AccountClicked();

        top.Controls.AddRange(new Control[]
        {
            menuButton, backButton, forwardButton, brand,
            searchBox, searchButton, accountButton
        });

        top.Resize += (_, _) => LayoutTop();
        LayoutTop();
    }

    private void StyleIcon(Button b, string tip)
    {
        b.Font = new Font("Segoe MDL2 Assets", 13f);
        b.FlatStyle = FlatStyle.Flat;
        b.BackColor = Theme.Background;
        b.ForeColor = Theme.Strong;
        b.Size = new Size(38, 36);
        b.Cursor = Cursors.Hand;
        b.TabStop = false;
        b.FlatAppearance.BorderSize = 0;
        new ToolTip().SetToolTip(b, tip);
    }

    private void LayoutTop()
    {
        menuButton.Location = new Point(10, 11);
        backButton.Location = new Point(52, 11);
        forwardButton.Location = new Point(92, 11);
        brand.Location = new Point(140, 15);

        accountButton.Location = new Point(top.Width - accountButton.Width - 14, 13);

        int left = 250;
        int available = accountButton.Left - 16 - left;
        int width = Math.Clamp(available - 56, 200, 640);

        // Centre the search box in the free space, like YouTube.
        searchBox.SetBounds(left + (available - width - 52) / 2, 14, width, 30);
        searchButton.Location = new Point(searchBox.Right + 2, 14);
    }

    private void ToggleSidebar()
    {
        sidebar.Collapsed = !sidebar.Collapsed;
        sidebar.Width = sidebar.Collapsed ? 62 : 212;
    }

    // --------------------------------------------------
    // SIDEBAR
    // --------------------------------------------------

    private static readonly (View View, string Key)[] SideKeys =
    {
        (View.Home, "home"), (View.Subs, "subs"), (View.Library, "library"),
        (View.History, "history"), (View.Later, "later"), (View.Queue, "queue"),
        (View.Playlists, "playlists"), (View.Sources, "sources"), (View.Settings, "settings")
    };

    private void BuildSidebar()
    {
        sidebar.Dock = DockStyle.Left;
        sidebar.Width = 212;

        sidebar.SetEntries(new[]
        {
            new SideBar.Entry("home", "", "Home"),
            new SideBar.Entry("subs", "", "Subscriptions"),
            SideBar.Entry.Divider(),
            new SideBar.Entry("library", "", "Saved videos"),
            new SideBar.Entry("history", "", "History"),
            new SideBar.Entry("later", "", "Watch later"),
            new SideBar.Entry("queue", "", "Downloads"),
            new SideBar.Entry("playlists", "\uE8FD", "Playlists"),
            SideBar.Entry.Divider(),
            new SideBar.Entry("sources", "", "Sources"),
            new SideBar.Entry("settings", "", "Settings")
        });

        sidebar.EntryClicked += key =>
        {
            foreach ((View v, string k) in SideKeys)
            {
                if (k == key)
                    Go(v);
            }
        };
    }

    private void UpdateSidebar()
    {
        sidebar.Active = SideKeys.FirstOrDefault(s => s.View == view).Key;

        List<TubeVideo> snapshot = library.Snapshot();

        sidebar.SetBadge("library", snapshot.Count(v => v.State == VideoState.Done));
        sidebar.SetBadge("later", library.WatchLater().Count);
        sidebar.SetBadge("queue", snapshot.Count(v =>
            v.State is VideoState.Downloading or VideoState.Queued or VideoState.Failed));
    }

    // --------------------------------------------------
    // CONTENT (header + body)
    // --------------------------------------------------

    private void BuildContent()
    {
        content.Dock = DockStyle.Fill;
        content.BackColor = Theme.Background;

        // ---- header ----
        header.Dock = DockStyle.Top;
        header.Height = 70;
        header.BackColor = Theme.Background;

        titleLabel.Font = new Font("Segoe UI", 17f, FontStyle.Bold);
        titleLabel.ForeColor = Theme.Strong;
        titleLabel.AutoSize = false;
        titleLabel.Location = new Point(22, 8);
        titleLabel.Size = new Size(600, 34);

        subtitleLabel.ForeColor = Theme.Text;
        subtitleLabel.AutoSize = false;
        subtitleLabel.Location = new Point(24, 42);
        subtitleLabel.Size = new Size(600, 22);

        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.WrapContents = false;
        actions.Dock = DockStyle.Right;
        actions.Width = 640;
        actions.Padding = new Padding(0, 16, 14, 0);
        actions.BackColor = Theme.Background;

        header.Controls.Add(actions);
        header.Controls.Add(titleLabel);
        header.Controls.Add(subtitleLabel);

        // ---- body ----
        body.Dock = DockStyle.Fill;
        body.BackColor = Theme.Background;

        grid.Dock = DockStyle.Fill;
        grid.ItemActivated += (_, _) => ActivateSelected();
        grid.QuickAction += OnQuickAction;
        grid.KeyDown += OnGridKey;
        grid.IsWatchLater = id => library.IsWatchLater(id);
        grid.ContextMenuStrip = BuildContextMenu();

        empty.AutoSize = false;
        empty.Dock = DockStyle.Fill;
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.ForeColor = Theme.Text;
        empty.Font = new Font("Segoe UI", 11.5f);

        emptyAction.Text = "Sign in with Google";
        emptyAction.FlatStyle = FlatStyle.Flat;
        emptyAction.BackColor = Theme.Accent;
        emptyAction.ForeColor = Theme.Background;
        emptyAction.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        emptyAction.Size = new Size(230, 46);
        emptyAction.Cursor = Cursors.Hand;
        emptyAction.FlatAppearance.BorderSize = 0;
        emptyAction.Click += (_, _) => SignIn();
        emptyAction.Visible = false;

        body.Resize += (_, _) => emptyAction.Location = new Point(
            (body.Width - emptyAction.Width) / 2, body.Height / 2 + 70);

        BuildSourcesView();
        BuildSettingsView();
        BuildPlaylistBar();

        body.Controls.Add(grid);
        body.Controls.Add(emptyAction);
        body.Controls.Add(empty);
        body.Controls.Add(sourcesView);
        body.Controls.Add(settingsView);
        body.Controls.Add(playlistBar);

        emptyAction.BringToFront();

        content.Controls.Add(body);
        content.Controls.Add(header);
    }

    private void BuildStatus()
    {
        statusBar.Dock = DockStyle.Bottom;
        statusBar.Height = 30;
        statusBar.BackColor = Theme.Search;

        statusLeft.Dock = DockStyle.Fill;
        statusLeft.TextAlign = ContentAlignment.MiddleLeft;
        statusLeft.Padding = new Padding(14, 0, 0, 0);
        statusLeft.ForeColor = Theme.Text;

        statusRight.Dock = DockStyle.Right;
        statusRight.AutoSize = false;
        statusRight.Width = 300;
        statusRight.TextAlign = ContentAlignment.MiddleRight;
        statusRight.Padding = new Padding(0, 0, 14, 0);
        statusRight.ForeColor = Theme.Text;

        statusBar.Controls.Add(statusLeft);
        statusBar.Controls.Add(statusRight);
    }

    // ---- header buttons ----

    private Button HeaderButton(string text, Action click, bool accent = false, bool active = false)
    {
        Button b = new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 32),
            Padding = new Padding(10, 0, 10, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = accent || active ? Theme.Accent : Theme.Search,
            ForeColor = accent || active ? Theme.Background : Theme.Strong,
            Cursor = Cursors.Hand,
            TabStop = false,
            Margin = new Padding(6, 0, 0, 0)
        };

        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildHeader()
    {
        actions.SuspendLayout();

        foreach (Control c in actions.Controls.Cast<Control>().ToList())
        {
            actions.Controls.Remove(c);
            c.Dispose();
        }

        // Added right to left.
        switch (view)
        {
            case View.Home:
            case View.Subs:
                actions.Controls.Add(HeaderButton("Save all offline", SaveAllShown));
                actions.Controls.Add(HeaderButton("Refresh", RefreshFeedNow, accent: true));
                break;

            case View.Search:
                actions.Controls.Add(HeaderButton("Save all offline", SaveAllShown));
                break;

            case View.Library:
            {
                actions.Controls.Add(HeaderButton("Sort: " + SortName(libSort), () =>
                {
                    libSort = (LibSort)(((int)libSort + 1) % Enum.GetValues<LibSort>().Length);
                    RefreshView();
                }));

                foreach (LibFilter f in new[] { LibFilter.Watched, LibFilter.InProgress, LibFilter.Unwatched, LibFilter.All })
                {
                    LibFilter captured = f;

                    actions.Controls.Add(HeaderButton(
                        FilterName(f),
                        () => { libFilter = captured; RefreshView(); },
                        active: libFilter == f));
                }

                break;
            }

            case View.History:
                actions.Controls.Add(HeaderButton("Clear history", ClearHistory));
                break;

            case View.Later:
                actions.Controls.Add(HeaderButton("Play all", PlayAll, accent: true));
                break;

            case View.Queue:
                actions.Controls.Add(HeaderButton("Clear failed", () =>
                {
                    foreach (TubeVideo v in library.Snapshot().Where(v => v.State == VideoState.Failed))
                        library.Cancel(v);
                }));

                actions.Controls.Add(HeaderButton(
                    library.Paused ? "Resume downloads" : "Pause downloads",
                    () =>
                    {
                        library.Paused = !library.Paused;
                        library.Bump();
                    },
                    accent: library.Paused));
                break;
        }

        actions.ResumeLayout();
    }

    private static string FilterName(LibFilter f) => f switch
    {
        LibFilter.Unwatched => "Unwatched",
        LibFilter.InProgress => "In progress",
        LibFilter.Watched => "Watched",
        _ => "All"
    };

    private static string SortName(LibSort s) => s switch
    {
        LibSort.Oldest => "oldest",
        LibSort.Title => "title",
        LibSort.Shortest => "shortest",
        LibSort.Longest => "longest",
        _ => "newest"
    };

    private string TitleFor(View v) => v switch
    {
        View.Home => "Recommended for you",
        View.Subs => "Subscriptions",
        View.Search => searchQuery.Length > 0 ? $"Results for \"{searchQuery}\"" : "Search",
        View.Library => "Saved videos",
        View.History => "Watch history",
        View.Later => "Watch later",
        View.Queue => "Downloads",
        View.Playlists => playlistTitle.Length > 0 ? playlistTitle : "Playlists",
        View.Sources => "Sources",
        _ => "Settings"
    };

    // --------------------------------------------------
    // NAVIGATION (with back / forward)
    // --------------------------------------------------

    private void Go(View target, string? query = null, bool record = true)
    {
        if (record)
        {
            // Opening something new discards the "forward" part.
            if (navPos < navStack.Count - 1)
                navStack.RemoveRange(navPos + 1, navStack.Count - navPos - 1);

            if (navPos < 0 || navStack[navPos] != (target, query))
            {
                navStack.Add((target, query));
                navPos = navStack.Count - 1;
            }
        }

        view = target;

        if (target == View.Search && query != null)
        {
            searchQuery = query;
            searchResults = searchCache.TryGetValue(query, out List<VideoInfo>? cached)
                ? cached
                : new List<VideoInfo>();

            searchBox.Text = query;
        }

        bool videos = target is View.Home or View.Subs or View.Search or
            View.Library or View.History or View.Later or View.Queue or View.Playlists;

        playlistBar.Visible = target == View.Playlists;

        if (target == View.Playlists)
        {
            RefreshPlaylistRecent();
            _ = LoadMyPlaylistsAsync(force: false);
        }

        grid.Visible = videos;
        sourcesView.Visible = target == View.Sources;
        settingsView.Visible = target == View.Settings;

        grid.ResetScroll();
        grid.ShowNotes = target == View.History;

        if (target is View.Library or View.History or View.Later)
        {
            // The search box filters these lists: start fresh.
            if (record && target != View.Search)
                searchBox.Text = "";
        }

        if (target == View.Settings)
            LoadSettingsControls();

        // Load fresh recommendations when you open Home / Subscriptions.
        if (target is View.Home or View.Subs && NeedsFeed(target == View.Subs))
            _ = library.RefreshFeedAsync(target == View.Subs);

        backButton.Enabled = navPos > 0;
        forwardButton.Enabled = navPos < navStack.Count - 1;

        BuildHeader();
        UpdateSidebar();
        RefreshView();
    }

    private void GoBack()
    {
        if (navPos <= 0)
            return;

        navPos--;
        (View v, string? q) = navStack[navPos];
        Go(v, q, record: false);
    }

    private void GoForward()
    {
        if (navPos >= navStack.Count - 1)
            return;

        navPos++;
        (View v, string? q) = navStack[navPos];
        Go(v, q, record: false);
    }

    private bool NeedsFeed(bool subs)
    {
        if (!YtAccount.SignedIn || library.AuthExpired || library.FeedBusy)
            return false;

        DateTime? updated = subs ? library.SubsUpdated : library.HomeUpdated;

        return updated == null || DateTime.Now - updated > TimeSpan.FromMinutes(20);
    }

    // --------------------------------------------------
    // VIEW CONTENT
    // --------------------------------------------------

    private TubeVideo Resolve(VideoInfo info)
    {
        TubeVideo? known = library.Get(info.Id);

        if (known != null)
            return known;

        if (!temp.TryGetValue(info.Id, out TubeVideo? video))
        {
            video = TubeVideo.From(info);
            temp[info.Id] = video;
        }

        return video;
    }

    private bool Matches(TubeVideo v, string filter) =>
        filter.Length == 0 ||
        v.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        v.Channel.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private List<TubeVideo> DesiredItems()
    {
        if (temp.Count > 600)
            temp.Clear();

        List<TubeVideo> all = library.Snapshot();
        string filter = searchBox.Text.Trim();

        switch (view)
        {
            case View.Home:
                return YtAccount.SignedIn
                    ? library.HomeFeed.Select(Resolve).ToList()
                    : new List<TubeVideo>();

            case View.Subs:
                return YtAccount.SignedIn
                    ? library.SubsFeed.Select(Resolve).ToList()
                    : new List<TubeVideo>();

            case View.Search:
                return searchResults.Select(Resolve).ToList();

            case View.Playlists:
                return playlistResults.Select(Resolve).ToList();

            case View.Library:
            {
                IEnumerable<TubeVideo> q = all
                    .Where(v => v.State == VideoState.Done)
                    .Where(v => Matches(v, filter));

                q = libFilter switch
                {
                    LibFilter.Unwatched => q.Where(v => !v.Watched && v.Position <= 5),
                    LibFilter.InProgress => q.Where(v => !v.Watched && v.Position > 5),
                    LibFilter.Watched => q.Where(v => v.Watched),
                    _ => q
                };

                q = libSort switch
                {
                    LibSort.Oldest => q.OrderBy(v => v.Downloaded),
                    LibSort.Title => q.OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
                    LibSort.Shortest => q.OrderBy(v => v.Duration),
                    LibSort.Longest => q.OrderByDescending(v => v.Duration),
                    _ => q.OrderByDescending(v => v.Downloaded)
                };

                return q.ToList();
            }

            case View.History:
                return library.History()
                    .Select(h =>
                    {
                        TubeVideo v = Resolve(new VideoInfo(h.Id, h.Title, h.Channel, h.Duration));
                        v.Note = "Watched " + Ago(h.WatchedAt);
                        return v;
                    })
                    .Where(v => Matches(v, filter))
                    .ToList();

            case View.Later:
                return library.WatchLater()
                    .Select(Resolve)
                    .Where(v => Matches(v, filter))
                    .ToList();

            case View.Queue:
                return all
                    .Where(v => v.State is VideoState.Downloading or VideoState.Queued or VideoState.Failed)
                    .OrderBy(v => v.State == VideoState.Downloading ? 0 : v.State == VideoState.Queued ? 1 : 2)
                    .ThenBy(v => v.Order)
                    .ToList();

            default:
                return new List<TubeVideo>();
        }
    }

    private void RefreshView()
    {
        lastVersion = library.Version;

        titleLabel.Text = TitleFor(view);

        if (view == View.Sources)
        {
            RefreshSources();
            subtitleLabel.Text = "Channels, playlists and topics checked in the background";
            empty.Visible = false;
            emptyAction.Visible = false;
            return;
        }

        if (view == View.Settings)
        {
            subtitleLabel.Text = "";
            empty.Visible = false;
            emptyAction.Visible = false;
            return;
        }

        List<TubeVideo> desired = DesiredItems();
        grid.SetItems(desired);

        subtitleLabel.Text = SubtitleFor(desired);

        bool none = desired.Count == 0;

        empty.Visible = none;
        emptyAction.Visible = false;

        if (!none)
            return;

        empty.BringToFront();
        emptyAction.BringToFront();

        bool signedIn = YtAccount.SignedIn;

        switch (view)
        {
            case View.Home:
            case View.Subs:
            {
                string name = view == View.Home ? "recommendations" : "subscriptions";

                if (!signedIn)
                {
                    empty.Text =
                        $"Sign in with your Google account to see your YouTube {name} here.\n\n" +
                        "You sign in on Google's own page and stay signed in.\n" +
                        "Your password is never seen by this app.";

                    emptyAction.Text = "Sign in with Google";
                    emptyAction.Visible = true;
                }
                else if (library.AuthExpired)
                {
                    empty.Text = "Google ended your session.\n\nSign in again to load your " + name + ".";
                    emptyAction.Text = "Sign in again";
                    emptyAction.Visible = true;
                }
                else if (library.FeedBusy)
                {
                    empty.Text = $"Loading your {name}...";
                }
                else if (library.FeedError != null)
                {
                    empty.Text = "Could not load your " + name + ":\n\n" + library.FeedError;
                }
                else
                {
                    empty.Text = $"No {name} to show yet.";
                }

                break;
            }

            case View.Playlists:
                empty.Text = "Pick one of your own playlists above, or paste a playlist link and press Load.\n\n" +
                             "Then play all of it, shuffle it, or save it for offline viewing.";
                break;

            case View.Library:
                empty.Text = libFilter != LibFilter.All || searchBox.Text.Length > 0
                    ? "No saved videos match."
                    : "Nothing saved yet.\n\n" +
                      "Hover a video and click the download button, or press D,\n" +
                      "or add Sources to fill this automatically.";
                break;

            case View.History:
                empty.Text = searchBox.Text.Length > 0
                    ? "Nothing in your history matches."
                    : "Videos you watch will show up here.";
                break;

            case View.Later:
                empty.Text = searchBox.Text.Length > 0
                    ? "Nothing matches."
                    : "Hover a video and click the clock button, or press L,\nto watch it later.";
                break;

            case View.Queue:
                empty.Text = "Nothing is downloading.";
                break;

            default:
                empty.Text = searching
                    ? "Searching..."
                    : "Type something in the search box and press Enter.";
                break;
        }
    }

    private static string PlaylistSummary(List<TubeVideo> items)
    {
        TimeSpan total = TimeSpan.FromSeconds(items.Sum(v => (long)v.Duration));

        string length = total.TotalHours >= 1
            ? $"{(int)total.TotalHours} h {total.Minutes} min"
            : $"{(int)total.TotalMinutes} min";

        return $"{items.Count} videos  -  {length} in all";
    }

    private string SubtitleFor(List<TubeVideo> items)
    {
        switch (view)
        {
            case View.Playlists:
                return items.Count == 0 ? "" : PlaylistSummary(items);

            case View.Home:
                return library.HomeUpdated != null
                    ? $"{items.Count} videos  -  updated {Ago(library.HomeUpdated.Value)}"
                    : "";

            case View.Subs:
                return library.SubsUpdated != null
                    ? $"{items.Count} videos  -  updated {Ago(library.SubsUpdated.Value)}"
                    : "";

            case View.Library:
            {
                long bytes = items.Sum(v => v.Size);
                return $"{items.Count} videos  -  {bytes / 1073741824.0:0.0} GB";
            }

            case View.History:
                return $"{items.Count} videos";

            case View.Later:
                return $"{items.Count} videos";

            case View.Queue:
                return library.Paused ? "Paused" : $"{items.Count} in the queue";

            case View.Search:
                return searching ? "Searching..." : $"{items.Count} results";

            default:
                return "";
        }
    }

    private static string Ago(DateTime time)
    {
        TimeSpan span = DateTime.Now - time;

        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays} d ago"
            : time.ToString("d MMM yyyy");
    }

    // --------------------------------------------------
    // TIMER / STATUS
    // --------------------------------------------------

    private void OnTimer()
    {
        // Watched videos are deleted automatically: say so when it happens.
        if (library.PurgedTotal != lastPurged)
        {
            int removed = library.PurgedTotal - lastPurged;
            lastPurged = library.PurgedTotal;

            if (removed > 0)
            {
                SetMessage(removed == 1
                    ? "Removed 1 watched video from this PC."
                    : $"Removed {removed} watched videos from this PC.", 10);
            }
        }

        if (library.Version != lastVersion)
        {
            RefreshView();
            UpdateSidebar();

            if (view == View.Queue)
                BuildHeader();
        }

        UpdateStatus();
    }

    private void SetMessage(string text, int seconds = 6)
    {
        message = text;
        messageUntil = Environment.TickCount64 + seconds * 1000L;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string left;

        List<TubeVideo> snapshot = library.Snapshot();
        TubeVideo? active = snapshot.FirstOrDefault(v => v.State == VideoState.Downloading);
        int queued = snapshot.Count(v => v.State == VideoState.Queued);

        if (Environment.TickCount64 < messageUntil)
            left = message;
        else if (!YtDlp.IsInstalled)
            left = "yt-dlp is not installed yet; it is set up the first time you search.";
        else if (library.Paused)
            left = "Downloads paused.";
        else if (library.StorageFull)
            left = "Library is full: raise the size limit in Settings, or watch / delete videos.";
        else if (library.LowDisk)
            left = "Downloads paused: less than 2 GB of free disk space.";
        else if (active != null)
        {
            left = $"Saving offline: {Trunc(active.Title, 46)}  {active.Progress:0}%  " +
                   $"{active.Speed}  ETA {active.Eta}" +
                   (queued > 0 ? $"   (+{queued} waiting)" : "");
        }
        else if (library.Activity.Length > 0)
            left = library.Activity;
        else if (queued > 0)
            left = $"{queued} waiting to download";
        else
            left = "Ready.";

        statusLeft.Text = left;

        long used = library.UsedBytes();
        int count = snapshot.Count(v => v.State == VideoState.Done);

        statusRight.Text =
            $"{count} saved  -  {used / 1073741824.0:0.0} / {library.Settings.MaxGb} GB";
    }

    private static string Trunc(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    // --------------------------------------------------
    // FIRST RUN: consent + tools
    // --------------------------------------------------

    private async Task<bool> EnsureReady()
    {
        TubeSettings settings = library.Settings;

        if (!settings.NoticeAccepted)
        {
            DialogResult ok = MessageBox.Show(
                this,
                "UtilTube lets you watch YouTube and save videos on your PC for offline viewing.\n\n" +
                "Downloading videos may be against YouTube's terms of service. " +
                "Use it only for videos you are allowed to keep for your own offline use.\n\n" +
                "Continue?",
                "UtilTube",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (ok != DialogResult.OK)
                return false;

            settings.NoticeAccepted = true;
            library.SettingsChanged();
        }

        if (!YtDlp.IsInstalled)
        {
            DialogResult install = MessageBox.Show(
                this,
                "UtilTube needs yt-dlp (a free, open-source tool) to talk to YouTube.\n\n" +
                "Download it now from the official GitHub release (about 23 MB) and check " +
                "its checksum before installing?",
                "UtilTube",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (install != DialogResult.Yes)
                return false;

            SetMessage("Downloading yt-dlp...", 120);

            try
            {
                await YtDlp.InstallAsync(CancellationToken.None);
                SetMessage("yt-dlp installed.", 5);
            }
            catch (Exception ex)
            {
                SetMessage("Could not install yt-dlp: " + ex.Message, 15);
                return false;
            }
        }

        if (FfmpegRecorder.FindFfmpeg() == null)
        {
            MessageBox.Show(
                this,
                "ffmpeg is needed to join the video and audio of a download.\n\n" +
                "Install it with:\n\nwinget install ffmpeg\n\nthen reopen UtilTube.",
                "UtilTube",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return false;
        }

        return true;
    }

    // --------------------------------------------------
    // ACCOUNT
    // --------------------------------------------------

    private void UpdateAccountButton()
    {
        bool signedIn = YtAccount.SignedIn;

        accountButton.Text = signedIn ? "  Account" : "  Sign in";
        accountButton.Font = new Font("Segoe MDL2 Assets", 9f);
        accountButton.UseCompatibleTextRendering = false;
        accountButton.Text = signedIn ? "Account" : "Sign in";
        accountButton.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        accountButton.BackColor = signedIn ? Theme.Search : Theme.Accent;
        accountButton.ForeColor = signedIn ? Theme.On : Theme.Background;
    }

    private void AccountClicked()
    {
        if (!YtAccount.SignedIn)
        {
            SignIn();
            return;
        }

        ContextMenuStrip menu = new();

        menu.Items.Add("Signed in with Google", null).Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh recommendations", null, (_, _) => RefreshFeedNow());
        menu.Items.Add("Account settings", null, (_, _) => Go(View.Settings));
        menu.Items.Add("Sign out", null, (_, _) => SignOut());

        menu.Show(accountButton, new Point(0, accountButton.Height));
    }

    private async void SignIn()
    {
        if (!await EnsureReady())
            return;

        DialogResult ok = MessageBox.Show(
            this,
            "Sign in with your Google account to get your YouTube recommendations.\n\n" +
            "  -  You sign in on Google's own page. This app never sees your password.\n" +
            "  -  You stay signed in: the session renews itself in the background.\n" +
            "  -  It is stored only on this PC, for your Windows user.\n" +
            "  -  It is used only to read your Home recommendations and Subscriptions.\n" +
            "      Videos are streamed and saved without your account.\n" +
            "  -  Automated use of a signed-in account can occasionally upset YouTube,\n" +
            "      so keep the refresh interval at 30 minutes or more.\n" +
            "  -  Sign out any time to delete it.\n\n" +
            "Continue?",
            "Sign in with Google",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);

        if (ok != DialogResult.OK)
            return;

        if (GoogleSignInForm.Run(this))
        {
            library.NoteSignedIn();
            UpdateAccountButton();

            SetMessage("Signed in. Loading your recommendations...", 20);
            Go(View.Home);

            _ = library.RefreshFeedAsync(false);
            _ = library.RefreshFeedAsync(true);
        }
        else
        {
            SetMessage("Sign-in was not completed.");
        }

        UpdateAccountButton();
        RefreshView();
    }

    private void SignOut()
    {
        DialogResult answer = MessageBox.Show(
            this,
            "Sign out and delete the saved Google session from this PC?\n\n" +
            "Videos already saved stay in your library.",
            "Sign out",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        YtAccount.SignOut();
        library.ClearFeeds();

        UpdateAccountButton();
        SetMessage("Signed out.");

        if (view is View.Home or View.Subs)
            RefreshView();
    }

    private void RefreshFeedNow()
    {
        if (!YtAccount.SignedIn)
        {
            SignIn();
            return;
        }

        library.AuthExpired = false;

        _ = library.RefreshFeedAsync(view == View.Subs);
        SetMessage("Refreshing...");
    }

    // --------------------------------------------------
    // SEARCH
    // --------------------------------------------------

    public async void BeginSearch(string query)
    {
        query = query.Trim();

        if (query.Length == 0)
            return;

        searchBox.Text = query;

        // A playlist link (or id) opens as a playlist, not as search results.
        if (YtDlp.ExtractVideoId(query) == null && YtDlp.ExtractPlaylistId(query) != null)
        {
            await LoadPlaylistAsync(query);
            return;
        }

        if (!await EnsureReady())
            return;

        searchCts?.Cancel();
        CancellationTokenSource cts = searchCts = new CancellationTokenSource();

        searching = true;
        temp.Clear();
        searchCache.Remove(query);
        Go(View.Search, query);

        SetMessage($"Searching for \"{query}\"...", 60);

        try
        {
            List<VideoInfo> found;
            string? id = YtDlp.ExtractVideoId(query);

            if (id != null)
            {
                VideoInfo? info = await YtDlp.InfoAsync(
                    "https://www.youtube.com/watch?v=" + id, cts.Token);

                found = info == null ? new List<VideoInfo>() : new List<VideoInfo> { info };
            }
            else if (YtDlp.IsPlaylistLink(query))
            {
                found = await YtDlp.ListAsync(query, 50, cts.Token);
            }
            else
            {
                found = await YtDlp.SearchAsync(query, 30, false, cts.Token);
            }

            if (searchCache.Count > 10)
                searchCache.Clear();

            searchCache[query] = found;

            if (searchQuery == query)
                searchResults = found;

            // A pasted video link means "save this": queue it straight away.
            if (id != null && found.Count == 1)
            {
                library.Enqueue(Resolve(found[0]), priority: true);
                SetMessage("Saving for offline viewing...", 8);
            }
            else
            {
                SetMessage($"{found.Count} results", 8);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            searchResults = new List<VideoInfo>();
            SetMessage("Search failed: " + ex.Message, 15);
        }
        finally
        {
            if (searchCts == cts)
                searching = false;
        }

        RefreshView();

        if (view == View.Search && grid.Items.Count > 0)
        {
            grid.Focus();
            grid.SelectFirst();
        }
    }

    // --------------------------------------------------
    // ACTIONS
    // --------------------------------------------------

    private List<TubeVideo> Selected() => grid.SelectedList();

    private async void ActivateSelected()
    {
        TubeVideo? video = Selected().FirstOrDefault();

        if (video == null)
            return;

        // Saved videos play offline; anything else streams right away.
        if (video.State != VideoState.Done && !await EnsureReady())
            return;

        // Watch right away, and keep saving it in the background (first in
        // line) so it is offline by the time you want it again.
        if (video.State is VideoState.None or VideoState.Failed or VideoState.Queued)
        {
            library.Enqueue(video, priority: true);
            SetMessage("Playing now. It keeps saving in the background.");
        }
        else if (video.State == VideoState.Downloading)
        {
            SetMessage("Playing now. It is still saving in the background.");
        }

        PlayVideo(video);
    }

    private void PlayVideo(TubeVideo video)
    {
        List<TubeVideo> playlist = grid.Items.ToList();
        int start = playlist.IndexOf(video);

        if (start < 0)
        {
            playlist = new List<TubeVideo> { video };
            start = 0;
        }

        // On the Playlists page: from this video on to the end of the playlist,
        // and (with Loop on) round again from the first.
        if (view == View.Playlists && playlist.Count > 1)
            VideoPlayerForm.PlayList(playlist, library.Settings.PlaylistLoop, start);
        else
            VideoPlayerForm.Play(playlist, start);
    }

    private void PlayAll()
    {
        if (grid.Items.Count == 0)
            return;

        if (view == View.Playlists)
            VideoPlayerForm.PlayList(grid.Items.ToList(), library.Settings.PlaylistLoop);
        else
            VideoPlayerForm.Play(grid.Items.ToList(), 0);
    }

    private async void SaveSelected()
    {
        List<TubeVideo> items = Selected()
            .Where(v => v.State is VideoState.None or VideoState.Failed)
            .ToList();

        if (items.Count == 0 || !await EnsureReady())
            return;

        // Offline downloads are busy: a video saved now would wait in line.
        // Offer to watch it online right away (it is still saved for later).
        if (items.Count == 1 &&
            library.Settings.AskOnlineWhenBusy &&
            library.DownloadsBusy(out int waiting, out string why))
        {
            OnlineChoice choice = OnlinePrompt.Ask(
                this, items[0].Title, why, waiting, out bool dontAsk);

            if (dontAsk)
            {
                library.Settings.AskOnlineWhenBusy = false;
                library.SettingsChanged();
            }

            if (choice == OnlineChoice.Cancel)
                return;

            library.Enqueue(items[0], priority: choice == OnlineChoice.SaveOnly);

            if (choice == OnlineChoice.Online)
            {
                SetMessage("Playing online. It is also queued, so it will be saved offline too.", 10);
                PlayVideo(items[0]);
            }
            else
            {
                SetMessage("Queued for offline viewing.");
            }

            return;
        }

        foreach (TubeVideo v in items)
            library.Enqueue(v, priority: true);

        SetMessage($"Saving {items.Count} video(s) for offline viewing.");
    }

    private async void SaveAllShown()
    {
        List<TubeVideo> items = grid.Items
            .Where(v => v.State is VideoState.None or VideoState.Failed)
            .ToList();

        if (items.Count == 0 || !await EnsureReady())
            return;

        foreach (TubeVideo v in items)
            library.Enqueue(v);

        SetMessage($"Queued {items.Count} video(s). They save one after another in the background.");
    }

    private void ToggleLaterSelected()
    {
        List<TubeVideo> items = Selected();

        foreach (TubeVideo v in items)
            library.ToggleWatchLater(v);

        if (items.Count > 0)
        {
            SetMessage(library.IsWatchLater(items[0].Id)
                ? "Added to Watch later."
                : "Removed from Watch later.");
        }
    }

    private void OnQuickAction(TubeVideo video, string kind)
    {
        if (kind == "later")
        {
            library.ToggleWatchLater(video);

            SetMessage(library.IsWatchLater(video.Id)
                ? "Added to Watch later."
                : "Removed from Watch later.");
        }
        else if (kind == "save")
        {
            grid.SelectOnly(video);
            SaveSelected();
        }
    }

    private void ClearHistory()
    {
        DialogResult answer = MessageBox.Show(
            this,
            "Clear your whole watch history?\n\nSaved videos are not affected.",
            "Clear history",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
        {
            library.ClearHistory();
            SetMessage("History cleared.");
        }
    }

    private void RemoveSelected()
    {
        List<TubeVideo> items = Selected();

        if (items.Count == 0)
            return;

        switch (view)
        {
            case View.Library:
            {
                DialogResult answer = MessageBox.Show(
                    this,
                    items.Count == 1
                        ? $"Delete \"{items[0].Title}\" from your PC?\n\nThe saved file will be removed."
                        : $"Delete {items.Count} videos from your PC?\n\nThe saved files will be removed.",
                    "Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (answer != DialogResult.Yes)
                    return;

                foreach (TubeVideo v in items)
                    library.Delete(v);

                break;
            }

            case View.Queue:
                foreach (TubeVideo v in items)
                    library.Cancel(v);

                break;

            case View.History:
                foreach (TubeVideo v in items)
                    library.RemoveFromHistory(v.Id);

                break;

            case View.Later:
                foreach (TubeVideo v in items)
                    library.RemoveWatchLater(v.Id);

                break;
        }
    }

    private void OnGridKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Enter:
                ActivateSelected();
                e.Handled = true;
                break;

            case Keys.Delete:
                RemoveSelected();
                e.Handled = true;
                break;

            case Keys.D:
            case Keys.S:
                SaveSelected();
                e.Handled = true;
                break;

            case Keys.L:
                ToggleLaterSelected();
                e.Handled = true;
                break;

            case Keys.W:
                foreach (TubeVideo v in Selected().Where(v => v.State == VideoState.Done))
                    library.SetWatched(v, !v.Watched);

                e.Handled = true;
                break;

            case Keys.F5:
                if (view is View.Home or View.Subs)
                    RefreshFeedNow();

                e.Handled = true;
                break;

            case Keys.Back:
                GoBack();
                e.Handled = true;
                break;

            case Keys.Oem2: // "/" focuses the search box
                searchBox.Focus();
                e.SuppressKeyPress = true;
                e.Handled = true;
                break;
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        ContextMenuStrip menu = new();

        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();

            List<TubeVideo> items = Selected();

            if (items.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            TubeVideo first = items[0];

            menu.Items.Add(first.State == VideoState.Done ? "Play (offline)" : "Watch now", null,
                (_, _) => ActivateSelected());

            menu.Items.Add(library.IsWatchLater(first.Id) ? "Remove from Watch later" : "Watch later", null,
                (_, _) => ToggleLaterSelected());

            if (first.State == VideoState.Done)
            {
                menu.Items.Add(first.Watched ? "Mark as not watched" : "Mark as watched", null, (_, _) =>
                {
                    foreach (TubeVideo v in items.Where(v => v.State == VideoState.Done))
                        library.SetWatched(v, !first.Watched);
                });
                menu.Items.Add(first.Keep ? "Allow auto-delete" : "Keep (never auto-delete)", null, (_, _) =>
                {
                    foreach (TubeVideo v in items.Where(v => v.State == VideoState.Done))
                        library.SetKeep(v, !first.Keep);
                });
                menu.Items.Add("Show file in folder", null, (_, _) =>
                {
                    if (first.File != null)
                        Process.Start("explorer.exe", $"/select,\"{first.File}\"");
                });
            }
            else
            {
                menu.Items.Add("Save offline", null, (_, _) => SaveSelected());

                if (view is View.Search or View.Home or View.Subs)
                    menu.Items.Add("Save all shown", null, (_, _) => SaveAllShown());

                if (view == View.Queue && first.State == VideoState.Failed)
                    menu.Items.Add("Retry", null, (_, _) => library.Retry(first));
            }

            if (view is View.Library or View.Queue or View.History or View.Later)
            {
                menu.Items.Add(new ToolStripSeparator());

                string remove = view switch
                {
                    View.Library => "Delete from PC",
                    View.Queue => "Cancel",
                    View.History => "Remove from history",
                    _ => "Remove from list"
                };

                menu.Items.Add(remove, null, (_, _) => RemoveSelected());
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open on YouTube", null, (_, _) =>
                Process.Start(new ProcessStartInfo(first.Url) { UseShellExecute = true }));
            menu.Items.Add("Copy link", null, (_, _) =>
            {
                try
                {
                    Clipboard.SetText(first.Url);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Clipboard busy.
                }
            });
        };

        return menu;
    }

    // --------------------------------------------------
    // PLAYLISTS: load any playlist, play it, shuffle it, save it
    // --------------------------------------------------

    private readonly Panel playlistBar = new();
    private readonly TextBox playlistBox = new();
    private readonly ComboBox playlistRecent = new();
    private List<VideoInfo> playlistResults = new();
    private string playlistTitle = "";
    private string playlistId = "";
    private CancellationTokenSource? playlistCts;
    private bool fillingRecent;

    private readonly ComboBox playlistMine = new();
    private Button playlistLoop = new();

    private void UpdateLoopButton()
    {
        bool on = library.Settings.PlaylistLoop;

        playlistLoop.Text = "Loop: " + (on ? "On" : "Off");
        playlistLoop.BackColor = on ? Theme.Accent : Theme.Search;
        playlistLoop.ForeColor = on ? Theme.Background : Theme.Strong;
    }

    private void BuildPlaylistBar()
    {
        playlistBar.Dock = DockStyle.Top;
        playlistBar.Height = 94;
        playlistBar.BackColor = Theme.Background;
        playlistBar.Visible = false;

        // ---- row 1: a link, and what to do with the playlist ----
        playlistBox.Location = new Point(22, 12);
        playlistBox.Size = new Size(330, 26);
        playlistBox.BackColor = Theme.Search;
        playlistBox.ForeColor = Theme.Strong;
        playlistBox.BorderStyle = BorderStyle.FixedSingle;
        playlistBox.PlaceholderText = "Paste a playlist link or id...";
        playlistBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                _ = LoadPlaylistAsync(playlistBox.Text);
            }
        };

        playlistBar.Controls.Add(playlistBox);

        int x = 360;
        int y = 9;

        Button Add(string text, int width, Action action)
        {
            Button b = new()
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Search,
                ForeColor = Theme.Strong,
                Cursor = Cursors.Hand,
                TabStop = false
            };

            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => action();
            playlistBar.Controls.Add(b);
            x += width + 8;
            return b;
        }

        Button load = Add("Load", 64, () => _ = LoadPlaylistAsync(playlistBox.Text));
        load.BackColor = Theme.Accent;
        load.ForeColor = Theme.Background;

        Add("Paste", 64, () =>
        {
            if (Clipboard.ContainsText())
            {
                playlistBox.Text = Clipboard.GetText().Trim();
                _ = LoadPlaylistAsync(playlistBox.Text);
            }
        });

        x += 20;
        Add("Play in order", 108, PlayPlaylist);
        Add("Shuffle", 74, () => PlayPlaylist(shuffle: true));

        playlistLoop = Add("", 96, () =>
        {
            library.Settings.PlaylistLoop = !library.Settings.PlaylistLoop;
            library.SettingsChanged();
            UpdateLoopButton();
        });
        UpdateLoopButton();

        Add("Save all", 78, SavePlaylistOffline);

        // ---- row 2: your account's playlists, and the ones you loaded before ----
        Label Caption(string text, int left)
        {
            Label label = new()
            {
                Text = text,
                Location = new Point(left, 55),
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Theme.Background
            };

            playlistBar.Controls.Add(label);
            return label;
        }

        void Style(ComboBox box, int left, int width)
        {
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            box.FlatStyle = FlatStyle.Flat;
            box.BackColor = Theme.Search;
            box.ForeColor = Theme.Strong;
            box.Location = new Point(left, 52);
            box.Width = width;
            box.DisplayMember = nameof(SavedPlaylist.Title);
            playlistBar.Controls.Add(box);
        }

        Caption("Your playlists:", 22);
        Style(playlistMine, 128, 280);
        playlistMine.SelectionChangeCommitted += (_, _) =>
        {
            if (fillingRecent || playlistMine.SelectedItem is not SavedPlaylist p)
                return;

            if (p.Id.Length == 0)
                SignIn();
            else
                _ = LoadPlaylistAsync("https://www.youtube.com/playlist?list=" + p.Id);
        };

        Button refresh = new()
        {
            Text = "Refresh",
            Location = new Point(416, 49),
            Size = new Size(74, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Search,
            ForeColor = Theme.Strong,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        refresh.FlatAppearance.BorderSize = 0;
        refresh.Click += (_, _) => _ = LoadMyPlaylistsAsync(force: true);
        playlistBar.Controls.Add(refresh);

        Caption("Loaded before:", 520);
        Style(playlistRecent, 624, 260);
        playlistRecent.SelectionChangeCommitted += (_, _) =>
        {
            if (!fillingRecent && playlistRecent.SelectedItem is SavedPlaylist p)
                _ = LoadPlaylistAsync("https://www.youtube.com/playlist?list=" + p.Id);
        };
    }

    private void RefreshMyPlaylistsUi()
    {
        fillingRecent = true;

        try
        {
            playlistMine.Items.Clear();

            if (!YtAccount.SignedIn)
            {
                playlistMine.Items.Add(new SavedPlaylist { Id = "", Title = "Sign in to see your playlists..." });
                playlistMine.SelectedIndex = 0;
                return;
            }

            List<SavedPlaylist> mine = library.MyPlaylists();

            foreach (SavedPlaylist p in mine)
                playlistMine.Items.Add(p);

            if (mine.Count == 0)
            {
                playlistMine.Items.Add(new SavedPlaylist { Id = "", Title = "Loading your playlists..." });
                playlistMine.SelectedIndex = 0;
            }
            else
            {
                int current = mine.FindIndex(p => p.Id == playlistId);
                playlistMine.SelectedIndex = current;
            }
        }
        finally
        {
            fillingRecent = false;
        }
    }

    private async Task LoadMyPlaylistsAsync(bool force)
    {
        RefreshMyPlaylistsUi();

        if (!YtAccount.SignedIn)
            return;

        // Opening the page is enough while the list is fresh.
        if (!force && library.MyPlaylists().Count > 0 &&
            library.MyPlaylistsUpdated != null &&
            DateTime.Now - library.MyPlaylistsUpdated.Value < TimeSpan.FromHours(1))
        {
            return;
        }

        try
        {
            List<SavedPlaylist> mine = await YtDlp.MyPlaylistsAsync(CancellationToken.None);

            if (mine.Count > 0)
            {
                library.SetMyPlaylists(mine);

                if (force)
                    SetMessage($"{mine.Count} playlists on your account", 8);
            }
            else if (force)
            {
                SetMessage("No playlists found on your account.", 8);
            }
        }
        catch (AuthExpiredException)
        {
            SetMessage("Google ended your session: sign in again to see your playlists.", 12);
        }
        catch (Exception ex)
        {
            if (force)
                SetMessage("Could not load your playlists: " + ex.Message, 12);
        }

        RefreshMyPlaylistsUi();
    }

    private void RefreshPlaylistRecent()
    {
        fillingRecent = true;

        try
        {
            List<SavedPlaylist> saved = library.Playlists();

            playlistRecent.Items.Clear();
            playlistRecent.DisplayMember = nameof(SavedPlaylist.Title);

            foreach (SavedPlaylist p in saved)
                playlistRecent.Items.Add(p);

            // Show the one on screen, or a hint when there are none yet.
            int current = saved.FindIndex(p => p.Id == playlistId);

            if (current >= 0)
                playlistRecent.SelectedIndex = current;
            else
                playlistRecent.SelectedIndex = -1;
        }
        finally
        {
            fillingRecent = false;
        }
    }

    private async Task LoadPlaylistAsync(string text)
    {
        text = text.Trim();

        if (text.Length == 0)
            return;

        if (YtDlp.ExtractPlaylistId(text) == null)
        {
            SetMessage("That does not look like a playlist link or id.", 10);
            return;
        }

        if (!await EnsureReady())
            return;

        playlistCts?.Cancel();
        CancellationTokenSource cts = playlistCts = new CancellationTokenSource();

        // Show the page straight away, with the old list until the new one arrives.
        if (view != View.Playlists)
            Go(View.Playlists);

        SetMessage("Loading the playlist...", 90);

        try
        {
            PlaylistData data = await YtDlp.PlaylistAsync(text, 500, cts.Token);

            if (cts.IsCancellationRequested)
                return;

            if (data.Videos.Count == 0)
            {
                SetMessage("That playlist has no videos you can watch (it may be private: sign in first).", 15);
                return;
            }

            playlistResults = data.Videos;
            playlistTitle = data.Title;
            playlistId = data.Id;
            playlistBox.Text = "";

            library.RememberPlaylist(data.Id, data.Title, data.Videos.Count);

            RefreshView();
            RefreshPlaylistRecent();

            SetMessage($"{data.Videos.Count} videos in \"{data.Title}\"", 10);

            if (view == View.Playlists && grid.Items.Count > 0)
            {
                grid.Focus();
                grid.SelectFirst();
            }
        }
        catch (OperationCanceledException)
        {
            // A newer load took over.
        }
        catch (AuthExpiredException)
        {
            SetMessage("This playlist is private: sign in with Google first (Settings).", 15);
        }
        catch (Exception ex)
        {
            SetMessage("Could not load the playlist: " + ex.Message, 15);
        }
    }

    private async void PlayPlaylist() => PlayPlaylist(shuffle: false);

    private async void PlayPlaylist(bool shuffle)
    {
        List<TubeVideo> list = grid.Items.ToList();

        if (list.Count == 0)
        {
            SetMessage("Load a playlist first.", 8);
            return;
        }

        if (!await EnsureReady())
            return;

        if (shuffle)
        {
            Random random = new();
            list = list.OrderBy(_ => random.Next()).ToList();
        }

        // In order (or shuffled), each video leading into the next; Loop starts it over.
        VideoPlayerForm.PlayList(list, library.Settings.PlaylistLoop);
    }

    private void SavePlaylistOffline()
    {
        List<TubeVideo> list = grid.Items.Where(v => v.State != VideoState.Done).ToList();

        if (list.Count == 0)
        {
            SetMessage("Everything in this playlist is already saved.", 8);
            return;
        }

        if (list.Count > 12 &&
            MessageBox.Show(this, $"Save {list.Count} videos for offline viewing?\n\nThey download in the background, one after another.",
                "UtilTube", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        foreach (TubeVideo v in list)
            library.Enqueue(v);

        SetMessage($"Saving {list.Count} videos...", 10);
    }

    // --------------------------------------------------
    // SOURCES VIEW
    // --------------------------------------------------

    private void BuildSourcesView()
    {
        sourcesView.Dock = DockStyle.Fill;
        sourcesView.BackColor = Theme.Background;

        Panel buttons = new() { Dock = DockStyle.Top, Height = 48, BackColor = Theme.Background };

        int x = 22;

        void Add(string text, int width, Action action)
        {
            Button b = new()
            {
                Text = text,
                Location = new Point(x, 8),
                Size = new Size(width, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Search,
                ForeColor = Theme.Strong,
                Cursor = Cursors.Hand,
                TabStop = false
            };

            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => action();
            buttons.Controls.Add(b);
            x += width + 8;
        }

        Add("+ Channel", 92, () => AddSource("channel", "Add a channel",
            "Channel handle or link  (for example @veritasium):"));
        Add("+ Playlist", 92, () => AddSource("playlist", "Add a playlist",
            "Playlist link:"));
        Add("+ Topic", 84, () => AddSource("search", "Add a topic",
            "Search words  (for example: lofi hip hop):"));
        Add("On / Off", 84, ToggleSelectedSource);
        Add("Remove", 80, RemoveSelectedSource);
        Add("Check now", 92, () =>
        {
            library.RefreshNow();
            SetMessage("Checking all sources...");
        });

        sourceList.Dock = DockStyle.Fill;
        sourceList.BorderStyle = BorderStyle.None;
        sourceList.BackColor = Theme.Background;
        sourceList.DrawMode = DrawMode.OwnerDrawFixed;
        sourceList.ItemHeight = 62;
        sourceList.IntegralHeight = false;
        sourceList.DrawItem += DrawSource;

        Label hint = new()
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            ForeColor = Theme.Text,
            Padding = new Padding(22, 6, 16, 0),
            Text = "New videos from these sources are saved in the background (newest first), " +
                   "so there is always something to watch offline."
        };

        sourcesView.Controls.Add(sourceList);
        sourcesView.Controls.Add(hint);
        sourcesView.Controls.Add(buttons);
    }

    private void RefreshSources()
    {
        int topIndex = sourceList.TopIndex;
        int selected = sourceList.SelectedIndex;

        sourceList.BeginUpdate();
        sourceList.Items.Clear();

        foreach (TubeSource s in library.Sources())
            sourceList.Items.Add(s);

        if (selected >= 0 && selected < sourceList.Items.Count)
            sourceList.SelectedIndex = selected;

        if (topIndex >= 0 && topIndex < sourceList.Items.Count)
            sourceList.TopIndex = topIndex;

        sourceList.EndUpdate();
    }

    private void DrawSource(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= sourceList.Items.Count)
            return;

        TubeSource s = (TubeSource)sourceList.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using SolidBrush back = new(selected ? Theme.ActiveRow : Theme.Background);
        e.Graphics.FillRectangle(back, e.Bounds);

        string kind = s.Kind switch
        {
            "channel" => "Channel",
            "playlist" => "Playlist",
            _ => "Topic"
        };

        string status = s.LastError != null
            ? "Error: " + Trunc(s.LastError, 80)
            : s.LastRefresh == null
                ? "Not checked yet"
                : "Checked " + Ago(s.LastRefresh.Value);

        using Font bold = new("Segoe UI", 10.5f, FontStyle.Bold);

        TextRenderer.DrawText(
            e.Graphics,
            $"{s.Title}   ({kind}{(s.Enabled ? "" : ", off")})",
            bold,
            new Rectangle(e.Bounds.X + 22, e.Bounds.Y + 10, e.Bounds.Width - 44, 22),
            s.Enabled ? Theme.Strong : Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(
            e.Graphics,
            $"newest {s.Count} per check  -  {status}",
            Font,
            new Rectangle(e.Bounds.X + 22, e.Bounds.Y + 34, e.Bounds.Width - 44, 20),
            s.LastError != null ? Theme.Off : Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private async void AddSource(string kind, string title, string label)
    {
        string? value = TextPrompt.Ask(this, title, label);

        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!await EnsureReady())
            return;

        TubeSource source = library.AddSource(kind, value, 10);
        SetMessage("Added. Its newest videos start saving shortly.");

        _ = library.RefreshSourceNow(source);
    }

    private TubeSource? SelectedSource() =>
        sourceList.SelectedItem as TubeSource;

    private void ToggleSelectedSource()
    {
        TubeSource? s = SelectedSource();

        if (s != null)
            library.ToggleSource(s);
    }

    private void RemoveSelectedSource()
    {
        TubeSource? s = SelectedSource();

        if (s == null)
            return;

        DialogResult answer = MessageBox.Show(
            this,
            $"Stop following \"{s.Title}\"?\n\nVideos already saved stay in your library.",
            "Remove source",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer == DialogResult.Yes)
            library.RemoveSource(s);
    }

    // --------------------------------------------------
    // SETTINGS VIEW
    // --------------------------------------------------

    private TubeSettingsList? settingsList;
    private readonly TextBox folderBox = new();

    private static readonly int[] Qualities = { 360, 480, 720, 1080 };

    private string AccountShort() =>
        !YtAccount.SignedIn ? "Not signed in"
        : library.AuthExpired ? "Signed in, but the session ended"
        : "Signed in with Google";

    private static string ShortPath(string path) =>
        path.Length <= 48 ? path : "..." + path[^45..];

    private void BuildSettingsView()
    {
        settingsView.Dock = DockStyle.Fill;
        settingsView.BackColor = Theme.Background;

        TubeSettingsList list = settingsList = new TubeSettingsList { Dock = DockStyle.Fill };
        settingsView.Controls.Add(list);

        void Toggle(string icon, string label, Func<bool> get, Action<bool> set, string tip) =>
            list.AddRow(icon, () => $"{label}: {(get() ? "On" : "Off")}", tip, _ =>
            {
                set(!get());
                library.SettingsChanged();
            });

        // Click = next value, right-click = the previous one.
        void Cycle(string icon, string label, int[] presets, Func<int> get, Action<int> set,
            Func<int, string> format, string tip) =>
            list.AddRow(icon, () => $"{label}: {format(get())}", tip, reverse =>
            {
                int[] all = presets.Contains(get())
                    ? presets
                    : presets.Append(get()).OrderBy(x => x).ToArray();

                int i = Array.IndexOf(all, get());
                i = (i + (reverse ? -1 : 1) + all.Length) % all.Length;

                set(all[i]);
                library.SettingsChanged();
            });

        string Count(int v) => v.ToString();
        string Off(int v, string unit) => v == 0 ? "Off" : v + " " + unit;
        TubeSettings s = library.Settings;

        // ---- account ----
        list.AddHeader("Account");

        list.AddRow("", () => "Account: " + AccountShort(),
            "Sign in for your YouTube recommendations and videos that require an account." +
            "\nThe session renews itself, so you stay signed in.", null);
        list.AddRow("", () => "Sign in with Google", "Opens a sign-in window.", _ => SignIn());
        list.AddRow("", () => "Sign out", "Forgets the saved sign-in on this computer.", _ => SignOut());
        list.AddRow("", () => "Import cookies.txt...",
            "An alternative to the sign-in window: use cookies exported from your browser.", _ => ImportCookies());

        int[] saveCounts = { 0, 1, 2, 3, 5, 10, 20, 30 };

        Cycle("", "Save recommended videos (per refresh)", saveCounts,
            () => library.Settings.RecommendedToSave, v => library.Settings.RecommendedToSave = v, Count,
            "How many recommended videos are downloaded for offline viewing each time the feed refreshes.\n0 = none.");
        Cycle("", "Save subscription videos (per refresh)", saveCounts,
            () => library.Settings.SubscriptionsToSave, v => library.Settings.SubscriptionsToSave = v, Count,
            "How many new videos from your subscriptions are downloaded each refresh.\n0 = none.");

        // ---- player ----
        list.AddHeader("Player");

        Toggle("", "Keep a watch history", () => library.Settings.KeepHistory, v => library.Settings.KeepHistory = v,
            "Remembers what you watched so it can show up in History.\nTurn off to stop recording.");
        Toggle("", "Remember playback speed per channel", () => library.Settings.RememberChannelSpeed,
            v => library.Settings.RememberChannelSpeed = v,
            "If you speed up or slow down a channel's video, the next one from the same channel starts at that speed.");
        Toggle("", "Play the next video automatically", () => library.Settings.Autoplay, v => library.Settings.Autoplay = v,
            "When a video ends, the next one in the list starts by itself after the countdown.");
        Cycle("", "Autoplay countdown", new[] { 0, 3, 5, 10, 15, 30 },
            () => library.Settings.AutoplayDelay, v => library.Settings.AutoplayDelay = v,
            v => v == 0 ? "at once" : v + " s",
            "How long the end screen waits before the next video starts.\nClick to step through, right-click to go back.");
        list.AddRow("\uE8A5", () => "Recommendations beside the video: " + (library.Settings.PlayerSidebar ? "On" : "Off"),
            "A panel on the right of the player with videos to watch next (press G in the player to show or hide it).\nIt appears when the window is wide enough.",
            reverse =>
            {
                library.Settings.PlayerSidebar = !library.Settings.PlayerSidebar;
                library.SettingsChanged();
            });
        Cycle("", "Player controls see-through", new[] { 25, 40, 55, 70, 85, 100 },
            () => library.Settings.PlayerBarOpacity, v => library.Settings.PlayerBarOpacity = v, v => v + "% visible",
            "How solid the bar at the bottom of the video is. It floats over the picture and hides while a video plays.\nLower = more see-through.");
        Cycle("", "Starting volume", new[] { 0, 25, 50, 75, 100 },
            () => library.Settings.DefaultVolume, v => library.Settings.DefaultVolume = v, v => v + "%",
            "The volume each video starts at.");

        // ---- downloads ----
        list.AddHeader("Videos and downloads");

        Cycle("", "Video quality", Qualities,
            () => library.Settings.Quality, v => library.Settings.Quality = v, v => v + "p",
            "Streamed videos and downloads use this quality from the start.\nLower it on a slow connection; 1080p looks sharpest.");
        list.AddRow("\uE8B2", () => "Better quality when the connection is fast: " + (library.Settings.AutoStreamQuality ? "On" : "Off"),
            "Streams start at 1080p when YouTube has been answering quickly, and stay gentler after a stall. It never switches in the middle of a video.\nOff = always the quality chosen above.",
            reverse =>
            {
                library.Settings.AutoStreamQuality = !library.Settings.AutoStreamQuality;
                library.SettingsChanged();
            });
        Toggle("\uE72E", "Use Chrome cookies if streaming fails",
            () => library.Settings.UseChromeCookiesForStreaming,
            v => library.Settings.UseChromeCookiesForStreaming = v,
            "Last resort only: if UtilTube's account and anonymous stream lookups fail, yt-dlp may read Chrome's local cookies for YouTube. Off by default.");
        Cycle("\uE839", "Network limit while I am in another program", new[] { 0, 5, 10, 20, 40 },
            () => library.Settings.UnfocusedLimitMbps, v => library.Settings.UnfocusedLimitMbps = v,
            v => v == 0 ? "no limit" : v + " Mbps",
            "Keeps this app from hogging your Wi-Fi: while another program is in front, the app (video buffering, downloads) stays under this speed.\nBack in the app it goes at full speed. Video plays fine at any of these; HD needs about 5 Mbps.");
        Cycle("", "Downloads at the same time", new[] { 1, 2, 3 },
            () => library.Settings.Parallel, v => library.Settings.Parallel = v, Count,
            "More at once is faster but uses more memory and bandwidth.");
        Cycle("", "Library size limit", new[] { 10, 25, 50, 100, 250, 500, 1000, 2000 },
            () => library.Settings.MaxGb, v => library.Settings.MaxGb = v, v => v + " GB",
            "When saved videos take more room than this, the oldest watched ones are deleted.");
        Cycle("", "Delete downloaded videos after", new[] { 0, 1, 2, 6, 12, 24, 48, 168 },
            () => library.Settings.DownloadDeleteHours, v => library.Settings.DownloadDeleteHours = v,
            v => v == 0 ? "never" : v < 48 ? v + " h" : v / 24 + " days",
            "Every downloaded video is removed this long after its download finished, watched or not.\nVideos marked Keep stay, and so does the one you are playing.");
        Cycle("", "Delete watched videos after", new[] { 0, 1, 6, 12, 24, 48, 168, 720 },
            () => library.Settings.AutoDeleteHours, v => library.Settings.AutoDeleteHours = v,
            v => v == 0 ? "never" : v < 48 ? v + " h" : v / 24 + " days",
            "Watched videos are removed from your disk after this long.");
        Cycle("", "Delete saved videos nobody opened after", new[] { 0, 7, 14, 30, 60, 90, 180, 365 },
            () => library.Settings.UnwatchedDeleteDays, v => library.Settings.UnwatchedDeleteDays = v,
            v => v == 0 ? "never" : v + " days",
            "Saved videos you never opened are removed after this long.");
        Cycle("", "Download speed limit", new[] { 0, 500, 1000, 2500, 5000, 10000 },
            () => library.Settings.RateKb, v => library.Settings.RateKb = v,
            v => v == 0 ? "none" : v >= 1000 ? v / 1000.0 + " MB/s" : v + " KB/s",
            "Caps how fast downloads use your connection, so other things stay fast.");
        Cycle("", "Check sources and feeds every", new[] { 15, 30, 60, 120, 240, 720, 1440 },
            () => library.Settings.RefreshMinutes, v => library.Settings.RefreshMinutes = v,
            v => v < 60 ? v + " min" : v / 60 + " h",
            "How often subscriptions and recommendations look for new videos.");
        Toggle("", "Offer to watch online when downloads are busy", () => library.Settings.AskOnlineWhenBusy,
            v => library.Settings.AskOnlineWhenBusy = v,
            "If a video is still waiting to download, ask whether to watch it online right now instead.");
        Toggle("", "Save new videos automatically", () => library.Settings.AutoDownload,
            v => library.Settings.AutoDownload = v,
            "New videos from your sources are downloaded without asking.");

        list.AddRow("", () => "Download folder: " + ShortPath(library.VideoFolder),
            library.VideoFolder + "\nClick to choose another folder. Videos already saved stay where they are.",
            _ => BrowseFolder());
        list.AddRow("", () => "Open the download folder", "Opens it in Explorer.", _ =>
        {
            Directory.CreateDirectory(library.VideoFolder);
            Process.Start("explorer.exe", library.VideoFolder);
        });

        // ---- tools ----
        list.AddHeader("Tools");

        list.AddRow("", () => "Update yt-dlp",
            "yt-dlp: " + (YtDlp.Find() ?? "not installed") + "\nffmpeg: " + (FfmpegRecorder.FindFfmpeg() ?? "not found") +
            "\nClick to update yt-dlp (it looks videos up).",
            reverse => { _ = UpdateTool(new Button()); });

        _ = s;
    }

    private void LoadSettingsControls()
    {
        folderBox.Text = library.VideoFolder;
        settingsList?.RefreshTexts();
    }

    private void ImportCookies()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Choose a cookies.txt exported from your browser",
            Filter = "Cookie files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            YtAccount.ImportFile(dialog.FileName);

            library.NoteSignedIn();
            UpdateAccountButton();
            LoadSettingsControls();

            SetMessage("Cookies imported. Loading your recommendations...", 15);

            _ = library.RefreshFeedAsync(false);
            _ = library.RefreshFeedAsync(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not use that file:\n\n" + ex.Message,
                "UtilTube", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BrowseFolder()
    {
        using FolderBrowserDialog dialog = new()
        {
            Description = "Where should saved videos go?",
            SelectedPath = library.VideoFolder
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        library.Settings.Folder = dialog.SelectedPath;
        library.SettingsChanged();
        folderBox.Text = library.VideoFolder;

        SetMessage("New downloads go to the new folder. Existing videos stay where they are.", 10);
    }

    private async Task UpdateTool(Button button)
    {
        button.Enabled = false;

        try
        {
            SetMessage("Updating yt-dlp...", 120);

            if (YtDlp.Find() == YtDlp.OwnCopyPath || !YtDlp.IsInstalled)
                await YtDlp.InstallAsync(CancellationToken.None);
            else
                await YtDlp.RunAsync(new[] { "-U" }, CancellationToken.None);

            SetMessage("yt-dlp is up to date: " +
                await YtDlp.VersionAsync(CancellationToken.None), 8);

            LoadSettingsControls();
        }
        catch (Exception ex)
        {
            SetMessage("Update failed: " + ex.Message, 12);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    // --------------------------------------------------
    // KEYS / CLOSING
    // --------------------------------------------------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F) || keyData == Keys.F3 ||
            keyData == (Keys.Control | Keys.L) || keyData == (Keys.Control | Keys.K))
        {
            searchBox.Focus();
            searchBox.SelectAll();
            return true;
        }

        if (keyData == (Keys.Alt | Keys.Left))
        {
            GoBack();
            return true;
        }

        if (keyData == (Keys.Alt | Keys.Right))
        {
            GoForward();
            return true;
        }

        if (keyData == Keys.Escape && ActiveControl == searchBox && searchBox.Text.Length > 0)
        {
            searchBox.Clear();
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
        timer.Stop();
        timer.Dispose();

        searchCts?.Cancel();
        grid.SetItems(new List<TubeVideo>());
        temp.Clear();
        searchCache.Clear();

        // Downloads continue in the background; only the window's memory goes.
        ThumbCache.TrimMemory();

        base.OnFormClosed(e);

        MemoryTrimmer.TrimSoon();
    }
}

// ======================================================
// SIDEBAR  -  icon + label navigation with counters
// ======================================================

internal sealed class SideBar : Control
{
    public sealed record Entry(string Key, string Glyph, string Label, bool IsDivider = false)
    {
        public static Entry Divider() => new("", "", "", true);
    }

    private List<Entry> entries = new();
    private readonly Dictionary<string, int> badges = new();
    private int hover = -1;
    private string active = "";

    private const int ItemHeight = 42;
    private const int TopPad = 10;

    private readonly Font glyphFont = new("Segoe MDL2 Assets", 14f);
    private readonly Font labelFont = new("Segoe UI", 10.5f);
    private readonly Font badgeFont = new("Segoe UI", 8.5f);

    public bool Collapsed { get; set; }

    public string Active
    {
        get => active;
        set
        {
            if (active == value)
                return;

            active = value;
            Invalidate();
        }
    }

    public event Action<string>? EntryClicked;

    public SideBar()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        TabStop = false;
        BackColor = Theme.Background;
    }

    public void SetEntries(IEnumerable<Entry> items)
    {
        entries = items.ToList();
        Invalidate();
    }

    public void SetBadge(string key, int count)
    {
        if (badges.TryGetValue(key, out int old) && old == count)
            return;

        badges[key] = count;
        Invalidate();
    }

    private Rectangle RectOf(int index)
    {
        int y = TopPad;

        for (int i = 0; i < index; i++)
            y += entries[i].IsDivider ? 16 : ItemHeight;

        return new Rectangle(8, y, Width - 16, entries[index].IsDivider ? 16 : ItemHeight - 4);
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].IsDivider && RectOf(i).Contains(p))
                return i;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = HitTest(e.Location);

        if (index != hover)
        {
            hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        int index = HitTest(e.Location);

        if (index >= 0 && e.Button == MouseButtons.Left)
            EntryClicked?.Invoke(entries[index].Key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            Rectangle r = RectOf(i);

            if (entry.IsDivider)
            {
                using Pen line = new(Color.FromArgb(50, Theme.Text));
                g.DrawLine(line, r.X + 8, r.Y + 8, r.Right - 8, r.Y + 8);
                continue;
            }

            bool isActive = entry.Key == active;

            if (isActive || i == hover)
            {
                using GraphicsPath path = Rounded(r, 8);
                using SolidBrush back = new(isActive
                    ? Theme.ActiveRow
                    : Theme.Mix(Theme.Background, Theme.Accent, 0.07f));

                g.FillPath(back, path);
            }

            if (isActive)
            {
                using SolidBrush accent = new(Theme.Accent);
                g.FillRectangle(accent, r.X, r.Y + 8, 3, r.Height - 16);
            }

            Color color = isActive ? Theme.Strong : Theme.Text;

            TextRenderer.DrawText(g, entry.Glyph, glyphFont,
                new Rectangle(r.X + 6, r.Y, 40, r.Height), isActive ? Theme.Accent : color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);

            if (Collapsed)
                continue;

            TextRenderer.DrawText(g, entry.Label, labelFont,
                new Rectangle(r.X + 50, r.Y, r.Width - 90, r.Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (badges.TryGetValue(entry.Key, out int count) && count > 0)
            {
                string text = count > 99 ? "99+" : count.ToString();
                Size size = TextRenderer.MeasureText(text, badgeFont);

                Rectangle pill = new(r.Right - size.Width - 18, r.Y + (r.Height - 18) / 2, size.Width + 10, 18);

                using GraphicsPath path = Rounded(pill, 9);
                using SolidBrush back = new(Theme.Search);

                g.FillPath(back, path);

                TextRenderer.DrawText(g, text, badgeFont, pill, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        GraphicsPath path = new();
        int d = radius * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            glyphFont.Dispose();
            labelFont.Dispose();
            badgeFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

// ======================================================
// VIDEO GRID  -  YouTube-style cards
// Only the cards on screen are ever painted, and only a handful of small
// thumbnails are kept in memory, however long the list is.
// ======================================================

internal sealed class VideoGrid : Control
{
    private const int CardW = 262;
    private const int CardH = 244;
    private const int Gap = 10;
    private const int Pad = 16;
    private const int ThumbW = 240;
    private const int ThumbH = 135;

    private List<TubeVideo> items = new();
    private readonly HashSet<string> selected = new();
    private readonly VScrollBar bar = new() { Dock = DockStyle.Right };

    private readonly Font titleFont = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font smallFont = new("Segoe UI", 9f);
    private readonly Font glyphFont = new("Segoe MDL2 Assets", 11f);

    private int focusIndex = -1;
    private int anchorIndex = -1;
    private int hoverIndex = -1;
    private string hoverQuick = "";

    /// <summary>Double click or Enter on a card.</summary>
    public event EventHandler? ItemActivated;

    /// <summary>A button on a card was clicked ("later" or "save").</summary>
    public event Action<TubeVideo, string>? QuickAction;

    /// <summary>Show each video's note (e.g. "Watched 2 h ago") instead of the hint.</summary>
    public bool ShowNotes { get; set; }

    /// <summary>Lets the grid mark videos that are on the Watch later list.</summary>
    public Func<string, bool> IsWatchLater { get; set; } = _ => false;

    public IReadOnlyList<TubeVideo> Items => items;

    public VideoGrid()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);

        TabStop = true;
        BackColor = Theme.Background;

        bar.ValueChanged += (_, _) => Invalidate();
        Controls.Add(bar);
    }

    // ---- items / selection ----

    public void SetItems(List<TubeVideo> list)
    {
        bool same = list.Count == items.Count;

        for (int i = 0; same && i < list.Count; i++)
            same = ReferenceEquals(list[i], items[i]);

        if (same)
        {
            // Only the live state (progress ...) changed.
            Invalidate();
            return;
        }

        items = list;

        // The first few videos on screen are prepared in the background,
        // so clicking one starts playing almost at once.
        int quality = YtDlp.StartHeight;

        foreach (TubeVideo v in list.Take(10))
        {
            if (v.File == null)
                YtDlp.Prefetch(v.Id, quality);
        }

        HashSet<string> present = list.Select(v => v.Id).ToHashSet();
        selected.RemoveWhere(id => !present.Contains(id));

        if (focusIndex >= items.Count)
            focusIndex = items.Count - 1;

        hoverIndex = -1;
        UpdateScroll();
        Invalidate();
    }

    public List<TubeVideo> SelectedList() =>
        items.Where(v => selected.Contains(v.Id)).ToList();

    public void SelectFirst()
    {
        if (items.Count == 0)
            return;

        SelectOnly(0);
        EnsureVisible(0);
    }

    public void SelectOnly(TubeVideo video)
    {
        int index = items.IndexOf(video);

        if (index >= 0)
            SelectOnly(index);
    }

    public void ResetScroll()
    {
        bar.Value = 0;
        selected.Clear();
        focusIndex = -1;
        anchorIndex = -1;
        Invalidate();
    }

    private void SelectOnly(int index)
    {
        selected.Clear();

        if (index >= 0 && index < items.Count)
            selected.Add(items[index].Id);

        focusIndex = index;
        anchorIndex = index;
        Invalidate();
    }

    // ---- geometry ----

    private int Columns =>
        Math.Max(1, (ClientSize.Width - bar.Width - Pad * 2 + Gap) / (CardW + Gap));

    private int RowCount => (items.Count + Columns - 1) / Columns;

    private int ContentHeight => Pad * 2 + RowCount * (CardH + Gap) - Gap;

    private int LeftMargin =>
        (ClientSize.Width - bar.Width - (Columns * (CardW + Gap) - Gap)) / 2;

    private Rectangle CardRect(int index)
    {
        int col = index % Columns;
        int row = index / Columns;

        return new Rectangle(
            LeftMargin + col * (CardW + Gap),
            Pad + row * (CardH + Gap) - bar.Value,
            CardW,
            CardH);
    }

    // The two round buttons on a card's thumbnail (top right).
    private static Rectangle QuickRect(Rectangle card, string kind)
    {
        Rectangle thumb = new(card.X + 10, card.Y + 10, ThumbW, ThumbH);
        int x = thumb.Right - 34;

        return kind == "later"
            ? new Rectangle(x, thumb.Y + 6, 28, 28)
            : new Rectangle(x, thumb.Y + 38, 28, 28);
    }

    private void UpdateScroll()
    {
        int view = Math.Max(1, ClientSize.Height);
        int content = Math.Max(view, ContentHeight);

        bar.SmallChange = 60;
        bar.LargeChange = view;
        bar.Maximum = content;

        int maxValue = Math.Max(0, content - view);

        if (bar.Value > maxValue)
            bar.Value = maxValue;

        bar.Enabled = content > view;
        bar.Visible = content > view;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScroll();
    }

    private int HitTest(Point p)
    {
        int cols = Columns;
        int x = p.X - LeftMargin;

        if (x < 0)
            return -1;

        int col = x / (CardW + Gap);

        if (col >= cols || x % (CardW + Gap) >= CardW)
            return -1;

        int y = p.Y + bar.Value - Pad;

        if (y < 0)
            return -1;

        int row = y / (CardH + Gap);

        if (y % (CardH + Gap) >= CardH)
            return -1;

        int index = row * cols + col;

        return index < items.Count ? index : -1;
    }

    private string QuickHit(int index, Point p)
    {
        if (index < 0)
            return "";

        Rectangle card = CardRect(index);

        if (QuickRect(card, "later").Contains(p))
            return "later";

        if (items[index].State != VideoState.Done && QuickRect(card, "save").Contains(p))
            return "save";

        return "";
    }

    private void EnsureVisible(int index)
    {
        if (index < 0 || index >= items.Count)
            return;

        Rectangle r = CardRect(index);
        int view = ClientSize.Height;

        if (r.Top < 0)
            bar.Value = Math.Max(0, bar.Value + r.Top - Pad);
        else if (r.Bottom > view)
            bar.Value = Math.Min(Math.Max(0, bar.Maximum - bar.LargeChange + 1),
                bar.Value + (r.Bottom - view) + Pad);
    }

    // ---- input ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        int index = HitTest(e.Location);

        if (index < 0)
        {
            if (e.Button == MouseButtons.Left)
            {
                selected.Clear();
                Invalidate();
            }

            return;
        }

        string id = items[index].Id;

        if (e.Button == MouseButtons.Right)
        {
            if (!selected.Contains(id))
                SelectOnly(index);

            return;
        }

        // A button on the thumbnail?
        string quick = QuickHit(index, e.Location);

        if (quick.Length > 0 && e.Button == MouseButtons.Left)
        {
            QuickAction?.Invoke(items[index], quick);
            Invalidate();
            return;
        }

        if (ModifierKeys.HasFlag(Keys.Control))
        {
            if (!selected.Remove(id))
                selected.Add(id);

            focusIndex = anchorIndex = index;
        }
        else if (ModifierKeys.HasFlag(Keys.Shift) && anchorIndex >= 0)
        {
            SelectRange(anchorIndex, index);
            focusIndex = index;
        }
        else
        {
            SelectOnly(index);

            // A plain click starts playing (Ctrl/Shift-click only select).
            Invalidate();
            ItemActivated?.Invoke(this, EventArgs.Empty);
            return;
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int index = HitTest(e.Location);
        string quick = QuickHit(index, e.Location);

        if (index != hoverIndex || quick != hoverQuick)
        {
            if (index != hoverIndex)
                PrefetchSoon(index);

            hoverIndex = index;
            hoverQuick = quick;
            Invalidate();
        }

        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
    }

    // Resting the mouse on a video resolves its stream in the background,
    // so clicking it starts playing at once.
    private readonly System.Windows.Forms.Timer prefetchTimer = new() { Interval = 350 };
    private int prefetchIndex = -1;
    private bool prefetchWired;

    private void PrefetchSoon(int index)
    {
        prefetchIndex = index;

        if (!prefetchWired)
        {
            prefetchWired = true;

            prefetchTimer.Tick += (_, _) =>
            {
                prefetchTimer.Stop();

                if (prefetchIndex >= 0 && prefetchIndex < items.Count &&
                    prefetchIndex == hoverIndex &&
                    items[prefetchIndex].File == null)
                {
                    YtDlp.Prefetch(items[prefetchIndex].Id, YtDlp.StartHeight);
                }
            };
        }

        prefetchTimer.Stop();

        if (index >= 0)
            prefetchTimer.Start();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        prefetchTimer.Stop();

        if (hoverIndex != -1)
        {
            hoverIndex = -1;
            hoverQuick = "";
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        int max = Math.Max(0, bar.Maximum - bar.LargeChange + 1);
        bar.Value = Math.Clamp(bar.Value - e.Delta / 120 * 90, 0, max);
    }

    private void SelectRange(int from, int to)
    {
        selected.Clear();

        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            selected.Add(items[i].Id);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
                return true;
        }

        return base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (items.Count > 0)
        {
            int cols = Columns;
            int target = focusIndex < 0 ? 0 : focusIndex;
            bool moved = true;

            switch (e.KeyCode)
            {
                case Keys.Left: target--; break;
                case Keys.Right: target++; break;
                case Keys.Up: target -= cols; break;
                case Keys.Down: target += cols; break;
                case Keys.PageUp: target -= cols * 2; break;
                case Keys.PageDown: target += cols * 2; break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = items.Count - 1; break;
                default: moved = false; break;
            }

            if (moved)
            {
                target = Math.Clamp(target, 0, items.Count - 1);

                if (e.Shift && anchorIndex >= 0)
                {
                    SelectRange(anchorIndex, target);
                    focusIndex = target;
                }
                else
                {
                    SelectOnly(target);
                }

                EnsureVisible(target);
                Invalidate();
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (items.Count == 0)
            return;

        int cols = Columns;
        int rowHeight = CardH + Gap;

        int firstRow = Math.Max(0, (bar.Value - Pad) / rowHeight);
        int lastRow = Math.Min(RowCount - 1, (bar.Value + ClientSize.Height - Pad) / rowHeight);

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int index = row * cols + col;

                if (index >= items.Count)
                    break;

                DrawCard(g, items[index], CardRect(index), index);
            }
        }
    }

    private void DrawCard(Graphics g, TubeVideo v, Rectangle r, int index)
    {
        bool isSelected = selected.Contains(v.Id);
        bool isHover = index == hoverIndex;

        if (isSelected || isHover)
        {
            using GraphicsPath cardPath = SideBar.Rounded(r, 10);
            using SolidBrush back = new(isSelected
                ? Theme.ActiveRow
                : Theme.Mix(Theme.Background, Theme.Accent, 0.06f));

            g.FillPath(back, cardPath);
        }

        if (index == focusIndex && Focused)
        {
            using GraphicsPath cardPath = SideBar.Rounded(r, 10);
            using Pen focus = new(Theme.Accent, 1.5f);
            g.DrawPath(focus, cardPath);
        }

        // ---- thumbnail (rounded) ----
        Rectangle thumb = new(r.X + 10, r.Y + 10, ThumbW, ThumbH);
        Bitmap? image = ThumbCache.TryGet(v.Id);

        using (GraphicsPath thumbPath = SideBar.Rounded(thumb, 8))
        {
            GraphicsState state = g.Save();
            g.SetClip(thumbPath, CombineMode.Intersect);

            if (image != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(image, thumb);
            }
            else
            {
                using SolidBrush placeholder = new(Theme.Search);
                g.FillRectangle(placeholder, thumb);
            }

            // progress along the bottom edge (clipped to the rounded corners)
            if (v.State == VideoState.Downloading)
            {
                using SolidBrush track = new(Color.FromArgb(160, 0, 0, 0));
                using SolidBrush fill = new(Theme.Accent);

                g.FillRectangle(track, thumb.X, thumb.Bottom - 5, thumb.Width, 5);
                g.FillRectangle(fill, thumb.X, thumb.Bottom - 5,
                    (int)(thumb.Width * Math.Clamp(v.Progress, 0, 100) / 100.0), 5);
            }
            else if (v.State == VideoState.Done && v.Position > 5 && v.Duration > 0 && !v.Watched)
            {
                using SolidBrush resume = new(Theme.Off);

                g.FillRectangle(resume, thumb.X, thumb.Bottom - 4,
                    (int)(thumb.Width * Math.Clamp(v.Position / v.Duration, 0, 1)), 4);
            }

            g.Restore(state);
        }

        if (v.Duration > 0)
        {
            string length = Duration(v.Duration);
            Size size = TextRenderer.MeasureText(length, smallFont);

            Rectangle badge = new(thumb.Right - size.Width - 12, thumb.Bottom - 26, size.Width + 8, 19);

            using GraphicsPath badgePath = SideBar.Rounded(badge, 4);
            using SolidBrush dark = new(Color.FromArgb(205, 0, 0, 0));
            g.FillPath(dark, badgePath);

            TextRenderer.DrawText(g, length, smallFont, badge, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // "saved" marker
        if (v.State == VideoState.Done)
        {
            Rectangle tag = new(thumb.X + 8, thumb.Y + 8, 62, 19);

            using GraphicsPath tagPath = SideBar.Rounded(tag, 4);
            using SolidBrush back = new(Color.FromArgb(205, 0, 0, 0));
            g.FillPath(back, tagPath);

            TextRenderer.DrawText(g, "SAVED", smallFont, tag, Theme.On,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // quick buttons appear when the pointer is over the card
        if (isHover)
        {
            DrawQuick(g, QuickRect(r, "later"), "", IsWatchLater(v.Id), hoverQuick == "later");

            if (v.State != VideoState.Done)
                DrawQuick(g, QuickRect(r, "save"), "", false, hoverQuick == "save");
        }

        // ---- text ----
        int x = r.X + 12;
        int width = r.Width - 24;

        TextRenderer.DrawText(g, v.Title, titleFont,
            new Rectangle(x, thumb.Bottom + 8, width, 36),
            v.Watched ? Theme.Text : Theme.Strong,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, v.Channel, smallFont,
            new Rectangle(x, thumb.Bottom + 46, width, 18), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        (string status, Color color) = Status(v);

        TextRenderer.DrawText(g, status, smallFont,
            new Rectangle(x, thumb.Bottom + 64, width, 18), color,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawQuick(Graphics g, Rectangle r, string glyph, bool on, bool hot)
    {
        using SolidBrush back = new(Color.FromArgb(hot ? 240 : 200, 0, 0, 0));
        g.FillEllipse(back, r);

        TextRenderer.DrawText(g, glyph, glyphFont, r, on ? Theme.Accent : Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private (string, Color) Status(TubeVideo v)
    {
        // History: when you watched it.
        if (ShowNotes && v.Note != null && v.State is VideoState.None or VideoState.Done)
        {
            string text = v.Note;

            if (v.State == VideoState.Done)
                text += "  -  saved";

            return (text, Theme.Text);
        }

        switch (v.State)
        {
            case VideoState.Done:
            {
                string text = FormatBytes(v.Size);

                if (v.Keep)
                    text += "  -  kept";

                if (v.Watched)
                    text += "  -  watched";
                else if (v.Position > 5)
                    text += $"  -  resume {Duration((int)v.Position)}";

                TimeSpan? left = TubeLibrary.Instance.DeletesIn(v);

                if (left != null)
                {
                    text += left.Value.TotalMinutes < 1
                        ? "  -  deleting now"
                        : left.Value.TotalHours >= 1
                            ? $"  -  deletes in {(int)left.Value.TotalHours} h {left.Value.Minutes} min"
                            : $"  -  deletes in {left.Value.Minutes} min";
                }

                return (text, Theme.On);
            }

            case VideoState.Downloading:
                return ($"Saving  {v.Progress:0}%   {v.Speed}", Theme.Accent);

            case VideoState.Queued:
                return (v.Attempts > 0
                    ? $"Retry {v.Attempts}/3: {v.Error}"
                    : "Waiting to save offline", Theme.Text);

            case VideoState.Failed:
                return ("Failed: " + v.Error, Theme.Off);

            default:
                return (IsWatchLater(v.Id) ? "In Watch later" : "Click to watch", Theme.Number);
        }
    }

    private static string Duration(int seconds)
    {
        TimeSpan t = TimeSpan.FromSeconds(seconds);

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0} MB";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            smallFont.Dispose();
            glyphFont.Dispose();
        }

        base.Dispose(disposing);
    }
}

// ======================================================
// SMALL TEXT PROMPT
// ======================================================

internal sealed class TextPrompt : DarkDialog
{
    private readonly TextBox box;

    public string? Result { get; private set; }

    private TextPrompt(string title, string label, string initial)
        : base(title)
    {
        ClientSize = new Size(440, 130);

        Controls.Add(MakeLabel(label, 20, 16, 400));

        box = MakeBox(20, 44, 400);
        box.Text = initial;
        Controls.Add(box);

        Button ok = MakeButton("OK", 235, 86);
        Button cancel = MakeButton("Cancel", 330, 86);

        ok.Click += (_, _) =>
        {
            Result = box.Text.Trim();
            DialogResult = DialogResult.OK;
        };

        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        box.Focus();
    }

    public static string? Ask(IWin32Window owner, string title, string label, string initial = "")
    {
        using TextPrompt dialog = new(title, label, initial);

        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : null;
    }
}


// ======================================================
// "DOWNLOADS ARE BUSY: WATCH ONLINE?"
// ======================================================

internal enum OnlineChoice
{
    Online,
    SaveOnly,
    Cancel
}

internal sealed class OnlinePrompt : DarkDialog
{
    private readonly CheckBox dontAsk;

    public OnlineChoice Choice { get; private set; } = OnlineChoice.Cancel;
    public bool DontAskAgain => dontAsk.Checked;

    private OnlinePrompt(string title, string reason, int waiting)
        : base("Downloads are busy")
    {
        ClientSize = new Size(520, 196);

        Controls.Add(new Label
        {
            Text = reason + (waiting > 0 ? "" : ".") +
                   "\n\nWatch \"" + Trunc(title, 48) + "\" online right now?\n" +
                   "It will still be saved for offline viewing when its turn comes.",
            Location = new Point(20, 16),
            Size = new Size(480, 90),
            ForeColor = Theme.Strong
        });

        dontAsk = new CheckBox
        {
            Text = "Don't ask again (just add it to the queue)",
            Location = new Point(20, 112),
            Size = new Size(420, 24),
            ForeColor = Theme.Text
        };

        Controls.Add(dontAsk);

        Button online = MakeButton("Watch online now", 20, 148, 160);
        Button save = MakeButton("Just save it", 190, 148, 130);
        Button cancel = MakeButton("Cancel", 330, 148, 90);

        online.BackColor = Theme.Accent;
        online.ForeColor = Theme.Background;

        online.Click += (_, _) => { Choice = OnlineChoice.Online; DialogResult = DialogResult.OK; };
        save.Click += (_, _) => { Choice = OnlineChoice.SaveOnly; DialogResult = DialogResult.OK; };
        cancel.Click += (_, _) => { Choice = OnlineChoice.Cancel; DialogResult = DialogResult.Cancel; };

        Controls.Add(online);
        Controls.Add(save);
        Controls.Add(cancel);

        AcceptButton = online;
        CancelButton = cancel;
    }

    private static string Trunc(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    public static OnlineChoice Ask(
        IWin32Window owner, string title, string reason, int waiting, out bool dontAsk)
    {
        using OnlinePrompt dialog = new(title, reason, waiting);

        dialog.ShowDialog(owner);
        dontAsk = dialog.DontAskAgain && dialog.Choice != OnlineChoice.Cancel;

        return dialog.Choice;
    }
}
