using System.Globalization;
using System.Text.Json.Nodes;
using PdfSharp.Drawing;
using Tabrilo.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tabrilo.Processing;

public sealed record PdfInfo(int Pages, bool Encrypted, bool Signed, bool Interactive, bool Complex,
    IReadOnlyDictionary<string, string> Properties, bool PageExtras = false);
public sealed class PdfRenderSession(PdfDocument document, IRandomAccessStream stream) : IDisposable
{
    internal PdfDocument Document { get; } = document;
    public uint PageCount => Document.PageCount;
    public void Dispose() => stream.Dispose();
}

public sealed record PdfPageChoice(string Path, int Number, int Rotation = 0);
public sealed record ImagePdfOptions(double WidthPoints = 595.28, double HeightPoints = 841.89,
    double MarginPoints = 24, bool Fill = false);

public sealed partial class Pdfs(string qpdfPath)
{
    private readonly PdfWorker worker = new(qpdfPath);
    private static readonly string[] PropertyKeys = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator"];

    public async Task<PdfInfo> InspectAsync(string input, string? password, CancellationToken token) =>
        (await ReadDetails(input, password, token)).Info;

    private sealed record Details(PdfInfo Info, JsonObject Trailer, JsonObject Objects, JsonNode Header, JsonArray Pages, JsonArray Outlines)
    {
        public JsonNode? Resolve(JsonNode? value) => ResolveValue(Objects, value);
    }

    private static JsonNode? ResolveValue(JsonObject objects, JsonNode? value)
    {
        for (var depth = 0; value is JsonValue scalar && scalar.TryGetValue<string>(out var reference) &&
            objects["obj:" + reference] is JsonObject entry; depth++)
        {
            if (depth >= 32) throw new InvalidDataException("A PDF reference is cyclic or too deeply nested.");
            value = entry["value"];
        }
        return value;
    }

