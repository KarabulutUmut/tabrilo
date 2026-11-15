using System;
using System.IO;
using System.Linq;
using System.Windows;
using Tabrilo.Core;

namespace Tabrilo.Windows;
public partial class App : Application
{
    internal static string[] InputPaths { get; private set; } = [];

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
