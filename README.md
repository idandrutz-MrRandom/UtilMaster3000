# UtilMaster3000

A lightweight, offline Windows launcher (WinForms + WPF, .NET 8) that lives in the tray. It combines an app/file/window launcher, letter conversion En -> Ru, screenshots and screen recording, a macro recorder, an autoclicker, a password vault, a clipboard history, an offline YouTube client (UtilTube) and a lot of small everyday tools, all behind one customizable menu.

## Run it

```powershell
dotnet build
dotnet run
```

Or open the folder in VS Code and run the **Build UtilMaster3000** task. Only one copy runs at a time: starting it again just shows the running launcher. If something goes wrong, details are saved to `%APPDATA%\UtilMaster3000\crash.log`.

Settings live in `%APPDATA%\UtilMaster3000\settings.json` (Setup → About → *Open settings folder*).

## Important to know

**Requirements.** Windows 10/11 and the .NET 8 runtime (the SDK if you build it yourself). UtilTube sign-in uses the WebView2 runtime, which is included with Windows 11 and up-to-date Windows 10.

**Optional helpers.**
- **ffmpeg** gives MP4 screen recordings of any length and joins video and audio in UtilTube downloads. The app can download it for you (about 200 MB, checked against its published checksum, kept in `%APPDATA%\UtilMaster3000\tools`): use **Capture → Download ffmpeg**, the set-up guide, or UtilTube → Settings. Without it only short GIF recordings (30 s max) are possible.
- **yt-dlp** powers UtilTube. It is found on your `PATH` or downloaded from the project's official GitHub release into `%APPDATA%\UtilMaster3000\tools`.

**Keyboard hooks and antivirus.** The launcher registers global hotkeys and a global keyboard hook (Print Screen keys, macros, and letter conversion). Some antivirus tools flag this kind of program. The hook for letter conversion is removed completely when **Setup → Typing mode** is **Off**.

**Letter conversion En -> Ru.** It rewrites what you type in every application (or restyles it, in Weird text mode). If text suddenly looks wrong, press `Ctrl + Alt + R` or set Typing mode to Off.

**Vault.** The master password is never stored and cannot be recovered. If you forget it, the vault cannot be opened. Keep a backup of `vault.dat` if the passwords matter.

**Your data stays on your PC.** There is no account or sign-up of its own. Network use is what you ask for: web search, UtilTube, link downloads, and fetching yt-dlp. The Google sign-in for UtilTube happens on Google's own page; only session cookies are kept, encrypted for your Windows user, and **Sign out** deletes them.

**Recording and macros.** Recordings and screenshots hide the launcher's own pop-ups, but the menu itself is captured if you open it while recording (see **Menu opens while recording**). Macros and the autoclicker act on the real mouse and keyboard, so know the stop key before you start (autoclicker `F6`, macros `F9` to record and `F10` to play) and avoid replaying macros over windows that hold unsaved work.

**One copy at a time.** Starting a second copy just shows the first. If a build fails because the file is in use, close the running launcher first (**System → Close app**).

**If something breaks.**
- Errors are logged to `%APPDATA%\UtilMaster3000\crash.log`.
- An unreadable `settings.json` is kept as `settings.json.bad` and the app starts with defaults, so nothing is lost.
- **Setup → Reset settings** and **Style → Reset appearance** restore defaults.
- Your look can be saved to a `.umlook.json` file from the Style page before you experiment.

## Opening the menu

| What | Default key |
| --- | --- |
| Open / close the launcher | `Ctrl + Alt + U` |
| Letter conversion En -> Ru on / off | `Ctrl + Alt + R` (hold it, 450 ms by default, to open the menu without toggling; the hold time is in Setup) |
| Tray icon | Double-click it to open the launcher (right-click for its menu) |

All shortcuts can be changed in **Setup → Shortcuts...** (or **Capture → Change shortcuts...**).

## Moving around the menu

