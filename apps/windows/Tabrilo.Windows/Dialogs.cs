using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tabrilo.Core;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal static class Dialogs
{
    public static Window WindowFor(Window owner, string title, Panel panel, double width = 530, double height = 500) =>
        new()
        {
            Owner = owner, Title = title + " – Tabrilo", Width = width, Height = height,
            MinWidth = 330, MinHeight = 250, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };

    public static T? Ask<T>(Window owner, string title, Func<Panel, Func<T>> create, string button = "Continue") where T : class
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        Controls.Heading(panel, title);
        var dialog = WindowFor(owner, title, panel);
        var read = create(panel);
        T? result = null;
        var error = Controls.Text("", 13, true);
        panel.Children.Add(error);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(Controls.Button("Cancel", dialog.Close));
        actions.Children.Add(Controls.Button(button, () =>
        {
            try { result = read(); dialog.DialogResult = true; }
            catch (ArgumentException exception) { error.Text = exception.Message; }
            catch (IOException exception) { error.Text = exception.Message; }
        }, true));
        panel.Children.Add(actions);
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { dialog.Close(); args.Handled = true; }
        };
        dialog.ShowDialog();
        return result;
    }

    public static void Details(Window owner, string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = WindowFor(owner, title, panel, 620, 500);
        Controls.Heading(panel, title);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0) });
        panel.Children.Add(Controls.Button("Done", dialog.Close, true));
        dialog.ShowDialog();
    }

    public static string? Password(Window owner) => Ask<string>(owner, "Unlock PDF", panel =>
    {
        panel.Children.Add(Controls.Text("Enter the password for this operation."));
        var password = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), MaxLength = 512 };
        Controls.AddLabel(panel, "PDF password", password);
        return () => password.Password;
    }, "Unlock");

    public static PdfPageChoice[]? ArrangePages(Window owner, string title, PdfPageChoice[] pages,
        Func<KeyEventArgs, string, bool> keys, Func<string[]?> choosePdf,
        Func<string[], System.Threading.Tasks.Task<PdfPageChoice[]>> inspect,
        Func<PdfPageChoice, System.Threading.CancellationToken, System.Threading.Tasks.Task<ProcessedImage>> thumbnail)
    {
        var layout = new StackPanel { Margin = new Thickness(18) };
        Controls.Heading(layout, title);
        var dialog = WindowFor(owner, title, layout, 750, 640);
        var entries = new List<PdfPageChoice>(pages);
        var undo = new Stack<PdfPageChoice[]>();
        var redo = new Stack<PdfPageChoice[]>();
        const int pageSize = 6;
        var list = new ListBox { Height = 326, SelectionMode = SelectionMode.Extended };
        var grid = new FrameworkElementFactory(typeof(UniformGrid));
        grid.SetValue(UniformGrid.ColumnsProperty, 3);
        list.ItemsPanel = new ItemsPanelTemplate(grid);
        var tile = new FrameworkElementFactory(typeof(StackPanel));
        tile.SetValue(FrameworkElement.MarginProperty, new Thickness(8));
        var preview = new FrameworkElementFactory(typeof(Image));
        preview.SetValue(FrameworkElement.HeightProperty, 106.0);
        preview.SetBinding(Image.SourceProperty, new Binding(nameof(PageRow.Preview)));
        tile.AppendChild(preview);
        var caption = new FrameworkElementFactory(typeof(TextBlock));
        caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(PageRow.Label)));
        caption.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(PageRow.Label)));
        caption.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        tile.AppendChild(caption);
        list.ItemTemplate = new DataTemplate { VisualTree = tile };
        Controls.AddLabel(layout, "Page order", list);
        var status = Controls.Text("", 13, true);
        layout.Children.Add(status);
        var offset = 0;
        System.Threading.CancellationTokenSource? request = null;
        var navigation = new WrapPanel();
        layout.Children.Add(navigation);
        var previous = new Button { Content = "Previous previews" };
        var next = new Button { Content = "Next previews" };
        previous.Click += (_, _) => Refresh(offset - pageSize);
        next.Click += (_, _) => Refresh(offset + pageSize);
        navigation.Children.Add(previous);
        navigation.Children.Add(next);

        async System.Threading.Tasks.Task RenderPreviews(PageRow[] rows)
        {
            request?.Cancel();
            using var cancel = new System.Threading.CancellationTokenSource();
            request = cancel;
            try
            {
                foreach (var row in rows)
                {
                    var result = await thumbnail(row.Page, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    BitmapSource image = Controls.Bitmap(result.Bytes);
                    if (row.Page.Rotation != 0)
                        image = new TransformedBitmap(image, new RotateTransform(row.Page.Rotation));
                    row.SetPreview(image);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            { if (!cancel.IsCancellationRequested) status.Text = error.Message; }
            finally { if (ReferenceEquals(request, cancel)) request = null; }
        }
        dialog.Closed += (_, _) => request?.Cancel();
        var actions = new WrapPanel();
        layout.Children.Add(actions);
        actions.Children.Add(Controls.Button("Earlier", () => Move(-1)));
        actions.Children.Add(Controls.Button("Later", () => Move(1)));
        actions.Children.Add(Controls.Button("Rotate 90°", () =>
        {
            var indices = Selected();
            if (indices.Length == 0) return;
            Save();
            foreach (var index in indices) entries[index] = entries[index] with { Rotation = (entries[index].Rotation + 90) % 360 };
            Refresh(indices[0]);
        }));
        actions.Children.Add(Controls.Button("Duplicate", () =>
        {
            var indices = Selected();
            if (indices.Length == 0) return;
            Save();
            for (var i = indices.Length - 1; i >= 0; i--) entries.Insert(indices[i], entries[indices[i]]);
            Refresh(indices[0]);
        }));
        actions.Children.Add(Controls.Button("Remove", () =>
        {
            var indices = Selected();
            if (indices.Length == 0) return;
            Save();
            foreach (var index in indices.Reverse()) entries.RemoveAt(index);
            Refresh(indices[0]);
        }));
        var insert = Controls.Button("Insert PDF", async () =>
        {
            if (choosePdf() is not { Length: > 0 } paths) return;
            try
            {
                layout.IsEnabled = false;
                var more = await inspect(paths);
                Save();
                var at = list.SelectedItem is PageRow row ? row.Index + 1 : entries.Count;
                if (entries.Count + more.Length > 2000) throw new ArgumentException("Choose up to 2000 pages.");
                entries.InsertRange(at, more);
                Refresh(at);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or ArgumentException)
            { status.Text = error.Message; }
            finally { layout.IsEnabled = true; }
        });
        actions.Children.Add(insert);
        actions.Children.Add(Controls.Button("Undo", () => Restore(undo, redo)));
        actions.Children.Add(Controls.Button("Redo", () => Restore(redo, undo)));
        var footer = new WrapPanel();
        layout.Children.Add(footer);
        footer.Children.Add(Controls.Button("Cancel", dialog.Close));
        footer.Children.Add(Controls.Button("Export PDF", () =>
        {
            if (entries.Count == 0) { status.Text = "Keep at least one page."; return; }
            dialog.DialogResult = true;
        }, true));
        dialog.PreviewKeyDown += (_, key) =>
        {
            if (keys(key, "undo")) { Restore(undo, redo); key.Handled = true; }
            else if (keys(key, "redo")) { Restore(redo, undo); key.Handled = true; }
            else if (key.Key == Key.Escape) { dialog.Close(); key.Handled = true; }
        };
        Refresh(0);
        return dialog.ShowDialog() == true ? entries.ToArray() : null;

        int[] Selected() => list.SelectedItems.Cast<PageRow>().Select(row => row.Index).Order().ToArray();
        void Save() { undo.Push(entries.ToArray()); redo.Clear(); }
        void Restore(Stack<PdfPageChoice[]> from, Stack<PdfPageChoice[]> to)
        {
            if (!from.TryPop(out var old)) return;
            to.Push(entries.ToArray());
            entries = old.ToList();
            Refresh(0);
        }
        void Refresh(int index)
        {
            index = Math.Clamp(index, 0, Math.Max(0, entries.Count - 1));
            offset = index / pageSize * pageSize;
            var rows = entries.Skip(offset).Take(pageSize).Select((page, i) => new PageRow(offset + i, page)).ToArray();
            list.ItemsSource = rows;
            if (entries.Count > 0) list.SelectedIndex = index - offset;
            previous.IsEnabled = offset > 0;
            next.IsEnabled = offset + rows.Length < entries.Count;
            status.Text = rows.Length == 0 ? "No pages selected." :
                $"Showing {offset + 1}–{offset + rows.Length} of {entries.Count} pages";
            _ = RenderPreviews(rows);
        }
        void Move(int direction)
        {
            var at = list.SelectedItem is PageRow row ? row.Index : -1;
            if (at < 0 || at + direction < 0 || at + direction >= entries.Count) return;
            Save();
            (entries[at], entries[at + direction]) = (entries[at + direction], entries[at]);
            Refresh(at + direction);
        }
    }

    private sealed class PageRow(int index, PdfPageChoice page) : System.ComponentModel.INotifyPropertyChanged
    {
        public int Index { get; } = index;
        public PdfPageChoice Page { get; } = page;
        public string Label => $"{Index + 1}. {Path.GetFileName(Page.Path)} · page {Page.Number}";
        public BitmapSource? Preview { get; private set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Label;

        public void SetPreview(BitmapSource image)
        {
            Preview = image;
            PropertyChanged?.Invoke(this, new(nameof(Preview)));
        }
    }

    public static void QrResults(Window owner, IReadOnlyList<(string Name, QrResult Result)> results)
    {
        if (results.Count == 0) { Details(owner, "Read QR", "No QR code found."); return; }
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = WindowFor(owner, "Read QR", panel, 670, 550);
        Controls.Heading(panel, $"{results.Count} QR result{(results.Count == 1 ? "" : "s")}");
        var labels = results.Select(item =>
            $"{item.Name}{(item.Result.Page is { } page ? $" · page {page}" : "")} · {item.Result.Type}").ToArray();
        var list = new ListBox { Height = 130, ItemsSource = labels };
        Controls.AddLabel(panel, "Detected codes", list);
        var payload = Controls.Field(panel, "Payload", "");
        payload.IsReadOnly = true;
        payload.TextWrapping = TextWrapping.Wrap;
        var points = Controls.Text("", 12, true);
        panel.Children.Add(points);
        var actions = new WrapPanel();
        panel.Children.Add(actions);
        actions.Children.Add(Controls.Button("Copy payload", () => Clipboard.SetText(payload.Text)));
        var open = Controls.Button("Open link…", () =>
        {
            if (list.SelectedIndex < 0 || results[list.SelectedIndex].Result.AllowedLink is not { } uri) return;
            if (MessageBox.Show(dialog, "Open this address in your browser?\n\n" + uri.AbsoluteUri,
                "Confirm link", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        });
        actions.Children.Add(open);
        actions.Children.Add(Controls.Button("Done", dialog.Close, true));
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var value = results[list.SelectedIndex].Result;
            payload.Text = value.Payload;
            points.Text = string.Join(" · ", value.Location.Select(point => $"({point.X}, {point.Y})"));
            open.IsEnabled = value.AllowedLink is not null;
        };
        list.SelectedIndex = 0;
        dialog.ShowDialog();
    }

    public static int[] ParsePages(string input, int total)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(1, total).ToArray();
        var pages = new List<int>();
        foreach (var part in input.Split(',', StringSplitOptions.TrimEntries))
        {
            var values = part.Split('-', StringSplitOptions.TrimEntries);
            if (values.Length > 2 || !int.TryParse(values[0], out var first) || first < 1 || first > total)
                throw new ArgumentException($"Enter page numbers or ranges from 1 to {total}.");
            var last = first;
            if (values.Length == 2 && (!int.TryParse(values[1], out last) || last < first || last > total))
                throw new ArgumentException($"Enter ascending page ranges within 1 to {total}.");
            if (pages.Count + last - first + 1 > 2000) throw new ArgumentException("Choose up to 2000 pages.");
            pages.AddRange(Enumerable.Range(first, last - first + 1));
        }
        return pages.Distinct().ToArray();
    }
}
