using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Chaos.NaCl;
using PdfSharp.Drawing;
using Tabrilo.Core;
using Tabrilo.Processing;
using Tabrilo.Windows;
using ZXing;
using ZXing.QrCode;

var current = new DirectoryInfo(Directory.GetCurrentDirectory());
while (current is not null && !File.Exists(Path.Combine(current.FullName, "Tabrilo.slnx"))) current = current.Parent;
var repository = current?.FullName ?? throw new InvalidOperationException("Run from the Tabrilo repository.");
var evidence = Path.Combine(repository, "artifacts", "tests", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
Directory.CreateDirectory(evidence);
var qpdf = Path.Combine(repository, ".tools", "qpdf", "qpdf-12.4.2-msvc64", "bin", "qpdf.exe");
Console.WriteLine($"Evidence directory {evidence}");
var checks = new (string Name, Func<Task> Run)[]
{
    ("Semantic catalog and local preferences", Contracts),
    ("Operation routing and output policy", OperationPolicy),
    ("Wheel sectors and editor coordinate transforms", DesktopGeometry),
    ("Atomic output, original hashes and cancellation", Atomic),
    ("Native PNG validation, masks, crop and rotation", Redaction),
    ("Annotation, framing, adjustments and WebP", Edits),
    ("Single-frame imports and JPEG alpha", Import),
    ("Multiple local QR results", ReadQr),
    ("ZIP, TAR, GZIP and TGZ publication", ArchiveFormats),
    ("Archive path, size and disk limits", ArchiveGuards),
    ("PDF properties, structural edits and WinRT render", PdfRewrite),
    ("Wheel modes and PDF navigation", ThreeWheel),
    ("PDF conservative and lossy compression", PdfCompress),
    ("Ed25519 update checks and interruption", UpdateTrust)
};
var failures = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL {name}\n{error}"); }
}
var total = checks.Length;
if (Environment.GetEnvironmentVariable("TABRILO_TEST_OFFICE") is { Length: > 0 } engine)
{
    total++;
    try { await OfficeLayouts(engine); Console.WriteLine("PASS DOCX and PPTX multi-page layouts"); }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL Office layouts\n{error}"); }
}
else Console.WriteLine("Coverage skipped: real Office engine (TABRILO_TEST_OFFICE).");
if (Environment.GetEnvironmentVariable("TABRILO_TEST_SPLIT_PDF") is { Length: > 0 } sample)
{
    total++;
    try
    {
        var pdf = new Pdfs(qpdf);
        var before = Hash(sample);
        var info = await pdf.InspectAsync(sample, null, default);
        var parts = await pdf.SplitAsync(sample, Enumerable.Range(1, info.Pages).Select(p => new[] { p }).ToArray(),
            Folder("owner-split"), true, default);
        Require(info.Pages == Directory.GetFiles(parts, "*.pdf").Length && before == Hash(sample), "owner PDF split and input hash");
        Console.WriteLine($"PASS Owner PDF split: {info.Pages} single-page PDFs, unchanged SHA-256 {before}");
    }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL Owner PDF split\n{error}"); }
}
if (args is ["--installed", var oldInstaller, var newInstaller])
{
    total++;
    try
    {
        await InstalledUpdate.RunAsync(Path.GetFullPath(oldInstaller), Path.GetFullPath(newInstaller), evidence);
        Console.WriteLine("PASS Installed signed isolated upgrade");
    }
    catch (Exception error) { failures++; Console.WriteLine($"FAIL Installed signed isolated upgrade\n{error}"); }
}
Console.WriteLine($"{total - failures}/{total} passed");
return failures == 0 ? 0 : 1;

Task Contracts()
{
    Require(Commands.All.Count >= 25 && Commands.All.Any(item => item.Id == "qr.read" &&
        item.AppliesTo(["test.pdf", "test.png"])), "semantic command applicability");
    var folder = Folder("preferences");
    var state = new LocalState(folder);
    state.Write(new Preferences { Appearance = "Dark", KeepHistory = false });
    Require(state.Read() is { Appearance: "Dark", KeepHistory: false }, "preference round trip");
    File.WriteAllText(Path.Combine(folder, "preferences.json"), "{ invalid");
    Require(state.Read().Appearance == "System" && File.Exists(Path.Combine(folder, "preferences.json.recovery")), "recovery");
    return Task.CompletedTask;
}

