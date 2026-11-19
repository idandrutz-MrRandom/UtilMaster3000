using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UtilMaster3000;

// Password manager UI. The crypto lives in Vault.cs.
public sealed partial class MainForm
{
    private static readonly int[] AutoLockChoices = { 1, 5, 15, 60 };

    private Vault? vault;
    private VaultEntry? openEntry;
    private DateTime vaultActivity = DateTime.Now;
    private System.Windows.Forms.Timer? vaultTimer;

    private bool VaultUnlocked => vault?.IsUnlocked == true;

    // --------------------------------------------------
    // LIFETIME / AUTO-LOCK
    // --------------------------------------------------

    private void SetupVault()
    {
        vaultTimer = new System.Windows.Forms.Timer { Interval = 15000 };

        vaultTimer.Tick += (_, _) =>
        {
            if (VaultUnlocked &&
                (DateTime.Now - vaultActivity).TotalMinutes >=
                    AutoLockMinutes())
            {
                LockVault(announce: true);
            }
        };

        vaultTimer.Start();

        // Locking Windows (Win + L) locks the vault too.
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock || IsDisposed)
            return;

        try
        {
            BeginInvoke(new Action(() => LockVault(announce: false)));
        }
        catch (InvalidOperationException)
        {
            // Closing.
        }
    }

    private void DisposeVault()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        vaultTimer?.Stop();
        vaultTimer?.Dispose();

        vault?.Lock();
        vault = null;
    }

    private int AutoLockMinutes() =>
        AutoLockChoices.Contains(settings.VaultAutoLockMinutes)
            ? settings.VaultAutoLockMinutes
            : 5;

    private void TouchVault() => vaultActivity = DateTime.Now;

    private void LockVault(bool announce)
    {
        if (vault == null)
            return;

        vault.Lock();
        vault = null;
        openEntry = null;

        if (announce)
            Toast("Vault locked", OffColor);

        if (currentPage == "Vault" || currentPage == "VaultEntry")
            ShowPage("Vault");
    }

    // Asks for the master password (or creates the vault the first time).
    private bool EnsureUnlocked()
    {
        if (VaultUnlocked)
        {
            TouchVault();
            return true;
        }

        try
        {
            if (!Vault.Exists)
            {
                string? created = PasswordPrompt.Ask(
                    this,
                    "Create vault",
                    "Choose a master password for your new vault.\n" +
                    "It cannot be recovered if you forget it.",
                    confirm: true);

                if (created == null)
                    return false;

                vault = Vault.Create(created);
                TouchVault();

                Toast("Vault created", OnColor);
                return true;
            }

            string? note = null;

            while (true)
            {
                string? typed = PasswordPrompt.Ask(
                    this,
                    "Unlock vault",
                    "Enter your master password.",
                    confirm: false,
                    note);

                if (typed == null)
                    return false;

                Vault? opened = Vault.Open(typed);

                if (opened != null)
                {
                    vault = opened;
                    TouchVault();
                    return true;
                }

                note = "Wrong password.";
            }
        }
        catch (Exception ex)
        {
            Say("Vault", "Could not open the vault:\n\n" + ex.Message);
            return false;
        }
    }

    // --------------------------------------------------
    // PAGES
    // --------------------------------------------------

    private List<MenuEntry> VaultPage(string query)
    {
        List<MenuEntry> list = new();

        if (!VaultUnlocked)
        {
            list.Add(Act("",
                Vault.Exists ? "Unlock vault" : "Create vault", () =>
                {
                    if (EnsureUnlocked())
                        ShowPage("Vault");
                }));

            list.Add(BackEntry());

            return Renumber(list);
        }

        TouchVault();

        if (query.Length == 0)
        {
            list.Add(Act("", "New entry", AddEntry));
            list.Add(Act("", "Lock vault", () => LockVault(announce: true)));
        }

        IEnumerable<VaultEntry> matches = vault!.Entries
            .Where(e => query.Length == 0 ||
                e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Url.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase);

        foreach (VaultEntry entry in matches)
        {
            VaultEntry captured = entry;

            string label = captured.Username.Length > 0
                ? $"{captured.Title}  ·  {captured.Username}"
                : captured.Title;

            list.Add(Act("", label, () =>
            {
                openEntry = captured;
                ShowPage("VaultEntry");
            }));
        }

        if (query.Length == 0)
        {
            list.Add(Act("", "Import from CSV (browser export)", ImportCsv));
            list.Add(Act("", "Change master password", ChangeMasterPassword));
            list.Add(Act("", $"Auto-lock after: {AutoLockMinutes()} min", () =>
            {
                int next = (Array.IndexOf(AutoLockChoices, AutoLockMinutes()) + 1)
                    % AutoLockChoices.Length;

                settings.VaultAutoLockMinutes = AutoLockChoices[next];
                SaveSettings();
                RefreshPage();
            }));

            list.Add(BackEntry());
        }

        return Renumber(list);
    }

    private List<MenuEntry> VaultEntryPage()
    {
        List<MenuEntry> list = new();

        MenuEntry back = new(-1, "", "Back", run: () => ShowPage("Vault"));

        if (!VaultUnlocked || openEntry == null)
        {
            list.Add(Info("Vault is locked"));
            list.Add(back);
            return Renumber(list);
        }

        TouchVault();

        VaultEntry entry = openEntry;

        list.Add(Act("", "Copy password",
            () => CopySecret(entry.Password, "Password copied")));

        if (entry.Username.Length > 0)
        {
            list.Add(Act("", "Copy username", () =>
            {
                if (SetClipboard(entry.Username))
                {
                    Toast("Username copied", OnColor);
                    HideToTray();
                }
            }));

            list.Add(Act("", "Type login  (username, Tab, password)", () =>
                TypeIntoPrevious(
                    entry.Username + "\t" + entry.Password, "Login")));
        }

        list.Add(Act("", "Type password", () =>
            TypeIntoPrevious(entry.Password, "Password")));

        if (entry.Url.Length > 0)
        {
            list.Add(Act("", "Open website", () =>
                OpenTarget(entry.Url.Contains("://")
                    ? entry.Url
                    : "https://" + entry.Url)));
        }

        list.Add(Act("", "Show password", () =>
            Say(entry.Title, entry.Password)));

        if (entry.Notes.Length > 0)
            list.Add(Act("", "Show notes", () => Say(entry.Title, entry.Notes)));

        list.Add(Act("", "Edit", () => EditEntry(entry)));
        list.Add(Act("", "Delete", () => DeleteEntry(entry)));
        list.Add(back);

        return Renumber(list);
    }

    // --------------------------------------------------
    // ACTIONS
    // --------------------------------------------------

    private void SaveVault()
    {
        try
        {
            vault?.Save();
        }
        catch (Exception ex)
        {
            Toast("Could not save the vault: " + Trunc(ex.Message, 50), OffColor);
        }
    }

    private void AddEntry()
    {
        VaultEntry? created = EntryEditorForm.Edit(this, null);

        if (created == null || vault == null)
            return;

        vault.Entries.Add(created);
        SaveVault();

        Toast("Entry saved", OnColor);
        RefreshPage();
    }

    private void EditEntry(VaultEntry entry)
    {
        VaultEntry? edited = EntryEditorForm.Edit(this, entry);

        if (edited == null || vault == null)
            return;

        int index = vault.Entries.FindIndex(e => e.Id == entry.Id);

        if (index < 0)
            return;

        vault.Entries[index] = edited;
        openEntry = edited;
        SaveVault();

        Toast("Entry updated", OnColor);
        RefreshPage();
    }

    private void DeleteEntry(VaultEntry entry)
    {
        DialogResult answer = MessageBox.Show(
            $"Delete \"{entry.Title}\" from the vault?\n\nThis cannot be undone.",
            "Delete entry",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes || vault == null)
            return;

        vault.Entries.RemoveAll(e => e.Id == entry.Id);
        openEntry = null;
        SaveVault();

        Toast("Entry deleted", OffColor);
        ShowPage("Vault");
    }

    private void ChangeMasterPassword()
    {
        if (vault == null)
            return;

        try
        {
            // Re-check the current password before allowing a change.
            string? current = PasswordPrompt.Ask(
                this, "Change master password",
                "Enter your current master password.", confirm: false);

            if (current == null)
                return;

            Vault? check = Vault.Open(current);

            if (check == null)
            {
                Say("Change master password", "That is not the current password.");
                return;
            }

            check.Lock();

            string? fresh = PasswordPrompt.Ask(
                this, "Change master password",
                "Choose a new master password.", confirm: true);

            if (fresh == null)
                return;

            vault.ChangeMasterPassword(fresh);
            Toast("Master password changed", OnColor);
        }
        catch (Exception ex)
        {
            Say("Vault", "Could not change the password:\n\n" + ex.Message);
        }
    }

    private void ImportCsv()
    {
        if (vault == null)
            return;

        using System.Windows.Forms.OpenFileDialog dialog = new()
        {
            Title = "Choose the exported CSV file",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            List<VaultEntry> found = Vault.ParseCsv(File.ReadAllText(dialog.FileName));

            // Skip anything already in the vault.
            List<VaultEntry> fresh = found
                .Where(n => !vault.Entries.Any(e =>
                    e.Title == n.Title &&
                    e.Username == n.Username &&
                    e.Password == n.Password))
                .ToList();

            if (fresh.Count == 0)
            {
                Say("Import", found.Count == 0
                    ? "No passwords were found in that file."
                    : "Everything in that file is already in the vault.");
                return;
            }

            vault.Entries.AddRange(fresh);
            SaveVault();

            Toast($"Imported {fresh.Count} entries", OnColor);

            DialogResult delete = MessageBox.Show(
                "The CSV file stores your passwords as plain text.\n\n" +
                "Permanently delete it now?",
                "Import finished",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (delete == DialogResult.Yes)
                File.Delete(dialog.FileName);

            RefreshPage();
        }
        catch (Exception ex)
        {
            Say("Import", "Could not import that file:\n\n" + ex.Message);
        }
    }

    // Copies a secret so it stays out of clipboard history, and wipes it
    // again after 20 seconds if nothing else has replaced it.
    private void CopySecret(string secret, string message)
    {
        try
        {
            DataObject data = new();

            data.SetText(secret);

            // Standard flags password managers use: keeps it out of Windows
            // clipboard history, cloud sync, and this app's own history.
            data.SetData(
                "ExcludeClipboardContentFromMonitorProcessing",
                new MemoryStream(new byte[] { 1, 0, 0, 0 }));

            data.SetData(
                "CanIncludeInClipboardHistory",
                new MemoryStream(new byte[] { 0, 0, 0, 0 }));

            data.SetData(
                "CanUploadToCloudClipboard",
                new MemoryStream(new byte[] { 0, 0, 0, 0 }));

            Clipboard.SetDataObject(data, true, 5, 100);
        }
        catch (ExternalException)
        {
            Toast("Clipboard is busy, try again", OffColor);
            return;
        }

        Toast(message + "  (clears in 20 s)", OnColor);
        HideToTray();

        After(20000, () =>
        {
            try
            {
                if (Clipboard.ContainsText() && Clipboard.GetText() == secret)
                    Clipboard.Clear();
            }
            catch (ExternalException)
            {
                // Someone else owns the clipboard right now.
            }
        });
    }

    // Types into the window you were in before opening the launcher.
    // Refuses if it cannot bring that window forward, so a password is
    // never typed into the wrong place.
    private void TypeIntoPrevious(string text, string what)
    {
        IntPtr target = lastExternalWindow;

        if (target == IntPtr.Zero || !Native.IsWindow(target))
        {
            Toast("No window to type into", OffColor);
            return;
        }

        HideToTray();

        After(350, () =>
        {
            Native.SetForegroundWindow(target);

            After(250, () =>
            {
                if (Native.GetForegroundWindow() != target)
                {
                    Toast("Could not switch to that window", OffColor);
                    return;
                }

                Safe(() => AutoType.Type(text));
                Toast($"{what} typed", OnColor);
            });
        });
    }
}
