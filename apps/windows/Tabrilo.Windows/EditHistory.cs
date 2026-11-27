using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Tabrilo.Windows;

internal sealed class EditHistory<T>
{
    private readonly LinkedList<T> entries = new();
    public void Clear() => entries.Clear();

    public void Push(T value)
    {
        entries.AddLast(value);
        if (entries.Count > 64) entries.RemoveFirst();
    }

    public bool TryPop([MaybeNullWhen(false)] out T value)
    {
        if (entries.Last is not { } last) { value = default; return false; }
        value = last.Value;
        entries.RemoveLast();
        return true;
    }
}