async Task OperationPolicy()
{
    var folder = Folder("operation-policy");
    var sub = Directory.CreateDirectory(Path.Combine(folder, "exports")).FullName;
    var first = new OperationInput(Path.Combine(folder, "first.png"));
    var second = new OperationInput(Path.Combine(sub, "second.png"));
    var clipboard = new OperationInput(Path.Combine(folder, "pasted.png"), Clipboard: true);
    var defaults = new OutputPolicy(null, null);
    Require(defaults.For(first) == folder && defaults.For(second) == sub, "per-source default");
    Require(defaults.For(clipboard) is null, "clipboard scratch path excluded");
    Require(defaults.ForCombined([first, second]) is null && defaults.ForCombined([first]) == folder,
        "explicit destination for mixed folders");
    Require(new OutputPolicy(sub, null).ForCombined([first, second, clipboard]) == sub &&
        new OutputPolicy(folder, sub).For(clipboard) == sub, "valid fixed export preference");
    Require(await OperationDestination.Writable(sub, default) &&
        !await OperationDestination.Writable(Path.Combine(sub, "unavailable"), default) &&
        !Directory.EnumerateFileSystemEntries(sub).Any(), "private write probe is cleaned up");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Expect<OperationCanceledException>(() => OperationDestination.Writable(sub, cancellation.Token));
    Require(new[] { "image.compress", "image.metadata", "pdf.compress", "archive.extract", "qr.read" }.All(id =>
        QuickOperations.Handles(id) && OperationRouting.For(id) == Presentation.Quick), "direct paths");
    Require(new[] { "convert.png", "convert.jpg", "convert.pdf", "convert.webp" }.All(id =>
        QuickOperations.Handles(id) && OperationRouting.For(id) == Presentation.Quick), "direct format commands");
    Require(OperationRouting.For("image.redact") == Presentation.Editor && !QuickOperations.Handles("image.redact") &&
        OperationRouting.For("pdf.compress", advanced: true) == Presentation.Choices, "editor and explicit advanced routing");
}

Task DesktopGeometry()
{
    var center = Wheel.Diameter / 2;
    var target = new System.Windows.Point(center + 35, center - 122);
    Require(Wheel.Sector(0).FillContains(target) && !Wheel.Sector(1).FillContains(target),
        "sector includes drop space outside the visible label");
    Require(Enumerable.Range(0, 7).All(index => !Wheel.Sector(index).FillContains(new System.Windows.Point(center, center))),
        "center is neutral");
    var view = new ImageViewport(new System.Windows.Rect(20, 95, 400, 200), 800, 400);
    Require(view.At(new System.Windows.Point(220, 195)) == new ImagePoint(400, 200) &&
        view.At(new System.Windows.Point(1000, -100)) == new ImagePoint(799, 0), "letterboxed fit and clamping");
    Require(view.Bounds(new(100, 20, 80, 60)) == new System.Windows.Rect(70, 105, 40, 30), "pixel edge mapping");
    Require(view.Move(new(700, 340, 80, 60), new(740, 350), new(790, 397)) == new PixelRect(720, 340, 80, 60),
        "move is clamped to image");
    Require(view.Resize(new(10, 10, 50, 50), new(10, 10), new(0, 0), -1, -1, 1) == new PixelRect(0, 0, 60, 60),
        "aspect resize holds the opposite corner");
    return Task.CompletedTask;
}

async Task Atomic()
{
    var folder = Folder("atomic");
    var original = Path.Combine(folder, "source.png");
    await File.WriteAllTextAsync(original, "preserve");
    var hash = Hash(original);
    var output = await OutputFiles.WriteAsync(folder, "source", ".png",
        (path, token) => File.WriteAllTextAsync(path, "result", token),
        (_, _) => Task.CompletedTask, default);
    Require(output != original && Hash(original) == hash && File.ReadAllText(output) == "result", "unique new name");
    await Expect<InvalidDataException>(() => OutputFiles.WriteAsync(folder, "bad", ".png",
        (path, token) => File.WriteAllTextAsync(path, "incomplete", token),
        (_, _) => throw new InvalidDataException(), default));
    using var stop = new CancellationTokenSource();
    await Expect<OperationCanceledException>(() => OutputFiles.WriteAsync(folder, "cancel", ".png",
        async (path, token) => { await File.WriteAllTextAsync(path, "temporary", token); stop.Cancel(); },
        (_, _) => Task.CompletedTask, stop.Token));
    Require(!Directory.EnumerateFiles(folder, ".tabrilo-*").Any() && !File.Exists(Path.Combine(folder, "bad.png")) &&
        !File.Exists(Path.Combine(folder, "cancel.png")), "failed/cancelled output cleanup");
}

