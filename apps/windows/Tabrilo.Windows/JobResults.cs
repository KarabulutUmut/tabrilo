using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Tabrilo.Core;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal sealed class JobResults : IDisposable
{
    private readonly StackPanel jobs = new() { Margin = new Thickness(16) };
    private readonly Window window;
    private Window? notice;

    private void Notify(string text, string button, Action click, string? output = null)
    {
        notice?.Close();
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(Controls.Text(text));
        var buttons = new WrapPanel();
        buttons.Children.Add(Controls.Button(button, click));
        if (output != null) buttons.Children.Add(Controls.Button("Reveal", () => Desktop.Reveal(output)));
        buttons.Children.Add(Controls.Button("Dismiss", () => notice?.Close()));
        panel.Children.Add(buttons);
        var area = SystemParameters.WorkArea;
        var popup = new Window { Title = "Tabrilo", Content = panel, Width = 340, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowActivated = false,
            ShowInTaskbar = false, Left = Math.Max(area.Left, area.Right - 350), Top = Math.Max(area.Top, area.Bottom - 155) };
        notice = popup;
        popup.Closed += (_, _) => { if (notice == popup) notice = null; };
        popup.Show();
    }

    public JobResults()
    {
        window = Dialogs.WindowFor(null, "Results", jobs, 480, 365);
        window.ShowActivated = false;
        window.Closing += Hide;
    }

    public void Open()
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public bool Owns(Window candidate) => ReferenceEquals(window, candidate);

    public JobView Start(string label, Action cancel)
    {
        foreach (var previous in jobs.Children.OfType<Expander>()) previous.IsExpanded = false;
        var details = new StackPanel { Margin = new Thickness(8) };
        var entry = new Expander { Header = label, IsExpanded = true, Content = details, Margin = new Thickness(0, 4, 0, 6) };
        jobs.Children.Insert(0, entry);
        while (jobs.Children.Count > 50) jobs.Children.RemoveAt(50);
        Notify(label + "…", "Cancel", cancel);
        return new JobView(this, entry, details, label, cancel);
    }

    private void Hide(object? sender, CancelEventArgs args)
    {
        args.Cancel = true;
        window.Hide();
    }

    public void Dispose()
    {
        notice?.Close();
        window.Closing -= Hide;
        window.Close();
    }

    internal sealed class JobView
    {
        private readonly JobResults owner;
        private readonly Expander entry;
        private readonly Panel panel;
        private readonly string label;
        private readonly TextBlock status = Controls.Text("Starting…", 13, true);
        private readonly ProgressBar bar = new() { Height = 5, Margin = new Thickness(0, 5, 0, 10), IsIndeterminate = true };
        private readonly Button stop;
        private bool finished;
        public string? Summary { get; private set; }

        public JobView(JobResults owner, Expander entry, Panel panel, string label, Action cancel)
        {
            this.owner = owner;
            this.entry = entry;
            this.panel = panel;
            this.label = label;
            panel.Children.Add(status);
            panel.Children.Add(bar);
            stop = Controls.Button("Cancel", () => { status.Text = "Cancelling…"; cancel(); stop!.IsEnabled = false; });
            panel.Children.Add(stop);
        }

        public void Update(JobProgress progress)
        {
            if (finished || !stop.IsEnabled) return;
            status.Text = progress.Total > 0 ? $"{progress.Completed}/{progress.Total} · {progress.Label}" : progress.Label;
            bar.IsIndeterminate = progress.Total <= 0;
            bar.Maximum = Math.Max(1, progress.Total);
            bar.Value = progress.Completed;
        }

        public void Finish(string summary, Action<Panel>? write = null, string? output = null)
        {
            if (finished) return;
            finished = true;
            Summary = summary;
            entry.Header = $"{label} · {summary}";
            panel.Children.Clear();
            write?.Invoke(panel);
            owner.Notify(label + " · " + summary, "Details", owner.Open, output);
            panel.Children.Add(Controls.Button("Dismiss", () =>
            {
                owner.jobs.Children.Remove(entry);
                if (owner.jobs.Children.Count == 0) owner.window.Hide();
            }));
        }

        public void Files(IReadOnlyList<FileOutcome> outcomes)
        {
            var published = outcomes.Count(item => item.Output is not null);
            var summary = published == 0 && outcomes.FirstOrDefault()?.Message is { } reason ? reason : $"{published}/{outcomes.Count} published";
            Finish(summary, content =>
            {
                foreach (var result in outcomes)
                {
                    Controls.Heading(content, System.IO.Path.GetFileName(result.Input), 14);
                    if (result.Message is { } message) content.Children.Add(Controls.Text(message, 13, true));
                    if (result.Output is not { } output) continue;
                    content.Children.Add(Controls.Text(output, 12, true));
                    var actions = new WrapPanel();
                    var error = Controls.Text("", 12, true);
                    actions.Children.Add(Controls.Button("Open", () => Run(() => Desktop.OpenResult(output), error)));
                    actions.Children.Add(Controls.Button("Reveal", () => Run(() => Desktop.Reveal(output), error)));
                    content.Children.Add(actions);
                    content.Children.Add(error);
                }
            }, outcomes.FirstOrDefault(item => item.Output != null)?.Output);
        }

        public void Codes(IReadOnlyList<(string Name, QrResult Result)> codes, IReadOnlyList<string> notes)
        {
            Finish(codes.Count == 0 && notes.Count == 0 ? "No QR codes found" : $"{codes.Count} QR codes", content =>
            {
                foreach (var note in notes) content.Children.Add(Controls.Text(note, 12, true));
                if (codes.Count == 0) return;
                var list = new ListBox { Height = 96, ItemsSource = codes.Select(item =>
                    $"{item.Name}{(item.Result.Page is { } page ? $" · page {page}" : "")} · {item.Result.Type}").ToArray() };
                Controls.AddLabel(content, "Detected codes", list);
                var payload = Controls.Field(content, "Payload", "");
                payload.IsReadOnly = true;
                payload.MaxLength = 0;
                payload.TextWrapping = TextWrapping.Wrap;
                var location = Controls.Text("", 12, true);
                content.Children.Add(location);
                var error = Controls.Text("", 12, true);
                var actions = new WrapPanel();
                actions.Children.Add(Controls.Button("Copy payload", () => Run(() => Clipboard.SetText(payload.Text), error)));
                var open = Controls.Button("Open link…", () =>
                {
                    if (list.SelectedIndex < 0 || codes[list.SelectedIndex].Result.AllowedLink is not { } uri) return;
                    if (MessageBox.Show(owner.window, $"Open this address in your browser?\n\n{uri.AbsoluteUri}", "Confirm link",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                        Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                            { UseShellExecute = true }), error);
                });
                actions.Children.Add(open);
                content.Children.Add(actions);
                content.Children.Add(error);
                list.SelectionChanged += (_, _) =>
                {
                    if (list.SelectedIndex < 0) return;
                    var result = codes[list.SelectedIndex].Result;
                    payload.Text = result.Payload;
                    location.Text = string.Join(" · ", result.Location.Select(point => $"({point.X}, {point.Y})"));
                    open.IsEnabled = result.AllowedLink is not null;
                };
                list.SelectedIndex = 0;
            });
        }

        private static void Run(Action action, TextBlock error)
        {
            try { action(); error.Text = ""; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            { error.Text = "Windows could not complete that action."; }
        }
    }
}