| Input | Does |
| --- | --- |
| `Up` / `Down`, `Page Up` / `Page Down` | Move the selection |
| `Enter` or `Right` (with an empty search box) | Open / run the selected row |
| `Left` (with an empty search box) | Go back one page |
| `Esc` | Clear the search box, then go back, then hide |
| Click | Run the row |
| Right-click | Alternative action where a row has one (for example clipboard items: paste vs. open in the player) |
| Just type | Search from the home page; on other pages it filters that page |
| **‹ Back** button (top right of every page but home), the **Back** row, or the mouse's back button | Returns to the previous page |
| Search on the home page, or open **Functions** | Finds any action or setting by name, for example `screenshot`, `dark mode`, `slide`, `ffmpeg`, `hotkey` |
| Hover a row with long text | It unfolds to show the rest (delay is adjustable) |
| Hover a row that opens a menu | A menu slides out beside the launcher (optional, see Setup) |

## Home page

The home page lists the main sections:

**Set-up guide · Functions · Modules · Apps · Find · Capture · Tools · Text · Clipboard · Vault · Windows · Learn · Style · Setup · About · System**

New here? Start with **Set-up guide** (it also opens by itself the first time you run the app). It walks through the shortcuts, letter conversion, the look, slide-out menus, screenshots and recording, downloading ffmpeg, UtilTube, the clipboard, the vault and starting with Windows. Click a step to do it, rest on it to read more.

### Search from the home page

Start typing and you get mixed results:

| Type | You get |
| --- | --- |
| `firefox` | Matching apps (including Store apps) to launch |
| `2+2*3`, `5^2`, `sqrt(2)` | Calculator answer; `Enter` copies it |
| `10 km to mi`, `72 f to c` | Unit and temperature conversion |
| `> ipconfig` | Runs the command in a console |
| `github.com` | Opens the website |
| `youtube cats` | Searches UtilTube; a pasted YouTube link offers to save it for offline viewing |
| A link to a supported site | Offers *Download this link and copy the file* |
| Any word | Matching sections, tools, **functions** (any action or setting, shown as `Page > Name`), open windows, files, and a web search (engine is selectable in Setup) |

## Sections

### Functions
Searches every action and setting in the launcher from one page. Type `dark`, `screenshot`, `slide-out` or `hotkey` and pick the result to do it right there, or click a page result to open it. Names match first; descriptions match after that.

### Apps
Your recently launched apps; type to search every installed app.

### Find
With an empty search it is a **window switcher** for your open windows. Type to search open windows and files/folders.

### Capture
Screenshots and screen recording (default keys in brackets).

- **Screenshot:** choose area (`Print Screen`), full screen (`Ctrl + Print Screen`), active window (`Alt + Print Screen`). Goes to the clipboard, a file, or both.
- **Record:** choose area (`Shift + Print Screen`), full screen (`Ctrl + Shift + Print Screen`). MP4 with ffmpeg, otherwise a short GIF (30 s max). Press the same key to stop.
- **Options:** recording quality, frame rate, record system sound, send finished recordings to the clipboard, **Menu opens while recording** (when off, the launcher key stops the recording; when on, it opens the menu and the recording keeps going).
- Screenshots go to `Pictures\UtilMaster3000`, recordings to `Videos\UtilMaster3000`; both have an *Open folder* row.
- **Quality and output** (Capture → *Quality and output...*):
  - *Screenshots:* format (PNG lossless, JPG, BMP), JPG quality, size (100 / 75 / 50 / 25 %), include the mouse pointer (full screen and window shots), a wait before capturing (3 / 5 / 10 s), and the folder.
  - *Video (MP4):* quality (small file, standard, high, max, near-lossless), frame rate (10 to 60), video size (100 / 75 / 50 / 25 %), **encoder** (CPU, or your NVIDIA / AMD / Intel graphics card: only ones this PC really supports are offered, each is tested first), CPU speed, the pointer, sound quality (96 to 320 kbps), a countdown before recording, and the folder.
  - *GIF:* frame rate, widest picture and longest length.
  - Click a row for the next value, right-click for the previous one. Folder rows: click to choose, right-click for the default.
- Toasts and the recording banner are hidden from the recording itself.

### Tools
- **UtilTube** (see below)
- **Download by link:** downloads from a pasted link and puts the file on the clipboard
- **Calculator:** full expression calculator (powers, roots, trig, percentages, `sum()`, `avg()`, `gcd()`, hex/binary, `ans`, variables)
- **Autoclicker:** click interval, start/stop key (default `F6`); also stops when you move the mouse into the top-left corner
- **Macros:** record and replay mouse and keyboard (record `F9`, play `F10`); edit steps, shift positions, change repeat and speed, remove mouse paths, rename or delete
- **Pin window on top:** pins the last window you used above everything; the **Pinned** row shows which windows are pinned and lets you unpin them
- Keep PC awake · Pick colour from the screen · Timers (5 / 10 / 25 min) · New GUID · Random password · Timestamp · Unix time · Quick note · System info

