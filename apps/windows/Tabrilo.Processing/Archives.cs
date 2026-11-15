using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using Tabrilo.Core;

namespace Tabrilo.Processing;

public sealed record ArchiveLimits(int Entries = 10_000, long FileBytes = 512L << 20,
    long TotalBytes = 2L << 30, long ReserveBytes = 64L << 20, int Seconds = 120);

public static class Archives
{
    public static async Task<string> ExtractAsync(string input, string destination, ArchiveLimits limits,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        destination = Path.GetFullPath(destination);
        using var destinationPin = new DirectoryPin(destination);
        var stem = Path.GetFileNameWithoutExtension(input);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = Path.GetFileNameWithoutExtension(stem);
        OutputFiles.ValidateLeaf(stem);
        var stage = Path.Combine(destination, ".tabrilo-extract-" + Guid.NewGuid().ToString("N"));
        if (!CreateDirectory(stage, IntPtr.Zero))
            throw new IOException("Could not create a new extraction staging directory.");
        var published = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.Seconds));
        try
        {
            using (var stagePin = new DirectoryPin(stage))
            {
                var writer = new Writer(stage, limits, timeout.Token);
                await using var file = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length == 0 || file.Length > limits.TotalBytes)
                    throw new InvalidDataException("The archive size is outside the supported limits.");
                var extension = Path.GetExtension(input).ToLowerInvariant();
                switch (extension)
                {
                    case ".zip":
                        using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                                var folder = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                                if (entry.IsEncrypted || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                                    type is not (0 or 0x4000 or 0x8000) ||
                                    type == 0x4000 && !folder || type == 0x8000 && folder)
                                    throw new InvalidDataException("The ZIP contains encryption, a link or a special entry.");
                                if (folder) writer.Directory(entry.FullName);
                                else
                                {
                                    await using var content = entry.Open();
                                    await writer.FileAsync(entry.FullName, content, entry.Length, entry.Crc32);
                                }
                            }
                        }
                        break;
                    case ".tar":
                        await ExtractTar(file, writer, timeout.Token);
                        break;
                    case ".gz":
                    case ".tgz":
                        // This decoder requires and validates the GZIP trailer when disposed.
                        using (var gzip = new SharpCompress.Compressors.Deflate.GZipStream(file,
                            SharpCompress.Compressors.CompressionMode.Decompress))
                        {
                            if (extension == ".tgz" || input.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                                await ExtractTar(gzip, writer, timeout.Token);
                            else
                                await writer.FileAsync(Path.GetFileNameWithoutExtension(input), gzip, null);
                        }
                        break;
                    case ".rar":
                        using (var archive = RarArchive.OpenArchive(file))
                        {
                            if (archive.IsEncrypted || !archive.IsComplete || archive.Volumes.Count() != 1 ||
                                archive.Volumes.Any(volume => volume is not RarVolume { IsMultiVolume: false }))
                                throw new InvalidDataException("Only complete, unencrypted, single-volume RAR archives are supported.");
                            if (archive.IsSolid)
                            {
                                using var reader = archive.ExtractAllEntries();
                                while (reader.MoveToNextEntry())
                                    await WriteRarEntry(reader.Entry, reader.OpenEntryStream, writer);
                            }
                            else foreach (var entry in archive.Entries)
                                await WriteRarEntry(entry, entry.OpenEntryStream, writer);
                        }
                        break;
                    default: throw new NotSupportedException("Choose ZIP, TAR, GZIP, TAR.GZ, TGZ or RAR.");
                }
                if (writer.Entries == 0) throw new InvalidDataException("The archive has no extractable entries.");
            }

            timeout.Token.ThrowIfCancellationRequested();
            for (var index = 0; index < 1000; index++)
            {
                var output = Path.Combine(destination, OutputFiles.Candidate(stem + " extracted", index));
                try
                {
                    Directory.Move(stage, output);
                    published = true;
                    return output;
                }
                catch (IOException) when (File.Exists(output) || Directory.Exists(output)) { }
            }
            throw new IOException("Could not choose an extraction folder name.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Extraction exceeded its time limit.");
        }
        catch (SharpCompressException error)
        {
            throw new InvalidDataException("The archive is incomplete, damaged, or uses unsupported RAR features.", error);
        }
        finally
        {
            if (!published) System.IO.Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task WriteRarEntry(IEntry entry, Func<Stream> open, Writer writer)
    {
        if (entry is not RarEntry rar || rar.IsRedir || rar.LinkTarget is not null ||
            rar.IsEncrypted || rar.IsSplitAfter || rar.Key is null ||
            (rar.Attrib is int attributes && ((attributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (attributes & 0xF000) is not (0 or 0x4000 or 0x8000))))
            throw new InvalidDataException("The RAR contains encryption, volumes, or a redirected entry.");
        if (entry.IsDirectory) { writer.Directory(rar.Key); return; }
        await using var content = open();
        await writer.FileAsync(rar.Key, content, entry.Size);
    }

    private static async Task ExtractTar(Stream input, Writer writer, CancellationToken token)
    {
        using var tar = new TarReader(input, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
            switch (entry.EntryType)
            {
                case TarEntryType.Directory: writer.Directory(entry.Name); break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    await writer.FileAsync(entry.Name, entry.DataStream ?? Stream.Null, entry.Length);
                    break;
                default: throw new InvalidDataException("The TAR contains a link or special entry.");
            }
        }
        // Reach the end of the compressed stream to validate its trailer. Bound trailing padding.
        var padding = new byte[8192];
        var remaining = 1024 * 1024;
        int count;
        while ((count = await input.ReadAsync(padding.AsMemory(), token)) > 0)
        {
            remaining -= count;
            if (remaining < 0 || padding.AsSpan(0, count).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Unexpected data after the TAR entries.");
        }
    }

    private sealed class Writer(string root, ArchiveLimits limits, CancellationToken token)
    {
        private readonly Dictionary<string, (string Spelling, bool Directory, bool Explicit)> paths =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly DriveInfo drive = new(Path.GetPathRoot(root)!);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long totalBytes;
        public int Entries { get; private set; }

        public void Directory(string name) => Resolve(name, folder: true);

        public async Task FileAsync(string name, Stream input, long? expected, uint? expectedCrc = null)
        {
            var target = Resolve(name, folder: false);
            using var pin = new DirectoryPin(Path.GetDirectoryName(target)!);
            if (expected > limits.FileBytes || expected > limits.TotalBytes - totalBytes)
                throw new InvalidDataException("An archive entry exceeds the configured byte limit.");
            await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[65536];
            var crc = expectedCrc.HasValue ? new Crc32() : null;
            long bytes = 0;
            var diskCheckAt = totalBytes;
            while (true)
            {
                CheckTime();
                var read = await input.ReadAsync(buffer.AsMemory(), token);
                if (read == 0) break;
                bytes += read;
                totalBytes += read;
                if (bytes > limits.FileBytes || totalBytes > limits.TotalBytes || expected < bytes)
                    throw new InvalidDataException("An archive entry exceeded its streamed byte limit or declared size.");
                if (totalBytes >= diskCheckAt)
                {
                    if (drive.AvailableFreeSpace < limits.ReserveBytes + read)
                        throw new IOException("The destination volume lacks free space for extraction.");
                    diskCheckAt += 1024 * 1024;
                }
                crc?.Append(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (expected is long size && bytes != size) throw new InvalidDataException("An archive entry is truncated.");
            if (crc is not null && crc.GetCurrentHashAsUInt32() != expectedCrc)
                throw new InvalidDataException("A ZIP entry failed its integrity check.");
            await file.FlushAsync(token);
        }

        private void CheckTime()
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds >= limits.Seconds)
                throw new TimeoutException("Extraction exceeded its time limit.");
        }

        private string Resolve(string name, bool folder)
        {
            CheckTime();
            if (++Entries > limits.Entries) throw new InvalidDataException("The archive contains too many entries.");
            var canonicalSeparators = name.Replace('\\', '/');
            if (folder) canonicalSeparators = canonicalSeparators.TrimEnd('/');
            var segments = canonicalSeparators.Split('/');
            if (segments.Length > 32) throw new InvalidDataException("The archive directory nesting is too deep.");
            var relative = "";
            var current = root;
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                OutputFiles.ValidateLeaf(segment);
                relative = index == 0 ? segment : relative + "/" + segment;
                if (relative.Length > 2048) throw new InvalidDataException("An archive path is too long.");
                var key = relative.Normalize(NormalizationForm.FormC);
                var explicitEntry = index == segments.Length - 1;
                var isDirectory = folder || !explicitEntry;
                if (paths.TryGetValue(key, out var previous))
                {
                    if (!string.Equals(previous.Spelling, relative, StringComparison.Ordinal) || !previous.Directory ||
                        !isDirectory || previous.Explicit && explicitEntry)
                        throw new InvalidDataException("The archive contains colliding entry names.");
                    if (explicitEntry) paths[key] = (relative, isDirectory, true);
                }
                else
                {
                    if (paths.Count >= limits.Entries)
                        throw new InvalidDataException("The archive contains too many paths.");
                    if (isDirectory)
                    {
                        using var parentPin = new DirectoryPin(current);
                        if (!CreateDirectory(Path.Combine(current, segment), IntPtr.Zero))
                            throw new IOException("Could not create a new archive subdirectory.");
                    }
                    paths.Add(key, (relative, isDirectory, explicitEntry));
                }
                current = Path.Combine(current, segment);
            }
            return current;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true, EntryPoint = "CreateDirectoryW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
}
