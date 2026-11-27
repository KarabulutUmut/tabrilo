using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Tabrilo.Core;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal sealed record OperationRequest(bool Advanced = false, bool ChooseDestination = false);

internal sealed record OperationContext(CommandSpec Command, ReadOnlyCollection<OperationInput> Inputs,
    InvocationOrigin Origin, OperationRequest Request, OutputPolicy Output, CancellationToken Cancellation);

internal sealed class OperationCoordinator(
    Func<Preferences> preferences, Action<string> rememberClipboardFolder, Func<InvocationOrigin, Window?> owner,
    Func<KeyEventArgs, string, bool> keys, Action<bool> busy, Action<string> status,
    Action<string, IEnumerable<string>> publish) : IDisposable
{
    private readonly FileActions actions = new();
    private readonly JobResults results = new();
    private CancellationTokenSource? cancellation;
    public bool Busy => cancellation is not null;
    public void Cancel() => cancellation?.Cancel();
    public void ShowResults() => results.Open();
    public void ShowBusy(System.Drawing.Point point) => results.Busy(point);
    public bool OwnsResults(Window candidate) => results.Owns(candidate);
    public void Dispose() { actions.Dispose(); results.Dispose(); }

    public async Task RunAsync(string id, IEnumerable<OperationInput> inputs, InvocationOrigin origin, OperationRequest? request = null,
        System.Drawing.Point? invocationPoint = null)
    {
        if (Busy) return;
        var snapshot = Array.AsReadOnly(inputs.ToArray());
        var paths = snapshot.Select(input => input.Path).ToArray();
        if (Commands.All.FirstOrDefault(item => item.Id == id) is not { } command || !command.AppliesTo(paths))
        {
            status("Select compatible files for this action.");
            return;
        }
        using var stop = new CancellationTokenSource();
        var workArea = (invocationPoint is { } point ? System.Windows.Forms.Screen.FromPoint(point) :
            owner(origin) is { } home ? System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(home).Handle) :
            System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position)).WorkingArea;
        cancellation = stop;
        var settings = preferences();
        var context = new OperationContext(command, snapshot, origin, request ?? new(),
            new(settings.ExportDirectory, settings.ClipboardDirectory), stop.Token);
        var destination = new OperationDestination(context, owner(origin), rememberClipboardFolder);
        var route = OperationRouting.For(id, context.Request.Advanced);
        JobResults.JobView? view = null;
        busy(true);
        status(command.Label + "…");
        try
        {
            if (id.StartsWith("action.", StringComparison.Ordinal))
            {
                var summary = await actions.Execute(id, paths, owner(origin), stop.Token, Publish, () => Begin());
                Begin().Finish(summary);
            }
            else if (QuickOperations.Handles(id))
                await new QuickOperations(context, owner(origin), settings, destination, Begin, Publish).Execute(route);
            else
                await new Workflows(owner(origin), keys, destination, progress => Begin().Update(progress), Publish)
                    .Execute(command, paths, stop.Token);
            status(view?.Summary ?? "Ready.");
        }
        catch (OperationCanceledException)
        {
            view?.Finish("Cancelled");
            status("Cancelled. Published results are kept.");
        }
        catch (Exception error) when (IsOperationError(error))
        {
            var message = ErrorMessage(error);
            Begin().Finish("Could not complete", content => content.Children.Add(Controls.Text(message)));
            status(message);
        }
        finally
        {
            cancellation = null;
            busy(false);
        }

        JobResults.JobView Begin() => view ??= results.Start(command.Label, stop.Cancel, workArea, id == "action.move");

        void Publish(IReadOnlyList<FileOutcome> outcomes)
        {
            Begin().Files(outcomes);
            var outputs = outcomes.Where(item => item.Output is not null).Select(item => item.Output!).ToArray();
            status($"{outputs.Length}/{outcomes.Count} published");
            if (outputs.Length > 0) publish(command.Label, outputs);
        }
    }

    internal static bool IsOperationError(Exception error) =>
        error is IOException or InvalidDataException or ArgumentException or NotSupportedException
            or UnauthorizedAccessException or TimeoutException or ExternalException or System.ComponentModel.Win32Exception;

    internal static string ErrorMessage(Exception error) => error is ExternalException
        ? "Windows could not complete this operation." : error.Message;
}

internal sealed class OperationDestination(OperationContext context, Window? owner, Action<string> rememberClipboardFolder)
{
    private readonly Dictionary<string, string> redirects = new(StringComparer.OrdinalIgnoreCase);
    private string? selected;

    public bool IsClipboard(string path) => context.Inputs.Any(input => input.Clipboard &&
        string.Equals(input.Path, path, StringComparison.OrdinalIgnoreCase));

    public async Task<Dictionary<string, string>> Separate(bool changeDestination = false)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in context.Inputs)
            folders.TryAdd(input.Path, await Resolve(context.Output.For(input), input.Clipboard, combined: false, changeDestination));
        return folders;
    }

    public Task<string> Combined() => Resolve(context.Output.ForCombined(context.Inputs),
        context.Inputs.Any(input => input.Clipboard), combined: true);

    private async Task<string> Resolve(string? preferred, bool clipboard, bool combined, bool changeDestination = false)
    {
        var token = context.Cancellation;
        token.ThrowIfCancellationRequested();
        if (selected is not null) return selected;
        var key = preferred ?? "";
        if (redirects.TryGetValue(key, out var prior)) return prior;
        var reason = preferred is null ? clipboard
            ? "Choose a folder for clipboard exports"
            : "Choose one folder for the combined output"
            : "The export folder is unavailable. Choose another";
        changeDestination |= context.Request.ChooseDestination;
        if (!changeDestination && preferred is not null && await Writable(preferred, token))
            return redirects[key] = preferred;

        var picker = new OpenFolderDialog
        {
            Title = changeDestination ? "Change destination" : reason,
            InitialDirectory = Directory.Exists(preferred) ? preferred! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        var accepted = owner is { IsVisible: true } ? picker.ShowDialog(owner) : picker.ShowDialog();
        if (accepted != true) throw new OperationCanceledException();
        if (!await Writable(picker.FolderName, token))
            throw new IOException("The selected folder cannot accept a new export. Choose another and retry.");
        if (changeDestination || combined) selected = picker.FolderName;
        if (clipboard && !changeDestination) rememberClipboardFolder(picker.FolderName);
        return redirects[key] = picker.FolderName;
    }

    internal static Task<bool> Writable(string path, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        return IsWritable(path);
    }, token);

    internal static bool IsWritable(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(Path.Combine(path, $".tabrilo-probe-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return false; }
    }
}
