using System.Runtime.InteropServices;
using Tabrilo.Core;

namespace Tabrilo.Processing;

public static class OutputGroup
{
    // All members are validated flat files. Only paths returned by our writer can be cleaned up.
    public static async Task<string> WriteAsync(string parent, string stem, int count,
        Func<string, int, CancellationToken, Task<string>> write, CancellationToken token)
    {
        OutputFiles.ValidateLeaf(stem);
        var stage = Path.Combine(parent, ".tabrilo-pages-" + Guid.NewGuid().ToString("N"));
        token.ThrowIfCancellationRequested();
        if (!CreateDirectory(stage, IntPtr.Zero)) throw new IOException("Could not create an output staging folder.");
        var owned = new List<string>();
        try
        {
            using (var pin = new DirectoryPin(stage))
                for (var index = 0; index < count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    owned.Add(await write(stage, index, token).ConfigureAwait(false));
                }
            token.ThrowIfCancellationRequested();
            for (var index = 0; index < 1000; index++)
            {
                var output = Path.Combine(parent, OutputFiles.Candidate(stem, index));
                try { Directory.Move(stage, output); return output; }
                catch (IOException) when (Directory.Exists(output) || File.Exists(output)) { }
            }
            throw new IOException("Could not choose a unique output folder.");
        }
        catch
        {
            using (var pin = new DirectoryPin(stage))
                foreach (var file in owned) File.Delete(file);
            Directory.Delete(stage, false);
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW")]
    private static extern bool CreateDirectory(string path, IntPtr security);
}
