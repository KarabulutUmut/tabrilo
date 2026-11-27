using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Compiled for the Windows inbox .NET Framework. Both installer and uninstaller
// extract their own copy of this helper into the NSIS private plugin directory.
internal static class PayloadReceipt
{
    private static string Hash(string path)
    {
        using (var sha = SHA256.Create())
        using (var file = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }

    private static void CheckAncestors(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation directories are unsupported.");
    }

    private static string Entry(string root, string relative)
    {
        if (relative.Split('\\').Any(s => s.Length == 0 || s == "." || s == ".." ||
            s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || s.EndsWith(".") || s.EndsWith(" ")))
            throw new InvalidDataException("Invalid payload entry.");
        var path = Path.Combine(root, relative);
        CheckAncestors(Path.GetDirectoryName(path));
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked payload entries are unsupported.");
        return path;
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3 || !new[] { "check", "repair", "remove" }.Contains(args[0])) return 2;
            var root = Path.GetFullPath(args[1]);
            var receipt = Entry(root, "payload.receipt");
            if (!Regex.IsMatch(args[2], "^[0-9A-F]{64}$") || Hash(receipt) != args[2]) return 2;
            var lines = File.ReadAllLines(receipt);
            if (lines.Length < 2 || (lines[0] != "core" && lines[0] != "office")) return 2;
            var directories = new List<string>();
            var missing = new List<string>();
            foreach (var line in lines.Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length != 2) return 2;
                var path = Entry(root, parts[1]);
                if (parts[0] == "dir") { directories.Add(path); continue; }
                if (!Regex.IsMatch(parts[0], "^[0-9A-F]{64}$")) return 2;
                if (!File.Exists(path)) { missing.Add(parts[1]); continue; }
                if (Hash(path) != parts[0])
                {
                    if (args[0] == "remove") continue; // preserve user-modified files
                    return 3;
                }
                if (args[0] == "remove") File.Delete(path);
            }
            if (args[0] == "remove")
            {
                foreach (var dir in directories.OrderByDescending(p => p.Length))
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                File.Delete(receipt);
                return 0;
            }
            if (args[0] == "check")
                return missing.Count == 0 && directories.All(Directory.Exists) ? 0 : 3;
            if (args.Length != 4) return 2;
            // Preflight all existing entries before restoring any missing ones. A retry is idempotent.
            foreach (var dir in directories) Directory.CreateDirectory(dir);
            foreach (var relative in missing)
            {
                var path = Entry(root, relative);
                var temp = Path.Combine(Path.GetDirectoryName(path), ".repair-" + Guid.NewGuid().ToString("N"));
                File.Copy(Entry(args[3], relative), temp, false);
                try { File.Move(temp, path); }
                finally { File.Delete(temp); }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
