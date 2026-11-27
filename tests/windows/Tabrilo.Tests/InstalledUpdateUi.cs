using System.Diagnostics;
using System.IO;
using System.Windows.Automation;

internal static class InstalledUpdateUi
{
    public static async Task Run(string original, string upgraded, string feed)
    {
        var start = new ProcessStartInfo(original) { UseShellExecute = false };
        start.ArgumentList.Add("--isolated-feed");
        start.ArgumentList.Add(feed);
        using var old = Process.Start(start) ?? throw new IOException("Could not start the installed review window.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        foreach (var title in new[] { "Signed update", "Install update" })
        {
            var match = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, old.Id),
                new PropertyCondition(AutomationElement.NameProperty, title));
            while (true)
            {
                await Task.Delay(150, timeout.Token);
                if (old.HasExited) throw new IOException("The installed app exited before update consent.");
                old.Refresh();
                if (old.MainWindowHandle == IntPtr.Zero) continue;
                var main = AutomationElement.FromHandle(old.MainWindowHandle);
                var dialog = main.FindFirst(TreeScope.Descendants, match) ??
                    AutomationElement.RootElement.FindFirst(TreeScope.Children, match);
                var yes = dialog?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
                if (yes is null) continue;
                ((InvokePattern)yes.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                Console.WriteLine($"Accepted installed application's {title} dialog.");
                break;
            }
        }
        await old.WaitForExitAsync(timeout.Token);
        while (true)
        {
            await Task.Delay(150, timeout.Token);
            foreach (var candidate in Process.GetProcessesByName("Tabrilo"))
            {
                using (candidate)
                {
                    if (candidate.MainWindowHandle == IntPtr.Zero ||
                        !string.Equals(candidate.MainModule?.FileName, upgraded, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!candidate.CloseMainWindow()) throw new IOException("Could not close the upgraded review window.");
                    await candidate.WaitForExitAsync(timeout.Token);
                    Console.WriteLine("Verified relaunched newer installed window.");
                    return;
                }
            }
        }
    }
}
