using System.IO;

internal static class Program
{
    // WPF and its COM rendering resources share the lifetime of the process's main STA.
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments is not [var picture, var document])
            throw new ArgumentException("Supply an image and a PDF from the repository root.");
        var evidence = Directory.CreateDirectory(Path.Combine("artifacts", "tests",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId));
        return DesktopAcceptance.RunOnMainThread(Path.GetFullPath(picture), Path.GetFullPath(document), evidence.FullName);
    }
}
