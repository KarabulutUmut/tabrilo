using System.Numerics;
using System.Runtime.InteropServices;
using ImageMagick;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Data.Pdf;
using WinRT;

namespace Tabrilo.Processing;

// The native PDF API accepts an application-owned DXGI surface. Its graphics device can
// therefore be disposed while Windows still has a working thread pool, including on WARP.
internal sealed class PdfSurfaceRenderer : IDisposable
{
    private static readonly SemaphoreSlim RenderSlot = new(1);
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IPdfRendererNative renderer;

    private PdfSurfaceRenderer()
    {
        var flags = DeviceCreationFlags.BgraSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device!).Failure)
            device = D3D11.D3D11CreateDevice(DriverType.Warp, flags | DeviceCreationFlags.PreventInternalThreadingOptimizations, levels);
        try
        {
            using var dxgi = device.QueryInterface<IDXGIDevice>();
            context = device.ImmediateContext;
            try { Marshal.ThrowExceptionForHR(PdfCreateRenderer(dxgi.NativePointer, out renderer!)); }
            catch { context.Dispose(); throw; }
        }
        catch { device.Dispose(); throw; }
    }

    internal static async Task<ProcessedImage> RenderAsync(PdfPage page, uint width, uint height, ImageFormat format, CancellationToken token)
    {
        // Windows advises rendering one page at a time with PdfCreateRenderer.
        await RenderSlot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var renderer = new PdfSurfaceRenderer();
            return renderer.Render(page, width, height, format, token);
        }
        finally { RenderSlot.Release(); }
    }

    private ProcessedImage Render(PdfPage page, uint width, uint height, ImageFormat format, CancellationToken token)
    {
        var pixels = new byte[checked((int)(width * height * 4))];
        // Tiles bound device memory and the interval between cancellation checks.
        for (uint y = 0; y < height; y += 1024)
        for (uint x = 0; x < width; x += 1024)
        {
            token.ThrowIfCancellationRequested();
            var w = Math.Min(width - x, 1024);
            var h = Math.Min(height - y, 1024);
            var size = page.Size;
            var options = new RenderParameters
            {
                Source = new((float)(size.Width * x / width), (float)(size.Height * y / height),
                    (float)(size.Width * (x + w) / width), (float)(size.Height * (y + h) / height)),
                Width = w, Height = h, Background = Vector4.One, IgnoreHighContrast = 1
            };
            using var target = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h,
                arraySize: 1, mipLevels: 1, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource));
            using var surface = target.QueryInterface<IDXGISurface>();
            renderer.RenderPageToSurface(((IWinRTObject)page).NativeObject.ThisPtr, surface.NativePointer, default, ref options);
            using var staging = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h,
                arraySize: 1, mipLevels: 1, bindFlags: BindFlags.None, usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read));
            context.CopyResource(staging, target);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (uint row = 0; row < h; row++)
                    Marshal.Copy(mapped.DataPointer + checked((int)(row * mapped.RowPitch)), pixels,
                        checked((int)(((y + row) * width + x) * 4)), checked((int)(w * 4)));
            }
            finally { context.Unmap(staging, 0); }
        }
        token.ThrowIfCancellationRequested();
        using var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings(width, height, StorageType.Char, "BGRA"));
        return Images.Process(image, new ImageOptions(format), token);
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(renderer);
        context.Dispose();
        device.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderParameters
    {
        public Vector4 Source;
        public uint Width, Height;
        public Vector4 Background;
        public byte IgnoreHighContrast;
    }

    [ComImport, Guid("7D9DCD91-D277-4947-8527-07A0DAEDA94A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPdfRendererNative
    {
        void RenderPageToSurface(IntPtr page, IntPtr surface, System.Drawing.Point offset, ref RenderParameters parameters);
    }

    [DllImport("Windows.Data.Pdf.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int PdfCreateRenderer(IntPtr dxgiDevice, [MarshalAs(UnmanagedType.Interface)] out IPdfRendererNative renderer);
}
