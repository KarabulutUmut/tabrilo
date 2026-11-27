namespace Tabrilo.Core;

public sealed record JobProgress(int Completed, int Total, string Label);
public sealed record FileOutcome(string Input, string? Output, string? Message = null, bool Cancelled = false,
    string? InspectionDirectory = null);
public sealed record BatchResult(IReadOnlyList<FileOutcome> Items)
{
    public int Published => Items.Count(item => item.Output is not null);
}

public static class Jobs
{
    public static Task<BatchResult> RunAsync(
        IReadOnlyList<string> inputs, Func<string, CancellationToken, Task<FileOutcome>> process,
        IProgress<JobProgress>? progress, CancellationToken token) => Task.Run(async () =>
    {
        var results = new List<FileOutcome>(inputs.Count);
        foreach (var path in inputs)
        {
            if (token.IsCancellationRequested)
            {
                results.Add(new(path, null, "Cancelled", true));
                continue;
            }

            progress?.Report(new(results.Count, inputs.Count, Path.GetFileName(path)));
            try { results.Add(await process(path, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                results.Add(new(path, null, "Cancelled", true));
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException
                or NotSupportedException or TimeoutException or InvalidDataException)
            {
                results.Add(new(path, null, error.Message));
            }
        }
        progress?.Report(new(results.Count, inputs.Count, "Complete"));
        return new BatchResult(results);
    });
}
