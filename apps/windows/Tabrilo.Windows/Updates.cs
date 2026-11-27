using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Tabrilo.Windows;

internal sealed record UpdateChannel(int Version, string Channel, string Platform, string? Feed,
    string? InstallerOrigin, string? PublicKey, string? InstallerRedirectOrigin = null, string Flavor = "core");
internal sealed record UpdateCandidate(Version Version, Version MinimumSystem, Uri Installer, long Length, string Signature,
    string Flavor, string Sha256);

internal sealed class Updates : IDisposable
{
    private sealed record UpdateLimits(long MaximumInstallerBytes, int ConnectionSeconds, int InactivitySeconds, int OverallSeconds);
    private static readonly UpdateLimits Limits = ReadLimits();
    internal static long MaximumInstaller => Limits.MaximumInstallerBytes;
    private static readonly XNamespace Sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
    private static readonly UpdateChannel BuiltIn = ReadChannel();
    private readonly HttpClient client;
    private readonly Ed25519Checker verifier;
    private readonly Ed25519PublicKeyParameters installerKey;
    private readonly Uri feed;
    private readonly Uri installerOrigin;
    private readonly Uri? installerRedirectOrigin;
    private readonly Version current;
    private readonly Version system;
    private readonly UpdateChannel channel;

    public static bool Configured => IsFixture || App.Metadata("Channel") == "stable" &&
        BuiltIn is { Version: 2, Feed: not null, InstallerOrigin: not null, PublicKey: not null };
    public static Updates Open()
    {
#if ISOLATED_ACCEPTANCE
        if (IsolatedFolder != null) return OpenIsolated(IsolatedFolder);
#endif
        return new(BuiltIn, typeof(Updates).Assembly.GetName().Version!, Environment.OSVersion.Version);
    }

#if ISOLATED_ACCEPTANCE
    internal static string? IsolatedFolder { get; set; }
    internal static bool IsFixture => IsolatedFolder != null;
    internal static Updates OpenIsolated(string folder)
    {
        const string origin = "https://tabrilo-install-test.invalid";
        var config = new UpdateChannel(2, "isolated-test", "windows-x64", origin + "/appcast.xml", origin,
            App.Metadata("IsolatedPublicKey"), Flavor: App.Metadata("Flavor"));
        return new(config, typeof(Updates).Assembly.GetName().Version!, Environment.OSVersion.Version, new FixtureTransport(folder));
    }

    private sealed class FixtureTransport(string folder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var file = request.RequestUri?.AbsolutePath switch
            {
                "/appcast.xml" => "appcast.xml",
                "/appcast.xml.signature" => "appcast.xml.signature",
                "/setup.exe" => "setup.exe",
                _ => throw new InvalidDataException("Unknown isolated feed asset.")
            };
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StreamContent(File.OpenRead(Path.Combine(folder, file))) });
        }
    }
#else
    internal static bool IsFixture => false;
