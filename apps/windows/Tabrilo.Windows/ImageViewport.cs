using System;
using System.Windows;
using Tabrilo.Processing;

namespace Tabrilo.Windows;

internal readonly record struct ImageViewport(Rect Display, int Width, int Height)
{
    public ImagePoint At(Point position) => new(
        Math.Clamp((int)Math.Floor((position.X - Display.Left) * Width / Display.Width), 0, Width - 1),
        Math.Clamp((int)Math.Floor((position.Y - Display.Top) * Height / Display.Height), 0, Height - 1));

    public Point At(double x, double y) =>
        new(Display.Left + x * Display.Width / Width, Display.Top + y * Display.Height / Height);

    public Rect Bounds(PixelRect area) => new(At(area.X, area.Y), At(area.X + area.Width, area.Y + area.Height));

    public PixelRect Move(PixelRect area, ImagePoint start, ImagePoint current) => area with
    {
        X = Math.Clamp(area.X + current.X - start.X, 0, Width - area.Width),
        Y = Math.Clamp(area.Y + current.Y - start.Y, 0, Height - area.Height)
    };

    public PixelRect Resize(PixelRect area, ImagePoint start, ImagePoint current, int horizontal, int vertical, double aspect)
    {
        var right = area.X + area.Width;
        var bottom = area.Y + area.Height;
        var edgeX = Math.Clamp((horizontal < 0 ? area.X : right) + current.X - start.X,
            horizontal < 0 ? 0 : area.X + 1, horizontal < 0 ? right - 1 : Width);
        var edgeY = Math.Clamp((vertical < 0 ? area.Y : bottom) + current.Y - start.Y,
            vertical < 0 ? 0 : area.Y + 1, vertical < 0 ? bottom - 1 : Height);
        var width = horizontal < 0 ? right - edgeX : edgeX - area.X;
        var height = vertical < 0 ? bottom - edgeY : edgeY - area.Y;
        if (aspect > 0)
        {
            width = Math.Max(1, Math.Min(width, (int)Math.Round(height * aspect)));
            height = Math.Max(1, Math.Min(height, (int)Math.Round(width / aspect)));
        }
        return new(horizontal < 0 ? right - width : area.X, vertical < 0 ? bottom - height : area.Y, width, height);
    }
}
