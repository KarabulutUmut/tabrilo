using ImageMagick;
using ZXing;
using ZXing.Common;
using ZXing.Multi.QrCode;

namespace Tabrilo.Processing;

public sealed record QrResult(string Payload, string Type, int? Page, IReadOnlyList<ImagePoint> Location)
{
    public Uri? AllowedLink => Uri.TryCreate(Payload, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0 &&
        !Payload.Any(char.IsControl) ? uri : null;
}

public static class Qr
{
    public static async Task<IReadOnlyList<QrResult>> ScanImageAsync(string path, CancellationToken token)
    {
        using var image = await Images.ReadAsync(path, token);
        Images.Normalize(image);
        return Scan(image, null, token);
    }

    public static IReadOnlyList<QrResult> ScanRenderedPage(ProcessedImage page, int number, CancellationToken token)
    {
        using var image = new MagickImage(page.Bytes, MagickFormat.Png);
        return Scan(image, number, token);
    }

    private static IReadOnlyList<QrResult> Scan(IMagickImage<byte> image, int? page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sourceWidth = image.Width;
        var sourceHeight = image.Height;
        if (Math.Max(sourceWidth, sourceHeight) > 2600) image.Resize(2600, 2600);
        using var pixels = image.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new InvalidDataException("QR input pixels are unavailable.");
        var source = new RGBLuminanceSource(rgb, (int)image.Width, (int)image.Height, RGBLuminanceSource.BitmapFormat.RGB24);
        var options = new DecodingOptions { TryHarder = true };
        var reader = new QRCodeMultiReader();
        var results = reader.decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), options.Hints) ??
            reader.decodeMultiple(new BinaryBitmap(new HybridBinarizer(source.invert())), options.Hints) ?? [];
        token.ThrowIfCancellationRequested();
        return results.Select(result => new QrResult(result.Text, Type(result.Text), page,
            (result.ResultPoints ?? []).Select(point => new ImagePoint(
                (int)Math.Round(point.X * sourceWidth / image.Width),
                (int)Math.Round(point.Y * sourceHeight / image.Height))).ToArray())).ToArray();
    }

    private static string Type(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? "Web link" :
        text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase) ? "Wi-Fi" :
        text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "Email" : "Text";
}
