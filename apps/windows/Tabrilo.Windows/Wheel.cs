using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Tabrilo.Core;

namespace Tabrilo.Windows;

internal sealed class Wheel : Window
{
    internal static double Diameter => WheelCatalog.Shared.Diameter;
    private static double Center => Diameter / 2;
    private static double Inner => WheelCatalog.Shared.InnerRadius;
    private static double Outer => WheelCatalog.Shared.OuterRadius;
    private int sectors = 6;
    private static Brush Disk => Palette(nameof(Disk));
    private static Brush Segment => Palette(nameof(Segment));
    private static Brush Selected => Palette(nameof(Selected));
    private static Brush Rim => Palette(nameof(Rim));
    private static Brush Mint => Palette(nameof(Mint));
    private static Brush Muted => Palette(nameof(Muted));
    private static Brush Palette(string name) => (Brush)Application.Current.FindResource("Wheel" + name);

    private readonly bool external;
    private readonly Action<string, string[]> invoke;
    private readonly Preferences preferences;
    private WheelMode mode;
    private readonly Canvas canvas = new() { Width = Diameter, Height = Diameter };
    private readonly List<Slot> slots = [];
    private readonly List<Window> rejectionWindows = [];
    private readonly TextBlock hint;
    private string[] files;
    private readonly DropRelease completion = new();
    private DragState dragState;
    private bool released;
    internal bool IsEnding => external && released;
    private readonly DispatcherTimer deadline = new();
    private bool closed;

    private enum DragState { Proposed, Acquired, Outside, Committed, Cancelled }
    private sealed record Slot(int Index, string Command, Button Button, System.Windows.Shapes.Path Path);

    public Wheel(string[] files, Preferences preferences, WheelMode mode, Action<string, string[]> invoke, bool external)
    {
        this.files = files;
        this.preferences = preferences;
        this.mode = mode;
        this.invoke = invoke;
        this.external = external;
        Title = "Tabrilo actions";
        Width = Height = Diameter * Theme.TextScale;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = !external;
        UseLayoutRounding = true;
        AllowDrop = external;
        KeyboardNavigation.SetTabNavigation(canvas, KeyboardNavigationMode.Cycle);
        var disk = new System.Windows.Shapes.Ellipse
        {
            Width = Diameter, Height = Diameter, Fill = SystemParameters.HighContrast ? SystemColors.WindowBrush : Disk, Stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Rim, StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        canvas.Children.Add(disk);
        hint = new TextBlock
        {
            Width = 91, Height = 58, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Muted, FontSize = 12, IsHitTestVisible = false,
            Text = external ? "Waiting for dragged files" : "Choose an action\nEsc to cancel"
        };
        Canvas.SetLeft(hint, Center - hint.Width / 2);
        Canvas.SetTop(hint, Center - 20);
        canvas.Children.Add(hint);
        canvas.Background = Brushes.Transparent;
        Content = new Viewbox { Child = canvas };
        Closed += (_, _) =>
        {
            closed = true;
            deadline.Stop();
            foreach (var window in rejectionWindows) window.Close();
            rejectionWindows.Clear();
        };
        deadline.Tick += (_, _) => Cancel();
        if (external)
        {
            // A nearly transparent OLE target acquires CF_HDROP before displaying any wheel.
            Opacity = .005;
            deadline.Interval = TimeSpan.FromMilliseconds(550);
            deadline.Start();
        }
        PreviewKeyDown += Navigate;
        if (external)
        {
            DragEnter += OnDrag;
            DragOver += OnDrag;
            DragLeave += OnLeave;
            Drop += OnDrop;
        }
        else
        {
            Loaded += (_, _) => slots.FirstOrDefault(slot => slot.Button.IsEnabled)?.Button.Focus();
            canvas.MouseLeftButtonUp += (_, args) => { if (args.OriginalSource == canvas) Close(); };
        }
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var slot in slots) canvas.Children.Remove(slot.Button);
        slots.Clear();
        var commands = WheelCatalog.Shared.For(mode, files, preferences.ShowWebP,
            Tabrilo.Processing.OfficeRenderer.Available(preferences.OfficeExecutable));
        sectors = Math.Max(2, commands.Length);
        hint.Text = commands.Length == 0 ? $"No {mode.ToString().ToLowerInvariant()} for this selection" : mode.ToString();
        for (var i = 0; i < commands.Length; i++)
        {
            var command = commands[i];
            var label = command.Id switch
            {
                "image.metadata" or "pdf.metadata" => "Metadata", "pdf.compress" or "image.compress" => "Compress",
                "image.edit" => "Edit", "pdf.organize" => "Organize", "pdf.split" => "Split",
                "pdf.merge" => "Merge", "archive.extract" => "Extract", _ => command.Label
            };
            Add(label, command.Id, i, true);
        }
    }

