using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Tabrilo.Core;

namespace Tabrilo.Windows;

public partial class MainWindow : Window
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Desktop.InstallIdentity);
    private readonly LocalState storage = new(DataRoot);
    private readonly ViewState state;
    private readonly OperationCoordinator operations;
    private readonly Desktop desktop;
    private readonly DragGesture drag;
    private readonly List<FileStream> pasted = [];
    private Preferences preferences;
    private CancellationTokenSource? job;
    private Wheel? wheel;
    private string page = "home";
    private bool quitting;

    public MainWindow()
    {
        InitializeComponent();
        state = new(id => _ = Dispatch(id), CancelJob);
        DataContext = state;
        try { preferences = storage.Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { preferences = new(); state.Status = "Preferences are unavailable."; }
        Theme.Apply(preferences.Appearance);
        operations = new(() => preferences, folder => Save(preferences with { ClipboardDirectory = folder }),
            origin => origin != InvocationOrigin.ExplorerWheel && IsVisible && WindowState != WindowState.Minimized ? this : null,
            IsShortcut, SetBusy, status => state.Status = status, RecordOutputs);
        desktop = new(this, Quit, operations.ShowResults);
        desktop.Tray(preferences.UseTray);
        drag = new(() => state.Busy ? "Off" : preferences.DragModifier, () => preferences.DragButton,
            point => OpenWheel([], point), escaped => wheel?.EndGesture(escaped));
        Title = App.BuildName;
        Loaded += (_, _) =>
        {
            if (App.InputPaths.Length != 0) Add(App.InputPaths);
            else Render();
        };
        PreviewKeyDown += OnKey;
        DragOver += OnDrag;
        Drop += OnDrop;
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            SystemEvents.UserPreferenceChanged -= ThemeChanged;
            drag.Dispose();
            wheel?.Close();
            desktop.Dispose();
            operations.Dispose();
            foreach (var lease in pasted) lease.Dispose();
        };
    }

    private void ThemeChanged(object? sender, UserPreferenceChangedEventArgs args) =>
        Dispatcher.InvokeAsync(() => Theme.Apply(preferences.Appearance));

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (preferences.UseTray && !quitting)
        {
            args.Cancel = true;
            Hide();
            return;
        }
        if (state.Busy)
        {
            args.Cancel = true;
            RequestQuit();
        }
    }

    private void CancelJob() { job?.Cancel(); operations.Cancel(); }

    private void RequestQuit()
    {
        quitting = true;
        CancelJob();
        foreach (var window in Application.Current.Windows.OfType<Window>().ToArray())
            if (window != this && !operations.OwnsResults(window) && window.IsVisible) window.Close();
    }

    private void Quit()
    {
        RequestQuit();
        if (!state.Busy) Close();
    }

    private void SetBusy(bool value)
    {
        state.Busy = value;
        if (!value && quitting) Dispatcher.BeginInvoke(new Action(Close));
    }

    private async Task Dispatch(string id)
    {
        try
        {
            switch (id)
            {
                case "home":
                case "recent":
                case "settings": page = id; Render(); return;
                case "results": operations.ShowResults(); return;
                case "open":
                    var picker = new OpenFileDialog
                    {
                        Multiselect = true,
                        Filter = "Tabrilo files|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff;*.heic;*.heif;*.pdf;*.zip;*.tar;*.gz;*.tgz;*.rar|All files|*.*"
                    };
                    if (picker.ShowDialog(this) == true) Add(picker.FileNames);
                    return;
                case "paste": Paste(); return;
                case "wheel":
                    var cursor = System.Windows.Forms.Cursor.Position;
                    OpenWheel(CurrentInputs(), new DragGesture.NativePoint { X = cursor.X, Y = cursor.Y }, external: false);
                    return;
                case "updates":
                    await CheckForUpdate();
                    return;
            }

            await RunSelected(id);
        }
        catch (OperationCanceledException) { state.Status = "Cancelled. Published results are kept."; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or NotSupportedException or UnauthorizedAccessException or TimeoutException)
        { state.Status = error.Message; }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or System.Net.Http.HttpRequestException
                                             or System.Security.Cryptography.CryptographicException)
        { state.Status = "Windows or the update service could not complete this action."; }
    }

    private Task RunSelected(string id, OperationRequest? request = null) => state.Busy ? Task.CompletedTask :
        operations.RunAsync(id, state.Inputs.Select(item => new OperationInput(item.Path, item.Clipboard)),
            state.Inputs.Any(item => item.Clipboard) ? InvocationOrigin.Clipboard : InvocationOrigin.Home, request);

    private void Add(IEnumerable<string> paths, bool clipboard = false)
    {
        var current = CurrentInputs().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Take(201))
        {
            if (state.Inputs.Count >= 200 || !File.Exists(path) || !current.Add(path)) continue;
            var kind = FileKinds.Classify(path);
            state.Inputs.Add(new(path, Path.GetFileName(path) + (kind == "unsupported" ? "   ·   Unsupported format" : "   ·   " + kind), clipboard));
        }
        page = "home";
        state.Status = state.Inputs.Count == 0 ? "No files were added." : $"{state.Inputs.Count} selected file references.";
        Render();
    }

    private void Paste()
    {
        if (Clipboard.ContainsFileDropList()) { Add(Clipboard.GetFileDropList().Cast<string>(), clipboard: true); return; }
        var bitmap = Clipboard.GetImage();
        if (bitmap is null) { state.Status = "The clipboard has no file or image."; return; }
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 45_000_000)
            throw new ArgumentException("The pasted image exceeds the 45-megapixel limit.");
        var folder = Path.Combine(DataRoot, "Clipboard");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Pasted image {Guid.NewGuid():N}.png");
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(file);
            }
            pasted.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.DeleteOnClose));
        }
        catch { File.Delete(path); throw; }
        Add([path], clipboard: true);
    }

    private string[] CurrentInputs() => state.Inputs.Select(item => item.Path).ToArray();

    private void OnDrag(object sender, DragEventArgs args)
    {
        args.Effects = !state.Busy && args.Data.GetDataPresent(DataFormats.FileDrop, false) &&
            (args.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        var paths = Wheel.GetFiles(args.Data);
        if (!state.Busy && paths.Length > 0 && (args.AllowedEffects & DragDropEffects.Copy) != 0)
        { Add(paths); args.Effects = DragDropEffects.Copy; }
        else args.Effects = DragDropEffects.None;
        args.Handled = true;
    }

    private void OpenWheel(string[] paths, DragGesture.NativePoint at, bool external = true)
    {
        if (wheel is not null || state.Busy) return;
        if (!external && paths.Length == 0) { state.Status = "Choose files before opening the wheel."; return; }
        var clipboard = external ? [] : state.Inputs.Where(item => item.Clipboard).Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menu = new Wheel(paths, preferences.Favorites, (id, received) =>
        {
            if (state.Busy || quitting) return;
            var origin = external ? InvocationOrigin.ExplorerWheel : InvocationOrigin.KeyboardWheel;
            _ = operations.RunAsync(id, received.Select(path => new OperationInput(path, clipboard.Contains(path))), origin);
        }, external, TrackWheel);
        if (!external && IsVisible) menu.Owner = this;
        TrackWheel(menu);
        menu.PositionAt(at.X, at.Y);
        if (external) menu.Show();
        else menu.ShowDialog();
    }

    private void TrackWheel(Wheel menu)
    {
        wheel = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(wheel, menu)) wheel = null; };
    }

    private void OnKey(object sender, KeyEventArgs args)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        foreach (var binding in Commands.All.Where(item => item.Scope == "app"))
        {
            if (!IsShortcut(args, binding.Id)) continue;
            args.Handled = true;
            _ = Dispatch(binding.Id);
            return;
        }
    }

    private string? Binding(CommandSpec item) => preferences.Shortcuts.GetValueOrDefault(item.Id) ??
        item.Shortcut?.Replace("Primary", "Ctrl", StringComparison.Ordinal);

    private bool IsShortcut(KeyEventArgs args, string id)
    {
        var command = Commands.All.FirstOrDefault(item => item.Id == id);
        if (command is null || Binding(command) is not { } binding || ParseGesture(binding) is not { } gesture) return false;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        return gesture.Key == key && gesture.Modifiers == Keyboard.Modifiers;
    }

    private static KeyGesture? ParseGesture(string value)
    {
        try { return new KeyGestureConverter().ConvertFromInvariantString(value) as KeyGesture; }
        catch (Exception error) when (error is FormatException or ArgumentException or NotSupportedException) { return null; }
    }

    private void Render()
    {
        Body.Content = page switch { "recent" => Recent(), "settings" => Settings(), _ => Home() };
    }

    private StackPanel Home()
    {
        var panel = new StackPanel();
        var target = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 8),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 8) };
        target.SetResourceReference(Border.BackgroundProperty, "Surface");
        target.SetResourceReference(Border.BorderBrushProperty, "Border");
        var drop = new StackPanel();
        target.Child = drop;
        panel.Children.Add(target);
        Controls.Heading(drop, "Drop files here", 18);
        drop.Children.Add(Controls.Text("Images, PDFs and archives", secondary: true));
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(Controls.Button("Choose files…", () => _ = Dispatch("open"), true));
        buttons.Children.Add(Controls.Button("Paste", () => _ = Dispatch("paste")));
        drop.Children.Add(buttons);
        if (state.Inputs.Count == 0) return panel;

        Controls.Heading(panel, "Selected files", 16);
        var files = new ListBox { ItemsSource = state.Inputs, DisplayMemberPath = nameof(InputItem.Label),
            Height = Math.Clamp(state.Inputs.Count * 32, 70, 164), SelectionMode = SelectionMode.Extended };
        System.Windows.Automation.AutomationProperties.SetName(files, "Selected file references");
        panel.Children.Add(files);
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Controls.Button("Remove selected", () =>
        {
            foreach (var input in files.SelectedItems.Cast<InputItem>().ToArray()) state.Inputs.Remove(input);
            Render();
        }));
        actions.Children.Add(Controls.Button("Clear", () => { state.Inputs.Clear(); Render(); }));
        var key = Binding(Commands.All.First(item => item.Id == "wheel"));
        actions.Children.Add(Controls.Button($"Actions wheel · {key}", () => _ = Dispatch("wheel")));
        panel.Children.Add(actions);
        var applicable = Commands.All.Where(command => command.AppliesTo(CurrentInputs())).ToArray();
        if (applicable.Length == 0)
        {
            panel.Children.Add(Controls.Text("The selected file types have no common action. Remove incompatible files to continue."));
            return panel;
        }
        Controls.Heading(panel, "Actions", 16);
        var operations = new WrapPanel();
        foreach (var command in applicable)
        {
            var button = Controls.Button(command.Label, () => _ = Dispatch(command.Id));
            operations.Children.Add(button);
            if (command.Id == "pdf.compress")
                operations.Children.Add(Controls.Button("Lossy PDF compression…", () => _ = RunSelected(command.Id, new(Advanced: true))));
            if (command.Id == "qr.read" && CurrentInputs().Any(path => FileKinds.Classify(path) == "pdf"))
                operations.Children.Add(Controls.Button("Scan selected PDF pages…", () => _ = RunSelected(command.Id, new(Advanced: true))));
            if (command.Id is "archive.extract" or "image.metadata" or "pdf.compress" or "image.pdf" or "pdf.merge")
            {
                var alternative = new MenuItem { Header = "Change destination…" };
                alternative.Click += (_, _) => _ = RunSelected(command.Id, new(ChooseDestination: true));
                button.ContextMenu = new ContextMenu { Items = { alternative } };
            }
        }
        panel.Children.Add(operations);
        return panel;
    }

    private StackPanel Recent()
    {
        var panel = new StackPanel();
        Controls.Heading(panel, "Recent results");
        if (!preferences.KeepHistory || preferences.Recent.Length == 0)
            panel.Children.Add(Controls.Text(preferences.KeepHistory ? "Completed exports will appear here." : "Result history is off.", secondary: true));
        foreach (var result in preferences.Recent)
        {
            var exists = File.Exists(result.Path) || Directory.Exists(result.Path);
            Controls.Heading(panel, Path.GetFileName(result.Path), 16);
            panel.Children.Add(Controls.Text($"{result.Operation} · {(exists ? result.Path : "Missing file")} ", secondary: true));
            if (!exists) continue;
            var actions = new WrapPanel();
            actions.Children.Add(Controls.Button("Open", () => TryDesktop(() => Desktop.OpenResult(result.Path))));
            actions.Children.Add(Controls.Button("Reveal", () => TryDesktop(() => Desktop.Reveal(result.Path))));
            panel.Children.Add(actions);
        }
        if (preferences.Recent.Length > 0)
            panel.Children.Add(Controls.Button("Clear history", () => { Save(preferences with { Recent = [] }); Render(); }));
        return panel;
    }

    private StackPanel Settings()
    {
        var panel = new StackPanel { MaxWidth = 670, HorizontalAlignment = HorizontalAlignment.Left };
        Controls.Heading(panel, "Settings");
        Controls.Heading(panel, "Appearance", 16);
        var appearance = Controls.Choice(panel, "Color theme", ["System", "Light", "Dark"], preferences.Appearance);
        Controls.Heading(panel, "Shortcuts and wheel", 16);
        var modifier = Controls.Choice(panel, "Explorer drag modifier", ["Shift", "Ctrl", "Alt", "Off"], preferences.DragModifier);
        var dragButton = Controls.Choice(panel, "Explorer drag button", ["Primary", "Secondary"], preferences.DragButton);
        var fields = Commands.All.Where(command => command.Shortcut is not null).ToDictionary(command => command,
            command => Controls.Field(panel, command.Label, Binding(command)!, 30));
        var catalog = Commands.All.Where(item => item.Scope == "operation").ToArray();
        var favoriteList = new ListBox { SelectionMode = SelectionMode.Single, Height = 130, DisplayMemberPath = nameof(CommandSpec.Label) };
        Controls.AddLabel(panel, "Pinned wheel slots", favoriteList);
        var favorites = preferences.Favorites.Select(id => catalog.FirstOrDefault(item => item.Id == id)).OfType<CommandSpec>().Take(6).ToList();
        void RefreshFavorites(int index)
        {
            favoriteList.ItemsSource = favorites.ToArray();
            if (favorites.Count != 0) favoriteList.SelectedIndex = Math.Clamp(index, 0, favorites.Count - 1);
        }
        void MoveFavorite(int offset)
        {
            var index = favoriteList.SelectedIndex;
            if (index < 0 || index + offset < 0 || index + offset >= favorites.Count) return;
            (favorites[index], favorites[index + offset]) = (favorites[index + offset], favorites[index]);
            RefreshFavorites(index + offset);
        }
        RefreshFavorites(0);
        var pick = Controls.Choice(panel, "Action to pin", catalog.Select(item => item.Label).ToArray(), catalog[0].Label);
        var ordering = new WrapPanel();
        ordering.Children.Add(Controls.Button("Pin", () =>
        {
            if (favorites.Count >= 6) { state.Status = "Remove a pin before adding another."; return; }
            var favorite = catalog.First(item => item.Label == (string)pick.SelectedItem);
            if (!favorites.Contains(favorite)) favorites.Add(favorite);
            RefreshFavorites(favorites.Count - 1);
        }));
        ordering.Children.Add(Controls.Button("Earlier", () => MoveFavorite(-1)));
        ordering.Children.Add(Controls.Button("Later", () => MoveFavorite(1)));
        ordering.Children.Add(Controls.Button("Unpin", () =>
        {
            if (favoriteList.SelectedIndex < 0) return;
            var index = favoriteList.SelectedIndex;
            favorites.RemoveAt(index);
            RefreshFavorites(index);
        }));
        panel.Children.Add(ordering);

        Controls.Heading(panel, "Files and privacy", 16);
        var destination = Controls.Field(panel, "Default export folder (optional)", preferences.ExportDirectory ?? "", 2048);
        var clipboardFolder = Controls.Field(panel, "Clipboard export folder (optional)", preferences.ClipboardDirectory ?? "", 2048);
        var jpeg = Controls.Field(panel, "JPEG conversion quality", preferences.JpegQuality.ToString(), 3);
        var webp = Controls.Field(panel, "WebP conversion quality", preferences.WebPQuality.ToString(), 3);
        var history = Controls.Checkbox(panel, "Keep up to 50 recent result references", preferences.KeepHistory);
        Controls.Heading(panel, "Startup and tray", 16);
        panel.Children.Add(Controls.Text("The Explorer wheel works while Tabrilo is running.", 13, true));
        var tray = Controls.Checkbox(panel, "Keep Tabrilo in the notification area when the window closes", preferences.UseTray);
        var startup = Controls.Checkbox(panel, "Launch at sign-in", preferences.StartAtLogin);
        Controls.Heading(panel, "Updates and about", 16);
        panel.Children.Add(Controls.Text(App.BuildName + " · " +
            (Updates.Configured ? "Windows x64 stable" : "Update channel is unconfigured."), secondary: true));
        if (Updates.Configured) panel.Children.Add(Controls.Button("Check for updates…", () => _ = Dispatch("updates")));
        panel.Children.Add(Controls.Button("Save preferences", () =>
        {
            var shortcuts = new Dictionary<string, string>();
            var used = new HashSet<(string Scope, Key Key, ModifierKeys Modifiers)>();
            foreach (var (command, input) in fields)
            {
                if (ParseGesture(input.Text) is not { } gesture || (gesture.Modifiers & ModifierKeys.Windows) != 0 ||
                    gesture.Key == Key.F4 && gesture.Modifiers == ModifierKeys.Alt ||
                    !used.Add((command.Scope, gesture.Key, gesture.Modifiers)))
                { state.Status = $"Enter a valid, unique shortcut for {command.Label}."; return; }
                shortcuts[command.Id] = input.Text;
            }
            var export = string.IsNullOrWhiteSpace(destination.Text) ? null : destination.Text.Trim();
            var clip = string.IsNullOrWhiteSpace(clipboardFolder.Text) ? null : clipboardFolder.Text.Trim();
            if (new[] { export, clip }.OfType<string>().Any(path => !OperationDestination.IsWritable(path)))
            { state.Status = "Choose an existing writable export folder."; return; }
            if (!int.TryParse(jpeg.Text, out var jpgQuality) || jpgQuality is < 1 or > 100 ||
                !int.TryParse(webp.Text, out var webQuality) || webQuality is < 1 or > 100)
            { state.Status = "Enter conversion quality from 1 to 100."; return; }
            var desired = preferences with
            {
                Appearance = (string)appearance.SelectedItem, DragModifier = (string)modifier.SelectedItem,
                DragButton = (string)dragButton.SelectedItem, JpegQuality = jpgQuality, WebPQuality = webQuality,
                KeepHistory = history.IsChecked == true, UseTray = tray.IsChecked == true, StartAtLogin = startup.IsChecked == true,
                ExportDirectory = export, ClipboardDirectory = clip,
                Shortcuts = shortcuts, Favorites = favorites.Select(item => item.Id).ToArray(),
                Recent = history.IsChecked == true ? preferences.Recent : []
            };
            TryDesktop(() =>
            {
                if (desired.StartAtLogin != preferences.StartAtLogin) Desktop.SetStartup(desired.StartAtLogin);
                if (!Save(desired)) return;
                desktop.Tray(preferences.UseTray);
                Theme.Apply(preferences.Appearance);
                state.Status = "Preferences saved.";
            });
        }, true));
        return panel;
    }

    private async Task CheckForUpdate()
    {
        if (state.Busy || !Updates.Configured) return;
        using var cancellation = new CancellationTokenSource();
        job = cancellation;
        state.Busy = true;
        try
        {
            state.Status = "Checking the signed Windows update channel…";
            using var updates = Updates.Open();
            var available = await updates.CheckAsync(cancellation.Token);
            if (available is null) { state.Status = "No newer compatible update found."; return; }
            if (MessageBox.Show(this, $"Download Tabrilo {available.Version}?", "Signed update",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { state.Status = "Update declined."; return; }
            state.Status = "Downloading and verifying update…";
            var installer = await updates.DownloadAsync(available, DataRoot, cancellation.Token);
            if (MessageBox.Show(this, $"Install Tabrilo {available.Version} and close this window?", "Install update",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { File.Delete(installer); state.Status = "Update declined."; return; }
            using var installation = updates.StartInstaller(available, installer);
            state.Busy = false;
            Quit();
        }
        finally { job = null; SetBusy(false); }
    }

    private void RecordOutputs(string operation, IEnumerable<string> paths)
    {
        if (!preferences.KeepHistory) return;
        var entries = paths.Select(path => new RecentResult(operation, path, DateTimeOffset.Now));
        Save(preferences with { Recent = entries.Concat(preferences.Recent).Take(50).ToArray() });
        if (page == "recent") Render();
    }

    private bool Save(Preferences desired)
    {
        try { storage.Write(desired); preferences = desired; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { state.Status = "Could not save preferences."; return false; }
    }

    private void TryDesktop(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { state.Status = "Windows could not complete the action."; }
    }
}
