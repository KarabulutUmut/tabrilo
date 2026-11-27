using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Tabrilo.Core;

namespace Tabrilo.Processing;

// Every creation is relative to a live, already validated directory handle. FILE_CREATE
// returns the handle to the newly created object in the same kernel operation.
internal sealed class ExtractionStage : IDisposable
{
    private sealed record Entry(Entry? Parent, string Leaf, bool Directory, FileId Id, SafeFileHandle Initial)
    {
        public SafeFileHandle Handle = Initial;
    }

    private readonly List<Entry> entries = [];
    private readonly Dictionary<string, Entry> directories = new(StringComparer.Ordinal);
    private readonly string folder;
    private bool published;
    private bool sealedForRename;
    private ChangeNotification? changes;

    public ExtractionStage(string folder, SafeFileHandle destination)
    {
        this.folder = folder;
        var leaf = ".tabrilo-extract-" + Guid.NewGuid().ToString("N");
        directories[""] = Remember(null, leaf, true, Open(destination, leaf, true, create: true));
    }

    public void AddDirectory(string relative, string parent, string leaf) =>
        directories.Add(relative, Create(directories[parent], leaf, true));

    public FileStream AddFile(string parent, string leaf)
    {
        var entry = Create(directories[parent], leaf, false);
        return new FileStream(new SafeFileHandle(entry.Handle.DangerousGetHandle(), ownsHandle: false), FileAccess.Write, 65536);
    }

    private Entry Create(Entry parent, string leaf, bool directory) =>
        Remember(parent, leaf, directory, Open(parent.Handle, leaf, directory, create: true));

    private Entry Remember(Entry? parent, string leaf, bool directory, SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, 24))
        {
            var error = Marshal.GetLastWin32Error();
            NtSetInformationFile(handle, out _, [1], 1, 13);
            handle.Dispose();
            throw new IOException("The destination must support stable file IDs.", new Win32Exception(error));
        }
        var entry = new Entry(parent, leaf, directory, id, handle);
        entries.Add(entry);
        return entry;
    }

    private static SafeFileHandle Open(SafeFileHandle parent, string leaf, bool directory, bool create)
    {
        OutputFiles.ValidateLeaf(leaf);
        var text = Marshal.StringToHGlobalUni(leaf);
        var name = new UnicodeString { Length = (ushort)(2 * leaf.Length), Capacity = (ushort)(2 * leaf.Length + 2), Text = text };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(name, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(),
                Root = parent.DangerousGetHandle(), Name = pointer, Attributes = 0x1040 }; // case-insensitive, don't reparse
            const uint deleteAndSynchronize = 0x110000;
            const uint synchronous = 0x20, openReparsePoint = 0x00200000;
            var status = NtCreateFile(out var handle, deleteAndSynchronize | (directory ? 0x83u : 0x82u),
                ref attributes, out _, IntPtr.Zero, 0, 1, create ? 2u : 1u, synchronous | openReparsePoint | (directory ? 1u : 0x40u), IntPtr.Zero, 0);
            if (status < 0)
            {
                handle.Dispose();
                throw new IOException("Could not create an archive entry.", new Win32Exception((int)RtlNtStatusToDosError(status)));
            }

            return handle;
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(text); }
    }

    public bool TryPublish(string leaf)
    {
        OutputFiles.ValidateLeaf(leaf);
        var bytes = Encoding.Unicode.GetBytes(Path.Combine(folder, leaf));
        var buffer = new byte[2 * IntPtr.Size + 4 + bytes.Length];

        BitConverter.GetBytes(bytes.Length).CopyTo(buffer, 2 * IntPtr.Size);
        bytes.CopyTo(buffer, 2 * IntPtr.Size + 4);
        // Windows rejects a directory rename with live descendant file objects.
        if (!sealedForRename)
        {
            // File IDs can be reused after deletion. Observe the namespace from before
            // releasing any handle until rollback has reacquired the original entries.
            // Watch the parent so the notification handle cannot block the root rename.
            changes = FindFirstChangeNotificationW(folder, true, 7); // names and attributes
            if (changes.IsInvalid) throw new IOException("Could not monitor the staging namespace.", new Win32Exception(Marshal.GetLastWin32Error()));
            for (var i = entries.Count - 1; i > 0; i--) entries[i].Handle.Dispose();
            sealedForRename = true;
        }
        if (WaitForSingleObject(changes!, 0) != 258)
            throw new IOException("The destination changed while preparing to publish the extraction.");
        // Rename the held root into its still-pinned parent without replacing any object.
        if (!SetFileInformationByHandle(entries[0].Handle, 3, buffer, (uint)buffer.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 80 or 183) return false;
            throw new IOException("Could not publish the extraction folder.", new Win32Exception(error));
        }
        published = true;
        return true;
    }

    public void Dispose()
    {
        Exception? failure = null;
        var removeChildren = true;
        // If publication failed, reacquire only the recorded objects through their
        // matched parents. Never traverse an unknown directory or delete by pathname.
        if (sealedForRename && !published)
        {
            foreach (var entry in entries.Skip(1))
            {
                if (entry.Parent!.Handle.IsClosed) continue;
                try
                {
                    var handle = Open(entry.Parent.Handle, entry.Leaf, entry.Directory, create: false);
                    if (GetFileInformationByHandleEx(handle, 18, out var id, 24) && id == entry.Id)
                        entry.Handle = handle;
                    else handle.Dispose();
                }
                catch (IOException error) { failure ??= error; }
            }
            removeChildren = WaitForSingleObject(changes!, 0) == 258; // WAIT_TIMEOUT means no changes
            if (!removeChildren) failure ??= new IOException("The destination changed during publication.");
        }
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var handle = entries[i].Handle;
            if (handle.IsClosed) continue;
            if (!published && (i == 0 || removeChildren))
            {
                var status = NtSetInformationFile(handle, out _, [1], 1, 13);
                if (status < 0) failure ??= new Win32Exception((int)RtlNtStatusToDosError(status));
            }
            handle.Dispose();
        }
        entries.Clear();
        changes?.Dispose();
        if (failure != null) throw new IOException($"Inspect the staging folder {Path.Combine(folder, directories[""].Leaf)}.", failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, Capacity; public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public int Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatus { public IntPtr Status, Information; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, byte[] buffer, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, out FileId id, uint size);
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileId(ulong Volume, ulong Low, ulong High);
    private sealed class ChangeNotification : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ChangeNotification() : base(true) { }
        protected override bool ReleaseHandle() => FindCloseChangeNotification(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ChangeNotification FindFirstChangeNotificationW(string path, bool subtree, uint filter);
    [DllImport("kernel32.dll")]
    private static extern bool FindCloseChangeNotification(IntPtr change);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(ChangeNotification change, uint milliseconds);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes,
        out IoStatus io, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatus io, byte[] buffer, uint size, int type);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
