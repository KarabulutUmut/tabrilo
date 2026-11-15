using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Tabrilo.Processing;

internal sealed class DirectoryPin : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];

    public DirectoryPin(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0]))
            throw new IOException("Choose a folder on a local Windows volume.");

        var parents = new Stack<string>();
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            parents.Push(current.FullName);
        try
        {
            foreach (var parent in parents) Pin(parent);
        }
        catch { Dispose(); throw; }
    }

    private void Pin(string path)
    {
        const uint readAttributes = 0x80;
        const uint shareRead = 0x1;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        var nativePath = @"\\?\" + path;
        var handle = CreateFile(nativePath, readAttributes, shareRead, IntPtr.Zero, 3,
            backupSemantics | openReparsePoint, IntPtr.Zero);
        handles.Add(handle);
        if (handle.IsInvalid) throw Error("Could not lock an extraction directory.");
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, 8))
            throw Error("Could not validate an extraction directory.");
        if ((attributes.Attributes & FileAttributes.ReparsePoint) != 0 ||
            (attributes.Attributes & FileAttributes.Directory) == 0)
            throw new IOException("Extraction cannot follow directory links or other reparse points.");

        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity ||
            !Path.TrimEndingDirectorySeparator(final.ToString()).Equals(
                Path.TrimEndingDirectorySeparator(nativePath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("An extraction directory resolved outside its expected path.");
    }

    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Dispose();
        handles.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public FileAttributes Attributes; public uint Tag; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTag info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
}