async Task Redaction()
{
    var folder = Folder("redaction");
    var source = Path.Combine(folder, "source.png");
    WriteBitmap(source, 8, 6, (x, y) => ((byte)(20 * x), (byte)(35 * y), (byte)240, (byte)(x == 0 ? 0 : 120)), metadata: true);
    var hash = Hash(source);
    var preview = await Images.ReadEditorPreviewAsync(source, default);
    Require(preview.Width == 8 && preview.Height == 6, "oriented editor dimensions");
    var result = await Images.ProcessAsync(source, new(ImageFormat.Png, Redaction: true,
        Edits: new(Redactions: [new(1, 2, 2, 3)])), default);
    var output = await Images.SaveAsync(result, folder, "redacted", default);
    var bitmap = Decode(output);
    Require(bitmap.PixelWidth == 8 && bitmap.PixelHeight == 6, "PNG dimensions");
    for (var y = 2; y < 5; y++)
    for (var x = 1; x < 3; x++)
        Require(Pixel(bitmap, x, y).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "opaque pixel replacement");
    Require(Pixel(bitmap, 0, 0)[3] == 0 && Pixel(bitmap, 3, 2)[3] == 120, "preserved outside alpha");
    Require(MetadataContains(Decode(source), "SyntheticPrivateText") &&
        !Encoding.Latin1.GetString(result.Bytes).Contains("SyntheticPrivateText") &&
        !MetadataContains(bitmap, "SyntheticPrivateText"),
        "source metadata absent");
    Require(Hash(source) == hash, "input hash");
    await Expect<NotSupportedException>(() => Images.ProcessAsync(source, new(ImageFormat.Jpeg,
        Redaction: true, Edits: new(Redactions: [new(1, 2, 2, 3)])), default));
    var rotated = await Images.ProcessAsync(source, new(ImageFormat.Png, Edits: new(
        Rotation: 90, Crop: new(2, 1, 4, 3), Redactions: [new(3, 2, 1, 1)])), default);
    var rotationPath = await Images.SaveAsync(rotated, folder, "rotated", default);
    var rotation = Decode(rotationPath);
    Require(rotation.PixelWidth == 3 && rotation.PixelHeight == 4 &&
        Pixel(rotation, 1, 1).SequenceEqual(new byte[] { 0, 0, 0, 255 }), "ordered crop, region and rotation");
}

async Task Edits()
{
    var folder = Folder("edits");
    var source = Path.Combine(folder, "source.png");
    WriteBitmap(source, 100, 70, (x, y) => ((byte)x, (byte)y, (byte)80, (byte)255));
    var hash = Hash(source);
    var marks = Enum.GetValues<MarkKind>().Select((kind, index) => new ImageMark(kind,
        [new(10 + index * 6, 32), new(40 + index * 6, 54)], "Sample")).ToArray();
    var result = await Images.ProcessAsync(source, new(ImageFormat.Png,
        Edits: new(MaxEdge: 80, FlipHorizontal: true, Brightness: 14, Contrast: 10, Saturation: 80,
            Marks: marks, Padding: 12, Radius: 6, Shadow: true, Gradient: "#99EEDD")), default);
    var output = await Images.SaveAsync(result, folder, "edited", default);
    var decoded = Decode(output);
    Require(decoded.PixelWidth == 104 && decoded.PixelHeight == 80, "resize and framing dimensions");
    Require(!Pixel(decoded, 0, 0).SequenceEqual(Pixel(decoded, 0, 79)), "gradient frame");
    var webp = await Images.ProcessAsync(source, new(ImageFormat.WebP, Lossless: true), default);
    Require(webp.Bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && webp.Bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8), "WebP container");
    var webpFile = await Images.SaveAsync(webp, folder, "webp", default);
    var webpImport = await Images.ProcessAsync(webpFile, new(ImageFormat.Png), default);
    var roundTrip = Decode((await Images.SaveAsync(webpImport, folder, "roundtrip", default)));
    Require(Pixel(roundTrip, 10, 12).SequenceEqual(new byte[] { 10, 12, 80, 255 }), "lossless WebP pixel");
    Require(Hash(source) == hash, "unchanged edited input");
}