    private async Task<Details> ReadDetails(
        string input, string? password, CancellationToken token)
    {
        input = Path.GetFullPath(input);
        if (new FileInfo(input).Length > 512L << 20) throw new InvalidDataException("The PDF exceeds the 512 MiB limit.");
        if (password is not null && password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("PDF passwords must fit on one line.");
        var args = new List<string> { input, "--json=2", "--json-key=qpdf", "--json-key=pages", "--json-key=encrypt", "--json-key=outlines" };
        if (password is not null) args.Add("--password-file=-");
        var json = JsonNode.Parse(await worker.RunAsync(args, password, token))!;
        var sections = json["qpdf"]!.AsArray();
        var objects = sections[1]!.AsObject();
        var trailer = objects["trailer"]!["value"]!.AsObject();
        var info = trailer["/Info"] is JsonValue reference && reference.TryGetValue<string>(out var name)
            ? objects["obj:" + name]?["value"] as JsonObject : trailer["/Info"] as JsonObject;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (info is not null)
        {
            foreach (var key in PropertyKeys)
            {
                if (info[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith("u:"))
                    fields[key[1..]] = text[2..];
            }
        }
        var count = json["pages"]!.AsArray().Count;
        if (count is 0 or > 2000) throw new InvalidDataException("The PDF is outside the supported 1–2000 page range.");
        var flags = new StructureFlags();
        InspectStructure(objects, objects, flags, token);
        return new(new PdfInfo(count, json["encrypt"]!["encrypted"]!.GetValue<bool>(), flags.Signed,
            flags.Active, flags.Complex, fields, flags.Extras), trailer, objects, sections[0]!, json["pages"]!.AsArray(), json["outlines"]!.AsArray());
    }

    private sealed class StructureFlags
    {
        public bool Signed;
        public bool Active;
        public bool Complex;
        public bool Extras;
    }

    private static void InspectStructure(JsonNode? node, JsonObject objects, StructureFlags flags, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node is JsonArray array)
        {
            foreach (var value in array) InspectStructure(value, objects, flags, token);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var (key, value) in obj)
        {
            var resolved = key is "/OpenAction" or "/Type" or "/FT" or "/S" or "/Subtype" ? ResolveValue(objects, value) : value;
            if (key is "/ByteRange" or "/DocMDP" || key is "/Type" or "/FT" && IsName(resolved, "/Sig"))
                flags.Signed = true;
            if (key == "/OpenAction" && resolved is not JsonArray || key is "/AA" or "/JavaScript" or "/JS" or "/RichMedia" or "/XFA" ||
                key == "/S" && (IsName(resolved, "/Launch") || IsName(resolved, "/SubmitForm") || IsName(resolved, "/GoToR")
                    || IsName(resolved, "/GoToE") || IsName(resolved, "/ImportData") || IsName(resolved, "/JavaScript")
                    || IsName(resolved, "/URI") && !SafeUri(ResolveValue(objects, obj["/URI"]))) ||
                key == "/F" && obj.ContainsKey("/Filter"))
                flags.Active = true;
            if (key is "/StructTreeRoot" or "/Names" or "/Dests" or "/OpenAction") flags.Extras = true;
            if (key == "/Subtype" && IsName(resolved, "/Widget") || key is "/AcroForm" or
                "/OCProperties" or "/Collection" or "/EmbeddedFiles" or "/Threads" or "/OutputIntents")
                flags.Complex = true;
            InspectStructure(value, objects, flags, token);
        }
    }

    private static bool SafeUri(JsonNode? node) => node is JsonValue scalar && scalar.TryGetValue<string>(out var value)
        && value.StartsWith("u:", StringComparison.Ordinal) && Uri.TryCreate(value[2..], UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto";

    private static bool IsName(JsonNode? value, string expected) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text == expected;

    public async Task<string> CompressAsync(string input, string folder, bool lossy, int quality, CancellationToken token)
    {
        input = Path.GetFullPath(input);
        var info = await InspectAsync(input, null, token);
        CheckTransform(info, editingPages: false);
        string[] options = lossy
            ? ["--object-streams=generate", "--recompress-flate", "--compression-level=9", "--optimize-images",
                "--keep-inline-images", $"--jpeg-quality={Math.Clamp(quality, 1, 100)}"]
            : ["--object-streams=generate", "--recompress-flate", "--compression-level=9"];
        return await OutputFiles.WriteAsync(folder, Stem(input, "compressed"), ".pdf",
            async (path, cancellation) =>
            {
                await worker.RunAsync([input, .. options, path], null, cancellation);
                if (new FileInfo(path).Length >= new FileInfo(input).Length)
                    throw new InvalidDataException("The selected settings did not produce a smaller PDF.");
            }, (path, cancellation) => Validate(path, info.Pages, cancellation), token);
    }

    public async Task<string> PropertiesAsync(string input, string folder, IReadOnlyDictionary<string, string> properties,
        CancellationToken token)
    {
        input = Path.GetFullPath(input);
        var (info, trailer, objects, header, _, _) = await ReadDetails(input, null, token);
        CheckTransform(info, editingPages: false);
        var values = new JsonObject();
        foreach (var key in PropertyKeys)
        {
            if (properties.TryGetValue(key[1..], out var value) && !string.IsNullOrWhiteSpace(value))
            {
                if (value.Length > 4096) throw new ArgumentException("A PDF property is too long.");
                values[key] = "u:" + value;
            }
        }
        var changes = new JsonObject();
        var updatedTrailer = (JsonObject)trailer.DeepClone();
        if (values.Count != 0)
        {
            var maximum = header["maxobjectid"]?.GetValue<int>() ??
                objects.Select(pair => pair.Key.StartsWith("obj:") ?
                    int.Parse(pair.Key.AsSpan(4, pair.Key.IndexOf(' ', 4) - 4), CultureInfo.InvariantCulture) : 0).Max();
            var reference = $"{checked(maximum + 1)} 0 R";
            updatedTrailer["/Info"] = reference;
            changes["obj:" + reference] = new JsonObject { ["value"] = values };
        }
        else updatedTrailer.Remove("/Info");
        changes["trailer"] = new JsonObject { ["value"] = updatedTrailer };
        var update = new JsonObject { ["qpdf"] = new JsonArray(header.DeepClone(), changes) }.ToJsonString();
        return await OutputFiles.WriteAsync(folder, Stem(input, "properties"), ".pdf",
            async (path, cancellation) =>
            {
                var patch = path + ".json";
                try
                {
                    using (var stream = new FileStream(patch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream))
                        await writer.WriteAsync(update.AsMemory(), cancellation);
                    await worker.RunAsync([input, "--update-from-json=" + patch, "--remove-metadata", path], null, cancellation);
                }
                finally { File.Delete(patch); }
            },
            async (path, cancellation) =>
            {
                await Validate(path, info.Pages, cancellation);
                var check = await ReadDetails(path, null, cancellation);
                if (values.Count == 0 && check.Trailer.ContainsKey("/Info") ||
                    values.Any(entry => !check.Info.Properties.TryGetValue(entry.Key[1..], out var actual) ||
                        "u:" + actual != entry.Value!.GetValue<string>()))
                    throw new InvalidDataException("The rewritten PDF properties could not be verified.");
            }, token);
    }

    public async Task<string> FromImagesAsync(IReadOnlyList<string> inputs, string folder, ImagePdfOptions options,
        CancellationToken token, string? outputStem = null)
    {
        var (width, height, margin, fill) = options;
        if (inputs.Count is 0 or > 200 || width is < 120 or > 2000 || height is < 120 or > 2000 ||
            margin < 0 || margin * 2 >= Math.Min(width, height))
            throw new ArgumentException("Choose valid page dimensions, margins and up to 200 images.");
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.Options.NoCompression = true;
        long pixels = 0;
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var processed = await Images.ForPdfAsync(input, token);
            pixels += (long)processed.Width * processed.Height;
            if (pixels > 60_000_000) throw new InvalidDataException("The selected images exceed the 60-megapixel PDF job limit.");
            using var stream = new MemoryStream(processed.Bytes, 0, processed.Bytes.Length, writable: false, publiclyVisible: true);
            using var image = XImage.FromStream(stream);
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using var graph = XGraphics.FromPdfPage(page);
            graph.IntersectClip(new XRect(margin, margin, width - margin * 2, height - margin * 2));
            var ratioX = (width - margin * 2) / processed.Width;
            var ratioY = (height - margin * 2) / processed.Height;
            var ratio = fill ? Math.Max(ratioX, ratioY) : Math.Min(ratioX, ratioY);
            var contentWidth = processed.Width * ratio;
            var contentHeight = processed.Height * ratio;
            graph.DrawImage(image, (width - contentWidth) / 2, (height - contentHeight) / 2, contentWidth, contentHeight);
        }
        return await OutputFiles.WriteAsync(folder, outputStem ?? Stem(inputs[0], "images"), ".pdf",
            async (path, cancellation) =>
            {
                var draft = path + ".draft";
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (var stream = new FileStream(draft, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        document.Save(stream, closeStream: false);
                    await worker.RunAsync([draft, "--stream-data=compress", "--recompress-flate", "--object-streams=generate", path], null,
                        cancellation, normalizeGeneratedStreams: true);
                }
                finally { File.Delete(draft); }
            },
            (path, cancellation) => Validate(path, inputs.Count, cancellation), token);
    }

    public async Task<PdfRenderSession> OpenForRenderingAsync(string path, string? password, CancellationToken token)
    {
        var info = await InspectAsync(path, password, token);
        if (info.Interactive) throw new NotSupportedException("This PDF contains actions or external references and cannot be rendered.");
        var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var stream = file.AsRandomAccessStream();
        try
        {
            var document = password is null ? await PdfDocument.LoadFromStreamAsync(stream).AsTask(token)
                : await PdfDocument.LoadFromStreamAsync(stream, password).AsTask(token);
            return new(document, stream);
        }
        catch { stream.Dispose(); throw; }
    }

    public static async Task<ProcessedImage> RenderPageAsync(PdfRenderSession document, int page, int dpi,
        ImageFormat format, CancellationToken token)
    {
        if (page < 1 || page > document.PageCount || dpi is < 48 or > 300)
            throw new ArgumentException("An invalid page or resolution was selected.");
        using var source = document.Document.GetPage((uint)(page - 1));
        var width = checked((uint)Math.Max(1, Math.Round(source.Size.Width * dpi / 96.0)));
        var height = checked((uint)Math.Max(1, Math.Round(source.Size.Height * dpi / 96.0)));
        if ((long)width * height > 20_000_000) throw new InvalidDataException("The rendered page exceeds 20 megapixels.");
        using var stream = new InMemoryRandomAccessStream();
        await source.RenderToStreamAsync(stream, new PdfPageRenderOptions
        {
            DestinationWidth = width, DestinationHeight = height,
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId
        }).AsTask(token);
        stream.Seek(0);
        using var net = stream.AsStreamForRead();
        return Images.ProcessPngStream(net, format, token);
    }

    public async Task ValidateRenderedAsync(string path, CancellationToken token)
    {
        var info = await InspectAsync(path, null, token);
        await Validate(path, info.Pages, token);
        using var document = await OpenForRenderingAsync(path, null, token);
        if (document.PageCount != info.Pages) throw new InvalidDataException("Office PDF page count mismatch.");
        await RenderPageAsync(document, 1, 48, ImageFormat.Png, token);
    }

    private async Task Validate(string path, int pages, CancellationToken token)
    {
        await worker.RunAsync([Path.GetFullPath(path), "--check"], null, token);
        var count = await worker.RunAsync([Path.GetFullPath(path), "--show-npages"], null, token);
        if (!int.TryParse(count, CultureInfo.InvariantCulture, out var actual) || pages != actual)
            throw new InvalidDataException("The PDF output page count differs from the request.");
    }

    private static void CheckTransform(PdfInfo info, bool editingPages)
    {
        if (info.Signed) throw new NotSupportedException("This PDF is digitally signed. A rewrite would invalidate its signature.");
        if (info.Encrypted) throw new NotSupportedException("Protected PDFs can be inspected and rendered with a password. Rewriting is unavailable.");
        if (editingPages && info.Interactive) throw new NotSupportedException("Page assembly cannot preserve active content or external resource actions.");
        if (editingPages && info.Complex) throw new NotSupportedException("Page assembly cannot preserve forms, attachments, layers, output intents or article threads.");
    }

    public static string Stem(string path, string suffix) => $"{Path.GetFileNameWithoutExtension(path)} {suffix}";
}
