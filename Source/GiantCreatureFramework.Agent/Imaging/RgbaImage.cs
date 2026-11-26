namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// 8-bit RGBA raster stored as a row-major byte array with the origin at the
/// top-left pixel, x growing right and y growing down. Four bytes per pixel in
/// R, G, B, A order. All imaging operations in this library produce and consume
/// this type; alpha is straight (non-premultiplied).
/// </summary>
public sealed class RgbaImage
{
    public int Width { get; }

    public int Height { get; }

    /// <summary>Length must equal Width * Height * 4. R,G,B,A per pixel, rows top-down.</summary>
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height)
    {
        ValidateSize(width, height);
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        ValidateSize(width, height);
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"Pixel buffer length {pixels.Length} does not match {width}x{height}x4.");
        }
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public byte GetR(int x, int y) => Pixels[(y * Width + x) * 4];
    public byte GetG(int x, int y) => Pixels[(y * Width + x) * 4 + 1];
    public byte GetB(int x, int y) => Pixels[(y * Width + x) * 4 + 2];
    public byte GetA(int x, int y) => Pixels[(y * Width + x) * 4 + 3];

    public void SetPixel(int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * Width + x) * 4;
        Pixels[i] = r;
        Pixels[i + 1] = g;
        Pixels[i + 2] = b;
        Pixels[i + 3] = a;
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    /// <summary>Crops [x0, x1) x [y0, y1). Bounds must lie inside the image.</summary>
    internal RgbaImage Crop(int x0, int y0, int x1, int y1)
    {
        if (x0 < 0 || y0 < 0 || x1 > Width || y1 > Height || x1 < x0 || y1 < y0)
        {
            throw new ArgumentOutOfRangeException(nameof(x0), $"Invalid crop box ({x0}, {y0}, {x1}, {y1}).");
        }
        int cw = x1 - x0;
        int ch = y1 - y0;
        var result = new RgbaImage(cw, ch);
        int srcStride = Width * 4;
        int dstStride = cw * 4;
        for (int row = 0; row < ch; row++)
        {
            System.Buffer.BlockCopy(
                Pixels,
                (y0 + row) * srcStride + x0 * 4,
                result.Pixels,
                row * dstStride,
                dstStride);
        }
        return result;
    }

    /// <summary>Mirrors columns: output x maps to source x = width - 1 - x.</summary>
    internal RgbaImage FlipHorizontal()
    {
        var result = new RgbaImage(Width, Height);
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            for (int x = 0; x < Width; x++)
            {
                int src = row + (Width - 1 - x) * 4;
                int dst = row + x * 4;
                result.Pixels[dst] = Pixels[src];
                result.Pixels[dst + 1] = Pixels[src + 1];
                result.Pixels[dst + 2] = Pixels[src + 2];
                result.Pixels[dst + 3] = Pixels[src + 3];
            }
        }
        return result;
    }

    private static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Image size {width}x{height} is not positive.");
        }
        if ((long)width * height > int.MaxValue / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image is too large for a 32-bit pixel buffer.");
        }
    }
}