async Task Import()
{
    var folder = Folder("formats");
    var source = Path.Combine(folder, "transparent.png");
    WriteBitmap(source, 7, 4, (_, _) => ((byte)165, (byte)40, (byte)250, (byte)0));
    foreach (var (name, create) in new (string, Func<BitmapEncoder>)[]
             { ("bmp", () => new BmpBitmapEncoder()), ("tiff", () => new TiffBitmapEncoder()), ("jpg", () => new JpegBitmapEncoder()) })
    {
        var path = Path.Combine(folder, "input." + name);
        var encoder = create();
        encoder.Frames.Add(Decode(source));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var result = await Images.ProcessAsync(path, new(ImageFormat.Png), default);
        Require(result is { Width: 7, Height: 4 }, $"decoded {name} dimensions");
    }
    var multipage = Path.Combine(folder, "pages.tiff");
    var tiff = new TiffBitmapEncoder();
    tiff.Frames.Add(Decode(source));
    tiff.Frames.Add(Decode(source));
    using (var stream = File.Create(multipage)) tiff.Save(stream);
    await Expect<NotSupportedException>(() => Images.ProcessAsync(multipage, new(ImageFormat.Png), default));
    var jpeg = await Images.ProcessAsync(source, new(ImageFormat.Jpeg), default);
    var jpg = Decode(await Images.SaveAsync(jpeg, folder, "white", default));
    Require(Pixel(jpg, 4, 2).Take(3).All(channel => channel > 245), "transparent JPEG flattened onto white");
    var heicFixture = Path.Combine(repository, "tests", "fixtures", "color.heic");
    var heic = await Images.ProcessAsync(heicFixture, new(ImageFormat.Png), default);
    Require(heic is { Width: 451, Height: 461 }, "real HEIC pixel export");
}

async Task ReadQr()
{
    var folder = Folder("qr");
    var source = Path.Combine(folder, "two.png");
    var first = new QRCodeWriter().encode("https://example.invalid/one", BarcodeFormat.QR_CODE, 190, 190);
    var second = new QRCodeWriter().encode("TABRILO-SYNTHETIC-TWO", BarcodeFormat.QR_CODE, 190, 190);
    WriteBitmap(source, 420, 220, (x, y) =>
    {
        var dark = y < 190 && (x < 190 && first[x, y] || x >= 220 && x < 410 && second[x - 220, y]);
        var shade = dark ? (byte)0 : (byte)255;
        return (shade, shade, shade, (byte)255);
    });
    var hash = Hash(source);
    var result = await Qr.ScanImageAsync(source, default);
    Require(result.Count == 2 && result.Any(value => value.AllowedLink?.Host == "example.invalid") &&
        result.All(value => value.Location.Count >= 3), "multiple QR codes with coordinates");
    Require(new QrResult("file:///c:/x.exe", "Text", null, []).AllowedLink is null, "file URL stays text");
    var pdf = new Pdfs(qpdf);
    var document = await pdf.FromImagesAsync([source, source], folder, new(), default);
    using var render = await pdf.OpenForRenderingAsync(document, null, default);
    var page = await Pdfs.RenderPageAsync(render, 2, 144, ImageFormat.Png, default);
    Require(Qr.ScanRenderedPage(page, 2, default).Count(item => item.Page == 2) == 2, "PDF QR page and multiple results");
    Require(Hash(source) == hash, "unchanged QR source");
}

async Task ArchiveFormats()
{
    var folder = Folder("archives");
    var zip = Path.Combine(folder, "sample.zip");
    using (var stream = File.Create(zip))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    using (var content = new StreamWriter(archive.CreateEntry("Folder/Grüße.txt").Open()))
        content.Write("synthetic");
    var tar = Path.Combine(folder, "sample.tar");
    using (var stream = File.Create(tar))
    using (var writer = new TarWriter(stream))
    using (var bytes = new MemoryStream("synthetic"u8.ToArray()))
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "Folder/Grüße.txt") { DataStream = bytes });
    var gz = Path.Combine(folder, "plain.txt.gz");
    using (var stream = File.Create(gz))
    using (var gzip = new GZipStream(stream, CompressionMode.Compress))
        gzip.Write("synthetic"u8);
    var tgz = Path.Combine(folder, "sample.tar.gz");
    using (var stream = File.Create(tgz))
    using (var gzip = new GZipStream(stream, CompressionMode.Compress))
    using (var input = File.OpenRead(tar))
        input.CopyTo(gzip);
    foreach (var archive in new[] { zip, tar, gz, tgz })
    {
        var original = Hash(archive);
        var output = await Archives.ExtractAsync(archive, folder, new(), default);
        var child = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Single();
        Require(File.ReadAllText(child) == "synthetic" && Hash(archive) == original, $"valid {Path.GetExtension(archive)} result");
    }
    var rar = Path.Combine(repository, "tests", "fixtures", "entry.rar");
    var originalRar = Hash(rar);
    var rarOutput = await Archives.ExtractAsync(rar, folder, new(), default);
    var entry = Path.Combine(rarOutput, "helloworld.txt");
    Require(File.Exists(entry) && new FileInfo(entry).Length > 0 && Hash(rar) == originalRar, "RAR extraction");
}

