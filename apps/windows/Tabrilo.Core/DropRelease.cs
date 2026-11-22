namespace Tabrilo.Core;

// A destination may receive a delayed Drop or no Drop. Only a matching physical release can commit.
public sealed class DropRelease
{
    private string? releaseTarget;
    private bool ended;
    public bool Released { get; private set; }

    public void Release(string? target)
    {
        if (ended || Released) return;
        Released = true;
        releaseTarget = target;
        if (target is null) ended = true;
    }

    public void Cancel() => ended = true;

    public bool TryCommit(string? target)
    {
        if (ended) return false;
        ended = true;
        return Released && target is not null && target == releaseTarget;
    }
}