#endif

    internal Updates(UpdateChannel channel, Version current, Version system, HttpMessageHandler? handler = null)
    {
        this.channel = channel;
        this.current = current;
        this.system = system;
        if (channel.Version != 2 || channel.Flavor is not ("core" or "office") ||
            channel.Platform != "windows-x64" || channel.Channel is not ("stable" or "isolated-test") ||
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
        verifier = new Ed25519Checker(SecurityMode.Strict, channel.PublicKey, null, true, 1 << 20);
        installerKey = new Ed25519PublicKeyParameters(Convert.FromBase64String(channel.PublicKey));
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(Limits.ConnectionSeconds) })
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
        if (!ValidSignature(signed) || verifier.VerifySignature(signed, xml.ToArray()) != ValidationResult.Valid)
            throw new InvalidDataException("The update feed signature is invalid.");
        xml.Position = 0;
        using var reader = XmlReader.Create(xml, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
        });
        var appcast = XDocument.Load(reader);
        var items = appcast.Root?.Element("channel")?.Elements("item") ??
            throw new InvalidDataException("The signed update feed has an invalid structure.");
        var candidates = items.Select(ParseItem).ToArray();
        var available = candidates.OfType<UpdateCandidate>().Where(IsCompatible).MaxBy(candidate => candidate.Version);
        if (available is null && candidates.Any(candidate => candidate is null || candidate.Version > current))
            throw new InvalidDataException("The signed feed advertises an incompatible or malformed update for this installation.");
        return available;
    }

    private UpdateCandidate? ParseItem(XElement item)
    {
        var enclosure = item.Element("enclosure");
        if (enclosure is null || (string?)enclosure.Attribute(Sparkle + "os") != channel.Platform ||
            (string?)enclosure.Attribute("flavor") != channel.Flavor ||
            (string?)enclosure.Attribute("sha256") is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) ||
            (string?)item.Element(Sparkle + "channel") != channel.Channel ||
            !Version.TryParse((string?)enclosure.Attribute(Sparkle + "version"), out var version) ||
            !Version.TryParse((string?)item.Element(Sparkle + "minimumSystemVersion"), out var minimum) ||
            !TryTrustedHttps((string?)enclosure.Attribute("url"), out var installer) ||
            !SameOrigin(installer, installerOrigin) || !installer.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse((string?)enclosure.Attribute("length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length <= 0 || length > MaximumInstaller || (string?)enclosure.Attribute(Sparkle + "edSignature") is not { } signature ||
            !ValidSignature(signature))
            return null;
        return new(version, minimum, installer, length, signature, channel.Flavor, digest);
    }

    private static bool ValidSignature(string signature)
    {
        Span<byte> decoded = stackalloc byte[64];
        return Convert.TryFromBase64String(signature, decoded, out var size) && size == decoded.Length;
    }

    public async Task<string> DownloadAsync(UpdateCandidate candidate, string root, CancellationToken token)
    {
        if (!IsCompatible(candidate)) throw new InvalidDataException("The update is incompatible with this installation.");
        var directory = Path.Combine(root, "Updates");
        Directory.CreateDirectory(directory);
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace < candidate.Length + (64L << 20))
            throw new IOException("Free disk space for the installer and 64 MiB of working space, then retry.");
        var path = Path.Combine(directory, $"Tabrilo-setup-{Guid.NewGuid():N}.exe");
        using var time = CancellationTokenSource.CreateLinkedTokenSource(token);
        time.CancelAfter(TimeSpan.FromSeconds(Limits.OverallSeconds));
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                             FileOptions.Asynchronous))
                await Download(candidate.Installer, file, MaximumInstaller, candidate.Length, time.Token, installerRedirectOrigin);
            time.Token.ThrowIfCancellationRequested();
            await Task.Run(() => ValidateInstaller(candidate, path), time.Token);
            time.Token.ThrowIfCancellationRequested();
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    public Task<Process> StartInstallerAsync(UpdateCandidate candidate, string path, CancellationToken token, bool unattendedTest = false)
        => Task.Run(() => StartInstaller(candidate, path, token, unattendedTest), token);

    private Process StartInstaller(UpdateCandidate candidate, string path, CancellationToken token, bool unattendedTest)
    {
        // Deny write/delete through process creation and validate again immediately before execution.
        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateInstaller(candidate, path);
        token.ThrowIfCancellationRequested();
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
        using var contents = File.OpenRead(path);
        if (!IsCompatible(candidate) || contents.Length != candidate.Length ||
            !Convert.ToHexString(SHA256.HashData(contents)).Equals(candidate.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !VerifyInstallerSignature(contents, candidate.Signature))
            throw new InvalidDataException("The installer failed strict signature verification.");
        var product = FileVersionInfo.GetVersionInfo(path);
        var version = new Version(product.ProductMajorPart, product.ProductMinorPart,
            product.ProductBuildPart, product.ProductPrivatePart);
        var expected = new Version(candidate.Version.Major, candidate.Version.Minor,
            Math.Max(candidate.Version.Build, 0), Math.Max(candidate.Version.Revision, 0));
        var identity = Desktop.InstallIdentity;
        if (version != expected || product.ProductName != "Tabrilo" ||
            product.FileDescription != $"{identity} Windows x64 {channel.Flavor} installer")
            throw new InvalidDataException("The signed installer identity or version differs from the update feed.");
    }

    private unsafe bool VerifyInstallerSignature(FileStream file, string encodedSignature)
    {
        // Present the signed bytes directly to the standard Ed25519 primitive. A read-only file view
        // lets Windows page the installer from disk without creating a package-sized private buffer.
        using var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, file.Length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            var message = new ReadOnlySpan<byte>(pointer + view.PointerOffset, checked((int)file.Length));
            return installerKey.Verify(Ed25519.Algorithm.Ed25519, null, message, Convert.FromBase64String(encodedSignature));
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    private bool IsCompatible(UpdateCandidate candidate) => candidate.Version > current && candidate.Flavor == channel.Flavor &&
        candidate.MinimumSystem <= system && candidate.Length > 0 && candidate.Length <= MaximumInstaller &&
        SameOrigin(candidate.Installer, installerOrigin) &&
        TryTrustedHttps(candidate.Installer.AbsoluteUri, out _);

    private async Task Download(Uri uri, Stream output, long maximum, long? expected, CancellationToken token, Uri? redirectOrigin = null)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(TimeSpan.FromSeconds(Limits.InactivitySeconds));
        using var initial = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, idle.Token);
        using var redirected = (int)initial.StatusCode is 301 or 302 or 307 or 308 && redirectOrigin is not null &&
            initial.Headers.Location is { IsAbsoluteUri: true } location &&
            TryTrustedHttps(location.AbsoluteUri, out var redirectedUri) && SameOrigin(redirectedUri, redirectOrigin)
                ? await client.GetAsync(redirectedUri, HttpCompletionOption.ResponseHeadersRead, idle.Token) : null;
        var response = redirected ?? initial;
        response.EnsureSuccessStatusCode();
        var declared = response.Content.Headers.ContentLength;
        if (declared > maximum || declared is not null && expected is not null && declared != expected)
            throw new InvalidDataException("The update server returned an unexpected payload size.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[65536];
        long received = 0;
        int size;
        while ((size = await input.ReadAsync(buffer.AsMemory(), idle.Token)) > 0)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(Limits.InactivitySeconds));
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
        var channel = JsonSerializer.Deserialize<UpdateChannel>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("The update channel configuration is empty.");
        var flavor = App.Metadata("Flavor");
        return channel with { Flavor = flavor, Feed = channel.Feed?.Replace("{flavor}", flavor, StringComparison.Ordinal) };
    }

    private static UpdateLimits ReadLimits()
    {
        using var stream = typeof(Updates).Assembly.GetManifestResourceStream("Tabrilo.Capabilities.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("updates").Deserialize<UpdateLimits>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Missing update download limits.");
    }

    public void Dispose() => client.Dispose();
}