### Text
Works on the clipboard text or on what you type:
Letter conversion En -> Ru and Ru -> En, UPPERCASE, lowercase, Title Case, plain text, tidy whitespace, remove line breaks, sort lines, remove duplicate lines, reverse, Base64 encode/decode, URL encode/decode, JSON prettify/minify, word and character count. `Enter` copies the result; another row pastes it into the window you came from.

### Clipboard
History of copied text, pictures and files (`Win + V` opens it by default; you can turn that off on this page). Click to paste; right-click opens pictures and videos in the built-in player (can be swapped). You can pause history recording and clear it.

### Vault
An encrypted password vault (AES-256-GCM, PBKDF2 key from your master password, stored in `%APPDATA%\UtilMaster3000\vault.dat`). The master password is never stored and cannot be recovered.

- Add, edit and delete entries; import from a browser CSV export; change the master password; auto-lock timer
- Per entry: copy password or username, **type the login** (username, Tab, password) into the previous window, open the website, show password or notes

### Windows
Shortcuts to Settings, Task Manager, Control Panel, Device Manager, Services, Disk Management, Environment variables, Terminal, Calculator, Notepad, Snipping tool, plus toggles for dark mode, file extensions and hidden files, and *Empty Recycle Bin* (asks first).

### Learn
Short explanations: how it works, examples, using letter conversion En -> Ru, search tips, the alphabet mapping.

### Style
Everything about the look. The fastest way is **Style editor...**, one window with theme tiles, live colour pickers (the menu updates while you drag), sliders for sizes, animation and slide-out delays, and the background picture.

The same options are also available as lists:

- **Theme:** built-in presets (including Rose Pine, One Dark, Midnight, Ocean, Everforest, Sunset, Solarized, Paper) or Custom
- **Colours:** background, accent, text, active row, border, search bar (own background and see-through level)
- **Layout and animation:** width, row height, number of rows, border thickness, corner radius, animation speed, search bar
- **Text and font:** font and size
- **Background image:** picture, GIF or video, fit, alignment and strength
- **Skin images and icons:** replace the built-in assets
- **Notifications:** pop-up and banner type, position, size, how long they stay, see-through level, with a preview
- **Save / Load a look** (`.umlook.json`) and **Reset appearance**

### Setup
- Shortcuts and *hold to open menu*
- **Typing mode:** **Letter conversion En -> Ru** (English letters become Russian as you type), **Weird text** (16 styles: random case, alternating case, bold, italic, script, gothic, double-struck, monospace, circled, wide, small caps, leet, look-alike symbols, chaos...) or **Off** (removes the keyboard hook entirely)
- **Re-order the main menu:** opens a window where you drag the menu's rows up and down; the others slide out of the way and the menu has the new order as soon as you let go. The order is saved. *Back to the default order* undoes it
- Letter conversion on/off
- Hide on focus loss
- **Slide-out menus on hover:** on/off, delay, side (automatic / right / left), position (level with the hovered row / launcher top), and the delay before long text unfolds
- Start with Windows · Web search engine · Reset settings

### Modules
Add your own entries to the launcher without any programming. A **module** is a small `.json` file in `%APPDATA%\UtilMaster3000\modules` (a folder containing `module.json`, or a loose `.json` file). Open **Modules** on the home page. **See module template** opens a fully commented template in Notepad (every field and type explained: copy it into the modules folder, rename it and edit it). **Create an example module** writes a small working one instead. After editing, choose **Reload modules**.

Each entry has a `label` and a `type`:

| Type | Does |
| --- | --- |
| `open` / `url` | Opens a folder, file, website or program (`target`, optional `args`) |
| `command` | Runs a command in a console (`target`; `keep: true` leaves the window open, `hidden: true` hides it) |
| `script` | Runs a PowerShell file that sits next to the module (`target`, optional `args`) |
| `copy` | Puts `text` on the clipboard |
| `paste` | Types `text` into the window you came from |
| `message` | Shows `text` in a box |