    public void Switch(WheelMode selected)
    {
        if (closed || released || mode == selected || dragState is DragState.Cancelled or DragState.Committed) return;
        mode = selected;
        Rebuild();
        if (!external) slots.FirstOrDefault()?.Button.Focus();
    }

    private void Add(string label, string command, int index, bool available)
    {
        var angle = -Math.PI / 2 + index * Math.Tau / sectors;
        var midpoint = At(angle, (Inner + Outer) / 2 + 6);
        var glyph = WheelCatalog.Shared.Symbols[command];
        var shape = new System.Windows.Shapes.Path
        {
            Data = Sector(index, sectors), Fill = SystemParameters.HighContrast ? SystemColors.WindowBrush : Segment, Stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Rim, StrokeThickness = 1, IsHitTestVisible = !external
        };
        var text = new TextBlock
        {
            Text = glyph + "\n" + label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, LineHeight = 14,
            Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : available ? Brushes.White : Muted, TextWrapping = TextWrapping.Wrap, Width = 87,
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false
        };
        var interior = new Canvas { Width = Diameter, Height = Diameter };
        interior.Children.Add(shape);
        Canvas.SetLeft(text, midpoint.X - text.Width / 2);
        Canvas.SetTop(text, midpoint.Y - (label.Length > 14 ? 24 : 16));
        interior.Children.Add(text);
        var template = new ControlTemplate(typeof(Button));
        template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        var button = new Button
        {
            Content = interior, Template = template, Width = Diameter, Height = Diameter,
            Background = null, Margin = new Thickness(0), Padding = new Thickness(0), IsEnabled = available,
            IsHitTestVisible = !external, FocusVisualStyle = null, Opacity = available ? 1 : .49
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => Choose(command);
        var slot = new Slot(index, command, button, shape);
        button.MouseEnter += (_, _) => Paint(slot, true);
        button.MouseLeave += (_, _) => Paint(slot, button.IsKeyboardFocused);
        button.GotKeyboardFocus += (_, _) => Paint(slot, true);
        button.LostKeyboardFocus += (_, _) => Paint(slot, button.IsMouseOver);
        canvas.Children.Add(button);
        slots.Add(slot);
    }

    private static void Paint(Slot slot, bool active)
    {
        active &= slot.Button.IsEnabled;
        slot.Path.Fill = SystemParameters.HighContrast ? SystemColors.WindowBrush : active ? Selected : Segment;
        slot.Path.Stroke = SystemParameters.HighContrast ? (active ? SystemColors.HighlightBrush : SystemColors.WindowTextBrush) : active ? Mint : Rim;
        slot.Path.StrokeThickness = active ? 2.5 : 1;
    }

    internal static Geometry Sector(int index, int sectors = 7)
    {
        var angle = -Math.PI / 2 + index * Math.Tau / sectors;
        var half = Math.PI / sectors - WheelCatalog.Shared.HalfGapRadians;
        var a = angle - half;
        var b = angle + half;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(At(a, Outer), true, true);
            context.ArcTo(At(b, Outer), new(Outer, Outer), 0, false, SweepDirection.Clockwise, true, false);
            context.LineTo(At(b, Inner), true, false);
            context.ArcTo(At(a, Inner), new(Inner, Inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point At(double angle, double radius) => new(Center + Math.Cos(angle) * radius, Center + Math.Sin(angle) * radius);

    private Slot? FindSlot(Point point) => slots.FirstOrDefault(slot => slot.Button.IsEnabled && slot.Path.Data.FillContains(point));

    private void OnDrag(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (dragState is DragState.Committed or DragState.Cancelled) return;
        if (!released && (dragState is DragState.Proposed or DragState.Outside || args.RoutedEvent == DragEnterEvent))
        {
            var received = GetFiles(args.Data);
            if (received.Length == 0 || files.Length != 0 && !received.SequenceEqual(files, StringComparer.OrdinalIgnoreCase))
            { Cancel(); return; }
            if (files.Length == 0)
            {
                files = received;
                CoverExternalTargets();
                if (closed) return;
                Rebuild();
            }
            dragState = DragState.Acquired;
            deadline.Stop();
            Opacity = 1;
        }
        var slot = FindSlot(args.GetPosition(canvas));
        foreach (var candidate in slots) Paint(candidate, candidate == slot);
        if (dragState == DragState.Acquired && slot is not null && (args.AllowedEffects & DragDropEffects.Copy) != 0)
            args.Effects = DragDropEffects.Copy;
    }

    // Only claim the desktop after OLE has supplied a supported file drag. Use physical
    // monitor bounds, including taskbars, and keep the original wheel above these HWNDs.
    private void CoverExternalTargets()
    {
        var wheelHandle = new WindowInteropHelper(this).Handle;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var surface = new Window
            {
                Title = "Tabrilo drag cancellation", ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), AllowDrop = true,
                Topmost = true, Width = 1, Height = 1, Focusable = false
            };
            surface.DragEnter += Reject;
            surface.DragOver += Reject;
            surface.Drop += Reject;
            rejectionWindows.Add(surface);
            surface.Show();
            var bounds = screen.Bounds;
            if (!SetWindowPos(new WindowInteropHelper(surface).Handle, wheelHandle, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height, 0x0010))
            {
                Cancel();
                return;
            }
        }

        static void Reject(object sender, DragEventArgs args)
        {
            args.Effects = DragDropEffects.None;
            args.Handled = true;
        }
    }

    private void OnLeave(object sender, DragEventArgs args)
    {
        if (dragState != DragState.Acquired) return;
        dragState = DragState.Outside;
        foreach (var slot in slots) Paint(slot, false);
        if (released) Cancel();
    }

    private void Cancel()
    {
        if (closed || dragState == DragState.Committed) return;
        completion.Cancel();
        dragState = DragState.Cancelled;
        Close();
    }

    public void EndGesture(bool escaped, DragGesture.NativePoint point)
    {
        if (!external || closed || dragState is DragState.Committed or DragState.Cancelled) return;
        released = true;
        var releaseSlot = escaped ? null : FindSlot(canvas.PointFromScreen(new Point(point.X, point.Y)));
        // Snapshot physical release coordinates even when OLE missed DragLeave. Never use the last hover.
        completion.Release(dragState == DragState.Acquired ? releaseSlot?.Command : null);
        if (escaped || dragState != DragState.Acquired || releaseSlot is null)
        {
            dragState = DragState.Cancelled;
            Opacity = .005;
        }
        // Keep a neutral OLE target alive until Explorer has consumed mouse-up.
        // A valid release also has a bounded wait if the source never sends Drop.
        deadline.Stop();
        deadline.Interval = TimeSpan.FromMilliseconds(160);
        deadline.Start();
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (closed || dragState == DragState.Committed) return;
        var received = GetFiles(args.Data);
        var slot = FindSlot(args.GetPosition(canvas));
        if (dragState != DragState.Acquired || slot is null || !completion.TryCommit(slot.Command) ||
            !received.SequenceEqual(files, StringComparer.OrdinalIgnoreCase) || (args.AllowedEffects & DragDropEffects.Copy) == 0)
        { Cancel(); return; }
        dragState = DragState.Committed;
        deadline.Stop();
        args.Effects = DragDropEffects.Copy;
        Close();
        Dispatcher.BeginInvoke(() => invoke(slot.Command, received), DispatcherPriority.Normal);
    }

    public static string[] GetFiles(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop, false) ||
                data.GetData(DataFormats.FileDrop, false) is not string[] paths || paths.Length is < 1 or > 200)
                return [];
            return paths.All(path => path is not null && System.IO.Path.IsPathFullyQualified(path))
                ? paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        catch (ExternalException) { return []; }
    }