async Task ArchiveGuards()
{
    var folder = Folder("archive-guards");
    foreach (var incorrectName in new[] { "NUL.txt", "trailing.", "invalid:name.txt" })
        await Expect<InvalidDataException>(() => Task.Run(() => OutputFiles.ValidateLeaf(incorrectName)));
    var zip = Path.Combine(folder, "data.zip");
    using (var stream = File.Create(zip))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    using (var output = archive.CreateEntry("entry.txt").Open())
        output.Write(new byte[16384]);
    await Expect<InvalidDataException>(() => Archives.ExtractAsync(zip, folder, new(FileBytes: 128), default));
    await Expect<IOException>(() => Archives.ExtractAsync(zip, folder, new(ReserveBytes: 1L << 60), default));
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Expect<OperationCanceledException>(() => Archives.ExtractAsync(zip, folder, new(), cancelled.Token));
    using var compressed = new MemoryStream();
    using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        gzip.Write("synthetic uncompressed content"u8);
    var truncated = Path.Combine(folder, "truncated.txt.gz");
    await File.WriteAllBytesAsync(truncated, compressed.ToArray()[..^5]);
    await Expect<InvalidDataException>(() => Archives.ExtractAsync(truncated, folder, new(), default));
    var collision = Path.Combine(folder, "case-collision.zip");
    using (var stream = File.Create(collision))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        archive.CreateEntry("Samples/Report.txt");
        archive.CreateEntry("samples/report.txt");
    }
    var collisionHash = Hash(collision);
    await Expect<InvalidDataException>(() => Archives.ExtractAsync(collision, folder, new(), default));
    Require(Hash(collision) == collisionHash, "colliding archive input remains unchanged");
    Require(!Directory.EnumerateDirectories(folder).Any(), "archive failure does not publish or leave staging");
}

async Task PdfRewrite()
{
    var folder = Folder("pdf");
    var source = CreatePdf(folder);
    var incremental = Path.Combine(folder, "incremental.pdf");
    File.Copy(source, incremental);
    await AppendPropertyRevision(incremental, qpdf);
    var incrementalHash = Hash(incremental);
    var original = Hash(source);
    var pdf = new Pdfs(qpdf);
    var info = await pdf.InspectAsync(source, null, default);
    Require(info.Pages == 2 && info.Properties.GetValueOrDefault("Author") == "SyntheticPropertiesAuthor", "property inspection");
    var cleaned = await pdf.PropertiesAsync(source, folder, new Dictionary<string, string>(), default);
    var cleanBytes = Encoding.Latin1.GetString(File.ReadAllBytes(cleaned));
    Require(!cleanBytes.Contains("SyntheticPropertiesAuthor") && !cleanBytes.Contains("/Prev"), "fresh revision and removed author bytes");
    Require((await pdf.InspectAsync(incremental, null, default)).Properties.GetValueOrDefault("Author") ==
        "SyntheticRevisionAuthor", "incremental property revision fixture");
    var cleanIncremental = await pdf.PropertiesAsync(incremental, folder, new Dictionary<string, string>(), default);
    var revisionBytes = Encoding.Latin1.GetString(File.ReadAllBytes(cleanIncremental));
    Require(!revisionBytes.Contains("SyntheticPropertiesAuthor") && !revisionBytes.Contains("SyntheticRevisionAuthor") &&
        !revisionBytes.Contains("/Prev") && Hash(incremental) == incrementalHash, "no prior Info revision in the rewritten bytes");
    var edited = await pdf.PropertiesAsync(source, folder, new Dictionary<string, string> { ["Title"] = "Updated Sample" }, default);
    Require((await pdf.InspectAsync(edited, null, default)).Properties.GetValueOrDefault("Title") == "Updated Sample", "updated property");
    var ordered = await pdf.AssembleAsync([new(source, 2, 90), new(source, 1), new(source, 2)], folder, "reordered", default);
    var inspection = PdfSharp.Pdf.IO.PdfReader.Open(ordered);
    Require(inspection.PageCount == 3 && inspection.Pages[0].Rotate == 90 &&
        inspection.Pages[1].Width.Point == 500, "independent page order, geometry and rotation");
    inspection.Dispose();
    using var rendered = await pdf.OpenForRenderingAsync(ordered, null, default);
    var image = await Pdfs.RenderPageAsync(rendered, 1, 96, ImageFormat.Jpeg, default);
    var path = await Images.SaveAsync(image, folder, "page", default);
    Require(Decode(path).PixelWidth > 100 && Hash(source) == original, "independent rendered JPEG and original hash");
}

