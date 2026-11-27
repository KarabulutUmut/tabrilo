using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using Tabrilo.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT;

namespace Tabrilo.Windows;

internal sealed class FileActions : IDisposable
{
    private Window? shelf;
    private Window? shareHost;
    private DataTransferManager? sharing;
    private IStorageItem[] shareItems = [];
    private readonly ObservableCollection<string> references = [];

    public async Task<string> Execute(string id, string[] paths, Window? owner, CancellationToken token,
        Action<IReadOnlyList<FileOutcome>> publish, Action? started = null)
    {
        if (id != "action.move" && paths.Any(path => !File.Exists(path) && !Directory.Exists(path))) throw new IOException("A selected item is no longer available.");
        switch (id)
        {
            case "action.path":
                System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, paths));
                return "Paths copied";
            case "action.copy":
                var list = new StringCollection();
                list.AddRange(paths);
                System.Windows.Clipboard.SetFileDropList(list);
                return "File references copied";
            case "action.float":
                Float(paths);
                return "Added to Float";
            case "action.terminal":
                var dirs = paths.Select(path => Directory.Exists(path) ? path : Path.GetDirectoryName(path)!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var dir = dirs.Length == 1 ? dirs[0] : Dialogs.Ask<string>(owner, "Terminal folder", panel =>
                {
                    var field = Controls.Choice(panel, "Directory", dirs, dirs[0]);
                    return () => (string)field.SelectedItem;
                }, "Open", 245);
                if (dir is null) return "Cancelled";
                var terminal = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                Process.Start(new ProcessStartInfo(terminal) { WorkingDirectory = dir, UseShellExecute = true, ArgumentList = { "-NoProfile" } });
                return "Terminal opened";
            case "action.move":
                var picker = new OpenFolderDialog { Title = "Move selected items to" };
                if ((owner?.IsVisible == true ? picker.ShowDialog(owner) : picker.ShowDialog()) != true) return "Cancelled";
                var destination = picker.FolderName;
                started?.Invoke();
                var completion = new TaskCompletionSource<List<FileOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var worker = new Thread(() =>
                {
                    try { completion.SetResult(Move(paths, destination, token)); }
                    catch (Exception error) { completion.SetException(error); }
                }) { IsBackground = true };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
                var results = await completion.Task;
                publish(results);
                return $"{results.Count(item => item.Output != null)}/{paths.Length} moved";
            case "action.share":
                var items = new List<IStorageItem>();
                foreach (var path in paths)
                    items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                shareHost?.Close();
                var shareOwner = owner is { IsVisible: true, WindowState: not WindowState.Minimized } ? owner : OpenShareHost(paths);
                var ptr = IntPtr.Zero;
                try
                {
                    var hwnd = new WindowInteropHelper(shareOwner).EnsureHandle();
                    var api = DataTransferManager.As<IShareInterop>();
                    var iid = new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c");
                    ptr = api.GetForWindow(hwnd, ref iid);
                    if (sharing != null) sharing.DataRequested -= OnShare;
                    sharing = MarshalInterface<DataTransferManager>.FromAbi(ptr);
                    shareItems = items.ToArray();
                    sharing.DataRequested += OnShare;
                    api.ShowShareUIForWindow(hwnd);
                }
                catch { shareHost?.Close(); throw; }
                finally { if (ptr != IntPtr.Zero) Marshal.Release(ptr); }
                return "Windows share opened";
            default: throw new ArgumentException("Unknown file action.");
        }
    }

    internal static List<FileOutcome> Move(string[] paths, string destination, CancellationToken token,
        Action<FileOutcome>? completed = null)
    {
        var results = new List<FileOutcome>(paths.Length);
        var cancelled = false;
        foreach (var path in paths)
        {
            if (cancelled || token.IsCancellationRequested)
            {
                results.Add(new(path, null, "Skipped after cancellation", true));
                continue;
            }
            var target = Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
            var transferStarted = false;
            try
            {
                var folder = Directory.Exists(path);
                if (!folder && !File.Exists(path)) throw new FileNotFoundException("The selected item is no longer available.", path);
                var parent = FinalDirectory(destination);
                var source = FinalDirectory(folder ? path : Path.GetDirectoryName(path)!);
                if (folder && (parent.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    parent.StartsWith(Path.EndsInDirectorySeparator(source) ? source : source + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Choose a destination outside the selected folder.");
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists.");
                if (Path.GetPathRoot(parent)!.Equals(Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
                {
                    if (folder) Directory.Move(path, target);
                    else File.Move(path, target);
                }
                else if (folder)
                {
                    transferStarted = true;
                    try { FileSystem.MoveDirectory(path, target, UIOption.AllDialogs, UICancelOption.ThrowException); }
                    catch (InvalidOperationException error) { throw new IOException("Windows could not move the folder to this destination.", error); }
                }
                else
                {
                    transferStarted = true;
                    FileSystem.MoveFile(path, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                }
                results.Add(new(path, target, "Moved"));
            }
            catch (OperationCanceledException) { cancelled = true; results.Add(Incomplete("Move cancelled.", true)); }
            catch (Exception error) when (OperationCoordinator.IsOperationError(error)) { results.Add(Incomplete(error.Message, false)); }
            completed?.Invoke(results[^1]);

            FileOutcome Incomplete(string reason, bool stopped) => new(path, null, transferStarted
                ? $"{reason} Some items may have transferred. Inspect both {path} and {destination} before retrying."
                : reason, stopped, transferStarted ? destination : null);
        }
        return results;
    }

    private static string FinalDirectory(string path)
    {
        using var handle = CreateFileW(Path.GetFullPath(path), 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        var name = new StringBuilder(32768);
        var size = handle.IsInvalid ? 0 : GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 1);
        if (size == 0 && !handle.IsInvalid) size = GetFinalPathNameByHandleW(handle, name, (uint)name.Capacity, 0);
        if (size == 0 || size >= name.Capacity) throw new IOException("Windows could not resolve the selected folder.");
        return Path.TrimEndingDirectorySeparator(name.ToString());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder path, uint capacity, uint flags);

    private void OnShare(DataTransferManager sender, DataRequestedEventArgs args)
    {
        args.Request.Data.Properties.Title = "Share selected items";
        args.Request.Data.SetStorageItems(shareItems, true);
        if (shareHost is { } host)
        {
            args.Request.Data.ShareCanceled += (_, _) => host.Dispatcher.BeginInvoke(() => { if (shareHost == host) host.Close(); });
            args.Request.Data.ShareCompleted += (_, _) => host.Dispatcher.BeginInvoke(() => { if (shareHost == host) host.Close(); });
        }
        sender.DataRequested -= OnShare;
        shareItems = [];
    }

    private Window OpenShareHost(string[] paths)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var window = Dialogs.WindowFor(null, "Share", panel, 360, 200);
        panel.Children.Add(Controls.Text(paths.Length == 1 ? Path.GetFileName(paths[0]) : $"{paths.Length} selected items"));
        panel.Children.Add(Controls.Button("Close", window.Close));
        window.Closed += (_, _) =>
        {
            if (sharing != null) sharing.DataRequested -= OnShare;
            shareItems = [];
            shareHost = null;
        };
        shareHost = window;
        window.Show();
        window.Activate();
        return window;
    }

    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShareInterop
    {
        IntPtr GetForWindow(IntPtr hwnd, ref Guid iid);
        void ShowShareUIForWindow(IntPtr hwnd);
    }

    private void Float(string[] paths)
    {
        var additions = paths.Except(references, StringComparer.OrdinalIgnoreCase).ToArray();
        if (references.Count + additions.Length > 64) throw new ArgumentException("Float holds up to 64 items. Remove some and try again.");
        foreach (var path in additions) references.Add(path);
        if (shelf != null) { shelf.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(10) };
        var list = new ListBox { ItemsSource = references, SelectionMode = SelectionMode.Extended, Height = 110 };
        panel.Children.Add(list);
        Point? origin = null;
        list.PreviewMouseLeftButtonDown += (_, e) => origin = e.GetPosition(list);
        list.MouseMove += (_, e) =>
        {
            if (origin is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var offset = e.GetPosition(list) - start;
            if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            origin = null;
            var selected = list.SelectedItems.Cast<string>().ToArray();
            if (selected.Length > 0) DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, selected), DragDropEffects.Copy);
        };
        var buttons = new WrapPanel();
        buttons.Children.Add(Controls.Button("Remove", () => { foreach (var path in list.SelectedItems.Cast<string>().ToArray()) references.Remove(path); }));
        buttons.Children.Add(Controls.Button("Clear", references.Clear));
        panel.Children.Add(buttons);
        shelf = Dialogs.WindowFor(null, "Float", panel, 360, 215);
        shelf.Topmost = true;
        shelf.Closed += (_, _) => { references.Clear(); shelf = null; };
        shelf.Show();
    }

    public void Dispose()
    {
        shareHost?.Close();
        shelf?.Close();
        if (sharing != null) sharing.DataRequested -= OnShare;
        shareItems = [];
    }
}
