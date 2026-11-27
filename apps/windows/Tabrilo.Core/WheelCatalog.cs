using System.Text.Json;

namespace Tabrilo.Core;

public enum WheelMode { Convert, Tools, Actions }

public sealed record WheelCatalog(int Version, string[] Bindings, double Diameter, double InnerRadius,
    double OuterRadius, double HalfGapRadians, string[] Conversion, string[] Tools, string[] Actions, Dictionary<string, string> Symbols)
{
    public static WheelCatalog Shared { get; } = Load();

    private static WheelCatalog Load()
    {
        using var stream = typeof(Commands).Assembly.GetManifestResourceStream("Tabrilo.Wheel.json")!;
        var catalog = JsonSerializer.Deserialize<WheelCatalog>(stream, JsonSerializerOptions.Web)!;
        if (catalog.Version != 1 || catalog.Actions.Length != 6 ||
            catalog.Conversion.Concat(catalog.Tools).Concat(catalog.Actions).Any(id => !Commands.All.Any(c => c.Id == id)))
            throw new InvalidDataException("Invalid wheel contract.");
        return catalog;
    }

    public CommandSpec[] For(WheelMode mode, IReadOnlyList<string> paths, bool webp, bool office)
    {
        if (paths.Count == 0) return [];
        var ids = mode switch { WheelMode.Convert => Conversion, WheelMode.Tools => Tools, _ => Actions };
        var kinds = mode == WheelMode.Actions ? [] : paths.Select(FileKinds.Classify).ToHashSet();
        var formats = paths.Select(path => Path.GetExtension(path).TrimStart('.').ToLowerInvariant())
            .Select(ext => ext == "jpeg" ? "jpg" : ext).ToHashSet();
        return ids.Select(id => Commands.All.First(c => c.Id == id)).Where(command =>
        {
            if (mode != WheelMode.Actions && !kinds.All(kind => command.Kinds.Contains(kind))) return false;
            if (mode == WheelMode.Convert)
                return !formats.Contains(command.Id[8..]) && (command.Id != "convert.webp" || webp) &&
                    (office || !kinds.Contains("office")) &&
                    (command.Id != "convert.pdf" || kinds.Count == 1);
            return command.Id switch
            {
                "pdf.merge" => paths.Count > 1,
                "image.edit" or "image.crop" or "image.frame" or "image.annotate" or "image.redact" or
                "pdf.metadata" or "pdf.split" or "pdf.organize" => paths.Count == 1,
                _ => true
            };
        }).ToArray();
    }
}