async Task ThreeWheel()
{
    foreach (var neutral in new string?[] { null, "different" })
    {
        var session = new DropRelease();
        session.Release(neutral);
        Require(!session.TryCommit("JPG") && !session.TryCommit("JPG"), "invalid physical release latches cancellation");
    }
    var valid = new DropRelease(); valid.Release("JPG");
    Require(valid.TryCommit("JPG") && !valid.TryCommit("JPG"), "one matching native drop");
    var cancelled = new DropRelease(); cancelled.Cancel(); cancelled.Release("JPG");
    Require(!cancelled.TryCommit("JPG") && !new DropRelease().TryCommit("JPG"), "escape and early drop do nothing");
    var wheel = WheelCatalog.Shared;
    var folder = Folder("wheel-and-pdf");
    foreach (var sample in new[] { folder, "a.pdf", "a.png", "a.docx", "a.pptx", "a.zip", "a.xyz" })
        Require(wheel.For(WheelMode.Actions, [sample], false, false).Select(c => c.Id).SequenceEqual(
            new[] { "action.share", "action.float", "action.copy", "action.move", "action.path", "action.terminal" }), "fixed action order");
    Require(wheel.For(WheelMode.Convert, ["a.png"], false, false).Select(c => c.Label).SequenceEqual(new[] { "JPG", "PDF" }), "direct PNG targets");
    Require(wheel.For(WheelMode.Convert, ["a.docx"], false, false).Length == 0, "missing engine hidden");
    Require(wheel.For(WheelMode.Tools, ["a.pdf", "a.png"], false, false).Single().Id == "qr.read", "mixed intersection");
    var source = Path.Combine(repository, "tests/fixtures/navigation.pdf");
    var hash = Hash(source);
    var pdf = new Pdfs(qpdf);
    var result = await pdf.SplitAsync(source, [[3, 2], [1]], folder, false, default);
    using var keep = PdfSharp.Pdf.IO.PdfReader.Open(Path.Combine(result, "Part 0001.pdf"));
    using var omit = PdfSharp.Pdf.IO.PdfReader.Open(Path.Combine(result, "Part 0002.pdf"));
    Require(keep.PageCount == 2 && keep.Outlines.Count == 2 && omit.Outlines.Count == 1, "retained bookmarks only");
    Require(keep.Pages[0].Annotations.Count == 3 && omit.Pages[0].Annotations.Count == 3, "passive annotations retained");
    var retained = keep.Pages[0].Annotations[1].Elements.GetArray("/Dest");
    Require(retained != null && retained.Elements.GetObject(0) == keep.Pages[1], "internal link remapped");
    Require(!omit.Pages[0].Annotations[1].Elements.ContainsKey("/Dest") && !omit.Pages[0].Annotations[1].Elements.ContainsKey("/A"), "omitted target disabled");
    Require(keep.Pages[0].Annotations[0].Elements.GetDictionary("/A")?.Elements.GetString("/URI") == "https://example.org/", "retained URI");
    var joined = await pdf.AssembleAsync([new(source, 2, 90), new(Path.Combine(result, "Part 0002.pdf"), 1)], folder, "merged", default);
    using var merged = PdfSharp.Pdf.IO.PdfReader.Open(joined);
    Require(merged.PageCount == 2 && merged.Pages[0].Rotate == 90 && merged.Outlines.Count == 2, "cross-source merge");
    Require(Hash(source) == hash, "input preserved");
    using var stop = new CancellationTokenSource();
    await Expect<OperationCanceledException>(() => OutputGroup.WriteAsync(folder, "cancelled", 2, async (dir, index, ct) =>
    {
        var file = Path.Combine(dir, "page.png");
        await File.WriteAllTextAsync(file, "staging", ct);
        stop.Cancel();
        return file;
    }, stop.Token));
    Require(!Directory.EnumerateDirectories(folder, ".tabrilo-*").Any() && !Directory.Exists(Path.Combine(folder, "cancelled")), "group cleanup");
}

