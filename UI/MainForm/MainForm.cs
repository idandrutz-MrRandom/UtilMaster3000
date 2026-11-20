using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

public sealed partial class MainForm : Form
{
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private const int MenuWidth = 380;
    private const int WindowWidth = MenuWidth + 40;
    private const int RowsWidth = MenuWidth - 28;
    private const int RowHeight = 32;
    private const int MaxRows = 13;

    // Pixels of this colour are see-through (TransparencyKey).
    private static readonly Color KeyColor = Color.FromArgb(1, 1, 1);

    private static Color BackgroundColor => Theme.Background;

    private static Color SearchColor => Theme.Search;

    private static Color TextColor => Theme.Text;

    private static Color OnColor => Theme.On;

    private static Color OffColor => Theme.Off;

    private static Color AccentColor => Theme.Accent;

    private static readonly string MonoFont = GetAvailableFont(
        "JetBrains Mono",
        "Consolas"
    );

    private sealed class HotkeySlot
    {
        public int Id;
        public string Name = "";
        public uint Mods;
        public Keys Key;
        public bool Registered;
    }

    private readonly HotkeySlot toggleKey = new()
    {
        Id = 1001,
        Name = "Translator shortcut"
    };

    private readonly HotkeySlot launcherKey = new()
    {
        Id = 1002,
        Name = "Launcher shortcut"
    };

    private bool hotkeyWarned;

    private HotkeySlot? bindingSlot;
    private bool waitingForHotkey => bindingSlot != null;

    private readonly GlobalTypingTranslator translator;
    private readonly ModeNotification notification;

    // System tray
    private readonly NotifyIcon trayIcon = new();
    private readonly ContextMenuStrip trayMenu = new();

    private readonly bool startHidden;
    private bool exitRequested;

