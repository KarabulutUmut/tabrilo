using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tabrilo.Core;
using Tabrilo.Processing;
using Tabrilo.Windows;

public sealed class DesktopAcceptance(string picture, string document, string evidence) : Application
{
    public static int RunOnMainThread(string picture, string document, string evidence)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Start the desktop driver from its STA entry point.");
        var app = new DesktopAcceptance(picture, document, evidence);
        var markup = System.Xml.Linq.XDocument.Load("apps/windows/Tabrilo.Windows/App.xaml").Root!;
        var ns = markup.Name.Namespace;
        var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
            markup.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            markup.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app.Run();
    }

    protected override async void OnStartup(StartupEventArgs args)
    {
        try
        {
            var home = new Tabrilo.Windows.MainWindow();
            MainWindow = home;
            home.Show();
            var state = (ViewState)home.DataContext;
            state.Dispatch.Execute("settings");
            foreach (var theme in new[] { "Light", "Dark" })
            {
                Theme.Apply(theme);
                await Save(home, "settings-" + theme);
            }
            Theme.Apply("Dark", 1.5);
            state.Dispatch.Execute("settings");
            if (home.FontSize != 21) throw new InvalidOperationException("Text scale was not applied.");
            await Save(home, "settings-large-text");
            Theme.Apply("Dark");
            state.Dispatch.Execute("settings");
            var original = await Images.ReadEditorPreviewAsync(picture, default);
            foreach (var command in new[] { "image.edit", "image.redact" })
            {
                var editor = new ImageEditor(home, Commands.All.Single(c => c.Id == command).Label, command, picture,
                    original, (_, _) => false, default);
                editor.Show();
                await Save(editor, command);
                editor.Close();
            }
            var pdf = new Pdfs(Path.Combine(AppContext.BaseDirectory, "tools/qpdf/qpdf.exe"));
            await VerifyExportColor(pdf);
            var info = await pdf.InspectAsync(document, null, default);
            var pages = Enumerable.Range(1, info.Pages).Select(i => new PdfPageChoice(document, i)).ToArray();
            var rendered = 0;
            Exception? captureError = null;
            Dialogs.ArrangePages(home, "Organize pages", pages, (_, _) => false, () => null, _ => Task.FromResult(pages),
                async (page, token) =>
                {
                    using var pdfDocument = await pdf.OpenForRenderingAsync(page.Path, null, token);
                    var image = await Pdfs.RenderPageAsync(pdfDocument, page.Number, 48, ImageFormat.Png, token);
                    if (++rendered == Math.Min(6, pages.Length))
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            var dialog = Windows.OfType<Window>().Single(w => w.Title.StartsWith("Organize pages"));
                            try { await Save(dialog, "pdf-organize"); }
                            catch (Exception ex) { captureError = ex; }
                            finally { dialog.Close(); }
                        }, DispatcherPriority.ApplicationIdle);
                    return image;
                });
            if (captureError != null) throw captureError;
            using var stop = new CancellationTokenSource();
            var count = 0;
            var workload = Task.Run(async () =>
            {
                while (count < 200 && !stop.IsCancellationRequested)
                {
                    await Images.ProcessAsync(picture, new(ImageFormat.Jpeg), stop.Token);
                    Interlocked.Increment(ref count);
                }
            });
            var timings = new List<double>();
            var displayDpi = new Dictionary<string, uint>();
            var memoryBefore = Environment.WorkingSet;
            for (var i = 0; i < 24; i++)
            {
                var mode = (WheelMode)(i % 3);
                var clock = Stopwatch.StartNew();
                var wheel = new Wheel([picture], new(), mode, (_, _) => { }, false);
                wheel.Show();
                var screens = System.Windows.Forms.Screen.AllScreens;
                var area = screens[i % screens.Length].WorkingArea;
                wheel.PositionAt(area.Right - 1, area.Bottom - 1);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(wheel).Handle;
                displayDpi[screens[i % screens.Length].DeviceName] = GetDpiForWindow(hwnd);
                if (!GetWindowRect(hwnd, out var bounds) || bounds.Left < area.Left || bounds.Top < area.Top ||
                    bounds.Right > area.Right || bounds.Bottom > area.Bottom) throw new InvalidOperationException("Wheel extends past monitor work area.");
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                timings.Add(clock.Elapsed.TotalMilliseconds);
                if (Keyboard.FocusedElement is not Button) throw new InvalidOperationException("No focused wheel action.");
                if (i < 3) await Save(wheel, "wheel-" + mode);
                wheel.Close();
            }
            using (var jobs = new JobResults())
            {
                var workArea = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
                var progress = jobs.Start("Compress image", stop.Cancel, workArea, false);
                var notice = Windows.OfType<Window>().Single(w => w.Title == "Tabrilo" && w.IsVisible);
                await Save(notice, "feedback");
                var cancelled = Stopwatch.StartNew();
                stop.Cancel();
                try { await workload; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                cancelled.Stop();
                progress.Finish("Cancelled");
                if (count is 0 or 200) throw new InvalidOperationException("The load did not overlap cancellation.");
                var report = new { build = App.BuildName, wheelCount = timings.Count, medianMs = timings.Order().ElementAt(12),
                maxMs = timings.Max(), completedEncodes = count, cancelMs = cancelled.Elapsed.TotalMilliseconds,
                workingSetBefore = memoryBefore, workingSetAfter = Environment.WorkingSet, highContrast = SystemParameters.HighContrast,
                displays = System.Windows.Forms.Screen.AllScreens.Select(s => new { s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, dpi = displayDpi[s.DeviceName] }) };
                File.WriteAllText(Path.Combine(evidence, "desktop.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            var quit = LogicalButtons(home).Single(button => Equals(button.Content, "Quit Tabrilo"));
            quit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (Windows.OfType<Window>().Any(window => window.IsVisible)) throw new IOException("A test window remained open after Quit.");
            if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted")
            {
                using var process = Process.GetCurrentProcess();
                var modules = process.Modules.Cast<ProcessModule>().Select(module => new
                {
                    name = module.ModuleName, address = module.BaseAddress.ToInt64(), size = module.ModuleMemorySize
                });
                File.WriteAllText(Path.Combine(evidence, "modules.json"), JsonSerializer.Serialize(modules));
            }
            Console.WriteLine("Visible WPF windows, focus, real rendering, bounded load and native Quit passed. " + evidence);
            Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
    }

    private static IEnumerable<Button> LogicalButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalButtons(child)) yield return descendant;
    }

    private async Task VerifyExportColor(Pdfs pdf)
    {
        var path = Path.Combine(evidence, "color-check.pdf");
        using (var fixture = new PdfSharp.Pdf.PdfDocument())
        {
            var page = fixture.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(864);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(828);
            using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            {
                graphics.DrawRectangle(new PdfSharp.Drawing.XSolidBrush(PdfSharp.Drawing.XColor.FromArgb(40, 120, 185)), 0, 0, 864, 828);
                graphics.DrawRectangle(PdfSharp.Drawing.XBrushes.Red, 725, 725, 90, 90);
            }
            fixture.Save(path);
        }
        using var document = await pdf.OpenForRenderingAsync(path, null, default);
        var result = await Pdfs.RenderPageAsync(document, 1, 96, ImageFormat.Png, default);
        using var stream = new MemoryStream(result.Bytes);
        var png = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(png.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(48, 48, 1, 1), pixel, 4, 0);
        if (Math.Abs(pixel[0] - 185) > 2 || Math.Abs(pixel[1] - 120) > 2 || Math.Abs(pixel[2] - 40) > 2 || pixel[3] != 255)
            throw new InvalidDataException($"Desktop appearance changed the exported PDF color: {Convert.ToHexString(pixel)}.");
        Console.WriteLine($"PDF export BGRA={Convert.ToHexString(pixel)}, high contrast={SystemParameters.HighContrast}.");
        if (bitmap.PixelWidth != 1152 || bitmap.PixelHeight != 1104) throw new IOException("Incorrect exported page geometry.");
        foreach (var x in new[] { 1023, 1024 })
        foreach (var y in new[] { 1023, 1024 })
        {
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel is not [0, 0, 255, 255]) throw new IOException("PDF color or geometry changed at a tile boundary.");
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out WindowBounds bounds);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    private async Task Save(Window window, string name)
    {
        await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!window.IsVisible || PresentationSource.FromVisual(window) is null) throw new IOException("Expected a visible native window.");
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var brush = background.RenderOpen())
        {
            var client = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            brush.DrawRectangle(window.Background, null, client);
            brush.DrawRectangle(new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = client }, null, client);
        }
        bitmap.Render(background);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(evidence, name + ".png"));
        png.Save(file);
    }
}