Inside `target`, `args` and `text` you can use `{clipboard}`, `{clipboard-url}` (the clipboard, ready for a web address), `{date}`, `{time}`, `{folder}` (the module's folder) and `%ENVIRONMENT%` variables. `info` shows when you rest on a row and `icon` sets its glyph. Your entries also show up in the home search and in **Functions**.

```json
{
  "name": "My tools",
  "items": [
    { "label": "Open my projects", "type": "open", "target": "C:\\Projects" },
    { "label": "Search the clipboard", "type": "url", "target": "https://www.google.com/search?q={clipboard-url}" },
    { "label": "Git status", "type": "command", "target": "git status", "folder": "C:\\Projects", "keep": true }
  ]
}
```

A module that cannot be read still appears, with the reason. Modules run what their author wrote, so only add ones you trust.

### About
Version and build info, *Open settings folder*, and **Updates**.

**Updates** (About → Updates) asks this project's public GitHub releases whether a newer version exists. It shows what's new, and for the one-file `UtilMaster3000.exe` it can download the new file, check it against the SHA-256 checksum GitHub published for it, and restart on the new version (the old file is kept as `%APPDATA%\UtilMaster3000\update\previous.exe`). A build run from the source folder only opens the release page.
- It checks automatically at most once a day, a few seconds after start (turn off with *Check automatically*), or by hand with *Check for updates now*. *Skip version* silences one release.
- Only the public release list is read. No login, no token, nothing about you is sent. Files from anywhere but this project's release downloads, or without a matching checksum, are refused.
- **Update log** (About → Updates → Update log) lists what changed in every version, offline. It comes from `CHANGELOG.md`, which is built into the program: add a `## 1.0.3 - date` section with `- ` lines before each release.

### System
Hide to tray · Lock · Sleep · Sign out · Restart · Shut down (all confirm) · Close app.

## UtilTube

An offline-friendly YouTube client built on yt-dlp. Open it from **Tools → UtilTube**, or type `youtube` in the search.

- **Home:** your recommendations (sign in with Google once)
- **Subscriptions:** newest videos from channels you follow
- **Saved videos:** everything stored on the PC, watchable with no internet (filter by unwatched / in progress / watched, sort by newest, oldest, title, length)
- **History**, **Watch later** (no account needed), **Playlists**, **Downloads**, **Sources** (channels, playlists and topics kept up to date), **Settings**
- **Playlists:** two tabs. **My playlists** are made by you and kept on your PC (no account): click *+ New playlist*, then add videos with the **+** button in the player (or the **Q** key, or right-click a video > *Add to playlist*, which also has *Watch later*). Each playlist is a chip you can click; *Rename* and *Delete* are on the right. **From YouTube** fetches the playlists on your account by itself when you open it and shows them as cards like on YouTube (cover, number of videos, who can see it, when it was updated; sign in with Google once). Click a card to open it, and *All my playlists* to come back; you can also paste a link or reopen ones you loaded before. For either tab, *Play*, *Shuffle*, *Loop* and *Save all* work on the list on screen.
- **In the player:** the **More** (...) menu is grouped into *Playback*, *Picture* and *This video*. **Subtitles** (also the **C** key) opens a picker that finds the tracks by itself, groups them (written by the uploader / automatic), has a search box when there are many, and a *Text size* control that is remembered.
- **Its look:** by default UtilTube matches the launcher: the same theme colours, border frame, font and background picture (shown behind the panels), with its own themed title bar and scroll bars. In **Settings → Look** you can give it colours of its own instead: pick a look (*Same as the launcher*, Dark, Midnight, Deep purple, Forest, Warm, Black, Light), pick the accent, background, panel and text colours yourself, and choose the video size (small / normal / large). It updates while you change it, and the video player follows it too.
- Click a video to watch immediately; the newest recommended and subscribed videos are saved in the background
- When UtilTube needs yt-dlp or ffmpeg it asks first, then shows a **download bar** with the percent, the speed and how long is left

Video files you open with "Open with UtilMaster3000" start only the built-in video player, with no launcher.

## Handy tips

- Slide-out menus are on by default; turn them off in **Setup** if you prefer clicking.
- **Hide on focus loss** (Setup) is off by default; turn it on to make the launcher disappear when you click elsewhere.
- Share a look with someone by saving it from **Style → Save this look to a file...**.
