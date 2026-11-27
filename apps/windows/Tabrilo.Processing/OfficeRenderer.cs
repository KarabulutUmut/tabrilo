using System.Diagnostics;
using System.IO.Compression;
using System.Xml;

namespace Tabrilo.Processing;

public sealed class OfficeRenderer(string? configured, Pdfs pdf)
{
    private static readonly SemaphoreSlim Queue = new(1);
    private static string Engine(string? path) => string.IsNullOrWhiteSpace(path)
        ? Path.Combine(AppContext.BaseDirectory, "tools", "office", "program", "soffice.com") : path;

    public static bool Available(string? path) => Path.IsPathFullyQualified(Engine(path)) &&
        Path.GetFileName(Engine(path)).Equals("soffice.com", StringComparison.OrdinalIgnoreCase) && File.Exists(Engine(path));

    public async Task<string> RenderAsync(string input, Func<string, Task<string>> consume, CancellationToken token)
    {
        if (!Available(configured)) throw new NotSupportedException("Configure a local LibreOffice renderer in Settings.");
        await Queue.WaitAsync(token);
        try
        {
            var root = Directory.CreateTempSubdirectory("tabrilo-office-");
            try { return await RenderIn(root.FullName, input, consume, token); }
            finally { root.Delete(recursive: true); }
        }
        finally { Queue.Release(); }
    }

    private async Task<string> RenderIn(string root, string input, Func<string, Task<string>> consume, CancellationToken token)
    {
        var extension = Path.GetExtension(input).ToLowerInvariant();
        if (extension is not (".docx" or ".pptx")) throw new NotSupportedException("Choose a DOCX or PPTX document.");
        var local = Path.Combine(root, "source" + extension);
        using (var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (source.Length > 128L << 20) throw new InvalidDataException("Office documents are limited to 128 MiB.");
            using var copy = File.Create(local);
            await source.CopyToAsync(copy, token);
        }
        await Task.Run(() => InspectPackage(local, extension, token), token);
        var profile = Directory.CreateDirectory(Path.Combine(root, "profile", "user"));
        await File.WriteAllTextAsync(Path.Combine(profile.FullName, "registrymodifications.xcu"), """
            <oor:items xmlns:oor="http://openoffice.org/2001/registry">
              <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                <prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="DisableActiveContent" oor:op="fuse"><value>true</value></prop>
                <prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop>
              </item>
            </oor:items>
            """, token);
        var start = new ProcessStartInfo(Engine(configured)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        start.Environment["TEMP"] = root;
        start.Environment["TMP"] = root;
        foreach (var value in new[] { "-env:UserInstallation=" + new Uri(profile.Parent!.FullName).AbsoluteUri,
            "--headless", "--nologo", "--norestore", "--nodefault", "--convert-to",
            extension == ".docx" ? "pdf:writer_pdf_Export" : "pdf:impress_pdf_Export", "--outdir", root, local })
            start.ArgumentList.Add(value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        using var process = Process.Start(start) ?? throw new IOException("Could not start LibreOffice.");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("LibreOffice exceeded the two-minute render limit."); }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        var rendered = Path.Combine(root, "source.pdf");
        if (process.ExitCode != 0 || !File.Exists(rendered)) throw new InvalidDataException("LibreOffice could not render this document.");
        await pdf.ValidateRenderedAsync(rendered, token);
        return await consume(rendered);
    }

    private static void InspectPackage(string path, string extension, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 6000 || archive.GetEntry(extension == ".docx" ? "word/document.xml" : "ppt/presentation.xml") is null)
            throw new InvalidDataException("Invalid or oversized Office package.");
        long bytes = 0;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!parts.Add(entry.FullName)) throw new InvalidDataException("The Office package has duplicate parts.");
            bytes = checked(bytes + entry.Length);
            if (bytes > 384L << 20 || entry.Length > 64L << 20)
                throw new InvalidDataException("Office package content exceeds the render limit.");
            var printerSettings = System.Text.RegularExpressions.Regex.IsMatch(entry.FullName,
                @"^(word|ppt)/printerSettings/printerSettings[0-9]+\.bin$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && !printerSettings ||
                entry.FullName.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Embedded programs and binary Office objects are unsupported.");
            var relationships = entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            if (!relationships && !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            using var content = entry.Open();
            using var xml = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = relationships ? 2_000_000 : 64L << 20 });
            try
            {
                while (xml.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (xml.NodeType != XmlNodeType.Element) continue;
                    if (relationships && xml.LocalName == "Relationship" &&
                        string.Equals(xml.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase) &&
                        xml.GetAttribute("Type") is not ("http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink"
                            or "http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink"))
                        throw new NotSupportedException("This document requires an external linked resource.");
                    if (xml.LocalName is "altChunk" or "oleObject" or "OLEObject" or "control" or "ddeLink")
                        throw new NotSupportedException("Linked content and embedded controls cannot be rendered offline.");
                }
            }
            catch (XmlException) { throw new InvalidDataException("Invalid Office relationships."); }
        }
    }
}
