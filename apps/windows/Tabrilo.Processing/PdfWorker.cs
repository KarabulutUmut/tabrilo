using System.Diagnostics;
using System.Text;

namespace Tabrilo.Processing;

internal sealed class PdfWorker
{
    private readonly string executable;

    public PdfWorker(string executable) => this.executable = Path.GetFullPath(executable);

    public async Task<string> RunAsync(IEnumerable<string> arguments, string? standardInput, CancellationToken token,
        bool normalizeGeneratedStreams = false)
    {
        if (!File.Exists(executable)) throw new NotSupportedException("The packaged PDF processor is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = Path.GetDirectoryName(executable)!
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        try
        {
            var output = ReadBounded(process.StandardOutput, 24 * 1024 * 1024, timeout.Token);
            var errors = ReadBounded(process.StandardError, 128 * 1024, timeout.Token);
            if (standardInput is not null)
                await process.StandardInput.WriteLineAsync(standardInput.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var remaining = new List<Task> { output, errors, process.WaitForExitAsync(timeout.Token) };
            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining);
                await completed;
                remaining.Remove(completed);
            }
            if (process.ExitCode != 0)
            {
                var error = await errors;
                if (error.Contains("invalid password", StringComparison.OrdinalIgnoreCase))
                    throw new PdfPasswordException();
                // PDFsharp's freshly generated drafts can trigger qpdf's Flate end-of-stream
                // notice during normalization. The normalized output must pass a separate strict check.
                if (!(normalizeGeneratedStreams && process.ExitCode == 3 && OnlyGeneratedFlateNotice(error)))
                    throw new InvalidDataException("The PDF processor rejected the document or could not confirm a valid result.");
            }
            return await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The PDF operation exceeded its time limit.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static bool OnlyGeneratedFlateNotice(string error)
    {
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 && lines[^1] == "qpdf: operation succeeded with warnings; resulting file may have some problems" &&
            lines[..^1].All(line => line.StartsWith("WARNING:", StringComparison.Ordinal) &&
                line.EndsWith(": input stream is complete but output may still be valid", StringComparison.Ordinal));
    }

    private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + read > limit) throw new InvalidDataException("PDF processor output exceeded its limit.");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }
}

public sealed class PdfPasswordException() : IOException("This PDF requires a valid password.");
