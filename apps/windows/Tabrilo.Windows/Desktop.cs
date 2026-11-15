using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;

namespace Tabrilo.Windows;

internal sealed class Desktop(Window owner, Action quit) : IDisposable
{
    internal static readonly string InstallIdentity = typeof(Desktop).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "TabriloInstallIdentity").Value!;
    private System.Windows.Forms.NotifyIcon? tray;

    public void Tray(bool enabled)
    {
        if (enabled && tray is null)
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Tabrilo", null, (_, _) => Show());
            menu.Items.Add("Quit Tabrilo", null, (_, _) => quit());
            var executable = Environment.ProcessPath!;
            tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "Tabrilo", Icon = System.Drawing.Icon.ExtractAssociatedIcon(executable) ??
                    System.Drawing.SystemIcons.Application, ContextMenuStrip = menu, Visible = true
            };
            tray.DoubleClick += (_, _) => Show();
        }
        else if (!enabled) Dispose();
    }

    public void Show()
    {
        owner.Show();
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        owner.Activate();
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue(InstallIdentity, '"' + Environment.ProcessPath! + '"');
        else key.DeleteValue(InstallIdentity, false);
    }

    public static void OpenResult(string path)
    {
        if (Directory.Exists(path) || File.Exists(path) && Path.GetExtension(path).ToLowerInvariant() is
                ".png" or ".jpg" or ".webp" or ".pdf")
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var command = new ProcessStartInfo(explorer) { UseShellExecute = false };
        command.ArgumentList.Add("/select," + Path.GetFullPath(path));
        Process.Start(command);
    }

    public void Dispose()
    {
        if (tray is null) return;
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        tray = null;
    }
}
