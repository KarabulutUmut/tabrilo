using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Windows;
using Tabrilo.Core;

namespace Tabrilo.Windows;
public partial class App : Application
{
    internal static string[] InputPaths { get; private set; } = [];
    internal static string BuildName { get; } = BuildTitle();

    private static string BuildTitle()
    {
        var assembly = typeof(App).Assembly;
        var version = assembly.GetName().Version!.ToString(3);
        var revision = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+');
        var commit = revision is [_, var hash] && hash.Length >= 8 ? hash[..8] : "local";
        return Updates.Configured ? $"Tabrilo {version}" : $"Tabrilo Development · {version} · {commit}";
    }

    protected override void OnStartup(StartupEventArgs args)
    {
        base.OnStartup(args);
        if (args.Args is ["--self-check"])
        {
            var ready = Commands.All.Count > 0 &&
                File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "qpdf", "qpdf.exe"));
            Shutdown(ready ? 0 : 1);
            return;
        }
        InputPaths = args.Args.Where(Path.IsPathFullyQualified).ToArray();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