    private void Choose(string command)
    {
        if (external) return;
        Close();
        var received = files;
        Dispatcher.BeginInvoke(() => invoke(command, received), DispatcherPriority.Normal);
    }

    private void Navigate(object sender, KeyEventArgs args)
    {
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var binding = key switch { Key.LeftShift or Key.RightShift => "Shift", Key.LeftCtrl or Key.RightCtrl => "Ctrl",
            Key.LeftAlt or Key.RightAlt => "Alt", _ => "" };
        var indexOfMode = Array.IndexOf(preferences.ModeKeys, binding);
        if (!external && indexOfMode >= 0 && !args.IsRepeat)
        { Switch((WheelMode)indexOfMode); args.Handled = true; return; }
        if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
        if (args.Key == Key.Enter && Keyboard.FocusedElement is Button button &&
            slots.FirstOrDefault(slot => slot.Button == button)?.Command is { } command)
        {
            Choose(command);
            args.Handled = true;
            return;
        }
        var direction = args.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (direction == 0) return;
        var enabled = slots.Where(slot => slot.Button.IsEnabled).ToList();
        if (enabled.Count == 0) return;
        var index = enabled.FindIndex(slot => slot.Button.IsKeyboardFocused);
        enabled[(index + direction + enabled.Count) % enabled.Count].Button.Focus();
        args.Handled = true;
    }


    public void PositionAt(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
        var bounds = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromPoint(new DragGesture.NativePoint { X = x, Y = y }, 2);
        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 ? (dpiX, dpiY) : (96U, 96U);
        var width = Math.Min(bounds.Width, (int)Math.Round(Diameter * Theme.TextScale * dpi.Item1 / 96.0));
        var height = Math.Min(bounds.Height, (int)Math.Round(Diameter * Theme.TextScale * dpi.Item2 / 96.0));
        SetWindowPos(handle, IntPtr.Zero, Math.Clamp(x - width / 2, bounds.Left, bounds.Right - width),
            Math.Clamp(y - height / 2, bounds.Top, bounds.Bottom - height), width, height, 0x0014);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(DragGesture.NativePoint point, uint flags);
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