    // UI tweening state
    private readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 15 };
    private readonly Stopwatch uiClock = new();
    private long lastUiTick;
    private float menuHeightCurrent;
    private int menuHeightTarget;
    private double targetOpacity = 1.0;
    private double restingOpacity = 1.0;
    private bool hiding;

    // Tap = toggle translator, hold = open the menu.
    private readonly System.Windows.Forms.Timer holdTimer = new() { Interval = 20 };
    private long holdStart;
    private bool pressActive;
    private bool holdFired;

    // Last window the user was in before the launcher (for pin / paste).
    private readonly System.Windows.Forms.Timer trackTimer = new() { Interval = 250 };
    private IntPtr lastExternalWindow;

    private readonly OutlinedLabel breadcrumb = new();
    private readonly Label statusLabel = new();

    private AppSettings settings = new();

    private readonly DoublePanel menuBox = new();
    private readonly DoublePanel searchBackground = new();
    private readonly TextBox searchBox = new();
    private readonly DoublePanel rowsPanel = new();

    private readonly List<MenuEntry> mainEntries = new()
    {
        new(0, "", "Apps", targetPage: "Apps"),
        new(1, "", "Find", targetPage: "Find"),
        new(2, "", "Capture", targetPage: "Capture"),
        new(3, "", "Tools", targetPage: "Tools"),
        new(4, "", "Text", targetPage: "Text"),
        new(5, "", "Clipboard", targetPage: "Clipboard"),
        new(6, "", "Vault", targetPage: "Vault"),
        new(7, "", "Windows", targetPage: "Windows"),
        new(8, "", "Learn", targetPage: "Learn"),
        new(9, "", "Style", targetPage: "Style"),
        new(10, "", "Setup", targetPage: "Setup"),
        new(11, "", "About", targetPage: "About"),
        new(12, "", "System", targetPage: "System"),
    };

    private readonly List<LauncherRow> visibleRows = new();

    private Dictionary<string, List<MenuEntry>> pages = new();

    private List<MenuEntry> displayedEntries = new();

    private string currentPage = "home";

    private bool updatingMenu;

    private int selectedIndex;
    private int scrollTop;

    public MainForm()
    {
        Text = "UtilMaster3000";

        startHidden = Environment.GetCommandLineArgs()
            .Any(arg => arg.Equals(
                "--startup",
                StringComparison.OrdinalIgnoreCase
            ));

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;

        BackColor = KeyColor;
        TransparencyKey = KeyColor;
        ForeColor = TextColor;

        ClientSize = new Size(WindowWidth, 438);

        ShowInTaskbar = true;
        KeyPreview = true;

        uiTimer.Tick += UiTick;
        holdTimer.Tick += HoldTick;

        trackTimer.Tick += (_, _) => TrackForeground();
        trackTimer.Start();
        SetupFlyouts();
        SetupOverlay();

        StartupManager.MigrateLegacy();

        settings = AppSettings.Load();
        Calc.Degrees = settings.CalcDegrees;
        Theme.Configure(settings);

        toggleKey.Mods = settings.HotkeyModifiers;
        toggleKey.Key = (Keys)settings.HotkeyKey;
        launcherKey.Mods = settings.LauncherHotkeyModifiers;
        launcherKey.Key = (Keys)settings.LauncherHotkeyKey;

        // Ctrl + Alt + Space was the first default, but other apps
        // (e.g. the Claude desktop app) already own it. Move off it.
        if (launcherKey.Mods == (MOD_CONTROL | MOD_ALT) &&
            launcherKey.Key == Keys.Space)
        {
            launcherKey.Key = Keys.U;
            settings.LauncherHotkeyKey = (int)Keys.U;
            settings.Save();
        }

        translator = new GlobalTypingTranslator();
        notification = new ModeNotification();
        ApplyNotificationSettings();

        translator.EnabledChanged += OnTranslatorChanged;

        AppIndex.BuildAsync();
        FileIndex.BuildAsync();

        SetupVault();
        SetupPrintScreen();
        SetupTube();

        BuildPages();
        BuildInterface();
        InitAppearance();
        SetupTrayIcon();

        ShowPage("home");
        UpdateStatus(translator.Enabled);

        if (!translator.HookInstalled)
        {
            MessageBox.Show(
                "The global keyboard hook could not be installed.",
                "UtilMaster3000",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    // --------------------------------------------------
    // SYSTEM TRAY
    // --------------------------------------------------

    private void SetupTrayIcon()
    {
        trayMenu.Items.Add(
            "Open launcher",
            null,
            (_, _) => OpenLauncher()
        );

        trayMenu.Items.Add(
            "Toggle translator",
            null,
            (_, _) => translator.Toggle()
        );

        trayMenu.Items.Add(
            "UtilTube (YouTube)",
            null,
            (_, _) => TubeClient.Open()
        );

        stopRecordingItem = trayMenu.Items.Add(
            "Stop recording",
            null,
            (_, _) => StopRecording()
        );

        stopRecordingItem.Visible = false;

        trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add(
            "Exit",
            null,
            (_, _) => ExitApplication()
        );

        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "UtilMaster3000";
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;

        trayIcon.DoubleClick += (_, _) => OpenLauncher();
    }

    private void TrackForeground()
    {
        IntPtr window = Native.GetForegroundWindow();

        if (window != IntPtr.Zero &&
            !WindowList.IsOwnWindow(window) &&
            !WindowList.IsShellWindow(window))
        {
            lastExternalWindow = window;
        }
    }

    private void OpenLauncher()
    {
        if (IsDisposed)
            return;

        TrackForeground();

        hiding = false;
        targetOpacity = restingOpacity;

        if (!Visible)
        {
            // Fade in from fully transparent. (ShowInTaskbar is never
            // toggled: that recreates the window handle every time.)
            Opacity = 0;
            Show();
        }

        StartUi();

        WindowState = FormWindowState.Normal;

        Activate();
        BringToFront();

        searchBox.Focus();
    }

    private void ToggleLauncher()
    {
        // While recording, the launcher shortcut stops the recording.
        if (IsRecording)
        {
            StopRecording();
            return;
        }

        if (Visible && !hiding && Native.GetForegroundWindow() == Handle)
        {
            HideToTray();
            return;
        }

        ShowPage("home");
        OpenLauncher();
    }

    private void HideToTray(bool animate = true)
    {
        CloseFlyout();
        CancelShortcutBinding();

        if (!animate || !Visible || !IsHandleCreated)
        {
            hiding = false;
            targetOpacity = restingOpacity;
            Opacity = restingOpacity;

            Hide();
            return;
        }

        if (hiding)
            return;

        hiding = true;
        targetOpacity = 0.0;
        StartUi();
    }

    private void ExitApplication()
    {
        exitRequested = true;

        trayIcon.Visible = false;

        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (startHidden)
            HideToTray(animate: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exitRequested &&
            e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);

        if (!settings.HideOnFocusLoss || waitingForHotkey)
            return;

        // Dialogs we open ourselves also deactivate this form, so only
        // hide once focus has really moved to another application.
        BeginInvoke(new Action(() =>
        {
            if (IsDisposed || !Visible)
                return;

            if (!WindowList.IsOwnWindow(Native.GetForegroundWindow()))
                HideToTray();
        }));
    }

    private void ApplyNotificationSettings()
    {
        notification.OpacitySetting = settings.NotificationOpacity;
        notification.Position = settings.NotificationPosition;
        notification.Muted = !settings.ShowNotification;
        notification.MessagesEnabled = settings.ShowMessages;
        notification.HoldMs = Math.Clamp(settings.NotificationHoldMs, 300, 30000);
        notification.ScalePercent = Math.Clamp(settings.NotificationScale, 50, 300);
    }

    private void SaveSettings()
    {
        settings.HotkeyModifiers = toggleKey.Mods;
        settings.HotkeyKey = (int)toggleKey.Key;
        settings.LauncherHotkeyModifiers = launcherKey.Mods;
        settings.LauncherHotkeyKey = (int)launcherKey.Key;
        settings.NotificationOpacity = notification.OpacitySetting;
        settings.NotificationPosition = notification.Position;
        settings.ShowNotification = !notification.Muted;
        settings.Save();
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Pretty(NotificationPosition position)
    {
        string name = position.ToString();
        System.Text.StringBuilder sb = new();

        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                sb.Append(' ');

            sb.Append(char.ToLowerInvariant(name[i]));
        }

        return sb.ToString();
    }

    // Where Back / Esc / Left go from the current page.
    private string ParentPage() =>
        currentPage == "VaultEntry" ? "Vault" :
        currentPage == "TextTool" ? "Text" :
        currentPage == "Grab" ? "Tools" :
        currentPage == "Notifications" ? "Style" :
        currentPage == "MacroEntry" ? "Macros" :
        currentPage == "MacroStep" ? "MacroEntry" : "home";

    private void RefreshPage()
    {
        BuildPages();
        ShowPage(currentPage, keepSelection: true);
    }

    // --------------------------------------------------
    // FONT
    // --------------------------------------------------

    private static string GetAvailableFont(params string[] fonts)
    {
        foreach (FontFamily family in FontFamily.Families)
        {
            foreach (string requested in fonts)
            {
                if (family.Name.Equals(
                    requested,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return family.Name;
                }
            }
        }

        return "Consolas";
    }

    // --------------------------------------------------
    // INTERFACE
    // --------------------------------------------------

    private void BuildInterface()
    {
        SuspendLayout();

        breadcrumb.Location = new Point(20, 8);
        breadcrumb.Size = new Size(MenuWidth, 25);
        breadcrumb.Font = new Font(MonoFont, 11f, FontStyle.Bold);
        breadcrumb.FillColor = Theme.Strong;
        breadcrumb.OutlineColor = BackgroundColor;
        breadcrumb.KeyColor = KeyColor;
        breadcrumb.Cursor = Cursors.SizeAll;

        // The launcher has no title bar: drag it by its title.
        breadcrumb.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;

            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        };

        Controls.Add(breadcrumb);

        menuBox.Location = new Point(20, 40);
        menuBox.Size = new Size(MenuWidth, 388);
        menuBox.BackColor = BackgroundColor;

        menuBox.Paint += (_, e) =>
        {
            // Background image (if any) goes under the rows and border.
            Theme.PaintBackground(e.Graphics, menuBox.ClientRectangle);

            using Pen border = new(AccentColor, 2f);

            int left = 1;
            int top = 1;
            int right = menuBox.ClientSize.Width - 2;
            int bottom = menuBox.ClientSize.Height - 2;

            e.Graphics.DrawLine(border, left, top, right, top);
            e.Graphics.DrawLine(border, left, top, left, bottom);
            e.Graphics.DrawLine(border, right, top, right, bottom);
            e.Graphics.DrawLine(border, left, bottom, right, bottom);
        };

        Controls.Add(menuBox);

        searchBackground.Location = new Point(12, 12);
        searchBackground.Size = new Size(RowsWidth, 28);
        searchBackground.BackColor = SearchColor;

        menuBox.Controls.Add(searchBackground);

        searchBox.Location = new Point(8, 3);
        searchBox.Size = new Size(RowsWidth - 16, 22);
        searchBox.BorderStyle = BorderStyle.None;
        searchBox.BackColor = SearchColor;
        searchBox.ForeColor = Theme.Strong;
        searchBox.Font = new Font(MonoFont, 9f);
        searchBox.PlaceholderText = "Go...";
        searchBox.TextChanged += SearchBox_TextChanged;

        // Mouse wheel moves the selection (and scrolls long lists),
        // wherever the pointer is over the launcher.
        searchBox.MouseWheel += OnListWheel;
        searchBackground.MouseWheel += OnListWheel;
        rowsPanel.MouseWheel += OnListWheel;
        menuBox.MouseWheel += OnListWheel;
        statusLabel.MouseWheel += OnListWheel;
        MouseWheel += OnListWheel;

        searchBackground.Controls.Add(searchBox);

        rowsPanel.Location = new Point(12, 50);
        rowsPanel.Size = new Size(RowsWidth, 288);
        rowsPanel.BackColor = Color.Transparent;

        // Thin scroll thumb when a list is longer than the window.
        rowsPanel.Paint += (_, e) =>
        {
            int count = displayedEntries.Count;

            // While a menu slides out, the bar is the curtain's edge.
            if (count <= MaxRows || flyoutCurtain)
                return;

            int height = rowsPanel.Height;

            int thumbHeight = Math.Max(
                16, (int)(height * (float)MaxRows / count));

            float progress = scrollTop / (float)(count - MaxRows);
            int thumbTop = (int)((height - thumbHeight) * progress);

            using SolidBrush brush = new(Color.FromArgb(110, AccentColor));

            e.Graphics.FillRectangle(
                brush, RowsWidth - 4, thumbTop, 3, thumbHeight);
        };

        menuBox.Controls.Add(rowsPanel);

        statusLabel.BackColor = Color.Transparent;
        statusLabel.Location = new Point(12, 362);
        statusLabel.Size = new Size(RowsWidth, 18);
        statusLabel.Font = new Font(MonoFont, 8f);
        statusLabel.TextAlign = ContentAlignment.MiddleCenter;

        menuBox.Controls.Add(statusLabel);

        ResumeLayout(false);
    }

    private void ShowPage(string page, bool keepSelection = false)
    {
        CloseFlyout();
        updatingMenu = true;

        currentPage = page;

        breadcrumb.Text = page == "home"
            ? "home"
            : page == "VaultEntry"
                ? $"home / Vault / {Trunc(openEntry?.Title ?? "", 22)}"
                : page == "TextTool"
                    ? $"home / Text / {Trunc(textToolName, 22)}"
                    : $"home / {page}";

        searchBox.PlaceholderText = page switch
        {
            "home" => "Search apps, files, math, web...",
            "Apps" => "Search apps...",
            "Find" => "Search files and open windows...",
            "Clipboard" => "Search clipboard history...",
            "TextTool" => $"{textToolName}: type or paste text...",
            "Grab" => "Paste a link (Instagram, TikTok, X, Reddit, SoundCloud, Spotify...)",
            "Macros" => "Search macros...",
            "MacroEntry" => "Filter steps, or type a new name to rename...",
            "MacroStep" => "Type x,y to set the position, or a time like 250 or 1.5s...",
            "Auto" => "Type a number to set the click speed (250, 0.5s, 2 min)...",
            "Calc" => "Calculate: 5squared, 3root(27), sin(30deg), 20% of 150...",
            _ => "Go..."
        };

        if (!keepSelection)
        {
            searchBox.Clear();
            selectedIndex = 0;
            scrollTop = 0;
        }

        searchBox.Enabled = true;

        updatingMenu = false;

        RefreshRows(animateIn: !keepSelection);
        UpdateStatus(translator.Enabled);

        menuBox.Invalidate();
    }

    private void SearchBox_TextChanged(object? sender, EventArgs e)
    {
        if (updatingMenu)
            return;

        selectedIndex = 0;
        scrollTop = 0;
        RefreshRows();
    }

    // --------------------------------------------------
    // ROWS (viewport of up to MaxRows entries)
    // --------------------------------------------------

    private void RefreshRows(bool animateIn = false)
    {
        string query = searchBox.Text.Trim();

        displayedEntries = GetEntries(query);

        selectedIndex = displayedEntries.Count == 0
            ? -1
            : Math.Clamp(selectedIndex, 0, displayedEntries.Count - 1);

        ClampScroll();
        RebuildRows(animateIn);
    }

    private void ClampScroll()
    {
        int maxTop = Math.Max(0, displayedEntries.Count - MaxRows);

        if (selectedIndex >= 0)
        {
            if (selectedIndex < scrollTop)
                scrollTop = selectedIndex;
            else if (selectedIndex >= scrollTop + MaxRows)
                scrollTop = selectedIndex - MaxRows + 1;
        }

        scrollTop = Math.Clamp(scrollTop, 0, maxTop);
    }

    private void MoveSelection(int newIndex)
    {
        if (displayedEntries.Count == 0)
            return;

        selectedIndex = Math.Clamp(
            newIndex, 0, displayedEntries.Count - 1);

        int oldTop = scrollTop;

        ClampScroll();

        if (oldTop != scrollTop)
            RebuildRows(false);
        else
            UpdateRowStates();
    }

    private void RebuildRows(bool animateIn)
    {
        // Same rows, new content: nothing is destroyed, so nothing flickers.
        bool reuse = !animateIn && visibleRows.Count > 0;

        int visible = Math.Clamp(
            displayedEntries.Count - scrollTop, 0, MaxRows);

        if (reuse)
        {
            while (visibleRows.Count > visible)
            {
                LauncherRow extra = visibleRows[^1];

                visibleRows.RemoveAt(visibleRows.Count - 1);
                rowsPanel.Controls.Remove(extra);
                extra.Dispose();
            }
        }
        else
        {
            foreach (Control control in rowsPanel.Controls)
                control.Dispose();

            rowsPanel.Controls.Clear();
            visibleRows.Clear();
        }

        // Window height follows the number of visible rows.
        int menuHeight = 100 + visible * RowHeight;

        menuHeightTarget = menuHeight;
        rowsPanel.Height = visible * RowHeight;

        if (Visible && IsHandleCreated && !hiding)
            StartUi();
        else
            SetMenuHeight(menuHeight);

        int rowWidth = RowsWidth -
            (displayedEntries.Count > MaxRows ? 8 : 0);

        List<LauncherRow> created = new();

        for (int i = 0; i < visible; i++)
        {
            int absolute = scrollTop + i;
            MenuEntry entry = displayedEntries[absolute];

            LauncherRow row;

            if (i < visibleRows.Count)
            {
                row = visibleRows[i];
                row.Rebind(entry.Index, entry.Icon, entry.Label);
                row.Width = rowWidth;
            }
            else
            {
                row = new LauncherRow(
                    entry.Index,
                    entry.Icon,
                    entry.Label,
                    MonoFont
                );

                row.Location = new Point(0, i * RowHeight);
                row.Size = new Size(rowWidth, RowHeight);

                // Left click runs the entry; right click runs its alternative
                // (e.g. clipboard: open in the player instead of pasting).
                row.MouseUp += (sender, e) =>
                {
                    if (sender is not LauncherRow r || r.Tag is not RowBinding bound)
                        return;

                    if (e.Button == MouseButtons.Left)
                    {
                        selectedIndex = bound.Absolute;
                        UpdateRowStates();
                        ActivateEntry(bound.Entry);
                    }
                    else if (e.Button == MouseButtons.Right && bound.Entry.runAlt != null)
                    {
                        selectedIndex = bound.Absolute;
                        UpdateRowStates();
                        bound.Entry.runAlt();
                    }
                };

                // Hover selects only when the mouse really moves. Updating
                // the rows under a still pointer (scrolling, keyboard) must
                // not re-select the row beneath it.
                row.MouseMove += (sender, _) =>
                {
                    if (sender is not LauncherRow r || r.Tag is not RowBinding bound)
                        return;

                    Point cursor = Cursor.Position;

                    if (cursor == lastHoverPoint)
                        return;

                    lastHoverPoint = cursor;

                    if (selectedIndex != bound.Absolute)
                    {
                        selectedIndex = bound.Absolute;
                        UpdateRowStates();
                    }
                };

                row.MouseWheel += OnListWheel;

                if (animateIn)
                    row.StartAppear(i * 0.03);

                rowsPanel.Controls.Add(row);
                visibleRows.Add(row);
                created.Add(row);
            }

            row.Tag = new RowBinding(absolute, entry);

            if (entry.iconBitmap != null)
                row.IconImage = entry.iconBitmap;
            else
                AttachIcon(row, entry.iconPath);

            // Anything too long for one line unfolds on hover so it can be read.
            row.SetReveal(
                entry.expandText ?? (entry.expandImage == null ? entry.Label : null),
                entry.expandImage);
        }

        UpdateRowStates();

        // New rows start in their final highlight state.
        foreach (LauncherRow row in created)
            row.SnapActive();

        LayoutRows();

        rowsPanel.Invalidate();

        if (animateIn)
            StartUi();
    }

    private bool rowsWereUnfolded;

    // Rows that are unfolding (long text, pictures) push the rows below
    // them down; the window grows with them frame by frame.
    private void LayoutRows()
    {
        int y = 0;
        bool unfolded = false;

        foreach (LauncherRow row in visibleRows)
        {
            int h = row.CurrentHeight;

            if (row.Top != y || row.Height != h)
                row.SetBounds(0, y, row.Width, h);

            if (row.RevealAmount > 0f)
                unfolded = true;

            y += h;
        }

        if (rowsPanel.Height != y)
            rowsPanel.Height = y;

        if (unfolded || rowsWereUnfolded)
        {
            menuHeightTarget = 100 + y;
            SetMenuHeight(menuHeightTarget);
        }

        rowsWereUnfolded = unfolded;
    }

    private Point lastHoverPoint = new(int.MinValue, int.MinValue);

    // The wheel goes to whichever control is under the pointer, so every
    // part of the list forwards it here.
    private void OnListWheel(object? sender, MouseEventArgs e)
    {
        int step = e.Delta > 0 ? -3 : 3;
        MoveSelection(selectedIndex + step);
    }

    // Shows the real icon (app / file type) once it is available.
    private void AttachIcon(LauncherRow row, string? path)
    {
        if (path == null)
            return;

        if (IconCache.TryGet(path, out Bitmap? cached))
        {
            row.IconImage = cached;
            return;
        }

        IconCache.Request(path, bitmap =>
        {
            if (bitmap == null)
                return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    // The row may have been reused for another entry by now.
                    if (!row.IsDisposed &&
                        row.Tag is RowBinding rb && rb.Entry.iconPath == path)
                    {
                        row.IconImage = bitmap;
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Form is closing.
            }
        });
    }

    private void UpdateRowStates()
    {
        for (int i = 0; i < visibleRows.Count; i++)
            visibleRows[i].IsActive = scrollTop + i == selectedIndex;

        StartUi();
    }

    // --------------------------------------------------
    // UI TWEENING
    // --------------------------------------------------

    private void SetMenuHeight(float height)
    {
        menuHeightCurrent = height;

        int h = (int)Math.Round(height);

        menuBox.Height = h;
        ClientSize = new Size(WindowWidth, 40 + h + 10);
        statusLabel.Location = new Point(12, h - 28);

        // Repaint the border so no old edges are left behind.
        menuBox.Invalidate();
    }

    private void StartUi()
    {
        if (uiTimer.Enabled)
            return;

        if (!uiClock.IsRunning)
            uiClock.Start();

        lastUiTick = uiClock.ElapsedMilliseconds;
        uiTimer.Start();
    }

    private void UiTick(object? sender, EventArgs e)
    {
        long now = uiClock.ElapsedMilliseconds;
        double dt = Math.Max(1, now - lastUiTick) / 1000.0;
        lastUiTick = now;

        bool busy = false;

        // Window height: exponential ease-out.
        if (Math.Abs(menuHeightTarget - menuHeightCurrent) > 0.5f)
        {
            double k = 1 - Math.Exp(-dt / 0.055);

            SetMenuHeight(
                menuHeightCurrent +
                (menuHeightTarget - menuHeightCurrent) * (float)k
            );

            busy = true;
        }
        else if (menuHeightCurrent != menuHeightTarget)
        {
            SetMenuHeight(menuHeightTarget);
        }

        // Row highlight + slide-in.
        foreach (LauncherRow row in visibleRows)
        {
            if (row.Step(dt))
                busy = true;
        }

        LayoutRows();

        // Launcher fade in / out.
        if (Math.Abs(Opacity - targetOpacity) > 0.001)
        {
            double step = dt / 0.14;

            Opacity = Opacity < targetOpacity
                ? Math.Min(targetOpacity, Opacity + step)
                : Math.Max(targetOpacity, Opacity - step);

            if (hiding && Opacity <= 0.001)
            {
                hiding = false;
                Hide();

                // Idle in the tray: hand freed memory back to Windows.
                MemoryTrimmer.TrimSoon();
            }
            else
            {
                busy = true;
            }
        }

        if (!busy)
            uiTimer.Stop();
    }

    // --------------------------------------------------
    // GLOBAL HOTKEYS
    // --------------------------------------------------

    private void BeginHotkeyBinding(HotkeySlot slot)
    {
        bindingSlot = slot;

        statusLabel.Text = "PRESS NEW SHORTCUT...";
        statusLabel.ForeColor = AccentColor;

        searchBox.Enabled = false;
        Focus();
    }

    private void CancelHotkeyBinding()
    {
        bindingSlot = null;

        searchBox.Enabled = true;

        UpdateStatus(translator.Enabled);
        searchBox.Focus();
    }

    private string FormatHotkey(HotkeySlot slot) =>
        ShortcutCatalog.Describe(slot.Mods, (int)slot.Key);

    private bool RegisterSlot(HotkeySlot slot)
    {
        slot.Registered = RegisterHotKey(
            Handle,
            slot.Id,
            slot.Mods,
            (int)slot.Key
        );

        return slot.Registered;
    }

    private void UnregisterSlot(HotkeySlot slot)
    {
        if (!slot.Registered)
            return;

        UnregisterHotKey(Handle, slot.Id);
        slot.Registered = false;
    }

    private bool SetHotkey(HotkeySlot slot, uint modifiers, Keys key)
    {
        UnregisterSlot(slot);

        uint oldModifiers = slot.Mods;
        Keys oldKey = slot.Key;

        slot.Mods = modifiers;
        slot.Key = key;

        if (RegisterSlot(slot))
            return true;

        slot.Mods = oldModifiers;
        slot.Key = oldKey;

        RegisterSlot(slot);

        MessageBox.Show(
            "That shortcut is already being used by another application.",
            "Shortcut unavailable",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );

        return false;
    }

    // Tap the translator shortcut = toggle. Hold it = open the menu.
    // RegisterHotKey only reports the key going down, so the toggle waits
    // for the key to come up (or the hold time to pass) before deciding.
    private void OnToggleHotkey()
    {
        if (settings.HoldToOpenMs <= 0)
        {
            translator.Toggle();
            return;
        }

        // Ignore the auto-repeat messages sent while the key is held.
        if (pressActive)
            return;

        pressActive = true;
        holdFired = false;
        holdStart = Environment.TickCount64;

        holdTimer.Start();
    }

    private void HoldTick(object? sender, EventArgs e)
    {
        bool down = (Native.GetAsyncKeyState((int)toggleKey.Key) & 0x8000) != 0;

        if (!down)
        {
            holdTimer.Stop();
            pressActive = false;

            if (!holdFired)
                translator.Toggle();

            return;
        }

        if (!holdFired &&
            Environment.TickCount64 - holdStart >= settings.HoldToOpenMs)
        {
            holdFired = true;

            ShowPage("home");
            OpenLauncher();
        }
    }

    private void OnTranslatorChanged(bool enabled)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() =>
                    OnTranslatorChanged(enabled)
                ));
            }
            catch (InvalidOperationException)
            {
                // Window is closing.
            }

            return;
        }

        UpdateStatus(enabled);
        notification.ShowStatus(enabled);

        if (currentPage == "Setup" || currentPage == "Apps")
            RefreshPage();

        if (currentPage == "home" &&
            searchBox.Text.Length == 0)
        {
            int trigger = mainEntries.FindIndex(e => e.Label == "Setup");

            if (trigger >= 0)
                MoveSelection(trigger);
        }
    }

    private void UpdateStatus(bool enabled)
    {
        if (waitingForHotkey)
            return;

        statusLabel.Text = enabled
            ? "● MODE: ON"
            : "○ MODE: OFF";

        statusLabel.ForeColor = enabled
            ? OnColor
            : OffColor;

        statusLabel.Invalidate();
    }

    // --------------------------------------------------
    // KEYBOARD NAVIGATION
    // --------------------------------------------------

    protected override bool ProcessCmdKey(
        ref Message msg,
        Keys keyData)
    {
        // The shortcut is still held after a long press opened the menu:
        // swallow its auto-repeat so it does not reach the search box.
        if (pressActive)
            return true;

        if (bindingSlot != null)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys modifiers = keyData & Keys.Modifiers;

            if (key == Keys.Escape)
            {
                CancelHotkeyBinding();
                return true;
            }

            if (key == Keys.ControlKey ||
                key == Keys.ShiftKey ||
                key == Keys.Menu)
            {
                uint held = 0;

                if ((modifiers & Keys.Control) != 0) held |= MOD_CONTROL;
                if ((modifiers & Keys.Alt) != 0) held |= MOD_ALT;
                if ((modifiers & Keys.Shift) != 0) held |= MOD_SHIFT;

                statusLabel.Text = held == 0
                    ? "PRESS NEW SHORTCUT..."
                    : "PRESS NEW SHORTCUT:  " + HeldLabel(held) + " + ...";

                return true;
            }

            uint nativeModifiers = 0;

            if ((modifiers & Keys.Control) != 0)
                nativeModifiers |= MOD_CONTROL;

            if ((modifiers & Keys.Alt) != 0)
                nativeModifiers |= MOD_ALT;

            if ((modifiers & Keys.Shift) != 0)
                nativeModifiers |= MOD_SHIFT;

            if (nativeModifiers == 0)
                return true;

            HotkeySlot slot = bindingSlot;

            bool success = SetHotkey(slot, nativeModifiers, key);

            bindingSlot = null;
            searchBox.Enabled = true;

            if (success)
            {
                SaveSettings();
                RefreshPage(); // every label that shows the shortcut updates now

                statusLabel.Text = $"BOUND: {FormatHotkey(slot)}";
                statusLabel.ForeColor = OnColor;
            }
            else
            {
                UpdateStatus(translator.Enabled);
            }

            searchBox.Focus();

            return true;
        }

        switch (keyData)
        {
            case Keys.Escape:
                if (searchBox.Text.Length > 0)
                {
                    searchBox.Clear();
                }
                else if (currentPage != "home")
                {
                    ShowPage(ParentPage());
                }
                else
                {
                    HideToTray();
                }

                return true;

            case Keys.Left:
                if (searchBox.Text.Length == 0)
                {
                    if (currentPage != "home")
                        ShowPage(ParentPage());

                    return true;
                }

                break;

            case Keys.Right:
                if (searchBox.Text.Length == 0)
                {
                    ActivateSelected();
                    return true;
                }

                break;

            case Keys.Enter:
                ActivateSelected();
                return true;

            case Keys.Down:
                MoveSelection(selectedIndex + 1);
                return true;

            case Keys.Up:
                MoveSelection(selectedIndex - 1);
                return true;

            case Keys.PageDown:
                MoveSelection(selectedIndex + MaxRows / 2);
                return true;

            case Keys.PageUp:
                MoveSelection(selectedIndex - MaxRows / 2);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ActivateSelected()
    {
        if (selectedIndex >= 0 &&
            selectedIndex < displayedEntries.Count)
        {
            ActivateEntry(displayedEntries[selectedIndex]);
        }
    }

    // --------------------------------------------------
    // WINDOW EVENTS
    // --------------------------------------------------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        List<string> failed = new();

        if (!RegisterSlot(toggleKey))
            failed.Add($"{toggleKey.Name}: {FormatHotkey(toggleKey)}");

        if (!RegisterSlot(launcherKey))
            failed.Add($"{launcherKey.Name}: {FormatHotkey(launcherKey)}");

        Native.AddClipboardFormatListener(Handle);

        // Tell the user once per run, with a toast instead of a dialog
        // that has to be dismissed every time.
        if (failed.Count > 0 && !hotkeyWarned)
        {
            hotkeyWarned = true;

            notification.ShowMessage(
                "Shortcut in use by another app: " +
                string.Join(", ", failed.Select(f => f.Split(": ")[^1])) +
                " (change in Setup)",
                OffColor
            );
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();

            if (id == toggleKey.Id)
                OnToggleHotkey();
            else if (id == launcherKey.Id)
                ToggleLauncher();
        }
        else if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            BeginInvoke(new Action(RecordClipboard));
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterSlot(toggleKey);
        UnregisterSlot(launcherKey);

        Native.RemoveClipboardFormatListener(Handle);

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        translator.EnabledChanged -= OnTranslatorChanged;

        uiTimer.Stop();
        uiTimer.Dispose();
        holdTimer.Stop();
        holdTimer.Dispose();
        trackTimer.Stop();
        trackTimer.Dispose();
        countdown?.Dispose();

        if (IsRecording)
            StopRecording();

        Theme.FrameChanged -= OnBackgroundFrame;
        Theme.ClearBackground();
        printScreen?.Dispose();
        DisposeVault();
        DisposeTube();

        if (keepAwake)
            SystemTools.SetKeepAwake(false);

        trayIcon.Visible = false;
        trayIcon.Dispose();
        trayMenu.Dispose();

        notification.Dispose();
        translator.Dispose();

        base.OnFormClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr window,
        int id,
        uint modifiers,
        int key
    );

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr window,
        int id
    );

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr window,
        int message,
        int wParam,
        int lParam
    );

    // What a (reused) row currently stands for.
    private sealed record RowBinding(int Absolute, MenuEntry Entry);

    private sealed record MenuEntry(
        int Index,
        string Icon,
        string Label,
        string? targetPage = null,
        string? command = null,
        Action? run = null,
        string? iconPath = null,
        string? expandText = null,
        Image? expandImage = null,
        Image? iconBitmap = null,
        Action? runAlt = null
    );
}
