using System.Globalization;
using System.Text.Json.Nodes;
using PdfSharp.Drawing;
using Tabrilo.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tabrilo.Processing;

public sealed record PdfInfo(int Pages, bool Encrypted, bool Signed, bool Interactive, bool Complex,
    IReadOnlyDictionary<string, string> Properties);
public sealed record PdfPageChoice(string Path, int Number, int Rotation = 0);
public sealed record ImagePdfOptions(double WidthPoints = 595.28, double HeightPoints = 841.89,
    double MarginPoints = 24, bool Fill = false);

public sealed class Pdfs(string qpdfPath)
{
    private readonly PdfWorker worker = new(qpdfPath);
    private static readonly string[] PropertyKeys = ["/Title", "/Author", "/Subject", "/Keywords", "/Creator"];

    public async Task<PdfInfo> InspectAsync(string input, string? password, CancellationToken token) =>
        (await ReadDetails(input, password, token)).Info;

    private async Task<(PdfInfo Info, JsonObject Trailer, JsonObject Objects, JsonNode Header)> ReadDetails(
        string input, string? password, CancellationToken token)
    {
        input = Path.GetFullPath(input);
        if (new FileInfo(input).Length > 512L << 20) throw new InvalidDataException("The PDF exceeds the 512 MiB limit.");
        if (password is not null && password.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("PDF passwords must fit on one line.");
        var args = new List<string> { input, "--json=2", "--json-key=qpdf", "--json-key=pages", "--json-key=encrypt" };
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
        InspectStructure(objects, flags, token);
        return (new PdfInfo(count, json["encrypt"]!["encrypted"]!.GetValue<bool>(), flags.Signed,
            flags.Active, flags.Complex, fields), trailer, objects, sections[0]!);
    }

    private sealed class StructureFlags
    {
        public bool Signed;
        public bool Active;
        public bool Complex;
    }

    private static void InspectStructure(JsonNode? node, StructureFlags flags, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node is JsonArray array)
        {
            foreach (var value in array) InspectStructure(value, flags, token);
            return;
        }
        if (node is not JsonObject obj) return;
        foreach (var (key, value) in obj)
        {
            if (key is "/ByteRange" or "/DocMDP" || key is "/Type" or "/FT" && IsName(value, "/Sig"))
                flags.Signed = true;
            if (key is "/OpenAction" or "/AA" or "/JavaScript" or "/JS" or "/RichMedia" or "/XFA" ||
                key == "/S" && (IsName(value, "/Launch") || IsName(value, "/SubmitForm") || IsName(value, "/GoToR")
                    || IsName(value, "/GoToE") || IsName(value, "/ImportData") || IsName(value, "/URI")) ||
                key == "/F" && obj.ContainsKey("/Filter"))
                flags.Active = true;
            if (key is "/AcroForm" or "/Annots" or "/Outlines" or "/Names" or "/StructTreeRoot" or "/PageLabels" or
                "/OCProperties" or "/Collection" or "/EmbeddedFiles" or "/Dests" or "/Threads" or "/OutputIntents")
                flags.Complex = true;
            InspectStructure(value, flags, token);
        }
    }

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
        var (info, trailer, objects, header) = await ReadDetails(input, null, token);
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

    public async Task<string> AssembleAsync(IReadOnlyList<PdfPageChoice> pages, string folder, string stem,
        CancellationToken token)
    {
        if (pages.Count is 0 or > 2000) throw new ArgumentException("Choose between 1 and 2000 pages.");
        var inputs = new Dictionary<string, PdfInfo>(StringComparer.OrdinalIgnoreCase);
        var options = new List<string> { "--empty", "--pages" };
        foreach (var page in pages)
        {
            var input = Path.GetFullPath(page.Path);
            if (!inputs.TryGetValue(input, out var info))
            {
                info = await InspectAsync(input, null, token);
                CheckTransform(info, editingPages: true);
                inputs.Add(input, info);
            }
            if (page.Number < 1 || page.Number > info.Pages || page.Rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentException("An invalid PDF page or rotation was selected.");
            options.Add(input);
            options.Add(page.Number.ToString(CultureInfo.InvariantCulture));
        }
        options.Add("--");
        foreach (var rotation in pages.Select((page, index) => (page.Rotation, Position: index + 1))
                     .Where(item => item.Rotation != 0).GroupBy(item => item.Rotation))
            options.Add($"--rotate=+{rotation.Key}:{string.Join(',', rotation.Select(item => item.Position))}");
        // qpdf reads one argument per line via stdin, avoiding Windows command-line length limits.
        var job = string.Join('\n', options);
        return await OutputFiles.WriteAsync(folder, stem, ".pdf",
            async (path, cancellation) => { await worker.RunAsync(["@-"], job + "\n" + path, cancellation); },
            (path, cancellation) => Validate(path, pages.Count, cancellation), token);
    }

    public async Task<string> FromImagesAsync(IReadOnlyList<string> inputs, string folder, ImagePdfOptions options,
        CancellationToken token)
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
        return await OutputFiles.WriteAsync(folder, Stem(inputs[0], "images"), ".pdf",
            async (path, cancellation) =>
            {
                var draft = path + ".draft";
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (var stream = new FileStream(draft, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        document.Save(stream, closeStream: false);
                    await worker.RunAsync([draft, "--stream-data=uncompress", "--object-streams=disable", path], null,
                        cancellation, normalizeGeneratedStreams: true);
                }
                finally { File.Delete(draft); }
            },
            (path, cancellation) => Validate(path, inputs.Count, cancellation), token);
    }

    public async Task<PdfDocument> OpenForRenderingAsync(string path, string? password, CancellationToken token)
    {
        var info = await InspectAsync(path, password, token);
        if (info.Interactive) throw new NotSupportedException("This PDF contains actions or external references and cannot be rendered.");
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(token);
        return password is null ? await PdfDocument.LoadFromFileAsync(file).AsTask(token)
            : await PdfDocument.LoadFromFileAsync(file, password).AsTask(token);
    }

    public static async Task<ProcessedImage> RenderPageAsync(PdfDocument document, int page, int dpi,
        ImageFormat format, CancellationToken token)
    {
        if (page < 1 || page > document.PageCount || dpi is < 48 or > 300)
            throw new ArgumentException("An invalid page or resolution was selected.");
        using var source = document.GetPage((uint)(page - 1));
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
        if (editingPages && (info.Complex || info.Interactive))
            throw new NotSupportedException("This PDF has document structures that page assembly cannot preserve.");
    }

    public static string Stem(string path, string suffix) => $"{Path.GetFileNameWithoutExtension(path)} {suffix}";
}
