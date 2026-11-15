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
    private sealed record Gesture(RegionRow Row, PixelRect Bounds, ImagePoint Start, int XEdge, int YEdge);
    private Gesture? gesture;
    private ImagePoint? gestureEnd;
    private readonly Stack<EditState> undo = new();
    private readonly Stack<EditState> redo = new();
    private Selection selection = new(null, [], []);
    private EditState? committed;
    private bool restoring;
    private Control[] editFields = [];
    private readonly ImageCanvasPreview original;
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
    private readonly System.Windows.Threading.DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly SemaphoreSlim encoder = new(1, 1);
    private CancellationTokenSource? rendering;
    private readonly CancellationToken operation;
    private bool showingOriginal = true;
    private bool finishing;
    private bool closed;
    private bool actualSize;
    private readonly Action<bool> zoomTo;
    private bool resultRequested;
    private ProcessedImage? processed;
    private ImageOptions? processedOptions;
    public ImageOptions? OutputOptions { get; private set; }
    public ProcessedImage? VerifiedPreview { get; private set; }
    public bool ChangeDestination { get; private set; }

    public ImageEditor(Window? owner, string label, string command, string path, ImageCanvasPreview original,
        Func<KeyEventArgs, string, bool> shortcut, CancellationToken cancellationToken)
    {
        Owner = owner;
        Title = label + " – Tabrilo";
        SetResourceReference(BackgroundProperty, "Background");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Width = 860;
        Height = 625;
        MinWidth = 590;
        MinHeight = 470;
        this.path = path;
        this.command = command;
        this.original = original;
        originalBitmap = Controls.Bitmap(original.Png);
        operation = cancellationToken;
        this.shortcut = shortcut;

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        Controls.Heading(header, label);
        header.Children.Add(Controls.Text(System.IO.Path.GetFileName(path), secondary: true));
        header.Children.Add(detail);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        footer.Children.Add(Controls.Button("Cancel", Close));
        copy = Controls.Button("Copy result", async () => await Finish(copyResult: true));
        footer.Children.Add(copy);
        export = Controls.Button("Save copy", async () => await Finish(copyResult: false), true);
        footer.Children.Add(export);
        footer.Children.Add(Controls.Button("Change destination…", async () => await Finish(copyResult: false, changeDestination: true)));

        var grid = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(246) });
        root.Children.Add(grid);
        var viewport = new DockPanel();
        var toolbar = new WrapPanel();
        var scroll = new ScrollViewer { Content = canvasHost, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(toolbar, Dock.Top);
        viewport.Children.Add(toolbar);
        toolbar.Children.Add(Controls.Button("Edit canvas", ShowOriginal));
        render = Controls.Button("Result view", RequestResult);
        toolbar.Children.Add(render);
        toolbar.Children.Add(Controls.Button("Fit", () => SetZoom(false)));
        toolbar.Children.Add(Controls.Button("Actual size", () => SetZoom(true)));

        canvasHost.MinWidth = 180;
        canvasHost.SizeChanged += (_, _) => Paint();
        scroll.SizeChanged += (_, _) =>
        {
            if (!actualSize) SizeToViewport();
        };
        const int inset = 6;
        scroll.ScrollChanged += (_, args) =>
        {
            if (!actualSize && (args.ViewportWidthChange != 0 || args.ViewportHeightChange != 0)) SizeToViewport();
        };
        canvasHost.Children.Add(preview);

        void SizeToViewport()
        {
            if (scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0) return;
            canvasHost.Width = Math.Max(1, scroll.ViewportWidth - inset);
            canvasHost.Height = Math.Max(1, scroll.ViewportHeight - inset);
        }

        void SetZoom(bool zoomed)
        {
            actualSize = zoomed;
            if (!zoomed) { SizeToViewport(); return; }
            canvasHost.Width = showingOriginal ? original.Width : processed?.Width ?? original.Width;
            canvasHost.Height = showingOriginal ? original.Height : processed?.Height ?? original.Height;
        }

        zoomTo = SetZoom;
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
        format = Controls.Choice(options, "Output format", redact ? ["Png"] : Enum.GetNames<ImageFormat>(), "Png");
        format.IsEnabled = !redact;
        if (!redact)
        {
            quality = Controls.Field(options, "JPEG / WebP quality", "85", 3);
            lossless = Controls.Checkbox(options, "Use lossless WebP encoding", false);
            options.Children.Add(Controls.Text("JPEG fills transparent areas with white.", 12, true));
        }
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
            regions.SelectionChanged += (_, _) => Paint();
            options.Children.Add(Controls.Text("Drag to draw. Drag a selected region to move it, or drag a corner to resize. Hold Shift to draw over another region.", 12, true));
            var changes = new WrapPanel();
            changes.Children.Add(Controls.Button("Undo", Undo));
            changes.Children.Add(Controls.Button("Redo", Redo));
            changes.Children.Add(Controls.Button("Remove region", RemoveRegion));
            changes.Children.Add(Controls.Button("Region dimensions…", MoveRegion));
            options.Children.Add(changes);
            if (redact || share)
            {
                options.Children.Add(Controls.Text("A redacted PNG replaces the selected pixels. The original and backups remain unchanged.", 12, true));
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
            overlay.LostMouseCapture += (_, _) =>
            {
                stroke.Clear();
                gesture = null;
                gestureEnd = null;
                Paint();
            };
        }
        if (adjust)
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
            if (args.Key == Key.Escape)
            {
                if (overlay.IsMouseCaptured) overlay.ReleaseMouseCapture();
                else Close();
                args.Handled = true;
                return;
            }
            if (finishing || Keyboard.FocusedElement is TextBoxBase) return;
            if (args.Key == Key.Delete && !finishing) { RemoveRegion(); args.Handled = true; }
            if (shortcut(args, "undo")) { Undo(); args.Handled = true; }
            if (shortcut(args, "redo")) { Redo(); args.Handled = true; }
        };
        previewDelay.Tick += async (_, _) =>
        {
            previewDelay.Stop();
            if (!finishing && resultRequested) await Process(copyResult: false, previewOnly: true);
        };
        Closing += (_, _) =>
        {
            closed = true;
            previewDelay.Stop();
            rendering?.Cancel();
        };
        ShowOriginal();
    }

    private void InvalidateResult()
    {
        processed = null;
        processedOptions = null;
        rendering?.Cancel();
        if (!resultRequested || finishing) return;
        detail.Text = "Updating result…";
        preview.Source = null;
        previewDelay.Stop();
        previewDelay.Start();
    }

    private void ShowOriginal()
    {
        resultRequested = false;
        previewDelay.Stop();
        showingOriginal = true;
        preview.Source = originalBitmap;
        detail.Text = $"Editing canvas · {original.Width} × {original.Height} px";
        zoomTo(actualSize);
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
        return new(output, quality is null ? 85 : Controls.Number(quality, 1, 100, "Quality"),
            lossless?.IsChecked == true, Redaction: command == "image.redact", Edits: edits);
    }

    private static string ReadColor(string value) => value.Length == 7 && value[0] == '#' &&
        value.AsSpan(1).ToString().All(Uri.IsHexDigit) ? value : throw new ArgumentException("Use a hex color such as #0F766E.");

    private void RequestResult()
    {
        resultRequested = true;
        showingOriginal = false;
        overlay.Children.Clear();
        previewDelay.Stop();
        _ = Process(copyResult: false, previewOnly: true);
    }

    private async Task Finish(bool copyResult, bool changeDestination = false)
    {
        if (finishing || overlay.IsMouseCaptured) return;
        finishing = true;
        previewDelay.Stop();
        options.IsEnabled = export.IsEnabled = copy.IsEnabled = render.IsEnabled = false;
        try
        {
            await Process(copyResult, previewOnly: false);
            if (closed || copyResult || processed is null) return;
            OutputOptions = processedOptions;
            VerifiedPreview = processed;
            ChangeDestination = changeDestination;
            DialogResult = true;
        }
        finally
        {
            finishing = false;
            if (!closed) options.IsEnabled = export.IsEnabled = copy.IsEnabled = render.IsEnabled = true;
        }
    }

    private async Task Process(bool copyResult, bool previewOnly)
    {
        rendering?.Cancel();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(operation);
        rendering = source;
        try
        {
            var request = ReadOptions();
            var cached = processed;
            detail.Text = previewOnly ? "Processing preview…" : "Processing current edits…";
            var (result, bitmap) = await Task.Run(async () =>
            {
                await encoder.WaitAsync(source.Token);
                try
                {
                    var output = cached ?? await Images.ProcessAsync(path, request, source.Token);
                    var png = (previewOnly || copyResult) ? Images.DisplayPng(output, fullSize: copyResult, source.Token) : null;
                    (byte[] Png, BitmapSource Bitmap)? display = png is null ? null : (png, Controls.Bitmap(png));
                    return (output, display);
                }
                finally { encoder.Release(); }
            }, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (closed || !ReferenceEquals(rendering, source)) return;
            processed = result;
            processedOptions = request;
            if (copyResult && bitmap is { } clipboard)
            {
                Controls.CopyImage(clipboard.Png, clipboard.Bitmap);
                detail.Text = "Processed image copied.";
            }
            else if (previewOnly && resultRequested && bitmap is { } display)
            {
                preview.Source = display.Bitmap;
                zoomTo(actualSize);
                var before = new FileInfo(path).Length;
                detail.Text = $"Result view · {result.Width} × {result.Height} px · {before:N0} → {result.Bytes.Length:N0} bytes";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (OperationCoordinator.IsOperationError(error))
        {
            if (!closed && ReferenceEquals(rendering, source))
            {
                detail.Text = OperationCoordinator.ErrorMessage(error);
                if (previewOnly && resultRequested) preview.Source = null;
                if (!copyResult) { processed = null; processedOptions = null; }
            }
        }
        finally { if (ReferenceEquals(rendering, source)) rendering = null; }
    }

    private ImageViewport Viewport()
    {
        var scale = Math.Min(canvasHost.ActualWidth / originalBitmap.PixelWidth, canvasHost.ActualHeight / originalBitmap.PixelHeight);
        var width = originalBitmap.PixelWidth * scale;
        var height = originalBitmap.PixelHeight * scale;
        return new(new Rect((canvasHost.ActualWidth - width) / 2, (canvasHost.ActualHeight - height) / 2, width, height),
            (int)original.Width, (int)original.Height);
    }

    private void StartStroke(object sender, MouseButtonEventArgs args)
    {
        var position = args.GetPosition(overlay);
        var view = Viewport();
        if (!showingOriginal || finishing || !view.Display.Contains(position) || view.Display.Width <= 0) return;
        var point = view.At(position);
        stroke.Clear();
        gesture = null;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var selected = regions?.SelectedItem as RegionRow;
            if (selected is not null && RegionBounds(selected) is { } area)
                foreach (var (handle, x, y) in Handles(view.Bounds(area)))
                    if ((position - handle).Length <= 10) { gesture = new(selected, area, point, x, y); break; }
            if (gesture is null && regions?.ItemsSource is IEnumerable<RegionRow> rows)
                foreach (var row in rows.Reverse())
                    if (RegionBounds(row) is { } region && view.Bounds(region).Contains(position))
                    {
                        regions.SelectedItem = row;
                        gesture = new(row, region, point, 0, 0);
                        break;
                    }
        }
        if (gesture is null) stroke.Add(point);
        else gestureEnd = point;
        overlay.CaptureMouse();
        args.Handled = true;
    }

    private void ContinueStroke(object sender, MouseEventArgs args)
    {
        if (!overlay.IsMouseCaptured || args.LeftButton != MouseButtonState.Pressed) return;
        var point = Viewport().At(args.GetPosition(overlay));
        if (gesture is not null) gestureEnd = point;
        else if (stroke.Count > 0 && point != stroke[^1] && stroke.Count < 4095) stroke.Add(point);
        Paint();
    }

    private void FinishStroke(object sender, MouseButtonEventArgs args)
    {
        if (!overlay.IsMouseCaptured) return;
        var point = Viewport().At(args.GetPosition(overlay));
        var active = gesture;
        var points = stroke.Count > 0 ? stroke.Append(point).ToArray() : [];
        overlay.ReleaseMouseCapture();
        if (active is not null)
        {
            var next = Adjusted(active, point);
            if (next != active.Bounds) Change(WithRegion(active.Row, active.Bounds, next), active.Row);
        }
        else if (points.Length > 0)
        {
            var region = Rectangle(points[0], points[^1]);
            var selected = (string)tool!.SelectedItem;
            if (selected == "Redaction") Change(selection with { Redactions = [.. selection.Redactions, region] });
            else if (selected == "Crop") Change(selection with { Crop = Aspect(region) }, new("crop", 0, "Crop"));
            else Change(selection with { Marks = [.. selection.Marks,
                new ImageMark(Enum.Parse<MarkKind>(selected), points, text?.Text ?? "")] });
        }
        args.Handled = true;
    }

    private PixelRect Adjusted(Gesture active, ImagePoint point) => active.XEdge == 0
        ? Viewport().Move(active.Bounds, active.Start, point)
        : Viewport().Resize(active.Bounds, active.Start, point, active.XEdge, active.YEdge, active.Row.Kind == "crop" ? CropAspect() : 0);

    private PixelRect? RegionBounds(RegionRow row) => row.Kind switch
    {
        "crop" => selection.Crop,
        "mask" => selection.Redactions[row.Index],
        _ => MarkBounds(selection.Marks[row.Index])
    };

    private static PixelRect MarkBounds(ImageMark mark)
    {
        var points = mark.Points;
        var area = Rectangle(new(points.Min(point => point.X), points.Min(point => point.Y)),
            new(points.Max(point => point.X), points.Max(point => point.Y)));
        return area;
    }

    private Selection WithRegion(RegionRow row, PixelRect previous, PixelRect next)
    {
        if (row.Kind == "crop") return selection with { Crop = next };
        if (row.Kind == "mask")
        {
            var masks = (PixelRect[])selection.Redactions.Clone();
            masks[row.Index] = next;
            return selection with { Redactions = masks };
        }
        var marks = (ImageMark[])selection.Marks.Clone();
        var mark = marks[row.Index];
        marks[row.Index] = mark with { Points = mark.Points.Select(point => new ImagePoint(
            next.X + ScaleOffset(point.X - previous.X, previous.Width, next.Width),
            next.Y + ScaleOffset(point.Y - previous.Y, previous.Height, next.Height))).ToArray() };
        return selection with { Marks = marks };

        static int ScaleOffset(int offset, int from, int to) => from <= 1 ? 0 : (int)((long)offset * (to - 1) / (from - 1));
    }

    private PixelRect Aspect(PixelRect region)
    {
        var aspect = CropAspect();
        if (aspect == 0) return region;
        return region with { Width = Math.Max(1, Math.Min(region.Width, (int)(region.Height * aspect))),
            Height = Math.Max(1, Math.Min(region.Height, (int)(region.Width / aspect))) };
    }

    private double CropAspect() => (string?)ratio?.SelectedItem switch { "1:1" => 1.0, "4:3" => 4.0 / 3, "16:9" => 16.0 / 9, _ => 0 };

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
        if (regions?.SelectedItem is not RegionRow row || RegionBounds(row) is not { } area) return;
        if (AskRegion(area, row.Label + " dimensions") is { } next) Change(WithRegion(row, area, next), row);
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

    private void Change(Selection next, RegionRow? selected = null)
    {
        selection = next;
        Commit();
        RefreshSelection(selected);
    }
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

    private void RefreshSelection(RegionRow? selected = null)
    {
        if (regions is not null)
        {
            var rows = new List<RegionRow>();
            if (selection.Crop is not null) rows.Add(new("crop", 0, "Crop"));
            rows.AddRange(selection.Redactions.Select((_, i) => new RegionRow("mask", i, $"Redaction {i + 1}")));
            rows.AddRange(selection.Marks.Select((mark, i) => new RegionRow("mark", i, $"{mark.Kind} {i + 1}")));
            regions.ItemsSource = rows;
            var index = selected is null ? -1 : rows.FindIndex(row => row.Kind == selected.Kind && row.Index == selected.Index);
            regions.SelectedIndex = index >= 0 ? index : rows.Count - 1;
        }
        InvalidateResult();
        Paint();
    }

    private void Paint()
    {
        overlay.Children.Clear();
        if (!showingOriginal) return;
        var view = Viewport();
        var display = view.Display;
        if (display.Width <= 0 || display.Height <= 0) return;
        var drawn = gesture is { } active && gestureEnd is { } end
            ? WithRegion(active.Row, active.Bounds, Adjusted(active, end)) : selection;
        if (drawn.Crop is { } crop) Box(view.Bounds(crop), Brushes.Transparent, Brushes.MediumTurquoise);
        foreach (var mask in drawn.Redactions) Box(view.Bounds(mask), Brushes.Black, Brushes.Transparent);
        foreach (var mark in drawn.Marks) DrawMark(mark, view);
        if (regions?.SelectedItem is RegionRow row)
        {
            var selected = row.Kind switch
            {
                "crop" => drawn.Crop,
                "mask" when row.Index < drawn.Redactions.Length => drawn.Redactions[row.Index],
                "mark" when row.Index < drawn.Marks.Length => MarkBounds(drawn.Marks[row.Index]),
                _ => null
            };
            if (selected is { } bounds)
            {
                var box = view.Bounds(bounds);
                Box(box, Brushes.Transparent, Brushes.White);
                foreach (var (handle, _, _) in Handles(box))
                    Box(new Rect(handle.X - 4, handle.Y - 4, 8, 8), Brushes.MediumTurquoise, Brushes.Black);
            }
        }
        if (stroke.Count <= 1) return;
        var toolName = (string)tool!.SelectedItem;
        if (toolName is "Redaction" or "Crop")
        {
            var area = Rectangle(stroke[0], stroke[^1]);
            if (toolName == "Crop") area = Aspect(area);
            Box(view.Bounds(area), toolName == "Redaction" ? Brushes.Black : Brushes.Transparent, Brushes.White);
        }
        else DrawMark(new ImageMark(Enum.Parse<MarkKind>(toolName), stroke.ToArray(), text?.Text ?? ""), view);
    }

    private static (Point Point, int X, int Y)[] Handles(Rect box) =>
        [(box.TopLeft, -1, -1), (box.TopRight, 1, -1), (box.BottomLeft, -1, 1), (box.BottomRight, 1, 1)];

    private void DrawMark(ImageMark mark, ImageViewport view)
    {
        var start = mark.Points[0];
        var end = mark.Points[^1];
        var a = view.At(start.X, start.Y);
        var b = view.At(end.X, end.Y);
        var bounds = new Rect(a, b);
        var color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(mark.Color));
        var thickness = Math.Max(2, Math.Max(original.Width, original.Height) / 250.0);
        var scale = view.Display.Width / original.Width;
        var border = Math.Max(1, thickness * scale);
        switch (mark.Kind)
        {
            case MarkKind.Rectangle:
                Box(bounds, Brushes.Transparent, color, border);
                break;
            case MarkKind.Ellipse:
                Place(new Ellipse { Width = bounds.Width, Height = bounds.Height, Stroke = color, StrokeThickness = border },
                    bounds.Left, bounds.Top);
                break;
            case MarkKind.Freehand:
                Place(new Polyline { Points = new PointCollection(mark.Points.Select(point => view.At(point.X, point.Y))),
                    Stroke = color, StrokeThickness = border }, 0, 0);
                break;
            case MarkKind.Arrow:
                var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                var length = thickness * 5;
                Line(a, b);
                Line(b, view.At(end.X - length * Math.Cos(angle - 0.5), end.Y - length * Math.Sin(angle - 0.5)));
                Line(b, view.At(end.X - length * Math.Cos(angle + 0.5), end.Y - length * Math.Sin(angle + 0.5)));
                break;
            case MarkKind.Text:
                var size = Math.Max(1, Math.Max(18, thickness * 6) * scale);
                Place(new TextBlock { Text = mark.Text, FontSize = size, Foreground = color }, a.X, a.Y - size);
                break;
        }

        void Line(Point p, Point q) => Place(new Line { X1 = p.X, Y1 = p.Y, X2 = q.X, Y2 = q.Y,
            Stroke = color, StrokeThickness = border }, 0, 0);
    }

    private void Box(Rect bounds, Brush fill, Brush border, double thickness = 1.5) =>
        Place(new Rectangle { Width = bounds.Width, Height = bounds.Height, Fill = fill, Stroke = border, StrokeThickness = thickness },
            bounds.Left, bounds.Top);

    private void Place(FrameworkElement element, double x, double y)
    {
        element.IsHitTestVisible = false;
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        overlay.Children.Add(element);
    }
}
