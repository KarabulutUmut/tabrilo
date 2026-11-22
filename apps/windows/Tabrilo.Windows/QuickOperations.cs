using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Tabrilo.Core;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal sealed class QuickOperations(OperationContext context, Window? owner, Preferences preferences,
    OperationDestination destination, Func<JobResults.JobView> start, Action<IReadOnlyList<FileOutcome>> publish)
{
    private const int DefaultQrPages = 24;
    private const int BatchQrPages = 96;
    private readonly Pdfs pdf = new(Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe"));
    private sealed record FormatChoice(ImageFormat Format, bool ChangeDestination);
    private sealed record PresetChoice(int Index, bool ChangeDestination);
    private sealed record CompressionChoice(ImageOptions? Advanced, bool Gentle, bool ChangeDestination);
    private sealed record PdfCompression(int Quality);

    public static bool Handles(string command) => command is "image.convert" or "image.compress" or "image.metadata"
        or "pdf.compress" or "archive.extract" or "qr.read" or "convert.pdf" or "convert.png" or "convert.jpg" or "convert.webp";

    public async Task Execute(Presentation presentation)
    {
        var token = context.Cancellation;
        var id = context.Command.Id;
        if (id.StartsWith("convert.", StringComparison.Ordinal))
        {
            await ConvertFiles(id);
            return;
        }
        if (id == "qr.read")
        {
            await Scan(presentation == Presentation.Choices);
            return;
        }

        Func<string, ImageOptions>? imageOptions = null;
        var changeDestination = context.Request.ChooseDestination;
        PdfCompression? advancedPdf = null;
        switch (id)
        {
            case "image.convert":
                var format = ChooseFormat();
                if (format is null) return;
                changeDestination |= format.ChangeDestination;
                imageOptions = _ => ConvertTo(format.Format);
                break;
            case "image.compress":
                if (!context.Request.Advanced) { imageOptions = input => CompressImage(input, false); break; }
                var compression = ChooseCompression();
                if (compression is null) return;
                changeDestination |= compression.ChangeDestination;
                imageOptions = input => compression.Advanced ?? CompressImage(input, compression.Gentle);
                break;
            case "image.metadata":
                imageOptions = input => NativeFormat(input) switch
                {
                    ImageFormat.Jpeg => new(ImageFormat.Jpeg, 95),
                    ImageFormat.WebP => new(ImageFormat.WebP, Lossless: true),
                    _ => new(ImageFormat.Png, Lossless: true)
                };
                break;
            case "pdf.compress" when presentation == Presentation.Choices:
                advancedPdf = Dialogs.Ask<PdfCompression>(owner, "Lossy PDF compression", panel =>
                {
                    panel.Children.Add(Controls.Text("Recompress embedded pictures. Text and vector pages stay searchable and scalable."));
                    var quality = Controls.Field(panel, "Image quality", "75", 3);
                    return () => new(Controls.Number(quality, 1, 100, "Quality"));
                }, "Compress", height: 270);
                if (advancedPdf is null) return;
                break;
        }
        var view = start();
        var folders = await destination.Separate(changeDestination);
        var paths = context.Inputs.Select(item => item.Path).ToArray();
        var result = await Jobs.RunAsync(paths, async (input, cancel) =>
        {
            var folder = folders[input];
            if (id == "archive.extract")
                return new(input, await Archives.ExtractAsync(input, folder, new(), cancel));
            var before = new FileInfo(input).Length;
            if (id == "pdf.compress")
            {
                var output = await pdf.CompressAsync(input, folder, advancedPdf is not null, advancedPdf?.Quality ?? 75, cancel);
                return new(input, output, Size(before, new FileInfo(output).Length));
            }
            var options = imageOptions!(input);
            var image = await Images.ProcessAsync(input, options, cancel);
            if (options.SmallerOnly && image.Bytes.Length >= before)
                return new(input, null, $"No smaller copy ({Size(before, image.Bytes.Length)}).");
            var suffix = id switch { "image.metadata" => "clean", "image.compress" => "compressed", _ => "converted" };
            var saved = await Images.SaveAsync(image, folder, Pdfs.Stem(input, suffix), cancel);
            var description = id == "image.metadata" ? " · Fresh sRGB copy with embedded personal metadata removed." : "";
            return new(input, saved, Size(before, image.Bytes.Length) + description);
        }, new Progress<JobProgress>(view.Update), token);
        publish(result.Items);
    }

    private async Task ConvertFiles(string id)
    {
        var paths = context.Inputs.Select(item => item.Path).ToArray();
        var token = context.Cancellation;
        var view = start();
        if (id == "convert.pdf" && paths.All(path => FileKinds.Classify(path) == "image"))
        {
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);
            var folder = await destination.Combined();
            var result = await Task.Run(() => pdf.FromImagesAsync(paths, folder, new(), token,
                Path.GetFileNameWithoutExtension(paths[0])), token);
            publish([new(paths[0], result)]);
            return;
        }

        var folders = await destination.Separate();
        var format = id switch { "convert.jpg" => ImageFormat.Jpeg, "convert.webp" => ImageFormat.WebP, _ => ImageFormat.Png };
        var outcomes = await Jobs.RunAsync(paths, async (input, cancel) =>
        {
            var stem = Path.GetFileNameWithoutExtension(input);
            var folder = folders[input];
            if (FileKinds.Classify(input) == "image")
            {
                var image = await Images.ProcessAsync(input, ConvertTo(format), cancel);
                return new(input, await Images.SaveAsync(image, folder, stem, cancel));
            }
            async Task<string> ExportPdf(string source)
            {
                if (id == "convert.pdf")
                    return await OutputFiles.WriteAsync(folder, stem, ".pdf",
                        (path, ct) => { ct.ThrowIfCancellationRequested(); File.Copy(source, path, true); return Task.CompletedTask; },
                        async (path, ct) => { await pdf.InspectAsync(path, null, ct); }, cancel);
                using var document = await pdf.OpenForRenderingAsync(source, null, cancel);
                var count = checked((int)document.PageCount);
                async Task<string> Page(string output, int index, CancellationToken ct)
                {
                    var pixels = await Pdfs.RenderPageAsync(document, index + 1, 150, format, ct);
                    return await Images.SaveAsync(pixels, output, count == 1 ? stem : $"Page {index + 1:D4}", ct);
                }
                return count == 1 ? await Page(folder, 0, cancel) :
                    await OutputGroup.WriteAsync(folder, stem + " pages", count, Page, cancel);
            }
            if (FileKinds.Classify(input) == "office")
            {
                var engine = new OfficeRenderer(preferences.OfficeExecutable, pdf);
                var output = await engine.RenderAsync(input, ExportPdf, cancel);
                return new(input, output, "Static layout. Check fonts, tables and pagination in the result.");
            }
            return new(input, await ExportPdf(input));
        }, new Progress<JobProgress>(view.Update), token);
        publish(outcomes.Items);
    }

    private FormatChoice? ChooseFormat() => Dialogs.Ask<FormatChoice>(owner, "Convert image", panel =>
    {
        panel.Children.Add(Controls.Text("JPEG fills transparent areas with white.", 13, true));
        var change = Controls.Checkbox(panel, "Change destination", false);
        var actions = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(actions);
        FormatChoice? choice = null;
        foreach (var (format, label) in new[] { (ImageFormat.Png, "PNG"), (ImageFormat.Jpeg, "JPEG"), (ImageFormat.WebP, "WebP") })
        {
            var option = Controls.Button(label, () =>
            {
                choice = new(format, change.IsChecked == true);
                if (Window.GetWindow(actions) is { } window) window.DialogResult = true;
            }, format == ImageFormat.Jpeg);
            option.IsDefault = format == ImageFormat.Jpeg;
            actions.Children.Add(option);
        }
        return () => choice!;
    }, button: "", height: 245);

    private CompressionChoice? ChooseCompression()
    {
        var selection = Dialogs.Ask<PresetChoice>(owner, "Compress image", panel =>
        {
            var preset = Controls.Choice(panel, "Preset",
                ["Balanced · JPEG/WebP quality 82, PNG lossless", "Gentle · JPEG/WebP quality 92, PNG lossless", "Advanced…"],
                "Balanced · JPEG/WebP quality 82, PNG lossless");
            panel.Children.Add(Controls.Text("Keeps JPEG, WebP and PNG in their format. Other inputs use PNG. Publishes only a smaller copy.", 13, true));
            var change = Controls.Checkbox(panel, "Change destination", false);
            return () => new(preset.SelectedIndex, change.IsChecked == true);
        }, "Compress", height: 310);
        if (selection is null) return null;
        if (selection.Index != 2) return new(null, selection.Index == 1, selection.ChangeDestination);
        var options = Dialogs.Ask<ImageOptions>(owner, "Advanced image compression", panel =>
        {
            var format = Controls.Choice(panel, "Output format", ["JPEG", "PNG", "WebP"], "JPEG");
            var quality = Controls.Field(panel, "Quality", "82", 3);
            var edge = Controls.Field(panel, "Maximum edge in pixels (0 keeps original)", "0", 5);
            var lossless = Controls.Checkbox(panel, "Use lossless WebP", false);
            panel.Children.Add(Controls.Text("JPEG is lossy and fills transparent areas with white.", 13, true));
            return () => new(new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP }[format.SelectedIndex],
                Controls.Number(quality, 1, 100, "Quality"), lossless.IsChecked == true, SmallerOnly: true,
                Edits: new(MaxEdge: (uint)Controls.Number(edge, 0, 16384, "Maximum edge")));
        }, "Compress", height: 435);
        return options is null ? null : new(options, false, selection.ChangeDestination);
    }

    private async Task Scan(bool choosePages)
    {
        var token = context.Cancellation;
        var view = start();
        var codes = new List<(string Name, QrResult Result)>();
        var notes = new List<string>();
        var remaining = BatchQrPages;
        try
        {
            for (var index = 0; index < context.Inputs.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var input = context.Inputs[index].Path;
                var name = Path.GetFileName(input);
                view.Update(new(index, context.Inputs.Count, $"Scanning {name}…"));
                try
                {
                    if (FileKinds.Classify(input) != "pdf")
                    {
                        var found = await Task.Run(() => Qr.ScanImageAsync(input, token), token);
                        codes.AddRange(found.Select(item => (name, item)));
                        continue;
                    }
                    if (remaining == 0) { notes.Add($"{name} · Reached the {BatchQrPages}-page batch limit."); continue; }
                    string? password = null;
                    PdfInfo info;
                    try { info = await Task.Run(() => pdf.InspectAsync(input, null, token), token); }
                    catch (PdfPasswordException)
                    {
                        password = Dialogs.Password(owner);
                        if (password is null) { notes.Add($"{name} · Password entry cancelled."); continue; }
                        info = await Task.Run(() => pdf.InspectAsync(input, password, token), token);
                    }
                    var limit = Math.Min(DefaultQrPages, remaining);
                    var pages = Enumerable.Range(1, Math.Min(info.Pages, limit)).ToArray();
                    if (choosePages)
                    {
                        var chosen = Dialogs.Ask<int[]>(owner, $"Scan pages · {name}", panel =>
                        {
                            var range = Controls.Field(panel, $"Pages (1–{info.Pages}, choose up to {limit})", $"1-{pages.Length}");
                            return () => Dialogs.ParsePages(range.Text, info.Pages) is { } parsed && parsed.Length <= limit
                                ? parsed : throw new ArgumentException($"Choose up to {limit} pages.");
                        }, "Scan", height: 265);
                        if (chosen is null) { notes.Add($"{name} · Page selection cancelled."); continue; }
                        pages = chosen;
                    }
                    if (pages.Length < info.Pages) notes.Add($"{name} · Scanning {pages.Length} of {info.Pages} pages.");
                    using var doc = await pdf.OpenForRenderingAsync(input, password, token);
                    foreach (var page in pages)
                    {
                        token.ThrowIfCancellationRequested();
                        remaining--;
                        view.Update(new(index, context.Inputs.Count, $"{name} · page {page}"));
                        var found = await Task.Run(async () =>
                        {
                            var rendered = await Pdfs.RenderPageAsync(doc, page, 144, ImageFormat.Png, token);
                            return Qr.ScanRenderedPage(rendered, page, token);
                        }, token);
                        codes.AddRange(found.Select(item => (name, item)));
                    }
                }
                catch (Exception error) when (OperationCoordinator.IsOperationError(error))
                { notes.Add($"{name} · {OperationCoordinator.ErrorMessage(error)}"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { notes.Add("Scan cancelled. Results from completed pages or files are shown."); }
        view.Codes(codes, notes);
    }

    private ImageOptions ConvertTo(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => new(format, preferences.JpegQuality),
        ImageFormat.WebP => new(format, preferences.WebPQuality),
        _ => new(format, Lossless: true)
    };

    private static ImageOptions CompressImage(string input, bool gentle)
    {
        var format = NativeFormat(input);
        return new(format, gentle ? 92 : 82, format == ImageFormat.Png, SmallerOnly: true);
    }

    private static ImageFormat NativeFormat(string input) => Path.GetExtension(input).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".webp" => ImageFormat.WebP,
        _ => ImageFormat.Png
    };

    private static string Size(long before, long after) => $"{before:N0} → {after:N0} bytes";
}
