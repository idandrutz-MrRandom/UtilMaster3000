using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UtilMaster3000;

// ======================================================
// PASSWORD VAULT
//
//  * One file: %AppData%\UtilMaster3000\vault.dat
//  * Master password -> PBKDF2-SHA256 (600,000 rounds, random salt)
//    -> 256-bit key -> AES-256-GCM. The header is authenticated, so
//    a wrong password and a tampered file are both detected.
//  * The master password is never stored; it cannot be recovered.
//  * A fresh random nonce is used for every save.
// ======================================================

internal sealed class VaultEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.Now;

    public VaultEntry Clone() => (VaultEntry)MemberwiseClone();
}

internal sealed class Vault
{
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 600_000;
    private const byte Version = 1;

    private static readonly byte[] Magic = { (byte)'U', (byte)'M', (byte)'3', (byte)'V' };

    // magic + version + iterations + salt
    private const int HeaderSize = 4 + 1 + 4 + SaltSize;

    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UtilMaster3000",
        "vault.dat");

    public static bool Exists => File.Exists(FilePath);

    private byte[]? key;
    private byte[] salt = Array.Empty<byte>();
    private int iterations;

    public List<VaultEntry> Entries { get; private set; } = new();

    public bool IsUnlocked => key != null;

    private Vault()
    {
    }

    // ---- create / open ----

    public static Vault Create(string masterPassword)
    {
        Vault vault = new()
        {
            salt = RandomNumberGenerator.GetBytes(SaltSize),
            iterations = DefaultIterations
        };

        vault.key = Derive(masterPassword, vault.salt, vault.iterations);
        vault.Save();

        return vault;
    }

    /// <summary>Returns null when the password is wrong (or the file was altered).</summary>
    public static Vault? Open(string masterPassword)
    {
        byte[] file = File.ReadAllBytes(FilePath);

        if (file.Length < HeaderSize + NonceSize + TagSize ||
            !file.AsSpan(0, 4).SequenceEqual(Magic) ||
            file[4] != Version)
        {
            throw new InvalidDataException("This is not a valid vault file.");
        }

        int iterations = BitConverter.ToInt32(file, 5);
        byte[] salt = file.AsSpan(9, SaltSize).ToArray();

        // Guard against a corrupted / hostile iteration count.
        if (iterations < 100_000 || iterations > 10_000_000)
            throw new InvalidDataException("The vault file is damaged.");

        byte[] nonce = file.AsSpan(HeaderSize, NonceSize).ToArray();
        byte[] tag = file.AsSpan(HeaderSize + NonceSize, TagSize).ToArray();

        byte[] cipher = file.AsSpan(
            HeaderSize + NonceSize + TagSize).ToArray();

        byte[] header = file.AsSpan(0, HeaderSize).ToArray();
        byte[] key = Derive(masterPassword, salt, iterations);

        byte[] plain = new byte[cipher.Length];

        try
        {
            using AesGcm aes = new(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, header);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }

        Vault vault = new()
        {
            key = key,
            salt = salt,
            iterations = iterations,
            Entries = JsonSerializer.Deserialize<List<VaultEntry>>(plain)
                ?? new List<VaultEntry>()
        };

        CryptographicOperations.ZeroMemory(plain);

        return vault;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySize);

    // ---- save ----

    public void Save()
    {
        if (key == null)
            throw new InvalidOperationException("The vault is locked.");

        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(Entries);

        byte[] header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BitConverter.GetBytes(iterations).CopyTo(header, 5);
        salt.CopyTo(header, 9);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] cipher = new byte[plain.Length];

        using (AesGcm aes = new(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag, header);

        CryptographicOperations.ZeroMemory(plain);

        byte[] output = new byte[HeaderSize + NonceSize + TagSize + cipher.Length];
        header.CopyTo(output, 0);
        nonce.CopyTo(output, HeaderSize);
        tag.CopyTo(output, HeaderSize + NonceSize);
        cipher.CopyTo(output, HeaderSize + NonceSize + TagSize);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write to a temp file first so a crash can never leave a
        // half-written vault; keep the previous version as .bak.
        string temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, output);

        if (File.Exists(FilePath))
            File.Replace(temp, FilePath, FilePath + ".bak");
        else
            File.Move(temp, FilePath);
    }

    public void ChangeMasterPassword(string newPassword)
    {
        if (key == null)
            throw new InvalidOperationException("The vault is locked.");

        CryptographicOperations.ZeroMemory(key);

        salt = RandomNumberGenerator.GetBytes(SaltSize);
        iterations = DefaultIterations;
        key = Derive(newPassword, salt, iterations);

        Save();
    }

    public void Lock()
    {
        if (key != null)
            CryptographicOperations.ZeroMemory(key);

        key = null;
        Entries.Clear();
    }

    // ---- CSV import (Chrome / Edge / Firefox / Bitwarden style exports) ----

    public static List<VaultEntry> ParseCsv(string csv)
    {
        List<List<string>> rows = ReadCsv(csv);
        List<VaultEntry> result = new();

        if (rows.Count < 2)
            return result;

        List<string> header = rows[0]
            .Select(h => h.Trim().ToLowerInvariant())
            .ToList();

        int Find(params string[] names) =>
            header.FindIndex(h => names.Contains(h));

        int title = Find("name", "title", "login_name");
        int url = Find("url", "website", "login_uri", "web site");
        int user = Find("username", "login_username", "user", "login");
        int pass = Find("password", "login_password");
        int notes = Find("note", "notes");

        if (pass < 0)
            return result;

        string Cell(List<string> row, int index) =>
            index >= 0 && index < row.Count ? row[index] : "";

        foreach (List<string> row in rows.Skip(1))
        {
            string password = Cell(row, pass);

            if (password.Length == 0)
                continue;

            string address = Cell(row, url);
            string name = Cell(row, title);

            if (name.Length == 0)
                name = address;

            if (name.Length == 0)
                name = "(untitled)";

            result.Add(new VaultEntry
            {
                Title = name,
                Url = address,
                Username = Cell(row, user),
                Password = password,
                Notes = Cell(row, notes)
            });
        }

        return result;
    }

    private static List<List<string>> ReadCsv(string text)
    {
        List<List<string>> rows = new();
        List<string> row = new();
        StringBuilder cell = new();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;

                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;

                default:
                    cell.Append(c);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
