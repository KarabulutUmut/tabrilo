using ImageMagick;
using ImageMagick.Configuration;
using ImageMagick.Drawing;
using Tabrilo.Core;

namespace Tabrilo.Processing;

public enum ImageFormat { Png, Jpeg, WebP }
public enum MarkKind { Rectangle, Ellipse, Arrow, Text, Freehand }
public sealed record ImagePoint(int X, int Y);
public sealed record PixelRect(int X, int Y, int Width, int Height)
{
    public bool Fits(uint width, uint height) => X >= 0 && Y >= 0 && Width > 0 && Height > 0
        && (long)X + Width <= width && (long)Y + Height <= height;
}
public sealed record ImageMark(MarkKind Kind, ImagePoint[] Points, string Text = "", string Color = "#0F766E");
public sealed record ImageEdits(uint MaxEdge = 0, int Rotation = 0, bool FlipHorizontal = false,
    bool FlipVertical = false, int Brightness = 0, int Contrast = 0, int Saturation = 100,
    PixelRect? Crop = null, PixelRect[]? Redactions = null, ImageMark[]? Marks = null,
    uint Padding = 0, uint Radius = 0, bool Shadow = false, string FrameColor = "#F7F8FA", string? Gradient = null,
    uint ResizeWidth = 0, uint ResizeHeight = 0);
public sealed record ImageOptions(ImageFormat Format, int Quality = 85, bool Lossless = false,
    bool SmallerOnly = false, bool Redaction = false, ImageEdits? Edits = null);
public sealed record ImageInfo(uint Width, uint Height, string Format, long Size, string Orientation, string ColorSpace,
    string[] ProfileNames, IReadOnlyDictionary<string, string> Properties);
public sealed record ProcessedImage(byte[] Bytes, ImageFormat Format, uint Width, uint Height);

