namespace Tabrilo.Core;

public enum InvocationOrigin { Home, Clipboard, KeyboardWheel, ExplorerWheel }
public enum Presentation { Quick, Choices, Editor, Information }

public sealed record OperationInput(string Path, bool Clipboard = false);

public sealed record OutputPolicy(string? ExportDirectory, string? ClipboardDirectory)
{
    public string? For(OperationInput input) =>
        input.Clipboard ? ClipboardDirectory ?? ExportDirectory : ExportDirectory ?? Path.GetDirectoryName(input.Path);

    public string? ForCombined(IReadOnlyList<OperationInput> inputs)
    {
        var destinations = inputs.Select(For).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return destinations.Length == 1 ? destinations[0] : null;
    }
}

public static class OperationRouting
{
    public static Presentation For(string command, bool advanced = false) => command switch
    {
        "image.pdf" or "pdf.merge" or "pdf.split" or "pdf.images"
            or "pdf.metadata" => Presentation.Choices,
        "image.compress" or "pdf.compress" or "qr.read" when advanced => Presentation.Choices,
        "image.crop" or "image.redact" or "image.annotate" or "image.edit" or "image.frame"
            or "image.share" or "pdf.organize" => Presentation.Editor,
        "image.info" or "pdf.info" => Presentation.Information,
        _ => Presentation.Quick
    };
}
