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
    private readonly Action<NativePoint> open;
    private readonly Action close;
    private NativePoint origin;
    private bool tracking;
    private bool opened;

    public DragGesture(Func<string> modifier, Action<NativePoint> open, Action close)
    {
        this.modifier = modifier;
        this.open = open;
        this.close = close;
        timer.Tick += Tick;
        timer.Start();
    }

    private void Tick(object? sender, EventArgs args)
    {
        var key = modifier() switch { "Shift" => 0x10, "Ctrl" => 0x11, "Alt" => 0x12, _ => 0 };
        var pressed = Down(1);
        if (!pressed || key == 0 || Down(0x1B))
        {
            if (opened) close();
            tracking = opened = false;
            return;
        }
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
    private readonly bool external;
    private readonly Action<string, string[]> invoke;
    private readonly IReadOnlyList<string> favorites;
    private readonly Canvas canvas = new();
    private readonly List<Button> slots = [];
    private readonly TextBlock hint;
    private string[] files;
    private int group;
    private readonly DispatcherTimer? deadline;
    public Wheel(string[] files, IReadOnlyList<string> favorites, Action<string, string[]> invoke, bool external)
    {
        this.files = files;
        this.favorites = favorites;
        this.invoke = invoke;
        this.external = external;
        Title = "Tabrilo actions";
        Width = Height = 320;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = !external;
        AllowDrop = external;
        var disk = new Border
        {
            CornerRadius = new CornerRadius(158), Background = new SolidColorBrush(Color.FromRgb(26, 36, 48)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(101, 118, 137)), BorderThickness = new Thickness(1),
            Child = canvas
        };
        hint = new TextBlock
        {
            Width = 92, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Foreground = Brushes.White, FontSize = 13,
            Text = external ? "Drag onto an action" : "Choose an action"
        };
        Canvas.SetLeft(hint, 113);
        Canvas.SetTop(hint, 142);
        canvas.Children.Add(hint);
        Content = disk;
        PreviewKeyDown += Navigate;
        if (external)
        {
            DragEnter += OnDrag;
            DragOver += OnDrag;
            Drop += OnDrop;
            deadline = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            deadline.Tick += (_, _) => { deadline.Stop(); if (this.files.Length == 0) Close(); };
            deadline.Start();
            Closed += (_, _) => deadline.Stop();
        }
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (var slot in slots) canvas.Children.Remove(slot);
        slots.Clear();
        var applicable = Commands.All.Where(item => item.AppliesTo(files)).ToList();
        var pinned = favorites.Take(6).Select(id => Commands.All.FirstOrDefault(item => item.Id == id)).ToArray();
        var used = pinned.Where(item => item is not null).Select(item => item!.Id).ToHashSet();
        if (group == 0)
        {
            var items = applicable.Where(item => !used.Contains(item.Id)).ToList();
            for (var index = 0; index < 6; index++)
            {
                var item = index < pinned.Length ? pinned[index] : null;
                if (item is null && items.Count > 0) { item = items[0]; items.RemoveAt(0); }
                if (item is not null) Add(item.Label, item.Id, index, item.AppliesTo(files));
            }
            if (applicable.Any(item => !used.Contains(item.Id))) Add("More", "more", 6, files.Length > 0);
        }
        else
        {
            var remaining = applicable.Where(item => !used.Contains(item.Id)).ToArray();
            var start = (group - 1) * 6;
            for (var index = 0; index < Math.Min(6, remaining.Length - start); index++)
                Add(remaining[start + index].Label, remaining[start + index].Id, index, true);
            Add(remaining.Length > start + 6 ? "Next" : "Back", remaining.Length > start + 6 ? "more" : "back", 6, true);
        }
        if (!external) slots.FirstOrDefault(slot => slot.IsEnabled)?.Focus();
    }

    private void Add(string label, string command, int position, bool available)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Tag = command, IsEnabled = available, Width = 84, Height = 41, Margin = new Thickness(0), Padding = new Thickness(5),
            Background = new SolidColorBrush(Color.FromRgb(25, 42, 52)),
            Foreground = new SolidColorBrush(Color.FromRgb(242, 246, 250)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(94, 234, 212)), FontSize = 12
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => Choose(command);
        var angle = -Math.PI / 2 + position * Math.Tau / 7;
        Canvas.SetLeft(button, 158 + 109 * Math.Cos(angle) - 42);
        Canvas.SetTop(button, 158 + 109 * Math.Sin(angle) - 20);
        canvas.Children.Add(button);
        slots.Add(button);
    }

    private void OnDrag(object sender, DragEventArgs args)
    {
        if (files.Length == 0 && GetFiles(args.Data) is { Length: > 0 } received)
        {
            files = received;
            hint.Text = "Drop on an action";
            Rebuild();
        }
        var slot = FindSlot(args.GetPosition(canvas));
        args.Effects = files.Length > 0 && slot is { IsEnabled: true } &&
            (args.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        foreach (var button in slots)
        {
            var color = ReferenceEquals(slot, button) && button.IsEnabled ? Color.FromRgb(15, 118, 110) : Color.FromRgb(25, 42, 52);
            button.Background = new SolidColorBrush(color);
        }
        args.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        var received = GetFiles(args.Data);
        var slot = FindSlot(args.GetPosition(canvas));
        args.Effects = DragDropEffects.None;
        args.Handled = true;
        if (received.Length == 0 || (args.AllowedEffects & DragDropEffects.Copy) == 0 ||
            slot is not { IsEnabled: true } || slot.Tag is not string command) { Close(); return; }
        files = received;
        if (command is "more" or "back")
        {
            // Continue with the dropped selection in a keyboard-capable wheel.
            var next = new Wheel(files, favorites, invoke, external: false) { Left = Left, Top = Top, group = 1 };
            next.Rebuild();
            Close();
            next.Show();
            return;
        }
        if (Commands.All.Any(item => item.Id == command && item.AppliesTo(received)))
        {
            args.Effects = (args.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
            var snapshot = files;
            Dispatcher.BeginInvoke(() => Choose(command, snapshot), DispatcherPriority.Normal);
        }
        else Close();
    }

    private Button? FindSlot(Point point) => slots.FirstOrDefault(button =>
        new Rect(Canvas.GetLeft(button), Canvas.GetTop(button), button.Width, button.Height).Contains(point));

    public static string[] GetFiles(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop, false) &&
                data.GetData(DataFormats.FileDrop, false) is string[] paths ?
                paths.Where(System.IO.File.Exists).Take(201).ToArray() : []; }
        catch (ExternalException) { return []; }
    }

    private void Choose(string command, string[]? snapshot = null)
    {
        if (command is "more" or "back")
        {
            group = command == "more" ? group + 1 : 0;
            Rebuild();
            return;
        }
        if (IsVisible) Close();
        invoke(command, snapshot ?? files);
    }

    private void Navigate(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
        var direction = args.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (direction == 0) return;
        var enabled = slots.Where(slot => slot.IsEnabled).ToList();
        if (enabled.Count == 0) return;
        var index = enabled.FindIndex(slot => slot.IsKeyboardFocused);
        enabled[(index + direction + enabled.Count) % enabled.Count].Focus();
        args.Handled = true;
    }

    public void PositionAt(int x, int y)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
        var bounds = screen.WorkingArea;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var monitor = MonitorFromPoint(new DragGesture.NativePoint { X = x, Y = y }, 2);
        var dpi = GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0 ? (dpiX, dpiY) : (96U, 96U);
        var width = Math.Min(bounds.Width, (int)Math.Round(320 * dpi.Item1 / 96.0));
        var height = Math.Min(bounds.Height, (int)Math.Round(320 * dpi.Item2 / 96.0));
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
