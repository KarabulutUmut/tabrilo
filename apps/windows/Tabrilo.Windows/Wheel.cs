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

internal sealed class DragGesture : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly Func<string> modifier;
    private readonly Func<string> button;
    private readonly Action<NativePoint> open;
    private readonly Action<bool> end;
    private NativePoint origin;
    private bool tracking;
    private bool opened;

    public DragGesture(Func<string> modifier, Func<string> button, Action<NativePoint> open, Action<bool> end)
    {
        this.modifier = modifier;
        this.open = open;
        this.button = button;
        this.end = end;
        timer.Tick += Tick;
        timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        var key = modifier() switch { "Shift" => 0x10, "Ctrl" => 0x11, "Alt" => 0x12, _ => 0 };
        var primary = GetSystemMetrics(23) == 0 ? 1 : 2;
        var pressed = Down(button() == "Secondary" ? 3 - primary : primary);
        if (!pressed)
        {
            if (opened) end(false);
            tracking = opened = false;
            return;
        }
        if (Down(0x1B))
        {
            if (opened) end(true);
            opened = true;
            return;
        }
        if (key == 0) return;
        if (opened || !GetCursorPos(out var point)) return;
        if (!tracking)
        {
            var window = GetForegroundWindow();
            var name = new System.Text.StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is not ("CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW")) return;
            origin = point;
            tracking = true;
        }
        if (Down(key) && Math.Abs(point.X - origin.X) + Math.Abs(point.Y - origin.Y) >= 20)
        {
            opened = true;
            open(point);
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose() => timer.Stop();

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder buffer, int count);
}

internal sealed class Wheel : Window
{
    internal const double Diameter = 328;
    private const int Sectors = 7;
    private const double Center = Diameter / 2;
    private const double Inner = 53;
    private const double Outer = 162;
    private const string More = "navigation.more";
    private const string Back = "navigation.back";
    private static readonly Brush Disk = Brush("#19232D");
    private static readonly Brush Segment = Brush("#253442");
    private static readonly Brush Selected = Brush("#155D57");
    private static readonly Brush Rim = Brush("#53697B");
    private static readonly Brush Mint = Brush("#79EDDC");
    private static readonly Brush Muted = Brush("#A0B0BF");

    private readonly bool external;
    private readonly Action<string, string[]> invoke;
    private readonly Action<Wheel> track;
    private readonly IReadOnlyList<string> favorites;
    private readonly Canvas canvas = new() { Width = Diameter, Height = Diameter };
    private readonly List<Slot> slots = [];
    private readonly TextBlock hint;
    private string[] files;
    private int group;
    private DragState dragState;
    private bool released;
    private bool closed;

    private enum DragState { Proposed, Acquired, Outside, Committed, Cancelled }
    private sealed record Slot(int Index, string Command, Button Button, System.Windows.Shapes.Path Path);

    public Wheel(string[] files, IReadOnlyList<string> favorites, Action<string, string[]> invoke, bool external, Action<Wheel> track)
    {
        this.files = files;
        this.favorites = favorites;
        this.invoke = invoke;
        this.track = track;
        this.external = external;
        Title = "Tabrilo actions";
        Width = Height = Diameter;
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
            Width = Diameter, Height = Diameter, Fill = Disk, Stroke = Rim, StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        canvas.Children.Add(disk);
        hint = new TextBlock
        {
            Width = 91, Height = 58, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = Muted, FontSize = 12, IsHitTestVisible = false,
            Text = external ? "Waiting for dragged files" : "Choose an action\nEsc to cancel"
        };
        Canvas.SetLeft(hint, Center - hint.Width / 2);
        Canvas.SetTop(hint, Center - 20);
        canvas.Children.Add(hint);
        canvas.Background = Brushes.Transparent;
        Content = new Viewbox { Child = canvas };
        Closed += (_, _) => closed = true;
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
        var applicable = Commands.All.Where(item => item.AppliesTo(files)).ToArray();
        var pinned = favorites.Take(6).Select(id => Commands.All.FirstOrDefault(item => item.Id == id)).ToArray();
        var used = pinned.OfType<CommandSpec>().Select(item => item.Id).ToHashSet();
        var remaining = applicable.Where(item => !used.Contains(item.Id)).ToArray();
        var start = group == 0 ? 0 : 6 - pinned.Length + (group - 1) * 5;
        var index = 0;
        if (group == 0)
        {
            foreach (var command in pinned)
            {
                if (command is not null) Add(command.Label, command.Id, index, command.AppliesTo(files));
                index++;
            }
        }
        var capacity = group == 0 ? 6 : 5;
        for (var item = start; index < capacity && item < remaining.Length; item++, index++)
            Add(remaining[item].Label, remaining[item].Id, index, true);
        var shown = group == 0 ? 6 - pinned.Length : 5;
        if (group > 0) Add("Back", Back, 5, true);
        if (remaining.Length > start + shown) Add(group == 0 ? "More" : "Next", More, 6, true);
    }

