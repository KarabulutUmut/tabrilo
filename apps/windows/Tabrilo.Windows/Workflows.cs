using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Tabrilo.Core;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal sealed class Workflows(Window owner, ViewState state, Func<KeyEventArgs, string, bool> keys,
    Action<string, IEnumerable<string>> publish)
{
    private sealed record PdfCompression(bool Lossy, int Quality);
    private sealed record PdfPages(int[] Pages, ImageFormat Format, int Dpi);
    private sealed record PdfSplit(int[] Pages, string Mode, int GroupSize);
    private readonly Pdfs pdf = new(Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe"));

    public async Task Execute(CommandSpec command, string[] inputs, string? preferredDirectory, CancellationToken token)
    {
        switch (command.Id)
        {
            case "image.info":
                var image = await Task.Run(() => Images.InspectAsync(inputs[0], token), token);
                var profiles = string.Join(", ", image.ProfileNames);
                Dialogs.Details(owner, command.Label,
                    $"{image.Format} · {image.Width} × {image.Height} px · {image.Size:N0} bytes\n" +
                    $"Orientation: {image.Orientation}\nColor: {image.ColorSpace}\nProfiles: {profiles}\n\n" +
                    string.Join('\n', image.Properties.Select(field => $"{field.Key}: {field.Value}")));
                return;
            case "pdf.info":
                var unlocked = await Unlock(inputs[0], token);
                if (unlocked is null) return;
                var info = unlocked.Value.Info;
                Dialogs.Details(owner, command.Label, $"{info.Pages} pages\nProtected: {info.Encrypted}\n" +
                    $"Digitally signed: {info.Signed}\nInteractive content: {info.Interactive}\n" +
                    $"Complex document structures: {info.Complex}\n\n" +
                    string.Join('\n', info.Properties.Select(field => $"{field.Key}: {field.Value}")));
                return;
            case "qr.read":
                await Scan(inputs, token);
                return;
        }

        if (command.Id == "image.pdf")
        {
            var arranged = ArrangeImages(inputs);
            if (arranged is null) return;
            var settings = Dialogs.Ask<ImagePdfOptions>(owner, command.Label, panel =>
            {
                var size = Controls.Choice(panel, "Page size", ["A4", "Letter", "A5"], "A4");
                var vertical = Controls.Choice(panel, "Orientation", ["Portrait", "Landscape"], "Portrait");
                var margin = Controls.Field(panel, "Margin in points", "24", 3);
                var fit = Controls.Choice(panel, "Fit", ["Contain", "Fill and crop"], "Contain");
                return () =>
                {
                    var (width, height) = (string)size.SelectedItem switch
                    {
                        "Letter" => (612.0, 792.0), "A5" => (419.53, 595.28), _ => (595.28, 841.89)
                    };
                    if ((string)vertical.SelectedItem == "Landscape") (width, height) = (height, width);
                    return new ImagePdfOptions(width, height, Controls.Number(margin, 0, 200, "Margin"),
                        (string)fit.SelectedItem == "Fill and crop");
                };
            }, "Choose export folder");
            if (settings is null || Folder(preferredDirectory, inputs) is not { } folder) return;
            state.Status = "Building PDF…";
            var output = await Task.Run(() => pdf.FromImagesAsync(arranged, folder, settings, token), token);
            Show(command.Label, [new(inputs[0], output)]);
            return;
        }

        if (command.Id.StartsWith("image.", StringComparison.Ordinal))
        {
            if (command.Id is "image.edit" or "image.annotate" or "image.frame" or "image.crop" or "image.redact" or "image.share"
                && inputs.Length != 1)
                throw new ArgumentException("Choose one image for the canvas editor.");
            ImageOptions request;
            ProcessedImage? ready = null;
            if (command.Id == "image.metadata")
            {
                var selected = Dialogs.Ask<ImageOptions>(owner, command.Label, panel =>
                {
                    panel.Children.Add(Controls.Text("Exports a fresh sRGB image. Embedded EXIF, XMP, IPTC, profiles and comments are removed."));
                    var formats = Controls.Choice(panel, "Output format", Enum.GetNames<ImageFormat>(), "Png");
                    return () => new ImageOptions(Enum.Parse<ImageFormat>((string)formats.SelectedItem), Lossless: true);
                }, "Choose export folder");
                if (selected is null) return;
                request = selected;
            }
            else
            {
                var original = await Task.Run(() => Images.ProcessAsync(inputs[0], new(ImageFormat.Png), token), token);
                var editor = new ImageEditor(owner, command.Label, command.Id, inputs[0], original, inputs.Length, keys);
                if (editor.ShowDialog() != true || editor.OutputOptions is not { } selected) return;
                request = selected;
                ready = editor.VerifiedPreview;
            }
            if (Folder(preferredDirectory, inputs) is not { } folder) return;
            var result = await Jobs.RunAsync(inputs, async (input, cancel) =>
            {
                var imageResult = ReferenceEquals(input, inputs[0]) && ready is not null ? ready :
                    await Images.ProcessAsync(input, request, cancel);
                var before = new FileInfo(input).Length;
                if (request.SmallerOnly && imageResult.Bytes.Length >= before)
                    return new(input, null, "No smaller output with these settings.");
                var output = await Images.SaveAsync(imageResult, folder, Pdfs.Stem(input, Suffix(command.Id)), cancel);
                return new(input, output, $"{before:N0} → {imageResult.Bytes.Length:N0} bytes");
            }, Progress(), token);
            Show(command.Label, result.Items);
            return;
        }

        if (command.Id == "archive.extract")
        {
            if (Folder(preferredDirectory, inputs) is not { } folder) return;
            var result = await Jobs.RunAsync(inputs,
                async (input, cancel) => new(input, await Archives.ExtractAsync(input, folder, new(), cancel)),
                Progress(), token);
            Show(command.Label, result.Items);
            return;
        }

        switch (command.Id)
        {
            case "pdf.compress": await CompressPdf(command.Label, inputs, preferredDirectory, token); break;
            case "pdf.metadata": await EditProperties(command.Label, inputs, preferredDirectory, token); break;
            case "pdf.organize":
            case "pdf.merge": await ArrangePdf(command.Label, inputs, preferredDirectory, token); break;
            case "pdf.split": await SplitPdf(command.Label, inputs, preferredDirectory, token); break;
            case "pdf.images": await ExportPages(command.Label, inputs, preferredDirectory, token); break;
        }
    }

    private async Task CompressPdf(string label, string[] inputs, string? directory, CancellationToken token)
    {
        var request = Dialogs.Ask<PdfCompression>(owner, label, panel =>
        {
            var mode = Controls.Choice(panel, "Compression method", ["Conservative", "Lossy image recompression"], "Conservative");
            var quality = Controls.Field(panel, "Lossy image quality", "75", 3);
            panel.Children.Add(Controls.Text("Both modes keep PDF pages as documents. Lossy mode may alter embedded image quality."));
            return () => new PdfCompression((string)mode.SelectedItem != "Conservative", Controls.Number(quality, 1, 100, "Quality"));
        });
        if (request is null || Folder(directory, inputs) is not { } folder) return;
        var result = await Jobs.RunAsync(inputs, async (input, cancel) =>
        {
            var output = await pdf.CompressAsync(input, folder, request.Lossy, request.Quality, cancel);
            return new(input, output, $"{new FileInfo(input).Length:N0} → {new FileInfo(output).Length:N0} bytes");
        }, Progress(), token);
        Show(label, result.Items);
    }

    private async Task EditProperties(string label, string[] inputs, string? directory, CancellationToken token)
    {
        RequireOne(inputs);
        var info = await pdf.InspectAsync(inputs[0], null, token);
        var request = Dialogs.Ask<Dictionary<string, string>>(owner, label, panel =>
        {
            panel.Children.Add(Controls.Text("Rewrites document properties and removes document XMP. Comments, attachments and page content remain."));
            var clear = Controls.Checkbox(panel, "Remove all document properties", false);
            var fields = new[] { "Title", "Author", "Subject", "Keywords", "Creator" }
                .ToDictionary(key => key, key => Controls.Field(panel, key, info.Properties.GetValueOrDefault(key, ""), 4096));
            return () => clear.IsChecked == true ? new Dictionary<string, string>() :
                fields.ToDictionary(field => field.Key, field => field.Value.Text);
        });
        if (request is null || Folder(directory, inputs) is not { } folder) return;
        state.Status = "Rewriting PDF properties…";
        var output = await pdf.PropertiesAsync(inputs[0], folder, request, token);
        Show(label, [new(inputs[0], output)]);
    }

    private async Task ArrangePdf(string label, string[] inputs, string? directory, CancellationToken token)
    {
        var pages = await PageChoices(inputs);
        string? currentPath = null;
        global::Windows.Data.Pdf.PdfDocument? current = null;
        var arranged = Dialogs.ArrangePages(owner, label, pages, keys, () =>
        {
            var picker = new OpenFileDialog { Filter = "PDF documents|*.pdf", Multiselect = true };
            return picker.ShowDialog(owner) == true ? picker.FileNames : null;
        }, PageChoices, async (page, cancel) =>
        {
            var doc = current;
            if (doc is null || currentPath != page.Path)
            {
                doc = await pdf.OpenForRenderingAsync(page.Path, null, cancel);
                cancel.ThrowIfCancellationRequested();
                currentPath = page.Path;
                current = doc;
            }
            return await Pdfs.RenderPageAsync(doc, page.Number, 48, ImageFormat.Png, cancel);
        });
        if (arranged is null || Folder(directory, inputs) is not { } folder) return;
        state.Status = "Assembling PDF…";
        var output = await pdf.AssembleAsync(arranged, folder, Pdfs.Stem(inputs[0], "pages"), token);
        Show(label, [new(inputs[0], output)]);

        async Task<PdfPageChoice[]> PageChoices(string[] paths)
        {
            var result = new List<PdfPageChoice>();
            foreach (var path in paths)
            {
                var info = await pdf.InspectAsync(path, null, token);
                if (info.Signed || info.Encrypted || info.Interactive || info.Complex)
                    throw new NotSupportedException("A selected PDF has protection or structures that page assembly cannot preserve.");
                if (result.Count + info.Pages > 2000) throw new ArgumentException("Choose up to 2000 pages.");
                result.AddRange(Enumerable.Range(1, info.Pages).Select(page => new PdfPageChoice(path, page)));
            }
            return result.ToArray();
        }
    }

    private async Task SplitPdf(string label, string[] inputs, string? directory, CancellationToken token)
    {
        RequireOne(inputs);
        var input = inputs[0];
        var info = await pdf.InspectAsync(input, null, token);
        var request = Dialogs.Ask<PdfSplit>(owner, label, panel =>
        {
            var range = Controls.Field(panel, $"Pages (1–{info.Pages})", "all");
            var mode = Controls.Choice(panel, "Output", ["Single PDF", "One file per page", "Fixed-size groups"], "Single PDF");
            var size = Controls.Field(panel, "Pages per group", "2", 4);
            return () => new PdfSplit(Dialogs.ParsePages(range.Text, info.Pages), (string)mode.SelectedItem,
                Controls.Number(size, 1, 2000, "Pages per group"));
        });
        if (request is null || Folder(directory, inputs) is not { } folder) return;
        var groupSize = request.Mode switch { "Single PDF" => request.Pages.Length, "One file per page" => 1, _ => request.GroupSize };
        var groups = request.Pages.Chunk(groupSize).ToArray();
        var choices = groups.Select((pages, index) => (pages, index)).ToDictionary(
            item => $"{Path.GetFileNameWithoutExtension(input)} part {item.index + 1}", item => item.pages);
        var result = await Jobs.RunAsync(choices.Keys.ToArray(), async (part, cancel) =>
        {
            var output = await pdf.AssembleAsync(choices[part].Select(page => new PdfPageChoice(input, page)).ToArray(),
                folder, part, cancel);
            return new(part, output);
        }, Progress(), token);
        Show(label, result.Items);
    }

    private async Task ExportPages(string label, string[] inputs, string? directory, CancellationToken token)
    {
        RequireOne(inputs);
        var input = inputs[0];
        var unlocked = await Unlock(input, token);
        if (unlocked is null) return;
        var options = AskPages(label, unlocked.Value.Info.Pages, qr: false);
        if (options is null || Folder(directory, inputs) is not { } folder) return;
        var document = await pdf.OpenForRenderingAsync(input, unlocked.Value.Password, token);
        var names = options.Pages.ToDictionary(page => $"{Path.GetFileNameWithoutExtension(input)} page {page}", page => page);
        var result = await Jobs.RunAsync(names.Keys.ToArray(), async (name, cancel) =>
        {
            var page = await Pdfs.RenderPageAsync(document, names[name], options.Dpi, options.Format, cancel);
            return new(name, await Images.SaveAsync(page, folder, name, cancel));
        }, Progress(), token);
        Show(label, result.Items);
    }

    private async Task Scan(string[] inputs, CancellationToken token)
    {
        var results = new List<(string Name, QrResult Result)>();
        var errors = new List<string>();
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(input);
            try
            {
                state.Status = $"Scanning {name}…";
                if (FileKinds.Classify(input) == "pdf")
                {
                    var unlocked = await Unlock(input, token);
                    if (unlocked is null) { errors.Add($"{name} · Password entry cancelled."); continue; }
                    var options = AskPages($"Read QR · {name}", unlocked.Value.Info.Pages, qr: true);
                    if (options is null) { errors.Add($"{name} · Page selection cancelled."); continue; }
                    var doc = await pdf.OpenForRenderingAsync(input, unlocked.Value.Password, token);
                    foreach (var page in options.Pages)
                    {
                        token.ThrowIfCancellationRequested();
                        state.Status = $"Scanning {name} · page {page}…";
                        var scan = await Task.Run(async () =>
                        {
                            var rendered = await Pdfs.RenderPageAsync(doc, page, options.Dpi, ImageFormat.Png, token);
                            return Qr.ScanRenderedPage(rendered, page, token);
                        }, token);
                        results.AddRange(scan.Select(item => (name, item)));
                    }
                }
                else
                {
                    var scan = await Task.Run(() => Qr.ScanImageAsync(input, token), token);
                    results.AddRange(scan.Select(item => (name, item)));
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            { errors.Add($"{name} · {error.Message}"); }
        }
        if (errors.Count > 0) Dialogs.Details(owner, "QR scan issues", string.Join('\n', errors));
        if (results.Count > 0 || errors.Count == 0) Dialogs.QrResults(owner, results);
        state.Status = $"{results.Count} QR results · {errors.Count} files with issues.";
    }

    private PdfPages? AskPages(string label, int total, bool qr) => Dialogs.Ask<PdfPages>(owner, label, panel =>
    {
        var pages = Controls.Field(panel, $"Pages (1–{total})", "all");
        var dpi = Controls.Field(panel, "Rendering resolution in DPI", qr ? "144" : "150", 3);
        var format = qr ? null : Controls.Choice(panel, "Output format", ["Png", "Jpeg"], "Png");
        return () => new PdfPages(Dialogs.ParsePages(pages.Text, total),
            format is null ? ImageFormat.Png : Enum.Parse<ImageFormat>((string)format.SelectedItem),
            Controls.Number(dpi, 48, 300, "Rendering resolution"));
    }, qr ? "Scan" : "Continue");

    private async Task<(PdfInfo Info, string? Password)?> Unlock(string path, CancellationToken token)
    {
        try { return (await pdf.InspectAsync(path, null, token), null); }
        catch (PdfPasswordException)
        {
            var password = Dialogs.Password(owner);
            return password is null ? null : (await pdf.InspectAsync(path, password, token), password);
        }
    }

    private string[]? ArrangeImages(string[] inputs) => Dialogs.Ask<string[]>(owner, "Arrange images", panel =>
    {
        var paths = inputs.ToList();
        var list = new ListBox { Height = 210 };
        Controls.AddLabel(panel, "Image order", list);
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        actions.Children.Add(Controls.Button("Earlier", () => Move(-1)));
        actions.Children.Add(Controls.Button("Later", () => Move(1)));
        Refresh(0);
        return paths.ToArray;

        void Refresh(int at) { list.ItemsSource = paths.Select(Path.GetFileName).ToArray(); list.SelectedIndex = at; }
        void Move(int offset)
        {
            var index = list.SelectedIndex;
            if (index < 0 || index + offset < 0 || index + offset >= paths.Count) return;
            (paths[index], paths[index + offset]) = (paths[index + offset], paths[index]);
            Refresh(index + offset);
        }
    });

    private Progress<JobProgress> Progress() => new(progress =>
        state.Status = $"{progress.Completed}/{progress.Total} · {progress.Label}");

    private void Show(string label, IReadOnlyList<FileOutcome> outcomes)
    {
        var paths = outcomes.Where(item => item.Output is not null).Select(item => item.Output!).ToArray();
        if (paths.Length > 0) publish(label, paths);
        var title = $"{paths.Length} of {outcomes.Count} published";
        state.Status = title + (paths.Length == 1 ? $" · {paths[0]}" : "");
        var panel = new StackPanel { Margin = new Thickness(18) };
        var dialog = Dialogs.WindowFor(owner, label, panel, 710, 530);
        Controls.Heading(panel, title);
        foreach (var result in outcomes)
        {
            Controls.Heading(panel, Path.GetFileName(result.Input), 15);
            if (result.Message is { } message) panel.Children.Add(Controls.Text(message, 13, secondary: true));
            if (result.Output is not { } output) continue;
            panel.Children.Add(Controls.Text(output, 12, secondary: true));
            var actions = new WrapPanel();
            actions.Children.Add(Controls.Button("Open", () => Desktop.OpenResult(output)));
            actions.Children.Add(Controls.Button("Reveal", () => Desktop.Reveal(output)));
            panel.Children.Add(actions);
        }
        panel.Children.Add(Controls.Button("Done", dialog.Close, true));
        dialog.ShowDialog();
    }

    private string? Folder(string? preferred, string[] inputs)
    {
        var folder = preferred ?? Path.GetDirectoryName(inputs[0]);
        var picker = new OpenFolderDialog { Title = "Choose export folder", InitialDirectory = Directory.Exists(folder) ? folder! : "" };
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }

    private static void RequireOne(string[] inputs)
    {
        if (inputs.Length != 1) throw new ArgumentException("Choose one PDF for this operation.");
    }

    private static string Suffix(string command) => command switch
    {
        "image.metadata" => "clean", "image.compress" => "compressed", "image.redact" => "redacted",
        "image.share" => "share", "image.crop" => "cropped", "image.frame" => "framed",
        "image.annotate" => "annotated", "image.edit" => "edited", _ => "converted"
    };
}
