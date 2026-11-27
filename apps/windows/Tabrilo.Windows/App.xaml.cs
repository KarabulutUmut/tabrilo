using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows;
using Tabrilo.Core;

namespace Tabrilo.Windows;
public partial class App : Application
{
    private System.Threading.Mutex? instance;
    internal static string[] InputPaths { get; private set; } = [];
    internal static string BuildName { get; } = BuildTitle();
    internal static string Metadata(string name) => typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(item => item.Key == name).Value!;

    private static string BuildTitle()
    {
        var assembly = typeof(App).Assembly;
        var version = assembly.GetName().Version!.ToString(3);
        var revision = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+');
        var commit = revision is [_, var hash] && hash.Length >= 8 ? hash[..8] : "local";
        return $"Tabrilo · {version} · {Metadata("Flavor")} · {Metadata("Channel")} · {commit}";
    }

    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
#if ISOLATED_ACCEPTANCE
        if (args.Args is ["--isolated-feed", var isolatedFolder])
            Updates.IsolatedFolder = Path.GetFullPath(isolatedFolder);
        if (args.Args is ["--exercise-installed-update", var feedDirectory])
        {
            ExerciseInstalledUpdate(feedDirectory);
            return;
        }
#endif
        if (args.Args is ["--self-check"])
        {
            var ready = Commands.All.Count > 0 &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe"));
            Shutdown(ready ? 0 : 1);
            return;
        }
        instance = new System.Threading.Mutex(true, @"Local\" + Desktop.InstallIdentity + "-Wheel", out var first);
        if (!first && args.Args is ["--resume-update"])
        {
            try { first = instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (System.Threading.AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            MessageBox.Show($"Quit the running Tabrilo instance from its tray menu, then open {BuildName}.", "Tabrilo already running");
            Shutdown(2);
            return;
        }
        InputPaths = args.Args.Length > 0 && args.Args[0].StartsWith("--", StringComparison.Ordinal)
            ? [] : args.Args.Where(Path.IsPathFullyQualified).ToArray();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

#if ISOLATED_ACCEPTANCE
    private async void ExerciseInstalledUpdate(string directory)
    {
        try
        {
            using var updates = Updates.OpenIsolated(directory);
            var available = await updates.CheckAsync(default) ?? throw new InvalidDataException("Expected an upgrade.");
            var setup = await updates.DownloadAsync(available, directory, default);
            try
            {
                using var install = await updates.StartInstallerAsync(available, setup, default, unattendedTest: true);
                await install.WaitForExitAsync();
                Shutdown(install.ExitCode);
            }
            finally { File.Delete(setup); }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "update-error.txt"), error.GetType().Name + ": " + error.Message);
            Shutdown(1);
        }
    }
#endif
}