async Task OfficeLayouts(string engine)
{
    var folder = Folder("office");
    var pdf = new Pdfs(qpdf);
    var renderer = new OfficeRenderer(engine, pdf);
    foreach (var ext in new[] { "docx", "pptx" })
    {
        var source = Path.Combine(repository, "tests/fixtures/layout." + ext);
        var hash = Hash(source);
        await renderer.RenderAsync(source, async path =>
        {
            using var inspect = PdfSharp.Pdf.IO.PdfReader.Open(path);
            Require(inspect.PageCount == 2, ext + " page count");
            File.Copy(path, Path.Combine(folder, ext + ".pdf"));
            using var document = await pdf.OpenForRenderingAsync(path, null, default);
            for (var page = 1; page <= 2; page++)
            {
                var image = await Pdfs.RenderPageAsync(document, page, 150, page == 1 ? ImageFormat.Jpeg : ImageFormat.Png, default);
                var output = await Images.SaveAsync(image, folder, ext + "-" + page, default);
                Require(Decode(output).PixelWidth > 600, "independent page image decode");
            }
            return path;
        }, default);
        Require(Hash(source) == hash, "Office source preserved");
    }
}

async Task PdfCompress()
{
    var folder = Folder("pdf-compress");
    var pdf = new Pdfs(qpdf);
    var source = CreatePdf(folder);
    var expanded = Path.Combine(folder, "expanded.pdf");
    await ProcessExit(qpdf, [source, "--qdf", "--object-streams=disable", expanded]);
    var original = Hash(expanded);
    foreach (var lossy in new[] { false, true })
    {
        var compressed = await pdf.CompressAsync(expanded, folder, lossy, 70, default);
        using var decoded = PdfSharp.Pdf.IO.PdfReader.Open(compressed);
        Require(decoded.PageCount == 2 && decoded.Pages[0].Width.Point == 500 &&
            new FileInfo(compressed).Length < new FileInfo(expanded).Length, "compressed PDF stays structural");
    }
    Require(Hash(expanded) == original, "unchanged compressed PDF source");
}

async Task UpdateTrust()
{
    var folder = Folder("updates");
    var seed = RandomNumberGenerator.GetBytes(32);
    Ed25519.KeyPairFromSeed(out var key, out var secret, seed);
    try
    {
        var bytes = "synthetic installer"u8.ToArray();
        var signature = Convert.ToBase64String(Ed25519.Sign(bytes, secret));
        const string site = "https://updates.example.invalid";
        var feed = Encoding.UTF8.GetBytes($"""
            <rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle"><channel><item>
              <sparkle:channel>isolated-test</sparkle:channel><sparkle:minimumSystemVersion>10.0.19041</sparkle:minimumSystemVersion>
              <enclosure url="{site}/setup.exe" length="{bytes.Length}" sparkle:os="windows-x64"
                sparkle:version="0.2.0" sparkle:edSignature="{signature}" />
            </item></channel></rss>
            """);
        var signed = Encoding.ASCII.GetBytes(Convert.ToBase64String(Ed25519.Sign(feed, secret)));
        var bad = Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[64]));
        var config = new UpdateChannel(1, "isolated-test", "windows-x64", site + "/appcast.xml", site, Convert.ToBase64String(key));
        using var updates = new Updates(config, new Version(0, 1, 0), new Version(10, 0, 19045),
            new Responses(new Dictionary<string, byte[]> { ["/appcast.xml"] = feed, ["/appcast.xml.signature"] = signed,
                ["/setup.exe"] = "interrupted"u8.ToArray() }));
        var valid = await updates.CheckAsync(default);
        Require(valid?.Version == new Version(0, 2, 0), "signed isolated appcast");
        await Expect<InvalidDataException>(() => updates.DownloadAsync(valid!, folder, default));
        Require(!Directory.EnumerateFiles(Path.Combine(folder, "Updates")).Any(), "incomplete download cleanup");
        using var tampered = new Updates(config, new Version(0, 1, 0), new Version(10, 0, 19045),
            new Responses(new Dictionary<string, byte[]> { ["/appcast.xml"] = feed, ["/appcast.xml.signature"] = bad }));
        await Expect<InvalidDataException>(() => tampered.CheckAsync(default));
        using var downgrade = new Updates(config, new Version(0, 2, 1), new Version(10, 0, 19045),
            new Responses(new Dictionary<string, byte[]> { ["/appcast.xml"] = feed, ["/appcast.xml.signature"] = signed }));
        Require(await downgrade.CheckAsync(default) is null, "refuse downgrade");
    }
    finally { CryptographicOperations.ZeroMemory(seed); CryptographicOperations.ZeroMemory(secret); }
}

