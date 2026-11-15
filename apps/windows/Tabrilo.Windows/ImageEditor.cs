using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal sealed class ImageEditor : Window
{
    private sealed record Selection(PixelRect? Crop, PixelRect[] Redactions, ImageMark[] Marks);
    private sealed record RegionRow(string Kind, int Index, string Label);
    private sealed record EditState(Selection Selection, object?[] Settings);
    private readonly Stack<EditState> undo = new();
    private readonly Stack<EditState> redo = new();
    private Selection selection = new(null, [], []);
    private EditState? committed;
    private bool restoring;
    private Control[] editFields = [];
    private readonly ProcessedImage original;
    private readonly BitmapSource originalBitmap;
    private readonly string path;
    private readonly string command;
    private readonly Grid canvasHost = new() { MinHeight = 240 };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Image preview = new() { Stretch = Stretch.Uniform };
    private readonly StackPanel options = new() { Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock detail = Controls.Text("", 13, true);
    private readonly List<ImagePoint> stroke = [];
    private readonly ComboBox format;
    private readonly TextBox? quality;
    private readonly CheckBox? lossless;
    private readonly TextBox? edge;
    private readonly TextBox? width;
    private readonly TextBox? height;
    private readonly ComboBox? rotation;
    private readonly CheckBox? flipX;
    private readonly CheckBox? flipY;
    private readonly TextBox? brightness;
    private readonly TextBox? contrast;
    private readonly TextBox? saturation;
    private readonly ComboBox? tool;
    private readonly ComboBox? ratio;
    private readonly TextBox? text;
    private readonly ListBox? regions;
    private readonly TextBox? padding;
    private readonly TextBox? radius;
    private readonly TextBox? background;
    private readonly TextBox? gradient;
    private readonly CheckBox? shadow;
    private readonly Button copy;
    private readonly Button render;
    private readonly Button export;
    private readonly Func<KeyEventArgs, string, bool> shortcut;
    private CancellationTokenSource? rendering;
    private bool showingOriginal = true;
    private ProcessedImage? processed;
    private ImageOptions? processedOptions;
    public ImageOptions? OutputOptions { get; private set; }
    public ProcessedImage? VerifiedPreview { get; private set; }

    public ImageEditor(Window owner, string label, string command, string path, ProcessedImage original,
        int count, Func<KeyEventArgs, string, bool> shortcut)
    {
        Owner = owner;
        Title = label + " – Tabrilo";
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 940;
        Height = 690;
        MinWidth = 590;
        MinHeight = 470;
        this.path = path;
        this.command = command;
        this.original = original;
        originalBitmap = Controls.Bitmap(original.Bytes);
        this.shortcut = shortcut;

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        Controls.Heading(header, label);
        header.Children.Add(Controls.Text(count == 1 ? System.IO.Path.GetFileName(path) :
            $"{count} images · showing the first image", secondary: true));
        header.Children.Add(detail);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        footer.Children.Add(Controls.Button("Cancel", Close));
        copy = Controls.Button("Copy result", Copy);
        copy.IsEnabled = false;
        footer.Children.Add(copy);
        render = Controls.Button("Preview result", async () => await Render());
        footer.Children.Add(render);
        export = Controls.Button(count > 1 ? "Export batch" : "Export", Accept, true);
        footer.Children.Add(export);

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(246) });
        root.Children.Add(grid);
        var viewport = new DockPanel();
        var toolbar = new WrapPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        viewport.Children.Add(toolbar);
        toolbar.Children.Add(Controls.Button("Original", ShowOriginal));
        toolbar.Children.Add(Controls.Button("Fit", () => { canvasHost.Width = double.NaN; canvasHost.Height = double.NaN; }));
        toolbar.Children.Add(Controls.Button("Actual size", () =>
        {
            var image = preview.Source as System.Windows.Media.Imaging.BitmapSource;
            if (image is null) return;
            canvasHost.Width = image.PixelWidth;
            canvasHost.Height = image.PixelHeight;
        }));

        var scroll = new ScrollViewer { Content = canvasHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        canvasHost.MinWidth = 180;
        canvasHost.SizeChanged += (_, _) => Paint();
        scroll.SizeChanged += (_, _) =>
        {
            canvasHost.MinHeight = Math.Max(200, scroll.ViewportHeight);
            canvasHost.MinWidth = Math.Max(180, scroll.ViewportWidth);
        };
        canvasHost.Children.Add(preview);
        canvasHost.Children.Add(overlay);
        viewport.Children.Add(scroll);
        grid.Children.Add(viewport);
        var settings = new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(settings, 1);
        grid.Children.Add(settings);
        Content = root;

        var redact = command == "image.redact";
        var share = command == "image.share";
        var crop = command == "image.crop";
        var annotate = command == "image.annotate";
        var adjust = command is "image.edit" or "image.share";
        var frame = command == "image.frame";
        var squeeze = command == "image.compress";
        format = Controls.Choice(options, "Output format", redact ? ["Png"] : Enum.GetNames<ImageFormat>(),
            squeeze && System.IO.Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase) ? "WebP" : "Png");
        quality = Controls.Field(options, "JPEG / WebP quality", "85", 3);
        lossless = Controls.Checkbox(options, "Use lossless WebP encoding", false);
        options.Children.Add(Controls.Text("JPEG flattens transparency onto white. Exports normalize color to sRGB and remove embedded profiles.", 12, true));
        if (annotate || share || crop || redact)
        {
            var tools = new List<string>();
            if (redact || share) tools.Add("Redaction");
            if (crop || share) tools.Add("Crop");
            if (annotate || share) tools.AddRange(Enum.GetNames<MarkKind>());
            tool = Controls.Choice(options, "Canvas tool", tools.ToArray(), tools[0]);
            if (crop || share)
            {
                ratio = Controls.Choice(options, "Crop aspect", ["Free", "1:1", "4:3", "16:9"], "Free");
                options.Children.Add(Controls.Button("Set crop dimensions", NumericCrop));
            }
            if (annotate || share) text = Controls.Field(options, "Annotation text", "", 250);
            regions = new ListBox { Height = 84, DisplayMemberPath = nameof(RegionRow.Label), Margin = new Thickness(0, 4, 0, 8) };
            Controls.AddLabel(options, "Canvas regions", regions);
            var changes = new WrapPanel();
            changes.Children.Add(Controls.Button("Undo", Undo));
            changes.Children.Add(Controls.Button("Redo", Redo));
            changes.Children.Add(Controls.Button("Remove region", RemoveRegion));
            changes.Children.Add(Controls.Button("Move or resize region", MoveRegion));
            options.Children.Add(changes);
            if (redact || share)
            {
                options.Children.Add(Controls.Text("Drag on the original to replace pixels with an opaque mask. Redacted copies use PNG. The original and backups remain unchanged.", 12, true));
                options.Children.Add(Controls.Button("Add redaction by dimensions", () =>
                {
                    if (AskRegion(new(0, 0, Math.Max(1, (int)original.Width / 4), Math.Max(1, (int)original.Height / 4)),
                        "Add redaction") is { } region)
                        Change(selection with { Redactions = [.. selection.Redactions, region] });
                }));
            }
            overlay.MouseLeftButtonDown += StartStroke;
            overlay.MouseMove += ContinueStroke;
            overlay.MouseLeftButtonUp += FinishStroke;
            overlay.LostMouseCapture += (_, _) => { if (stroke.Count > 0) { stroke.Clear(); Paint(); } };
        }
        if (adjust || squeeze)
            edge = Controls.Field(options, "Maximum edge in px (0 keeps size)", "0", 5);
        if (adjust)
        {
            width = Controls.Field(options, "Resize width in px (0 auto)", "0", 5);
            height = Controls.Field(options, "Resize height in px (0 auto)", "0", 5);
            rotation = Controls.Choice(options, "Rotation", ["0", "90", "180", "270"], "0");
            flipX = Controls.Checkbox(options, "Flip horizontally", false);
            flipY = Controls.Checkbox(options, "Flip vertically", false);
            brightness = Controls.Field(options, "Brightness (−100 to 100)", "0", 4);
            contrast = Controls.Field(options, "Contrast (−100 to 100)", "0", 4);
            saturation = Controls.Field(options, "Saturation (0 to 200)", "100", 3);
        }
        if (frame)
        {
            padding = Controls.Field(options, "Padding in px", "32", 3);
            radius = Controls.Field(options, "Corner radius in px", "12", 3);
            background = Controls.Field(options, "Background hex color", "#F7F8FA", 7);
            gradient = Controls.Field(options, "Gradient end hex color (optional)", "", 7);
            shadow = Controls.Checkbox(options, "Shadow", true);
        }
        if (adjust || frame)
        {
            var changes = new WrapPanel();
            changes.Children.Add(Controls.Button("Undo edit", Undo));
            changes.Children.Add(Controls.Button("Redo edit", Redo));
            options.Children.Add(changes);
        }
        var fields = options.Children.OfType<Control>().ToArray();
        foreach (var field in fields)
        {
            if (field is TextBox fieldText)
            {
                fieldText.TextChanged += (_, _) => InvalidateResult();
                fieldText.LostKeyboardFocus += (_, _) => Commit();
            }
            if (field is ComboBox choice) choice.SelectionChanged += (_, _) => { InvalidateResult(); Commit(); };
            if (field is CheckBox check) check.Click += (_, _) => { InvalidateResult(); Commit(); };
        }
        editFields = fields.Where(field => field is TextBox or ComboBox or CheckBox &&
            !ReferenceEquals(field, tool) && !ReferenceEquals(field, ratio) && !ReferenceEquals(field, text)).ToArray();
        committed = Snapshot();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            if (Keyboard.FocusedElement is TextBoxBase) return;
            if (shortcut(args, "undo")) { Undo(); args.Handled = true; }
            if (shortcut(args, "redo")) { Redo(); args.Handled = true; }
        };
        Closing += (_, _) => rendering?.Cancel();
        ShowOriginal();
    }

    private void InvalidateResult() { processed = null; processedOptions = null; copy.IsEnabled = false; }

    private void ShowOriginal()
    {
        showingOriginal = true;
        preview.Source = originalBitmap;
        detail.Text = $"Original view · {original.Width} × {original.Height} px · {new FileInfo(path).Length:N0} bytes";
        Paint();
    }

    private ImageOptions ReadOptions()
    {
        var output = Enum.Parse<ImageFormat>((string)format.SelectedItem);
        var edits = new ImageEdits(
            edge is null ? 0 : (uint)Controls.Number(edge, 0, 16384, "Maximum edge"),
            rotation is null ? 0 : int.Parse((string)rotation.SelectedItem, System.Globalization.CultureInfo.InvariantCulture),
            flipX?.IsChecked == true, flipY?.IsChecked == true,
            brightness is null ? 0 : Controls.Number(brightness, -100, 100, "Brightness"),
            contrast is null ? 0 : Controls.Number(contrast, -100, 100, "Contrast"),
            saturation is null ? 100 : Controls.Number(saturation, 0, 200, "Saturation"),
            selection.Crop, selection.Redactions, selection.Marks,
            padding is null ? 0 : (uint)Controls.Number(padding, 0, 512, "Padding"),
            radius is null ? 0 : (uint)Controls.Number(radius, 0, 512, "Corner radius"),
            shadow?.IsChecked == true, ReadColor(background?.Text ?? "#F7F8FA"),
            string.IsNullOrWhiteSpace(gradient?.Text) ? null : ReadColor(gradient.Text),
            width is null ? 0 : (uint)Controls.Number(width, 0, 16384, "Resize width"),
            height is null ? 0 : (uint)Controls.Number(height, 0, 16384, "Resize height"));
        if (command == "image.crop" && edits.Crop is null) throw new ArgumentException("Select a crop area.");
        if ((command == "image.redact" || edits.Redactions is { Length: > 0 }) && output != ImageFormat.Png)
            throw new ArgumentException("Choose PNG for an image with redactions.");
        if (command == "image.redact" && edits.Redactions is not { Length: > 0 })
            throw new ArgumentException("Draw at least one redaction mask.");
        return new(output, Controls.Number(quality!, 1, 100, "Quality"), lossless?.IsChecked == true,
            command == "image.compress", command == "image.redact", edits);
    }

    private static string ReadColor(string value) => value.Length == 7 && value[0] == '#' &&
        value.AsSpan(1).ToString().All(Uri.IsHexDigit) ? value : throw new ArgumentException("Use a hex color such as #0F766E.");

    private async Task Render()
    {
        if (rendering is not null) return;
        using var source = new CancellationTokenSource();
        rendering = source;
        render.IsEnabled = export.IsEnabled = false;
        options.IsEnabled = false;
        try
        {
            var request = ReadOptions();
            detail.Text = "Processing preview…";
            var result = await Task.Run(() => Images.ProcessAsync(path, request, source.Token), source.Token);
            source.Token.ThrowIfCancellationRequested();
            preview.Source = Controls.Bitmap(result.Bytes);
            processed = result;
            processedOptions = request;
            copy.IsEnabled = true;
            showingOriginal = false;
            Paint();
            var before = new FileInfo(path).Length;
            detail.Text = $"Result · {result.Width} × {result.Height} px · {before:N0} → {result.Bytes.Length:N0} bytes";
            if (request.SmallerOnly && result.Bytes.Length >= before)
                detail.Text += " · The result is larger than the source.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        { detail.Text = error.Message; }
        finally { rendering = null; render.IsEnabled = export.IsEnabled = options.IsEnabled = true; }
    }

    private void Accept()
    {
        try
        {
            if (processed is not null) { OutputOptions = processedOptions; VerifiedPreview = processed; }
            else OutputOptions = ReadOptions();
            DialogResult = true;
        }
        catch (ArgumentException error) { detail.Text = error.Message; }
    }

    private void Copy()
    {
        if (processed is null) return;
        try { Controls.CopyImage(processed.Bytes); detail.Text = "Processed image copied."; }
        catch (System.Runtime.InteropServices.ExternalException) { detail.Text = "The clipboard is in use. Try again."; }
    }

    private Rect DisplayRect()
    {
        var scale = Math.Min(canvasHost.ActualWidth / original.Width, canvasHost.ActualHeight / original.Height);
        var width = original.Width * scale;
        var height = original.Height * scale;
        return new((canvasHost.ActualWidth - width) / 2, (canvasHost.ActualHeight - height) / 2, width, height);
    }

    private ImagePoint PointAt(Point point)
    {
        var rect = DisplayRect();
        return new((int)Math.Clamp(Math.Floor((point.X - rect.X) * original.Width / rect.Width), 0, original.Width - 1),
            (int)Math.Clamp(Math.Floor((point.Y - rect.Y) * original.Height / rect.Height), 0, original.Height - 1));
    }

    private void StartStroke(object sender, MouseButtonEventArgs args)
    {
        if (!showingOriginal || !options.IsEnabled || !DisplayRect().Contains(args.GetPosition(overlay))) return;
        stroke.Clear();
        stroke.Add(PointAt(args.GetPosition(overlay)));
        overlay.CaptureMouse();
        args.Handled = true;
    }

    private void ContinueStroke(object sender, MouseEventArgs args)
    {
        if (stroke.Count == 0 || args.LeftButton != MouseButtonState.Pressed) return;
        var point = PointAt(args.GetPosition(overlay));
        if (!Equals(point, stroke[^1]) && stroke.Count < 4095) stroke.Add(point);
        Paint();
    }

    private void FinishStroke(object sender, MouseButtonEventArgs args)
    {
        if (stroke.Count == 0) return;
        stroke.Add(PointAt(args.GetPosition(overlay)));
        var points = stroke.ToArray();
        stroke.Clear();
        overlay.ReleaseMouseCapture();
        var region = Rectangle(points[0], points[^1]);
        var selected = (string)tool!.SelectedItem;
        if (selected == "Redaction") Change(selection with { Redactions = [.. selection.Redactions, region] });
        else if (selected == "Crop") Change(selection with { Crop = Aspect(region) });
        else Change(selection with { Marks = [.. selection.Marks,
            new ImageMark(Enum.Parse<MarkKind>(selected), points, text?.Text ?? "")] });
        args.Handled = true;
    }

    private PixelRect Aspect(PixelRect region)
    {
        var selected = ratio?.SelectedItem as string;
        var aspect = selected switch { "1:1" => 1.0, "4:3" => 4.0 / 3, "16:9" => 16.0 / 9, _ => 0 };
        if (aspect == 0) return region;
        return region with { Width = Math.Max(1, Math.Min(region.Width, (int)(region.Height * aspect))),
            Height = Math.Max(1, Math.Min(region.Height, (int)(region.Width / aspect))) };
    }

    private static PixelRect Rectangle(ImagePoint a, ImagePoint b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X) + 1, Math.Abs(b.Y - a.Y) + 1);

    private void NumericCrop()
    {
        var region = selection.Crop ?? new(0, 0, (int)original.Width, (int)original.Height);
        if (AskRegion(region, "Crop dimensions") is { } next) Change(selection with { Crop = next });
    }

    private PixelRect? AskRegion(PixelRect region, string title)
    {
        var fields = new StackPanel { Margin = new Thickness(16) };
        var dialog = new Window { Owner = this, Width = 330, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Title = title, Content = fields };
        var x = Controls.Field(fields, "Left", region.X.ToString(), 6);
        var y = Controls.Field(fields, "Top", region.Y.ToString(), 6);
        var width = Controls.Field(fields, "Width", region.Width.ToString(), 6);
        var height = Controls.Field(fields, "Height", region.Height.ToString(), 6);
        var message = Controls.Text("", 12, true);
        fields.Children.Add(message);
        PixelRect? result = null;
        fields.Children.Add(Controls.Button("Apply", () =>
        {
            try
            {
                var next = new PixelRect(Controls.Number(x, 0, (int)original.Width - 1, "Left"),
                    Controls.Number(y, 0, (int)original.Height - 1, "Top"),
                    Controls.Number(width, 1, (int)original.Width, "Width"),
                    Controls.Number(height, 1, (int)original.Height, "Height"));
                if (!next.Fits(original.Width, original.Height)) throw new ArgumentException("The area extends past the image.");
                result = next;
                dialog.DialogResult = true;
            }
            catch (ArgumentException error) { message.Text = error.Message; }
        }, true));
        dialog.ShowDialog();
        return result;
    }

    private void MoveRegion()
    {
        if (regions?.SelectedItem is not RegionRow row) return;
        if (row.Kind == "mark")
        {
            var mark = selection.Marks[row.Index];
            var shape = new PixelRect(mark.Points.Min(point => point.X), mark.Points.Min(point => point.Y),
                mark.Points.Max(point => point.X) - mark.Points.Min(point => point.X) + 1,
                mark.Points.Max(point => point.Y) - mark.Points.Min(point => point.Y) + 1);
            if (AskRegion(shape, "Move annotation") is not { } next) return;
            var revised = mark with
                { Points = mark.Points.Select(point => new ImagePoint(
                    next.X + (int)((long)(point.X - shape.X) * next.Width / shape.Width),
                    next.Y + (int)((long)(point.Y - shape.Y) * next.Height / shape.Height))).ToArray() };
            var marks = (ImageMark[])selection.Marks.Clone();
            marks[row.Index] = revised;
            Change(selection with { Marks = marks });
        }
        else if (row.Kind == "mask" && AskRegion(selection.Redactions[row.Index], "Move redaction") is { } redaction)
        {
            var masks = (PixelRect[])selection.Redactions.Clone();
            masks[row.Index] = redaction;
            Change(selection with { Redactions = masks });
        }
        else if (row.Kind == "crop") NumericCrop();
    }

    private void RemoveRegion()
    {
        if (regions?.SelectedItem is not RegionRow row) return;
        switch (row.Kind)
        {
            case "crop": Change(selection with { Crop = null }); break;
            case "mask": Change(selection with { Redactions = selection.Redactions.Where((_, i) => i != row.Index).ToArray() }); break;
            case "mark": Change(selection with { Marks = selection.Marks.Where((_, i) => i != row.Index).ToArray() }); break;
        }
    }

    private EditState Snapshot() => new(selection, editFields.Select(control => control switch
    {
        TextBox field => (object?)field.Text, ComboBox choice => choice.SelectedItem,
        CheckBox check => check.IsChecked, _ => null
    }).ToArray());

    private void Commit()
    {
        if (restoring || committed is null) return;
        var snapshot = Snapshot();
        if (snapshot.Selection == committed.Selection && snapshot.Settings.SequenceEqual(committed.Settings)) return;
        undo.Push(committed);
        redo.Clear();
        committed = snapshot;
    }

    private void Change(Selection next) { selection = next; Commit(); RefreshSelection(); }
    private void Undo() { Commit(); Restore(undo, redo); }
    private void Redo() => Restore(redo, undo);

    private void Restore(Stack<EditState> source, Stack<EditState> destination)
    {
        if (!source.TryPop(out var value)) return;
        destination.Push(Snapshot());
        restoring = true;
        try
        {
            selection = value.Selection;
            for (var index = 0; index < editFields.Length; index++)
            {
                switch (editFields[index])
                {
                    case TextBox field: field.Text = (string)value.Settings[index]!; break;
                    case ComboBox choice: choice.SelectedItem = value.Settings[index]; break;
                    case CheckBox check: check.IsChecked = (bool?)value.Settings[index]; break;
                }
            }
            committed = value;
        }
        finally { restoring = false; }
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (regions is not null)
        {
            var rows = new List<RegionRow>();
            if (selection.Crop is not null) rows.Add(new("crop", 0, "Crop"));
            rows.AddRange(selection.Redactions.Select((_, i) => new RegionRow("mask", i, $"Redaction {i + 1}")));
            rows.AddRange(selection.Marks.Select((mark, i) => new RegionRow("mark", i, $"{mark.Kind} {i + 1}")));
            regions.ItemsSource = rows;
            regions.SelectedIndex = rows.Count - 1;
        }
        InvalidateResult();
        ShowOriginal();
    }

    private void Paint()
    {
        overlay.Children.Clear();
        if (!showingOriginal) return;
        var rect = DisplayRect();
        if (rect.Width <= 0 || rect.Height <= 0) return;
        if (selection.Crop is { } crop) Box(crop, Brushes.Transparent, Brushes.DeepSkyBlue, rect);
        foreach (var mask in selection.Redactions) Box(mask, Brushes.Black, Brushes.White, rect);
        foreach (var mark in selection.Marks)
            Box(Rectangle(mark.Points[0], mark.Points[^1]), Brushes.Transparent, Brushes.MediumTurquoise, rect);
        if (stroke.Count > 1)
            Box(Rectangle(stroke[0], stroke[^1]), (string)tool!.SelectedItem == "Redaction" ? Brushes.Black : Brushes.Transparent,
                Brushes.White, rect);
    }

    private void Box(PixelRect area, Brush fill, Brush border, Rect displayed)
    {
        var x = displayed.Width / original.Width;
        var y = displayed.Height / original.Height;
        var element = new System.Windows.Shapes.Rectangle { Width = area.Width * x, Height = area.Height * y,
            Fill = fill, Stroke = border, StrokeThickness = 1.5, IsHitTestVisible = false };
        Canvas.SetLeft(element, displayed.Left + area.X * x);
        Canvas.SetTop(element, displayed.Top + area.Y * y);
        overlay.Children.Add(element);
    }
}
