using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

namespace Tabrilo.Windows;

internal sealed record UpdateChannel(int Version, string Channel, string Platform, string? Feed,
    string? InstallerOrigin, string? PublicKey, string? InstallerRedirectOrigin = null);
internal sealed record UpdateCandidate(Version Version, Version MinimumSystem, Uri Installer, long Length, string Signature);

internal sealed class Updates : IDisposable
{
    private const long MaximumInstaller = 300L << 20;
    private static readonly XNamespace Sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
    private static readonly UpdateChannel BuiltIn = ReadChannel();
    private readonly HttpClient client;
    private readonly Ed25519Checker verifier;
    private readonly Uri feed;
    private readonly Uri installerOrigin;
    private readonly Uri? installerRedirectOrigin;
    private readonly Version current;
    private readonly Version system;
    private readonly UpdateChannel channel;

    public static bool Configured => BuiltIn is { Version: 1, Feed: not null, InstallerOrigin: not null, PublicKey: not null };
    public static Updates Open() => new(BuiltIn,
        typeof(Updates).Assembly.GetName().Version!, Environment.OSVersion.Version);

    internal Updates(UpdateChannel channel, Version current, Version system, HttpMessageHandler? handler = null)
    {
        this.channel = channel;
        this.current = current;
        this.system = system;
        if (channel.Version != 1 || channel.Platform != "windows-x64" || channel.Channel is not ("stable" or "isolated-test") ||
            !TryTrustedHttps(channel.Feed, out feed) || !TryTrustedHttps(channel.InstallerOrigin, out installerOrigin) ||
            channel.PublicKey is null || Convert.FromBase64String(channel.PublicKey).Length != 32)
            throw new InvalidDataException("The updater trust configuration is missing or invalid.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new NotSupportedException("This update channel is for Windows x64.");
        if (channel.InstallerRedirectOrigin is not null)
        {
            if (!TryTrustedHttps(channel.InstallerRedirectOrigin, out var redirect))
                throw new InvalidDataException("The update redirect origin is invalid.");
            installerRedirectOrigin = redirect;
        }
        verifier = new Ed25519Checker(SecurityMode.Strict, channel.PublicKey);
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken token)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(token);
        time.CancelAfter(TimeSpan.FromSeconds(30));
        using var xml = new MemoryStream();
        using var signature = new MemoryStream();
        await Download(feed, xml, 1024 * 1024, null, time.Token);
        var signatureUri = new UriBuilder(feed) { Path = feed.AbsolutePath + ".signature" }.Uri;
        await Download(signatureUri, signature, 1024, null, time.Token);
        var signed = Encoding.ASCII.GetString(signature.ToArray()).Trim();
        if (verifier.VerifySignature(signed, xml.ToArray()) != ValidationResult.Valid)
            throw new InvalidDataException("The update feed signature is invalid.");
        xml.Position = 0;
        using var reader = XmlReader.Create(xml, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var appcast = XDocument.Load(reader);
        var items = appcast.Root?.Element("channel")?.Elements("item") ??
            throw new InvalidDataException("The signed update feed has an invalid structure.");
        return items.Select(ParseItem).OfType<UpdateCandidate>().Where(candidate => candidate.Version > current &&
            candidate.MinimumSystem <= system).MaxBy(candidate => candidate.Version);
    }

    private UpdateCandidate? ParseItem(XElement item)
    {
        var enclosure = item.Element("enclosure");
        if (enclosure is null || (string?)enclosure.Attribute(Sparkle + "os") != channel.Platform ||
            (string?)item.Element(Sparkle + "channel") != channel.Channel ||
            !Version.TryParse((string?)enclosure.Attribute(Sparkle + "version"), out var version) ||
            !Version.TryParse((string?)item.Element(Sparkle + "minimumSystemVersion"), out var minimum) ||
            !TryTrustedHttps((string?)enclosure.Attribute("url"), out var installer) ||
            !SameOrigin(installer, installerOrigin) || !installer.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse((string?)enclosure.Attribute("length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length <= 0 || length > MaximumInstaller || (string?)enclosure.Attribute(Sparkle + "edSignature") is not { } signature)
            return null;
        return new(version, minimum, installer, length, signature);
    }

    public async Task<string> DownloadAsync(UpdateCandidate candidate, string root, CancellationToken token)
    {
        if (!IsCompatible(candidate)) throw new InvalidDataException("The update is incompatible with this installation.");
        var directory = Path.Combine(root, "Updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"Tabrilo-setup-{Guid.NewGuid():N}.exe");
        using var time = CancellationTokenSource.CreateLinkedTokenSource(token);
        time.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                             FileOptions.Asynchronous))
                await Download(candidate.Installer, file, MaximumInstaller, candidate.Length, time.Token, installerRedirectOrigin);
            time.Token.ThrowIfCancellationRequested();
            ValidateInstaller(candidate, path);
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    public Process StartInstaller(UpdateCandidate candidate, string path, bool unattendedTest = false)
    {
        // Deny write/delete through process creation and validate again immediately before execution.
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateInstaller(candidate, path);
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        if (unattendedTest)
        {
            if (channel.Channel != "isolated-test") throw new InvalidOperationException("An isolated test channel is required.");
            start.ArgumentList.Add("/S");
        }
        return Process.Start(start) ?? throw new IOException("Windows could not start the installer.");
    }

    private void ValidateInstaller(UpdateCandidate candidate, string path)
    {
        if (!IsCompatible(candidate) || new FileInfo(path).Length != candidate.Length ||
            verifier.VerifySignatureOfFile(candidate.Signature, path) != ValidationResult.Valid)
            throw new InvalidDataException("The installer failed strict signature verification.");
        var product = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(product.ProductMajorPart, product.ProductMinorPart,
            product.ProductBuildPart, product.ProductPrivatePart);
        var expected = new Version(candidate.Version.Major, candidate.Version.Minor,
            Math.Max(candidate.Version.Build, 0), Math.Max(candidate.Version.Revision, 0));
        if (version != expected || product.ProductName != "Tabrilo")
            throw new InvalidDataException("The signed installer identity or version differs from the update feed.");
    }

    private bool IsCompatible(UpdateCandidate candidate) => candidate.Version > current &&
        candidate.MinimumSystem <= system && candidate.Length is > 0 and <= MaximumInstaller &&
        SameOrigin(candidate.Installer, installerOrigin) &&
        TryTrustedHttps(candidate.Installer.AbsoluteUri, out _);

    private async Task Download(Uri uri, Stream output, long maximum, long? expected, CancellationToken token, Uri? redirectOrigin = null)
    {
        using var initial = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        using var redirected = (int)initial.StatusCode is 301 or 302 or 307 or 308 && redirectOrigin is not null &&
            initial.Headers.Location is { IsAbsoluteUri: true } location &&
            TryTrustedHttps(location.AbsoluteUri, out var redirectedUri) && SameOrigin(redirectedUri, redirectOrigin)
                ? await client.GetAsync(redirectedUri, HttpCompletionOption.ResponseHeadersRead, token) : null;
        var response = redirected ?? initial;
        response.EnsureSuccessStatusCode();
        var declared = response.Content.Headers.ContentLength;
        if (declared > maximum || declared is not null && expected is not null && declared != expected)
            throw new InvalidDataException("The update server returned an unexpected payload size.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[65536];
        long received = 0;
        int size;
        while ((size = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            received += size;
            if (received > maximum || received > expected) throw new InvalidDataException("The update exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, size), token);
        }
        if (received == 0 || expected is not null && expected != received)
            throw new InvalidDataException("The download is empty or incomplete.");
    }

    private static bool SameOrigin(Uri first, Uri second) =>
        first.Scheme == second.Scheme && first.IdnHost == second.IdnHost && first.Port == second.Port;

    private static bool TryTrustedHttps(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var result) && result.Scheme == "https" &&
            result.Host.Length > 0 && result.UserInfo.Length == 0 && result.Fragment.Length == 0)
        { uri = result; return true; }
        uri = null!;
        return false;
    }

    private static UpdateChannel ReadChannel()
    {
        using var stream = typeof(Updates).Assembly.GetManifestResourceStream("Tabrilo.Updates.json")!;
        return JsonSerializer.Deserialize<UpdateChannel>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The update channel configuration is empty.");
    }

    public void Dispose() => client.Dispose();
}
