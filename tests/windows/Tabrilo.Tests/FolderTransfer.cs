using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using Tabrilo.Core;
using Tabrilo.Windows;

internal static class FolderTransfer
{
    // Drive the same native progress window that Windows displays for a cross-volume folder Move.
    public static async Task CancelAsync(string evidence, string otherVolume)
    {
        var source = Directory.CreateDirectory(Path.Combine(evidence, "transfer-source")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(otherVolume, "Tabrilo-" + Guid.NewGuid().ToString("N"))).FullName;
        var target = Path.Combine(destination, Path.GetFileName(source));
        var firstVolume = Path.GetPathRoot(FileActions.FinalDirectory(source))!;
        var secondVolume = Path.GetPathRoot(FileActions.FinalDirectory(destination))!;
        if (!firstVolume.StartsWith(@"\\?\Volume{") || !secondVolume.StartsWith(@"\\?\Volume{") ||
            firstVolume.Equals(secondVolume, StringComparison.OrdinalIgnoreCase)) throw new IOException("Two distinct volumes are required.");
        var bytes = new byte[16 * 1024];
        var hashes = new Dictionary<string, byte[]>();
        for (var i = 0; i < 4000; i++)
        {
            BitConverter.TryWriteBytes(bytes, i);
            var name = $"sample-{i:D4}.bin";
            hashes.Add(name, SHA256.HashData(bytes));
            File.WriteAllBytes(Path.Combine(source, name), bytes);
        }
        var result = new TaskCompletionSource<List<FileOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(FileActions.Move([source], destination, default)); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var owned = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId),
            new PropertyCondition(AutomationElement.ClassNameProperty, "OperationStatusWindow"));
        var cancel = new PropertyCondition(AutomationElement.AutomationIdProperty, "eltCancelButton");
        var elapsed = Stopwatch.StartNew();
        var requested = false;
        while (!result.Task.IsCompleted && elapsed.Elapsed < TimeSpan.FromMinutes(2))
        {
            if (Directory.Exists(target) && Directory.EnumerateFiles(target).Any())
            {
                var dialog = AutomationElement.RootElement.FindFirst(TreeScope.Children, owned);
                var button = dialog?.FindFirst(TreeScope.Descendants, cancel);
                if (button?.TryGetCurrentPattern(InvokePattern.Pattern, out var action) == true)
                {
                    ((InvokePattern)action).Invoke();
                    requested = true;
                    break;
                }
            }
            await Task.Delay(40);
        }
        var outcome = (await result.Task.WaitAsync(TimeSpan.FromMinutes(2))).Single();
        if (!requested || !outcome.Cancelled || outcome.Output != null || outcome.InspectionDirectory != destination)
            throw new IOException($"Native active folder cancellation was not confirmed. Requested={requested}, outcome={outcome}");
        var remaining = Directory.GetFiles(source).Length;
        var published = Directory.Exists(target) ? Directory.GetFiles(target).Length : 0;
        if (remaining == 0) throw new IOException("No source files remained after the interruption.");
        CheckAcross(source, target);
        // A partially populated sibling is deliberately not merged or overwritten on retry.
        var retry = Directory.CreateDirectory(Path.Combine(destination, "retry")).FullName;
        var moved = FileActions.Move([source], retry, default).Single();
        if (moved.Output == null || Directory.Exists(source)) throw new IOException($"Retry failed: {moved}");
        CheckAcross(moved.Output, target);
        Console.WriteLine($"Active folder transfer cancelled between {firstVolume} and {secondVolume}. " +
            $"{remaining} source and {published} destination files after cancellation. All {hashes.Count} hashes verified again after retry.");

        void CheckAcross(string left, string right)
        {
            foreach (var (name, expected) in hashes)
            {
                var found = false;
                foreach (var directory in new[] { left, right })
                {
                    var path = Path.Combine(directory, name);
                    if (!File.Exists(path)) continue;
                    if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(expected))
                        throw new IOException("Partial or changed file after interruption: " + name);
                    found = true;
                }
                if (!found) throw new IOException("Missing file after interruption: " + name);
            }
        }
    }
}