string Folder(string name)
{
    var folder = Path.Combine(evidence, name);
    Directory.CreateDirectory(folder);
    return folder;
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static void WriteBitmap(string path, int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> paint,
    bool metadata = false)
{
    var pixels = new byte[width * height * 4];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var values = paint(x, y);
        var index = (y * width + x) * 4;
        pixels[index] = values.B;
        pixels[index + 1] = values.G;
        pixels[index + 2] = values.R;
        pixels[index + 3] = values.A;
    }
    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    var encoder = new PngBitmapEncoder();
    var tags = metadata ? new BitmapMetadata("png") : null;
    tags?.SetQuery("/tEXt/{str=Title}", "SyntheticPrivateText");
    encoder.Frames.Add(BitmapFrame.Create(bitmap, null, tags, null));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static BitmapFrame Decode(string path)
{
    using var file = File.OpenRead(path);
    return BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Single();
}

static bool MetadataContains(BitmapFrame image, string text) =>
    image.Metadata is BitmapMetadata metadata && metadata.GetQuery("/tEXt/{str=Title}") as string == text;

static byte[] Pixel(BitmapSource image, int x, int y)
{
    var pixels = new byte[4];
    var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
    bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixels, 4, 0);
    return [pixels[2], pixels[1], pixels[0], pixels[3]];
}

static string CreatePdf(string folder)
{
    var source = Path.Combine(folder, "source.pdf");
    using var doc = new PdfSharp.Pdf.PdfDocument();
    doc.Options.CompressContentStreams = false;
    doc.Info.Author = "SyntheticPropertiesAuthor";
    doc.Info.Title = "Synthetic PDF";
    for (var index = 0; index < 2; index++)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(500 + index * 40);
        page.Height = XUnit.FromPoint(700);
        using var graphics = XGraphics.FromPdfPage(page);
        for (var i = 0; i < 300; i++) graphics.DrawRectangle(XBrushes.Teal, 30, 30 + i, 200, 10);
    }
    doc.Save(source);
    return source;
}

static async Task ProcessExit(string file, string[] arguments)
{
    var startup = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in arguments) startup.ArgumentList.Add(argument);
    using var process = Process.Start(startup)!;
    await process.WaitForExitAsync();
    Require(process.ExitCode == 0, $"{Path.GetFileName(file)} returned {process.ExitCode}");
}

static async Task AppendPropertyRevision(string file, string qpdf)
{
    var start = new ProcessStartInfo(qpdf) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    foreach (var argument in new[] { file, "--json=2", "--json-key=qpdf", "--json-object=trailer" }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    using var json = JsonDocument.Parse(await process.StandardOutput.ReadToEndAsync());
    await process.WaitForExitAsync();
    Require(process.ExitCode == 0, "qpdf inspected the test fixture");
    var trailer = json.RootElement.GetProperty("qpdf")[1].GetProperty("trailer").GetProperty("value");
    var root = trailer.GetProperty("/Root").GetString();
    var number = trailer.GetProperty("/Size").GetInt32();
    var original = await File.ReadAllBytesAsync(file);
    var previous = Regex.Matches(Encoding.Latin1.GetString(original), @"startxref\s+([0-9]+)\s+%%EOF")[^1].Groups[1].Value;

    // Append a normal Info revision to this synthetic fixture using its real object and xref offsets.
    var entry = $"\n{number} 0 obj\n<< /Author (SyntheticRevisionAuthor) >>\nendobj\n";
    var xref = original.Length + Encoding.ASCII.GetByteCount(entry);
    var revision = entry + $"xref\n{number} 1\n{original.Length + 1:D10} 00000 n \n" +
        $"trailer\n<< /Size {number + 1} /Root {root} /Info {number} 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n";
    await File.AppendAllTextAsync(file, revision, Encoding.ASCII);
}

internal sealed class Responses(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}
