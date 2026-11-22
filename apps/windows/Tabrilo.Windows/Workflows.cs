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

internal sealed class Workflows(Window? owner, Func<KeyEventArgs, string, bool> keys,
    OperationDestination destination, Action<JobProgress> report, Action<IReadOnlyList<FileOutcome>> publish)
{
    private sealed record PdfPages(int[] Pages, ImageFormat Format, int Dpi);
    private sealed record PdfSplit(int[] Pages, string Mode, int GroupSize);
    private readonly Pdfs pdf = new(Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe"));

    public async Task Execute(CommandSpec command, string[] inputs, CancellationToken token)
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
            }, "Build PDF");
            if (settings is null) return;
            var folder = await destination.Combined();
            report(new(0, 0, "Building PDF…"));
            var output = await Task.Run(() => pdf.FromImagesAsync(arranged, folder, settings, token), token);
            publish([new(inputs[0], output)]);
            return;
        }

        if (command.Id.StartsWith("image.", StringComparison.Ordinal))
        {
            if (inputs.Length != 1) throw new ArgumentException("Choose one image for the canvas editor.");
            // Keep the file shown by the editor stable until the user saves or closes the session.
            var sharing = destination.IsClipboard(inputs[0]) ? FileShare.Read | FileShare.Delete : FileShare.Read;
            using var sourceLease = await Task.Run(() => new FileStream(inputs[0], FileMode.Open, FileAccess.Read, sharing), token);
            var original = await Task.Run(() => Images.ReadEditorPreviewAsync(inputs[0], token), token);
            var editor = new ImageEditor(owner is { IsVisible: true, WindowState: not WindowState.Minimized } ? owner : null,
                command.Label, command.Id, inputs[0], original, keys, token);
            if (editor.ShowDialog() != true || editor.OutputOptions is not { } request) return;
            var folders = await destination.Separate(editor.ChangeDestination);
            var ready = editor.VerifiedPreview;
            var result = await Jobs.RunAsync(inputs, async (input, cancel) =>
            {
                var image = ready ?? await Images.ProcessAsync(input, request, cancel);
                var before = new FileInfo(input).Length;
                var output = await Images.SaveAsync(image, folders[input], Pdfs.Stem(input, Suffix(command.Id)), cancel);
                return new(input, output, $"{before:N0} → {image.Bytes.Length:N0} bytes");
            }, Progress(), token);
            publish(result.Items);
            return;
        }

        switch (command.Id)
        {
            case "pdf.metadata": await EditProperties(command.Label, inputs, token); break;
            case "pdf.organize":
            case "pdf.merge": await ArrangePdf(command.Label, inputs, token); break;
            case "pdf.split": await SplitPdf(command.Label, inputs, token); break;
            case "pdf.images": await ExportPages(command.Label, inputs, token); break;
        }
    }

    private async Task EditProperties(string label, string[] inputs, CancellationToken token)
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
        if (request is null) return;
        var folder = await destination.Combined();
        report(new(0, 0, "Rewriting PDF properties…"));
        var output = await pdf.PropertiesAsync(inputs[0], folder, request, token);
        publish([new(inputs[0], output)]);
    }

    private async Task ArrangePdf(string label, string[] inputs, CancellationToken token)
    {
        var pages = await PageChoices(inputs);
        var arranged = Dialogs.ArrangePages(owner, label, pages, keys, () =>
        {
            var picker = new OpenFileDialog { Filter = "PDF documents|*.pdf", Multiselect = true };
            return (owner is { IsVisible: true } ? picker.ShowDialog(owner) : picker.ShowDialog()) == true ? picker.FileNames : null;
        }, PageChoices, async (page, cancel) =>
        {
            using var doc = await pdf.OpenForRenderingAsync(page.Path, null, cancel);
            return await Pdfs.RenderPageAsync(doc, page.Number, 48, ImageFormat.Png, cancel);
        });
        if (arranged is null) return;
        var folder = await destination.Combined();
        report(new(0, 0, "Assembling PDF…"));
        var output = await pdf.AssembleAsync(arranged, folder, Pdfs.Stem(inputs[0], "pages"), token, allowPageExtrasLoss: true);
        publish([new(inputs[0], output)]);

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

    private async Task SplitPdf(string label, string[] inputs, CancellationToken token)
    {
        RequireOne(inputs);
        var input = inputs[0];
        var info = await pdf.InspectAsync(input, null, token);
        var request = Dialogs.Ask<PdfSplit>(owner, label, panel =>
        {
            panel.Children.Add(Controls.Text(AssemblyNotice, 13, true));
            var range = Controls.Field(panel, $"Pages (1–{info.Pages})", "all");
            var mode = Controls.Choice(panel, "Output", ["Single PDF", "One file per page", "Fixed-size groups"], "Single PDF");
            var size = Controls.Field(panel, "Pages per group", "2", 4);
            return () => new PdfSplit(Dialogs.ParsePages(range.Text, info.Pages), (string)mode.SelectedItem,
                Controls.Number(size, 1, 2000, "Pages per group"));
        });
        if (request is null) return;
        var folder = await destination.Combined();
        var groupSize = request.Mode switch { "Single PDF" => request.Pages.Length, "One file per page" => 1, _ => request.GroupSize };
        var groups = request.Pages.Chunk(groupSize).ToArray();
        report(new(0, groups.Length, "Splitting pages…"));
        var output = await Task.Run(() => pdf.SplitAsync(input, groups, folder, true, token), token);
        publish([new(input, output)]);
    }

    internal const string AssemblyNotice = "Keeps page content, passive annotations and links to retained pages. " +
        "Page bookmarks become a flat list. Links to omitted pages lose their target. Document tags, name indexes and initial view are omitted.";

    private async Task ExportPages(string label, string[] inputs, CancellationToken token)
    {
        RequireOne(inputs);
        var input = inputs[0];
        var unlocked = await Unlock(input, token);
        if (unlocked is null) return;
        var options = AskPages(label, unlocked.Value.Info.Pages);
        if (options is null) return;
        var folder = await destination.Combined();
        using var document = await pdf.OpenForRenderingAsync(input, unlocked.Value.Password, token);
        var names = options.Pages.ToDictionary(page => $"{Path.GetFileNameWithoutExtension(input)} page {page}", page => page);
        var result = await Jobs.RunAsync(names.Keys.ToArray(), async (name, cancel) =>
        {
            var page = await Pdfs.RenderPageAsync(document, names[name], options.Dpi, options.Format, cancel);
            return new(name, await Images.SaveAsync(page, folder, name, cancel));
        }, Progress(), token);
        publish(result.Items);
    }

    private PdfPages? AskPages(string label, int total) => Dialogs.Ask<PdfPages>(owner, label, panel =>
    {
        var pages = Controls.Field(panel, $"Pages (1–{total})", "all");
        var dpi = Controls.Field(panel, "Rendering resolution in DPI", "150", 3);
        var format = Controls.Choice(panel, "Output format", ["Png", "Jpeg"], "Png");
        return () => new PdfPages(Dialogs.ParsePages(pages.Text, total),
            Enum.Parse<ImageFormat>((string)format.SelectedItem),
            Controls.Number(dpi, 48, 300, "Rendering resolution"));
    }, "Continue");

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

    private Progress<JobProgress> Progress() => new(report);

    private static void RequireOne(string[] inputs)
    {
        if (inputs.Length != 1) throw new ArgumentException("Choose one PDF for this operation.");
    }

    private static string Suffix(string command) => command switch
    {
        "image.redact" => "redacted",
        "image.share" => "share", "image.crop" => "cropped", "image.frame" => "framed",
        "image.annotate" => "annotated", "image.edit" => "edited", _ => "converted"
    };
}
