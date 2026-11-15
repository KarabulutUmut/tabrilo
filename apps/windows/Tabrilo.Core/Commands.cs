using System.Text.Json;

namespace Tabrilo.Core;

public sealed record CommandSpec(string Id, string Label, string Scope, string? Shortcut, string[] Kinds)
{
    public bool AppliesTo(IReadOnlyCollection<string> inputs) =>
        Scope == "operation" && inputs.Count > 0 && inputs.All(path => Kinds.Contains(FileKinds.Classify(path)));
}

public static class Commands
{
    private sealed record Contract(int Version, CommandSpec[] Commands);
    public static IReadOnlyList<CommandSpec> All { get; } = Load();

    private static CommandSpec[] Load()
    {
        using var stream = typeof(Commands).Assembly.GetManifestResourceStream("Tabrilo.Commands.json")
            ?? throw new InvalidOperationException("The semantic command catalog is missing.");
        var contract = JsonSerializer.Deserialize<Contract>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The semantic command catalog is empty.");
        if (contract.Version != 1 || contract.Commands.Select(command => command.Id).Distinct().Count() != contract.Commands.Length)
            throw new InvalidDataException("Invalid semantic command catalog.");
        return contract.Commands;
    }
}

public static class FileKinds
{
    private static readonly IReadOnlyDictionary<string, string> ByExtension = Load();

    public static string Classify(string path) => ByExtension.GetValueOrDefault(Path.GetExtension(path), "unsupported");

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(FileKinds).Assembly.GetManifestResourceStream("Tabrilo.Formats.json")
            ?? throw new InvalidOperationException("The format contract is missing.");
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported format contract version.");
        return json.RootElement.GetProperty("input").EnumerateObject().SelectMany(kind =>
            kind.Value.EnumerateArray().Select(extension => (Extension: "." + extension.GetString(), Kind: kind.Name)))
            .ToDictionary(item => item.Extension, item => item.Kind, StringComparer.OrdinalIgnoreCase);
    }
}
