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

internal sealed class DesktopAcceptance(string picture, string document, string evidence) : Application
{
    internal static Task<int> RunAsync(string picture, string document, string evidence)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new DesktopAcceptance(picture, document, evidence);
                var markup = System.Xml.Linq.XDocument.Load("apps/windows/Tabrilo.Windows/App.xaml").Root!;
                var ns = markup.Name.Namespace;
                var dictionary = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
                    markup.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                    markup.Element(ns + "Application.Resources")!.Elements());
                app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                done.SetResult(app.Run());
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
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
            using var jobs = new JobResults();
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
            Console.WriteLine("Visible WPF windows, focus, real rendering and bounded load passed. " + evidence);
            Shutdown(0);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Shutdown(1); }
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
            brush.DrawRectangle(window.Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(evidence, name + ".png"));
        png.Save(file);
    }
}
