using System.Text.Json;

namespace Tabrilo.Core;

public sealed record RecentResult(string Operation, string Path, DateTimeOffset Created);

public sealed record Preferences
{
    public int Version { get; init; } = 2;
    public string Appearance { get; init; } = "System";
    public bool KeepHistory { get; init; } = true;
    public bool UseTray { get; init; } = true;
    public bool StartAtLogin { get; init; }
    public bool CheckUpdatesOnStartup { get; init; }
    public string[] ModeKeys { get; init; } = [.. WheelCatalog.Shared.Bindings];
    public bool ShowWebP { get; init; }
    public string? OfficeExecutable { get; init; }
    public string DragModifier { get; init; } = "Shift";
    public string DragButton { get; init; } = "Primary";
    public string? ExportDirectory { get; init; }
    public string? ClipboardDirectory { get; init; }
    public int JpegQuality { get; init; } = 88;
    public int WebPQuality { get; init; } = 85;
    public Dictionary<string, string> Shortcuts { get; init; } = [];
    public string[] Favorites { get; init; } = ["image.convert", "image.redact", "image.share", "qr.read", "pdf.compress", "archive.extract"];
    public RecentResult[] Recent { get; init; } = [];
}

public sealed class LocalState
{
    private readonly string file;
    public LocalState(string root) => file = Path.Combine(root, "preferences.json");

    public Preferences Read()
    {
        if (!File.Exists(file)) return new();
        try
        {
            using var input = File.OpenRead(file);
            if (input.Length > 1024 * 1024) throw new InvalidDataException("Preferences exceed the size limit.");
            var settings = JsonSerializer.Deserialize<Preferences>(input, JsonSerializerOptions.Web);
            if (settings is not { Version: 1 or 2, Recent: not null, Shortcuts: not null, Favorites: not null })
                throw new InvalidDataException("Invalid preferences.");
            return settings with
            {
                Version = 2,
                ModeKeys = settings.ModeKeys is { Length: 3 } keys && keys.Distinct().Count() == 3 &&
                    keys.All(WheelCatalog.Shared.Bindings.Contains) ? keys : [.. WheelCatalog.Shared.Bindings],
                Recent = settings.KeepHistory ? settings.Recent.Where(item => item is { Path: not null } &&
                    Path.IsPathFullyQualified(item.Path)).Take(50).ToArray() : [],
                Appearance = settings.Appearance is "Light" or "Dark" ? settings.Appearance : "System",
                DragModifier = settings.DragModifier is "Ctrl" or "Alt" or "Off" ? settings.DragModifier : "Shift",
                DragButton = settings.DragButton == "Secondary" ? "Secondary" : "Primary",
                Favorites = settings.Favorites.Where(id => Commands.All.Any(c => c.Id == id && c.Scope == "operation"))
                    .Distinct().Take(6).ToArray(),
                ExportDirectory = settings.Version == 1 ? null : ValidDirectory(settings.ExportDirectory),
                ClipboardDirectory = ValidDirectory(settings.ClipboardDirectory),
                JpegQuality = Math.Clamp(settings.JpegQuality, 1, 100),
                WebPQuality = Math.Clamp(settings.WebPQuality, 1, 100)
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            File.Copy(file, file + ".recovery", overwrite: true);
            return new();
        }
    }

    private static string? ValidDirectory(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) ? path : null;

    public void Write(Preferences settings)
    {
        var parent = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, JsonSerializerOptions.Web);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
