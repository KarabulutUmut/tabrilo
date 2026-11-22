using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Tabrilo.Core;

namespace Tabrilo.Windows;

// Observe physical edges without consuming them. Explorer owns the OLE source loop and Escape.
internal sealed class DragGesture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint { public int X; public int Y; }

    private delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    private readonly Hook mouse, keyboard;
    private readonly IntPtr mouseHandle;
    private IntPtr keyHandle;
    private readonly Func<string[]> bindings;
    private readonly Action<NativePoint, WheelMode> select;
    private readonly Action<bool, NativePoint> finish;
    private readonly bool[] held = new bool[3];
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private NativePoint origin;
    private bool invoked, cancelled;
    private int generation;
    private static bool Swapped => GetSystemMetrics(23) != 0;
    private static bool Down(int key) => GetAsyncKeyState(key) < 0;

    public DragGesture(Func<string[]> bindings, Action<NativePoint, WheelMode> select, Action<bool, NativePoint> finish)
    {
        this.bindings = bindings;
        this.select = select;
        this.finish = finish;
        mouse = Mouse;
        keyboard = Key;
        mouseHandle = SetWindowsHookEx(14, mouse, GetModuleHandle(null), 0);
        watchdog.Tick += (_, _) =>
        {
            if (!Down(Swapped ? 2 : 1) && GetCursorPos(out var point)) End(true, point);
        };
    }

    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var msg = message.ToInt32();
            if (msg == (Swapped ? 0x204 : 0x201))
            {
                var point = Marshal.PtrToStructure<NativePoint>(data);
                End(true, point);
                var name = new StringBuilder(128);
                GetClassName(GetAncestor(WindowFromPoint(point), 2), name, name.Capacity);
                if (name.ToString() is "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW")
                {
                    origin = point;
                    for (var i = 0; i < held.Length; i++) held[i] = Down(0x10 + i);
                    keyHandle = SetWindowsHookEx(13, keyboard, GetModuleHandle(null), 0);
                    watchdog.Start();
                }
            }
            else if (msg == (Swapped ? 0x205 : 0x202)) End(false, Marshal.PtrToStructure<NativePoint>(data));
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private IntPtr Key(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !cancelled && GetCursorPos(out var point))
        {
            var key = Marshal.ReadInt32(data);
            var down = message.ToInt32() is 0x100 or 0x104;
            if (key == 0x1b && down)
            {
                cancelled = true;
                if (invoked) finish(true, point);
            }
            var index = key switch { 0xa0 or 0xa1 => 0, 0xa2 or 0xa3 => 1, 0xa4 or 0xa5 => 2, _ => -1 };
            if (index >= 0)
            {
                var fresh = down && !held[index];
                held[index] = down || Down(key ^ 1);
                if (fresh && (invoked || Math.Abs(point.X - origin.X) >= GetSystemMetrics(68) ||
                    Math.Abs(point.Y - origin.Y) >= GetSystemMetrics(69)))
                {
                    var mode = Array.IndexOf(bindings(), WheelCatalog.Shared.Bindings[index]);
                    if (mode >= 0)
                    {
                        invoked = true;
                        var epoch = generation;
                        // Avoid creating the overlay inside the system's low-level input hook.
                        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                        {
                            if (epoch == generation && !cancelled && invoked) select(point, (WheelMode)mode);
                        }, DispatcherPriority.Input);
                    }
                }
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    private void End(bool escape, NativePoint point)
    {
        generation++;
        watchdog.Stop();
        if (keyHandle != IntPtr.Zero) { UnhookWindowsHookEx(keyHandle); keyHandle = IntPtr.Zero; }
        if (invoked && !cancelled) finish(escape, point);
        invoked = cancelled = false;
    }

    public void Dispose()
    {
        End(true, default);
        if (mouseHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHandle);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, Hook proc, IntPtr module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
