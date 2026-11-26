namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// Renders RGBA sprites over a dark two-tone checkerboard grid for visual
/// inspection. Sprites are downscaled with a Lanczos resampler whose fixed-point
/// weighting matches the reference raster pipeline, composited with the
/// standard integer alpha-over rule, and optionally captioned with a frozen
/// bitmap glyph atlas.
/// </summary>
public static class PreviewRenderer
{
    private const int Pad = 8;
    private const int HeaderHeight = 24;
    private const int SquareSize = 16;

    public static RgbaImage RenderCheckerboardPreview(
        IReadOnlyList<RgbaImage> images,
        IReadOnlyList<string>? labels = null,
        int tileSize = 256,
        int cols = 4)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (tileSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tileSize));
        }
        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols));
        }
        bool hasLabels = labels != null && labels.Count > 0;
        int count = Math.Max(1, images.Count);
        int columnCount = Math.Min(cols, count);
        int rowCount = (count + columnCount - 1) / columnCount;
        int cellW = tileSize + Pad * 2;
        int cellH = tileSize + (hasLabels ? HeaderHeight : 0) + Pad * 2;

        var board = new RgbaImage(columnCount * cellW, rowCount * cellH);
        byte[] bp = board.Pixels;
        for (int i = 0; i < bp.Length; i += 4)
        {
            bp[i] = 28;
            bp[i + 1] = 30;
            bp[i + 2] = 36;
            bp[i + 3] = 255;
        }
        for (int y = 0; y < board.Height; y++)
        {
            for (int x = 0; x < board.Width; x++)
            {
                bool even = ((x / SquareSize) + (y / SquareSize)) % 2 == 0;
                int p = (y * board.Width + x) * 4;
                if (even)
                {
                    bp[p] = 46;
                    bp[p + 1] = 48;
                    bp[p + 2] = 56;
                }
                else
                {
                    bp[p] = 34;
                    bp[p + 1] = 36;
                    bp[p + 2] = 42;
                }
            }
        }

        for (int idx = 0; idx < images.Count; idx++)
        {
            var rgba = images[idx] ?? throw new ArgumentNullException(nameof(images), $"Image {idx} is null.");
            int row = idx / columnCount;
            int col = idx % columnCount;
            int ox = col * cellW + Pad;
            int oy = row * cellH + Pad + (hasLabels ? HeaderHeight : 0);
            double scale = Math.Min(tileSize / (double)rgba.Width, tileSize / (double)rgba.Height);
            int nw = Math.Max(1, (int)Math.Round(rgba.Width * scale, MidpointRounding.ToEven));
            int nh = Math.Max(1, (int)Math.Round(rgba.Height * scale, MidpointRounding.ToEven));
            // The reference resample path premultiplies alpha, resizes, and
            // removes the premultiplication, so fully transparent pixels keep
            // black RGB instead of bleeding color halos into the output.
            RgbaImage sprite = nw == rgba.Width && nh == rgba.Height
                ? rgba.Clone()
                : UnpremultiplyAlpha(LanczosResize(PremultiplyAlpha(rgba), nw, nh));
            int px = ox + (tileSize - nw) / 2;
            int py = oy + (tileSize - nh) / 2;
            AlphaCompositeOnto(board, px, py, sprite);
            if (hasLabels && idx < labels!.Count)
            {
                DrawText(board, ox + 2, row * cellH + 6, labels[idx], (220, 224, 232, 255));
            }
        }
        return board;
    }

    /// <summary>Straight alpha to premultiplied: RGB scaled by alpha over 255.</summary>
    private static RgbaImage PremultiplyAlpha(RgbaImage src)
    {
        var result = new RgbaImage(src.Width, src.Height);
        for (int i = 0; i < src.Pixels.Length; i += 4)
        {
            uint a = src.Pixels[i + 3];
            result.Pixels[i] = (byte)ShiftForDiv255(src.Pixels[i] * a + 128u);
            result.Pixels[i + 1] = (byte)ShiftForDiv255(src.Pixels[i + 1] * a + 128u);
            result.Pixels[i + 2] = (byte)ShiftForDiv255(src.Pixels[i + 2] * a + 128u);
            result.Pixels[i + 3] = src.Pixels[i + 3];
        }
        return result;
    }

    /// <summary>Premultiplied back to straight; zero-alpha pixels keep their stored RGB.</summary>
    private static RgbaImage UnpremultiplyAlpha(RgbaImage src)
    {
        var result = new RgbaImage(src.Width, src.Height);
        for (int i = 0; i < src.Pixels.Length; i += 4)
        {
            uint a = src.Pixels[i + 3];
            if (a == 255 || a == 0)
            {
                result.Pixels[i] = src.Pixels[i];
                result.Pixels[i + 1] = src.Pixels[i + 1];
                result.Pixels[i + 2] = src.Pixels[i + 2];
            }
            else
            {
                result.Pixels[i] = (byte)Math.Clamp(255 * src.Pixels[i] / a, 0, 255);
                result.Pixels[i + 1] = (byte)Math.Clamp(255 * src.Pixels[i + 1] / a, 0, 255);
                result.Pixels[i + 2] = (byte)Math.Clamp(255 * src.Pixels[i + 2] / a, 0, 255);
            }
            result.Pixels[i + 3] = src.Pixels[i + 3];
        }
        return result;
    }

    /// <summary>
    /// Integer alpha-over composite of the sprite onto the board region at
    /// (destX, destY); the region is replaced by the composite including alpha.
    /// </summary>
    private static void AlphaCompositeOnto(RgbaImage board, int destX, int destY, RgbaImage sprite)
    {
        byte[] dp = board.Pixels;
        byte[] sp = sprite.Pixels;
        for (int y = 0; y < sprite.Height; y++)
        {
            int boardRow = (destY + y) * board.Width;
            for (int x = 0; x < sprite.Width; x++)
            {
                int s = (y * sprite.Width + x) * 4;
                int d = (boardRow + destX + x) * 4;
                if (sp[s + 3] == 0)
                {
                    continue;
                }
                uint srcA = sp[s + 3];
                uint dstA = dp[d + 3];
                uint blend = dstA * (255u - srcA);
                uint outA255 = srcA * 255u + blend;
                uint coef1 = srcA * 255u * 255u * 128u / outA255;
                uint coef2 = 255u * 128u - coef1;
                dp[d] = (byte)(ShiftForDiv255(sp[s] * coef1 + dp[d] * coef2 + (0x80u << 7)) >> 7);
                dp[d + 1] = (byte)(ShiftForDiv255(sp[s + 1] * coef1 + dp[d + 1] * coef2 + (0x80u << 7)) >> 7);
                dp[d + 2] = (byte)(ShiftForDiv255(sp[s + 2] * coef1 + dp[d + 2] * coef2 + (0x80u << 7)) >> 7);
                dp[d + 3] = (byte)ShiftForDiv255(outA255 + 0x80u);
            }
        }
    }

    private static uint ShiftForDiv255(uint value)
    {
        return ((value >> 8) + value) >> 8;
    }

    /// <summary>
    /// Lanczos-3 resample in two passes (horizontal then vertical). Filter
    /// weights are computed per output coordinate in double precision over the
    /// scaled support, normalized, then quantized to 22.22 fixed point; each
    /// accumulation starts at the half point and shifts with saturation.
    /// </summary>
    internal static RgbaImage LanczosResize(RgbaImage src, int newWidth, int newHeight)
    {
        RgbaImage current = src;
        if (newWidth != src.Width)
        {
            current = ResamplePass(current, newWidth, current.Height, horizontal: true);
        }
        if (newHeight != current.Height)
        {
            current = ResamplePass(current, newHeight, current.Width, horizontal: false);
        }
        return current;
    }

    private static RgbaImage ResamplePass(RgbaImage src, int newSize, int otherSize, bool horizontal)
    {
        int inSize = horizontal ? src.Width : src.Height;
        (int[] bounds, int[] weights, int ksize) = PrecomputeCoefficients(inSize, newSize);
        var result = horizontal ? new RgbaImage(newSize, otherSize) : new RgbaImage(otherSize, newSize);
        byte[] sp = src.Pixels;
        byte[] rp = result.Pixels;
        int channels = 4;

        for (int other = 0; other < otherSize; other++)
        {
            for (int outIdx = 0; outIdx < newSize; outIdx++)
            {
                int xmin = bounds[outIdx * 2];
                int xmax = bounds[outIdx * 2 + 1];
                int wBase = outIdx * ksize;
                int ss0 = 1 << 21, ss1 = 1 << 21, ss2 = 1 << 21, ss3 = 1 << 21;
                for (int t = 0; t < xmax; t++)
                {
                    int k = weights[wBase + t];
                    int srcMain = xmin + t;
                    int srcIdx = horizontal
                        ? (other * src.Width + srcMain) * channels
                        : (srcMain * src.Width + other) * channels;
                    ss0 += sp[srcIdx] * k;
                    ss1 += sp[srcIdx + 1] * k;
                    ss2 += sp[srcIdx + 2] * k;
                    ss3 += sp[srcIdx + 3] * k;
                }
                int dstIdx = horizontal
                    ? (other * newSize + outIdx) * channels
                    : (outIdx * otherSize + other) * channels;
                rp[dstIdx] = Clip8(ss0);
                rp[dstIdx + 1] = Clip8(ss1);
                rp[dstIdx + 2] = Clip8(ss2);
                rp[dstIdx + 3] = Clip8(ss3);
            }
        }
        return result;
    }

    private static (int[] Bounds, int[] Weights, int Ksize) PrecomputeCoefficients(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterScale = Math.Max(scale, 1.0);
        double support = 3.0 * filterScale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var weights = new int[outSize * ksize];
        double inverseFilterScale = 1.0 / filterScale;
        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0)
            {
                xmin = 0;
            }
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize)
            {
                xmax = inSize;
            }
            xmax -= xmin;
            double ww = 0.0;
            var k = new double[ksize];
            for (int x = 0; x < xmax; x++)
            {
                double w = LanczosKernel((x + xmin - center + 0.5) * inverseFilterScale);
                k[x] = w;
                ww += w;
            }
            for (int x = 0; x < xmax; x++)
            {
                if (ww != 0.0)
                {
                    k[x] /= ww;
                }
            }
            for (int x = xmax; x < ksize; x++)
            {
                k[x] = 0;
            }
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
            int wBase = xx * ksize;
            for (int x = 0; x < ksize; x++)
            {
                double v = k[x] * (1 << 22);
                weights[wBase + x] = (int)(k[x] < 0 ? -0.5 + v : 0.5 + v);
            }
        }
        return (bounds, weights, ksize);
    }

    private static double LanczosKernel(double x)
    {
        if (x < -3.0 || x >= 3.0)
        {
            return 0.0;
        }
        return Sinc(x) * Sinc(x / 3.0);
    }

    private static double Sinc(double x)
    {
        if (x == 0.0)
        {
            return 1.0;
        }
        x *= Math.PI;
        return Math.Sin(x) / x;
    }

    private static byte Clip8(int value)
    {
        int index = value >> 22;
        return (byte)Math.Clamp(index, 0, 255);
    }

    /// <summary>
    /// Blends caption text onto the board using the frozen glyph atlas. Glyphs
    /// compose left to right at whole-pixel pen positions with an over-blend
    /// coverage rule; the composed strip is then blended per channel with the
    /// ink color through the same divide-by-255 approximation as the composite.
    /// </summary>
    private static void DrawText(RgbaImage board, int x, int y, string text, (byte R, byte G, byte B, byte A) ink)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        (byte[] mask, int width, int height, int offX, int offY) = RenderString(text);
        if (width == 0 || height == 0)
        {
            return;
        }
        int originX = x + offX;
        int originY = y + offY;
        byte[] bp = board.Pixels;
        for (int row = 0; row < height; row++)
        {
            int by = originY + row;
            if (by < 0 || by >= board.Height)
            {
                continue;
            }
            for (int col = 0; col < width; col++)
            {
                byte m = mask[row * width + col];
                if (m == 0)
                {
                    continue;
                }
                int bx = originX + col;
                if (bx < 0 || bx >= board.Width)
                {
                    continue;
                }
                int p = (by * board.Width + bx) * 4;
                // The destination is opaque, so the text coverage applies directly.
                bp[p] = BlendChannel(m, bp[p], ink.R);
                bp[p + 1] = BlendChannel(m, bp[p + 1], ink.G);
                bp[p + 2] = BlendChannel(m, bp[p + 2], ink.B);
                bp[p + 3] = BlendChannel(m, bp[p + 3], ink.A);
            }
        }
    }

    private static byte BlendChannel(byte mask, byte dst, byte srcInk)
    {
        uint v = (uint)(dst * (255 - mask) + srcInk * mask) + 128u;
        v = ((v >> 8) + v) >> 8;
        return (byte)v;
    }

    private static readonly byte[] FontBlob = PreviewFontData.Decode();

    private static readonly int Ascent = FontBlob[0];

    private static readonly GlyphInfo[] Glyphs = ParseGlyphs(FontBlob);

    private sealed class GlyphInfo
    {
        public int Width;
        public int Height;
        public int OffsetX;
        public int OffsetY;
        public int InkRight;   // right edge of the layout box
        public int InkBottom;  // bottom edge of the layout box
        public int Advance;
        public byte[] Bitmap = Array.Empty<byte>();
    }

    private static GlyphInfo[] ParseGlyphs(byte[] blob)
    {
        var glyphs = new GlyphInfo[PreviewFontData.CharCount];
        int pos = 1;
        for (int i = 0; i < PreviewFontData.CharCount; i++)
        {
            var glyph = new GlyphInfo
            {
                Width = blob[pos],
                Height = blob[pos + 1],
                OffsetX = (sbyte)blob[pos + 2],
                OffsetY = (sbyte)blob[pos + 3],
                InkRight = blob[pos + 4],
                InkBottom = blob[pos + 5],
                Advance = blob[pos + 6],
            };
            pos += 7;
            int bytes = glyph.Width * glyph.Height;
            if (bytes > 0)
            {
                glyph.Bitmap = new byte[bytes];
                Buffer.BlockCopy(blob, pos, glyph.Bitmap, 0, bytes);
            }
            pos += bytes;
            glyphs[i] = glyph;
        }
        return glyphs;
    }

    private static GlyphInfo GlyphFor(char c)
    {
        int index = c - PreviewFontData.FirstChar;
        if (c > 127 || index < 0 || index >= Glyphs.Length)
        {
            // Characters outside the atlas have no glyph; the reference raster
            // would fail to encode them, so map them to the blank slot.
            index = ' ' - PreviewFontData.FirstChar;
        }
        return Glyphs[index];
    }

    private static (byte[] Mask, int Width, int Height, int OffsetX, int OffsetY) RenderString(string text)
    {
        int count = text.Length;
        var pixelPositions = new int[count];
        int position = 0;
        int xmin = 0;
        int xmax = 0;
        int minOffsetY = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var glyph = GlyphFor(text[i]);
            pixelPositions[i] = position;
            xmin = Math.Min(xmin, position + glyph.OffsetX);
            xmax = Math.Max(xmax, position + glyph.InkRight);
            minOffsetY = Math.Min(minOffsetY, glyph.OffsetY);
            position += glyph.Advance;
        }
        xmax = Math.Max(xmax, position);
        int width = xmax - xmin;
        int ymax = Ascent - (count > 0 ? minOffsetY : 0);
        int ymin = 0;
        for (int i = 0; i < count; i++)
        {
            ymin = Math.Min(ymin, Ascent - GlyphFor(text[i]).InkBottom);
        }
        int height = ymax - ymin;
        if (width <= 0 || height <= 0)
        {
            return (Array.Empty<byte>(), 0, 0, xmin, count > 0 ? minOffsetY : 0);
        }
        var canvas = new byte[width * height];
        for (int i = 0; i < count; i++)
        {
            var glyph = GlyphFor(text[i]);
            if (glyph.Bitmap.Length == 0)
            {
                continue;
            }
            int col0 = pixelPositions[i] + glyph.OffsetX - xmin;
            int row0 = glyph.OffsetY - minOffsetY;
            for (int gr = 0; gr < glyph.Height; gr++)
            {
                int cy = row0 + gr;
                if (cy < 0 || cy >= height)
                {
                    continue;
                }
                for (int gc = 0; gc < glyph.Width; gc++)
                {
                    int cx = col0 + gc;
                    if (cx < 0 || cx >= width)
                    {
                        continue;
                    }
                    byte src = glyph.Bitmap[gr * glyph.Width + gc];
                    if (src == 0)
                    {
                        continue;
                    }
                    int di = cy * width + cx;
                    byte dst = canvas[di];
                    canvas[di] = dst > 0
                        ? (byte)Math.Clamp(src + MulDiv255(dst, 255 - src), 0, 255)
                        : src;
                }
            }
        }
        return (canvas, width, height, xmin, minOffsetY);
    }

    private static int MulDiv255(int a, int b)
    {
        uint v = (uint)(a * b) + 128u;
        v = ((v >> 8) + v) >> 8;
        return (int)v;
    }
}
