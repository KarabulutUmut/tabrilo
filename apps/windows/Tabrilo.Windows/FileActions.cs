using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private DataTransferManager? sharing;
    private IStorageItem[] shareItems = [];
    private readonly ObservableCollection<string> references = [];

    public async Task<string> Execute(string id, string[] paths, Window? owner, Action<IReadOnlyList<FileOutcome>> publish)
    {
        if (paths.Any(path => !File.Exists(path) && !Directory.Exists(path))) throw new IOException("A selected item is no longer available.");
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
                var results = new List<FileOutcome>();
                foreach (var path in paths)
                {
                    var target = Path.Combine(picker.FolderName, Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
                    try
                    {
                        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("An item with that name already exists.");
                        if (Directory.Exists(path)) FileSystem.MoveDirectory(path, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                        else FileSystem.MoveFile(path, target, UIOption.AllDialogs, UICancelOption.ThrowException);
                        results.Add(new(path, target));
                    }
                    catch (OperationCanceledException) { results.Add(new(path, null, "Move cancelled", true)); break; }
                    catch (Exception error) when (OperationCoordinator.IsOperationError(error)) { results.Add(new(path, null, error.Message)); }
                }
                publish(results);
                return $"{results.Count(item => item.Output != null)}/{paths.Length} moved";
            case "action.share":
                var items = new List<IStorageItem>();
                foreach (var path in paths)
                    items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
                var hwnd = new WindowInteropHelper(owner ?? Application.Current.MainWindow).EnsureHandle();
                var api = DataTransferManager.As<IShareInterop>();
                var iid = new Guid("a5caee9b-8708-49d1-8d36-67d25a8da00c");
                var ptr = api.GetForWindow(hwnd, ref iid);
                try
                {
                    if (sharing != null) sharing.DataRequested -= OnShare;
                    sharing = MarshalInterface<DataTransferManager>.FromAbi(ptr);
                    shareItems = items.ToArray();
                    sharing.DataRequested += OnShare;
                    api.ShowShareUIForWindow(hwnd);
                }
                finally { Marshal.Release(ptr); }
                return "Windows share opened";
            default: throw new ArgumentException("Unknown file action.");
        }
    }

    private void OnShare(DataTransferManager sender, DataRequestedEventArgs args)
    {
        args.Request.Data.Properties.Title = "Share selected items";
        args.Request.Data.SetStorageItems(shareItems, true);
        sender.DataRequested -= OnShare;
        shareItems = [];
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
        shelf?.Close();
        if (sharing != null) sharing.DataRequested -= OnShare;
        shareItems = [];
    }
}
