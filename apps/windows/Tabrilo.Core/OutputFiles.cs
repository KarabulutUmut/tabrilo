namespace Tabrilo.Core;

public static class OutputFiles
{
    public static async Task<string> WriteAsync(string destination, string stem, string extension,
        Func<string, CancellationToken, Task> write, Func<string, CancellationToken, Task> validate,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateLeaf(stem + extension);
        var parent = new DirectoryInfo(destination);
        if (!parent.Exists) throw new DirectoryNotFoundException("The export folder is unavailable.");
        var stage = Path.Combine(parent.FullName, $".tabrilo-{Guid.NewGuid():N}{extension}");
        using (new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            await write(stage, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (new FileInfo(stage).Length == 0) throw new InvalidDataException("The result file is empty.");
            await validate(stage, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            for (var index = 0; index < 1000; index++)
            {
                var path = Path.Combine(parent.FullName, Candidate(stem, index) + extension);
                try { File.Move(stage, path, overwrite: false); return path; }
                catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
            }
            throw new IOException("Could not choose a unique result filename.");
        }
        finally { File.Delete(stage); }
    }

    public static string Candidate(string stem, int index) => index == 0 ? stem : $"{stem} ({index + 1})";

    public static void ValidateLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.EndsWith(' ') || name.EndsWith('.')
            || name is "." or ".." || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0
            || name.Any(char.IsControl) || Reserved(name))
            throw new InvalidDataException("An output name is invalid or reserved by Windows.");
    }

    private static bool Reserved(string name)
    {
        var device = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3]);
    }
}