    private void Add(string label, string command, int index, bool available)
    {
        var angle = -Math.PI / 2 + index * Math.Tau / Sectors;
        var midpoint = At(angle, (Inner + Outer) / 2 + 6);
        var glyph = command switch
        {
            "image.convert" => "⇄", "image.compress" or "pdf.compress" => "▣", "image.redact" => "■",
            "image.crop" => "⌑", "qr.read" => "▦", "archive.extract" => "↓", More => "›", Back => "‹", _ => "·"
        };
        var shape = new System.Windows.Shapes.Path
        {
            Data = Sector(index), Fill = Segment, Stroke = Rim, StrokeThickness = 1, IsHitTestVisible = !external
        };
        var text = new TextBlock
        {
            Text = glyph + "\n" + label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, LineHeight = 14,
            Foreground = available ? Brushes.White : Muted, TextWrapping = TextWrapping.Wrap, Width = 87,
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
        slot.Path.Fill = active ? Selected : Segment;
        slot.Path.Stroke = active ? Mint : Rim;
        slot.Path.StrokeThickness = active ? 2.5 : 1;
    }

    internal static Geometry Sector(int index)
    {
        var angle = -Math.PI / 2 + index * Math.Tau / Sectors;
        var half = Math.PI / Sectors - .026;
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
        if (dragState is DragState.Proposed or DragState.Outside || args.RoutedEvent == DragEnterEvent)
        {
            files = (args.AllowedEffects & DragDropEffects.Copy) != 0 ? GetFiles(args.Data) : [];
            dragState = files.Length > 0 ? DragState.Acquired : DragState.Proposed;
            group = 0;
            Rebuild();
        }
        var slot = FindSlot(args.GetPosition(canvas));
        foreach (var candidate in slots) Paint(candidate, candidate == slot);
        hint.Text = files.Length == 0 ? "File drag required" : slot is null ? "Drop in center to cancel" : "Release to choose";
        if (dragState == DragState.Acquired && slot is not null && (args.AllowedEffects & DragDropEffects.Copy) != 0)
            args.Effects = DragDropEffects.Copy;
    }

    private void OnLeave(object sender, DragEventArgs args)
    {
        if (dragState != DragState.Acquired) return;
        dragState = DragState.Outside;
        files = [];
        group = 0;
        Rebuild();
        hint.Text = "Drag files back here";
        if (released) Close();
    }

    public void EndGesture(bool escaped)
    {
        if (!external || closed || dragState == DragState.Committed) return;
        if (escaped)
        {
            dragState = DragState.Cancelled;
            Close();
            return;
        }
        released = true;
        // A source can observe mouse-up before OLE dispatches Drop. OLE owns completion after acquisition.
        Dispatcher.BeginInvoke(() =>
        {
            if (!closed && dragState is DragState.Proposed or DragState.Outside) Close();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        if (dragState is DragState.Committed or DragState.Cancelled) return;
        var received = GetFiles(args.Data);
        var slot = FindSlot(args.GetPosition(canvas));
        var navigation = slot?.Command is More or Back;
        if (dragState != DragState.Acquired || slot is null || !received.SequenceEqual(files, StringComparer.OrdinalIgnoreCase) ||
            (args.AllowedEffects & DragDropEffects.Copy) == 0 ||
            !navigation && !Commands.All.Any(command => command.Id == slot.Command && command.AppliesTo(received)))
        {
            dragState = DragState.Cancelled;
            Close();
            return;
        }
        dragState = DragState.Committed;
        args.Effects = DragDropEffects.Copy;
        Dispatcher.BeginInvoke(() =>
        {
            if (closed) return;
            if (navigation)
            {
                var next = new Wheel(received, favorites, invoke, external: false, track) { group = 1, Left = Left, Top = Top };
                next.Rebuild();
                Close();
                track(next);
                next.Show();
                return;
            }
            Close();
            invoke(slot.Command, received);
        }, DispatcherPriority.Normal);
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
        if (command is More or Back)
        {
            group += command == More ? 1 : -1;
            Rebuild();
            slots.FirstOrDefault(slot => slot.Button.IsEnabled)?.Button.Focus();
            return;
        }
        Close();
        var received = files;
        Dispatcher.BeginInvoke(() => invoke(command, received), DispatcherPriority.Normal);
    }

    private void Navigate(object sender, KeyEventArgs args)
    {
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

    private static Brush Brush(string color)
    {
        var value = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        value.Freeze();
        return value;
    }

    public void PositionAt(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
        var bounds = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromPoint(new DragGesture.NativePoint { X = x, Y = y }, 2);
        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 ? (dpiX, dpiY) : (96U, 96U);
        var width = Math.Min(bounds.Width, (int)Math.Round(Diameter * dpi.Item1 / 96.0));
        var height = Math.Min(bounds.Height, (int)Math.Round(Diameter * dpi.Item2 / 96.0));
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
