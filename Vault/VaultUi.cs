using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilMaster3000;

// Shared look for the vault dialogs.
internal abstract class DarkDialog : Form
{
    protected static Color Back => Theme.Background;
    protected static Color Field => Theme.Search;
    protected static Color Soft => Theme.Text;
    protected static Color Accent => Theme.Accent;
    protected static Color Bad => Theme.Off;

    protected readonly Font Mono = new("Consolas", 9.5f);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    protected DarkDialog(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Back;
        ForeColor = Theme.Strong;
        Font = Mono;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Passwords never show up in screenshots or recordings.
        SetWindowDisplayAffinity(Handle, 0x11);
        WindowStyle.ApplyTitleBar(this);
    }

    protected Label MakeLabel(string text, int x, int y, int width = 380) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 18),
        ForeColor = Soft
    };

    protected TextBox MakeBox(int x, int y, int width = 380) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, 24),
        BackColor = Field,
        ForeColor = Theme.Strong,
        BorderStyle = BorderStyle.FixedSingle
    };

    protected Button MakeButton(string text, int x, int y, int width = 90) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(width, 28),
        FlatStyle = FlatStyle.Flat,
        BackColor = Field,
        ForeColor = Theme.Strong,
        UseVisualStyleBackColor = false
    };
}

// ======================================================
// MASTER PASSWORD PROMPT
// ======================================================

internal sealed class PasswordPrompt : DarkDialog
{
    private readonly TextBox first;
    private readonly TextBox? second;
    private readonly Label error;
    private readonly bool confirm;

    public string? Result { get; private set; }

    private PasswordPrompt(string title, string message, bool confirm, string? note)
        : base(title)
    {
        this.confirm = confirm;

        ClientSize = new Size(420, confirm ? 220 : 175);

        Controls.Add(new Label
        {
            Text = message,
            Location = new Point(20, 15),
            Size = new Size(380, 36),
            ForeColor = Theme.Strong
        });

        first = MakeBox(20, 58);
        first.UseSystemPasswordChar = true;
        Controls.Add(first);

        int y = 58;

        if (confirm)
        {
            Controls.Add(MakeLabel("Repeat it:", 20, 88));

            second = MakeBox(20, 108);
            second.UseSystemPasswordChar = true;
            Controls.Add(second);

            y = 108;
        }

        error = new Label
        {
            Text = note ?? "",
            Location = new Point(20, y + 32),
            Size = new Size(380, 36),
            ForeColor = Bad
        };

        Controls.Add(error);

        Button ok = MakeButton("OK", 215, ClientSize.Height - 42);
        Button cancel = MakeButton("Cancel", 310, ClientSize.Height - 42);

        ok.Click += (_, _) => Submit();
        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Submit()
    {
        if (first.Text.Length == 0)
        {
            error.Text = "Enter a password.";
            return;
        }

        if (confirm)
        {
            if (first.Text.Length < 8)
            {
                error.Text = "Use at least 8 characters.";
                return;
            }

            if (first.Text != second!.Text)
            {
                error.Text = "The two passwords do not match.";
                return;
            }
        }

        Result = first.Text;
        DialogResult = DialogResult.OK;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        first.Focus();
    }

    public static string? Ask(
        IWin32Window owner, string title, string message,
        bool confirm, string? note = null)
    {
        using PasswordPrompt dialog = new(title, message, confirm, note);

        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : null;
    }
}

// ======================================================
// ENTRY EDITOR
// ======================================================

internal sealed class EntryEditorForm : DarkDialog
{
    private readonly TextBox title;
    private readonly TextBox user;
    private readonly TextBox password;
    private readonly TextBox url;
    private readonly TextBox notes;
    private readonly Label error;
    private readonly VaultEntry entry;

    public VaultEntry? Result { get; private set; }

    private EntryEditorForm(VaultEntry? existing)
        : base(existing == null ? "New entry" : "Edit entry")
    {
        entry = existing?.Clone() ?? new VaultEntry();

        ClientSize = new Size(420, 410);

        Controls.Add(MakeLabel("Title", 20, 14));
        title = MakeBox(20, 34);
        title.Text = entry.Title;
        Controls.Add(title);

        Controls.Add(MakeLabel("Username / email", 20, 66));
        user = MakeBox(20, 86);
        user.Text = entry.Username;
        Controls.Add(user);

        Controls.Add(MakeLabel("Password", 20, 118));
        password = MakeBox(20, 138, 262);
        password.Text = entry.Password;
        password.UseSystemPasswordChar = true;
        Controls.Add(password);

        Button show = MakeButton("Show", 290, 136, 52);
        show.Click += (_, _) =>
        {
            password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
            show.Text = password.UseSystemPasswordChar ? "Show" : "Hide";
        };
        Controls.Add(show);

        Button generate = MakeButton("Make", 348, 136, 52);
        generate.Click += (_, _) =>
        {
            password.Text = SystemTools.RandomPassword(20);
            password.UseSystemPasswordChar = false;
            show.Text = "Hide";
        };
        Controls.Add(generate);

        Controls.Add(MakeLabel("Website", 20, 170));
        url = MakeBox(20, 190);
        url.Text = entry.Url;
        Controls.Add(url);

        Controls.Add(MakeLabel("Notes", 20, 222));
        notes = MakeBox(20, 242);
        notes.Multiline = true;
        notes.Height = 80;
        notes.ScrollBars = ScrollBars.Vertical;
        notes.AcceptsReturn = true;
        notes.Text = entry.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        Controls.Add(notes);

        error = new Label
        {
            Location = new Point(20, 330),
            Size = new Size(380, 20),
            ForeColor = Bad
        };
        Controls.Add(error);

        Button save = MakeButton("Save", 215, 366);
        Button cancel = MakeButton("Cancel", 310, 366);

        save.Click += (_, _) => Save();
        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(save);
        Controls.Add(cancel);

        CancelButton = cancel;
    }

    private void Save()
    {
        if (title.Text.Trim().Length == 0)
        {
            error.Text = "Give the entry a title.";
            return;
        }

        entry.Title = title.Text.Trim();
        entry.Username = user.Text.Trim();
        entry.Password = password.Text;
        entry.Url = url.Text.Trim();
        entry.Notes = notes.Text;
        entry.Modified = DateTime.Now;

        Result = entry;
        DialogResult = DialogResult.OK;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        title.Focus();
    }

    /// <summary>Pass null to create a new entry.</summary>
    public static VaultEntry? Edit(IWin32Window owner, VaultEntry? existing)
    {
        using EntryEditorForm dialog = new(existing);

        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : null;
    }
}