public static class Images
{
    static Images()
    {
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*"/>
              <policy domain="filter" rights="none" pattern="*"/>
              <policy domain="path" rights="none" pattern="@*"/>
              <policy domain="coder" rights="none" pattern="*"/>
              <policy domain="coder" rights="read|write" pattern="{PNG,PNG8,PNG24,PNG32,JPEG,WEBP,RGBA}"/>
              <policy domain="coder" rights="read" pattern="{BMP,BMP2,BMP3,TIFF,HEIC,HEIF,XC}"/>
            </policymap>
            """;
        MagickNET.Initialize(configuration);
        ResourceLimits.Width = 16384;
        ResourceLimits.Height = 16384;
        ResourceLimits.ListLength = 2;
        ResourceLimits.Memory = 384UL << 20;
        ResourceLimits.MaxMemoryRequest = 256UL << 20;
        ResourceLimits.MaxProfileSize = 8UL << 20;
        ResourceLimits.Disk = 0;
        ResourceLimits.Thread = 2;
    }

    public static async Task<ImageInfo> InspectAsync(string path, CancellationToken token)
    {
        using var image = await ReadAsync(path, token);
        var properties = image.GetExifProfile()?.Values.Take(150)
            .ToDictionary(value => value.Tag.ToString(), value => Truncate(value.ToString())) ??
            new Dictionary<string, string>();
        return new(image.Width, image.Height, image.Format.ToString(), new FileInfo(path).Length,
            image.Orientation.ToString(), image.ColorSpace.ToString(), image.ProfileNames.ToArray(), properties);
    }

    private static string Truncate(string? value) => value is null ? "" : value[..Math.Min(value.Length, 512)];

    internal static async Task<IMagickImage<byte>> ReadAsync(string path, CancellationToken token)
    {
        MagickFormat format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MagickFormat.Jpeg,
            ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP,
            ".bmp" => MagickFormat.Bmp,
            ".tif" or ".tiff" => MagickFormat.Tiff,
            ".heic" or ".heif" => MagickFormat.Heic,
            _ => throw new NotSupportedException("The image format is unsupported.")
        };
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 192L << 20 || input.Length < 8) throw new InvalidDataException("Image size is outside the import limits.");
        using var frames = new MagickImageCollection();
        try { await frames.ReadAsync(input, format, token); }
        catch (MagickException error) when (error.Message.Contains("list length", StringComparison.OrdinalIgnoreCase))
        { throw new NotSupportedException("Choose a single-frame image. Multi-image and animated files are unsupported.", error); }
        catch (MagickException error)
        { throw new InvalidDataException("The image could not be decoded or it exceeded import limits.", error); }
        if (frames.Count != 1) throw new NotSupportedException("Choose a single-frame image. Multi-image and animated files are unsupported.");
        CheckDimensions(frames[0].Width, frames[0].Height);
        return frames[0].Clone();
    }

    internal static void Normalize(IMagickImage<byte> image)
    {
        image.AutoOrient();
        if (image.GetColorProfile() is not null)
        {
            if (!image.TransformColorSpace(ColorProfiles.SRGB))
                throw new InvalidDataException("The embedded color profile cannot be converted to sRGB.");
        }
        else image.ColorSpace = ColorSpace.sRGB;
        image.Strip();
        image.ResetPage();
    }

    public static async Task<ProcessedImage> ProcessAsync(string path, ImageOptions options, CancellationToken token)
    {
        using var image = await ReadAsync(path, token);
        return Process(image, options, token);
    }

    internal static ProcessedImage ProcessPngStream(Stream stream, ImageFormat format, CancellationToken token)
    {
        using var image = new MagickImage(stream, MagickFormat.Png);
        CheckDimensions(image.Width, image.Height);
        return Process(image, new ImageOptions(format), token);
    }

    internal static async Task<ProcessedImage> ForPdfAsync(string path, CancellationToken token)
    {
        using var image = await ReadAsync(path, token);
        Normalize(image);
        return new(image.ToByteArray(MagickFormat.Png32), ImageFormat.Png, image.Width, image.Height);
    }

    internal static ProcessedImage Process(IMagickImage<byte> image, ImageOptions options, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            Normalize(image);
            var edits = options.Edits ?? new();
            foreach (var mark in edits.Marks ?? []) Draw(image, mark);
            if ((options.Redaction || edits.Redactions is { Length: > 0 }) && options.Format != ImageFormat.Png)
                throw new NotSupportedException("Redaction exports use lossless PNG.");
            if (options.Redaction && edits.Redactions is not { Length: > 0 })
                throw new InvalidDataException("Draw at least one redaction mask before exporting.");

            if (edits.Redactions is { Length: > 0 } regions)
            {
                // Replace before any resampling or geometric transform can mix protected source pixels.
                var width = image.Width;
                var height = image.Height;
                using var pixels = image.GetPixels();
                var rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("Pixels unavailable.");
                foreach (var region in regions)
                {
                    token.ThrowIfCancellationRequested();
                    if (!region.Fits(width, height)) throw new InvalidDataException("A redaction is outside the image.");
                    for (var y = region.Y; y < region.Y + region.Height; y++)
                    {
                        if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                        var offset = (y * checked((int)width) + region.X) * 4;
                        rgba.AsSpan(offset, region.Width * 4).Clear();
                        for (var x = 0; x < region.Width; x++) rgba[offset + x * 4 + 3] = 255;
                    }
                }
                using var fresh = new MagickImage();
                fresh.ReadPixels(rgba, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGBA));
                return TransformAndEncode(fresh, edits, options, token);
            }
            return TransformAndEncode(image, edits, options, token);
        }
        catch (MagickException error)
        { throw new InvalidDataException("The image could not be processed within the format or resource limits.", error); }
    }

    private static ProcessedImage TransformAndEncode(IMagickImage<byte> image, ImageEdits edits, ImageOptions options,
        CancellationToken token)
    {
        if (edits.Crop is { } crop)
        {
            if (!crop.Fits(image.Width, image.Height)) throw new InvalidDataException("Crop is outside the oriented image.");
            image.Crop(new MagickGeometry(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height));
            image.ResetPage();
        }
        if (edits.FlipHorizontal) image.Flop();
        if (edits.FlipVertical) image.Flip();
        if (edits.Rotation is 90 or 180 or 270) image.Rotate(edits.Rotation);
        if (edits.Brightness != 0 || edits.Contrast != 0)
            image.BrightnessContrast((Percentage)edits.Brightness, (Percentage)edits.Contrast);
        if (edits.Saturation != 100) image.Modulate((Percentage)100, (Percentage)edits.Saturation);
        if (edits.ResizeWidth != 0 || edits.ResizeHeight != 0)
        {
            var width = edits.ResizeWidth != 0 ? edits.ResizeWidth :
                (uint)Math.Max(1, Math.Round(edits.ResizeHeight * (double)image.Width / image.Height));
            var height = edits.ResizeHeight != 0 ? edits.ResizeHeight :
                (uint)Math.Max(1, Math.Round(edits.ResizeWidth * (double)image.Height / image.Width));
            CheckDimensions(width, height);
            image.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        }
        if (edits.MaxEdge > 0 && Math.Max(image.Width, image.Height) > edits.MaxEdge)
            image.Resize(edits.MaxEdge, edits.MaxEdge);
        token.ThrowIfCancellationRequested();
        if (edits.Padding > 0 || edits.Radius > 0 || edits.Shadow || edits.Gradient is not null)
        {
            using var framed = Frame(image, edits);
            return Encode(framed, options, token);
        }
        return Encode(image, options, token);
    }

    private static void Draw(IMagickImage<byte> image, ImageMark mark)
    {
        if (mark.Points.Length < 2 || mark.Points.Length > 4096)
            throw new InvalidDataException("An annotation has an invalid number of points.");
        var start = mark.Points[0];
        var end = mark.Points[^1];
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        var color = new MagickColor(mark.Color);
        var thickness = Math.Max(2, Math.Max(image.Width, image.Height) / 250.0);
        var draw = new Drawables().FillColor(MagickColors.Transparent).StrokeColor(color).StrokeWidth(thickness);
        switch (mark.Kind)
        {
            case MarkKind.Rectangle: draw.Rectangle(left, top, right, bottom); break;
            case MarkKind.Ellipse: draw.Ellipse((left + right) / 2.0, (top + bottom) / 2.0,
                (right - left) / 2.0, (bottom - top) / 2.0, 0, 360); break;
            case MarkKind.Freehand:
                if (mark.Points.Length == 2) draw.Line(start.X, start.Y, end.X, end.Y);
                else draw.Polyline(mark.Points.Select(point => new PointD(point.X, point.Y)));
                break;
            case MarkKind.Arrow:
                var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                var size = thickness * 5;
                draw.Line(start.X, start.Y, end.X, end.Y)
                    .Line(end.X, end.Y, end.X - size * Math.Cos(angle - 0.5), end.Y - size * Math.Sin(angle - 0.5))
                    .Line(end.X, end.Y, end.X - size * Math.Cos(angle + 0.5), end.Y - size * Math.Sin(angle + 0.5));
                break;
            case MarkKind.Text:
                var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
                if (!File.Exists(font)) throw new NotSupportedException("Segoe UI is unavailable for text annotations.");
                draw.FillColor(color).StrokeColor(MagickColors.Transparent).Font(font)
                    .FontPointSize(Math.Max(18, thickness * 6))
                    .Text(start.X, start.Y, mark.Text[..Math.Min(250, mark.Text.Length)].Replace("%", "%%"));
                break;
        }
        draw.Draw(image);
    }

    private static MagickImage Frame(IMagickImage<byte> image, ImageEdits edits)
    {
        var padding = (int)Math.Min(edits.Padding, 512);
        var width = image.Width + (uint)(padding * 2);
        var height = image.Height + (uint)(padding * 2);
        CheckDimensions(width, height);
        var color = new MagickColor(edits.FrameColor);
        var background = new MagickImage(color, width, height);
        try
        {
            if (edits.Gradient is { } gradient)
            {
                var finish = new MagickColor(gradient);
                var draw = new Drawables().StrokeColor(MagickColors.Transparent);
                for (var band = 0; band < 128; band++)
                {
                    var fraction = band / 127.0;
                    var tint = new MagickColor(
                        (byte)Math.Round(color.R + (finish.R - color.R) * fraction),
                        (byte)Math.Round(color.G + (finish.G - color.G) * fraction),
                        (byte)Math.Round(color.B + (finish.B - color.B) * fraction));
                    draw.FillColor(tint).Rectangle(0, (long)band * height / 128, width, (long)(band + 1) * height / 128);
                }
                draw.Draw(background);
            }
            if (edits.Radius > 0)
            {
                using var mask = new MagickImage(MagickColors.Transparent, image.Width, image.Height);
                var radius = Math.Min(edits.Radius, Math.Min(image.Width, image.Height) / 2.0);
                new Drawables().FillColor(MagickColors.White).RoundRectangle(0, 0, image.Width - 1, image.Height - 1,
                    radius, radius).Draw(mask);
                image.Composite(mask, CompositeOperator.DstIn);
            }
            if (edits.Shadow && padding > 0)
            {
                using var shadow = image.Clone();
                shadow.Shadow(0, 0, Math.Min(18, padding / 2.0), (Percentage)45, MagickColors.Black);
                background.Composite(shadow, padding + shadow.Page.X, padding + shadow.Page.Y + 3, CompositeOperator.Over);
            }
            background.Composite(image, padding, padding, CompositeOperator.Over);
            return background;
        }
        catch { background.Dispose(); throw; }
    }

    private static ProcessedImage Encode(IMagickImage<byte> image, ImageOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        image.Strip();
        var format = options.Format switch
        {
            ImageFormat.Png => MagickFormat.Png,
            ImageFormat.Jpeg => MagickFormat.Jpeg,
            ImageFormat.WebP => MagickFormat.WebP,
            _ => throw new NotSupportedException("Output format is unsupported.")
        };
        if (format == MagickFormat.Jpeg)
        {
            image.ColorAlpha(MagickColors.White);
            image.Alpha(AlphaAction.Off);
            image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
        }
        if (format == MagickFormat.Png)
        {
            image.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "all");
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        }
        if (format == MagickFormat.WebP)
        {
            image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", options.Lossless ? "true" : "false");
        }
        var bytes = image.ToByteArray(format);
        token.ThrowIfCancellationRequested();
        using var decoded = new MagickImage(bytes, format);
        if (decoded.Width != image.Width || decoded.Height != image.Height || decoded.ProfileNames.Any())
            throw new InvalidDataException("The encoded result failed validation.");
        return new(bytes, options.Format, image.Width, image.Height);
    }

    public static Task<string> SaveAsync(ProcessedImage image, string folder, string stem, CancellationToken token) =>
        OutputFiles.WriteAsync(folder, stem, Extension(image.Format),
            (path, cancellation) => File.WriteAllBytesAsync(path, image.Bytes, cancellation),
            async (path, cancellation) =>
            {
                using var decoded = await ReadAsync(path, cancellation);
                if (decoded.Width != image.Width || decoded.Height != image.Height)
                    throw new InvalidDataException("The staged image dimensions differ from the result.");
            }, token);

    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg", ImageFormat.WebP => ".webp", _ => ".png"
    };

    private static void CheckDimensions(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 16384 || height > 16384 || (long)width * height > 45_000_000)
            throw new InvalidDataException("The image exceeds the 45-megapixel limit.");
    }
}
