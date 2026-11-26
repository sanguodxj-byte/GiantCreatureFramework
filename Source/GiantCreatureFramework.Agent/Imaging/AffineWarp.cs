using System.Buffers.Binary;

namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// Inverse-mapped affine warp with 1/32-pixel fixed-point source coordinates.
/// The forward matrix is built in double precision, narrowed to 32-bit float,
/// widened back to double and inverted; every destination pixel maps to a source
/// position rounded to the nearest 1/32 pixel (round-half-up on the 1/32 grid),
/// then resampled. Bilinear sampling uses integer weights scaled to 32768 for
/// 8-bit sources and float weights for 32-bit float sources; cubic sampling
/// always runs on float sources with a 4x4 float kernel. Constant border value
/// replaces any tap that falls outside the source.
/// </summary>
internal static class AffineWarp
{
    private const int FracBits = 5;
    private const int FracSize = 1 << FracBits;          // 32
    private const int AccumBits = 10;                    // intermediate scale
    private const int AccumScale = 1 << AccumBits;       // 1024

    /// <summary>Builds the 2x3 forward matrix (source to destination) as 32-bit floats.</summary>
    public static float[] BuildForwardMatrix(
        double srcAnchorX, double srcAnchorY,
        double dstAnchorX, double dstAnchorY,
        double angleDeg, double scaleX, double scaleY)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double ca = Math.Cos(rad);
        double sa = Math.Sin(rad);
        return new float[]
        {
            (float)(ca * scaleX),
            (float)(-sa * scaleY),
            (float)(dstAnchorX - (ca * scaleX * srcAnchorX - sa * scaleY * srcAnchorY)),
            (float)(sa * scaleX),
            (float)(ca * scaleY),
            (float)(dstAnchorY - (sa * scaleX * srcAnchorX + ca * scaleY * srcAnchorY)),
        };
    }

    /// <summary>Inverts a 2x3 forward matrix (row-major a11,a12,tx,a21,a22,ty) in double precision.</summary>
    public static double[] InvertForwardMatrix(float[] m)
    {
        double a11 = m[0], a12 = m[1], tx = m[2], a21 = m[3], a22 = m[4], ty = m[5];
        double det = a11 * a22 - a12 * a21;
        det = det != 0 ? 1.0 / det : 0;
        double i11 = a22 * det, i22 = a11 * det;
        double m0 = i11, m1 = a12 * -det, m3 = a21 * -det, m4 = i22;
        double b1 = -m0 * tx - m1 * ty;
        double b2 = -m3 * tx - m4 * ty;
        return new[] { m0, m1, b1, m3, m4, b2 };
    }

    /// <summary>
    /// Computes per-destination-pixel integer source coordinates (whole pixels)
    /// and interpolation fraction indices on the 1/32 grid.
    /// </summary>
    public static (int[] Sx, int[] Sy, int[] FracIndex) BuildMap(double[] invM, int dstWidth, int dstHeight)
    {
        var adelta = new int[dstWidth];
        var bdelta = new int[dstWidth];
        for (int x = 0; x < dstWidth; x++)
        {
            adelta[x] = SaturateRound(invM[0] * x * AccumScale);
            bdelta[x] = SaturateRound(invM[3] * x * AccumScale);
        }
        int roundDelta = AccumScale / FracSize / 2;
        var sx = new int[dstWidth * dstHeight];
        var sy = new int[dstWidth * dstHeight];
        var frac = new int[dstWidth * dstHeight];
        for (int y = 0; y < dstHeight; y++)
        {
            int x0 = SaturateRound((invM[1] * y + invM[2]) * AccumScale) + roundDelta;
            int y0 = SaturateRound((invM[4] * y + invM[5]) * AccumScale) + roundDelta;
            int rowBase = y * dstWidth;
            for (int x = 0; x < dstWidth; x++)
            {
                int vx = (x0 + adelta[x]) >> (AccumBits - FracBits);
                int vy = (y0 + bdelta[x]) >> (AccumBits - FracBits);
                int i = rowBase + x;
                sx[i] = SaturateShort(vx >> FracBits);
                sy[i] = SaturateShort(vy >> FracBits);
                frac[i] = ((vy & (FracSize - 1)) << FracBits) | (vx & (FracSize - 1));
            }
        }
        return (sx, sy, frac);
    }

    private static int SaturateRound(double value)
    {
        const double max = int.MaxValue - 0.5;
        const double min = int.MinValue + 0.5;
        if (value >= max)
        {
            return int.MaxValue;
        }
        if (value <= min)
        {
            return int.MinValue;
        }
        return (int)Math.Round(value, MidpointRounding.ToEven);
    }

    private static int SaturateShort(int value)
    {
        return value switch
        {
            > short.MaxValue => short.MaxValue,
            < short.MinValue => short.MinValue,
            _ => value,
        };
    }

    private static int BorderInterpolateConstant(int index, int size)
    {
        return (uint)index < (uint)size ? index : -1;
    }

    /// <summary>Warp of a 3-plane float source with a constant border color.</summary>
    public static float[] WarpFloat3(
        float[] src, int srcWidth, int srcHeight,
        int[] mapSx, int[] mapSy, int[] mapFrac,
        int dstWidth, int dstHeight,
        bool cubic, float borderR, float borderG, float borderB)
    {
        var dst = new float[dstWidth * dstHeight * 3];
        Span<int> xs = stackalloc int[4];
        Span<int> ys = stackalloc int[4];
        for (int i = 0; i < dstWidth * dstHeight; i++)
        {
            int fx = mapFrac[i];
            int sx = mapSx[i], sy = mapSy[i];
            int o = i * 3;
            if (cubic)
            {
                int bx = sx - 1, by = sy - 1;
                if (srcWidth >= 4 && srcHeight >= 4 && (uint)bx < (uint)(srcWidth - 3) && (uint)by < (uint)(srcHeight - 3))
                {
                    var w = CubicTabF2D[fx];
                    int row0 = (by * srcWidth + bx) * 3;
                    int row1 = row0 + srcWidth * 3;
                    int row2 = row1 + srcWidth * 3;
                    int row3 = row2 + srcWidth * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        float sum =
                            src[row0 + c] * w[0] + src[row0 + 3 + c] * w[1] + src[row0 + 6 + c] * w[2] + src[row0 + 9 + c] * w[3];
                        sum += src[row1 + c] * w[4] + src[row1 + 3 + c] * w[5] + src[row1 + 6 + c] * w[6] + src[row1 + 9 + c] * w[7];
                        sum += src[row2 + c] * w[8] + src[row2 + 3 + c] * w[9] + src[row2 + 6 + c] * w[10] + src[row2 + 9 + c] * w[11];
                        sum += src[row3 + c] * w[12] + src[row3 + 3 + c] * w[13] + src[row3 + 6 + c] * w[14] + src[row3 + 9 + c] * w[15];
                        dst[o + c] = sum;
                    }
                }
                else if (bx >= srcWidth || bx + 4 <= 0 || by >= srcHeight || by + 4 <= 0)
                {
                    dst[o] = borderR;
                    dst[o + 1] = borderG;
                    dst[o + 2] = borderB;
                }
                else
                {
                    for (int t = 0; t < 4; t++)
                    {
                        xs[t] = BorderInterpolateConstant(bx + t, srcWidth);
                        ys[t] = BorderInterpolateConstant(by + t, srcHeight);
                    }
                    var w = CubicTabF2D[fx];
                    for (int c = 0; c < 3; c++)
                    {
                        float cv = c == 0 ? borderR : c == 1 ? borderG : borderB;
                        float sum = cv;
                        for (int r = 0; r < 4; r++)
                        {
                            if (ys[r] < 0)
                            {
                                continue;
                            }
                            int rowBase = (ys[r] * srcWidth) * 3;
                            for (int t = 0; t < 4; t++)
                            {
                                if (xs[t] >= 0)
                                {
                                    sum += (src[rowBase + xs[t] * 3 + c] - cv) * w[r * 4 + t];
                                }
                            }
                        }
                        dst[o + c] = sum;
                    }
                }
            }
            else
            {
                if (srcWidth >= 2 && srcHeight >= 2 && (uint)sx < (uint)(srcWidth - 1) && (uint)sy < (uint)(srcHeight - 1))
                {
                    int wIdx = fx;
                    float w00 = BilinearTabF[wIdx * 4 + 0];
                    float w01 = BilinearTabF[wIdx * 4 + 1];
                    float w10 = BilinearTabF[wIdx * 4 + 2];
                    float w11 = BilinearTabF[wIdx * 4 + 3];
                    int baseIdx = (sy * srcWidth + sx) * 3;
                    int right = baseIdx + 3;
                    int down = baseIdx + srcWidth * 3;
                    int downRight = down + 3;
                    for (int c = 0; c < 3; c++)
                    {
                        dst[o + c] = src[baseIdx + c] * w00 + src[right + c] * w01 + src[down + c] * w10 + src[downRight + c] * w11;
                    }
                }
                else if (sx >= srcWidth || sx + 1 < 0 || sy >= srcHeight || sy + 1 < 0)
                {
                    dst[o] = borderR;
                    dst[o + 1] = borderG;
                    dst[o + 2] = borderB;
                }
                else
                {
                    int x0 = BorderInterpolateConstant(sx, srcWidth);
                    int x1 = BorderInterpolateConstant(sx + 1, srcWidth);
                    int y0 = BorderInterpolateConstant(sy, srcHeight);
                    int y1 = BorderInterpolateConstant(sy + 1, srcHeight);
                    int wIdx = fx;
                    float w00 = BilinearTabF[wIdx * 4 + 0];
                    float w01 = BilinearTabF[wIdx * 4 + 1];
                    float w10 = BilinearTabF[wIdx * 4 + 2];
                    float w11 = BilinearTabF[wIdx * 4 + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        float cv = c == 0 ? borderR : c == 1 ? borderG : borderB;
                        float v0 = x0 >= 0 && y0 >= 0 ? src[(y0 * srcWidth + x0) * 3 + c] : cv;
                        float v1 = x1 >= 0 && y0 >= 0 ? src[(y0 * srcWidth + x1) * 3 + c] : cv;
                        float v2 = x0 >= 0 && y1 >= 0 ? src[(y1 * srcWidth + x0) * 3 + c] : cv;
                        float v3 = x1 >= 0 && y1 >= 0 ? src[(y1 * srcWidth + x1) * 3 + c] : cv;
                        dst[o + c] = v0 * w00 + v1 * w01 + v2 * w10 + v3 * w11;
                    }
                }
            }
        }
        return dst;
    }

    /// <summary>Warp of a single-plane byte source with a constant border value.</summary>
    public static byte[] WarpByte1(
        byte[] src, int srcWidth, int srcHeight,
        int[] mapSx, int[] mapSy, int[] mapFrac,
        int dstWidth, int dstHeight, byte border)
    {
        var dst = new byte[dstWidth * dstHeight];
        for (int i = 0; i < dstWidth * dstHeight; i++)
        {
            int fx = mapFrac[i];
            int sx = mapSx[i], sy = mapSy[i];
            if (sx >= srcWidth || sx + 1 < 0 || sy >= srcHeight || sy + 1 < 0)
            {
                dst[i] = border;
                continue;
            }
            var w = BilinearTabI[fx];
            int x0 = BorderInterpolateConstant(sx, srcWidth);
            int x1 = BorderInterpolateConstant(sx + 1, srcWidth);
            int y0 = BorderInterpolateConstant(sy, srcHeight);
            int y1 = BorderInterpolateConstant(sy + 1, srcHeight);
            int v0 = x0 >= 0 && y0 >= 0 ? src[y0 * srcWidth + x0] : border;
            int v1 = x1 >= 0 && y0 >= 0 ? src[y0 * srcWidth + x1] : border;
            int v2 = x0 >= 0 && y1 >= 0 ? src[y1 * srcWidth + x0] : border;
            int v3 = x1 >= 0 && y1 >= 0 ? src[y1 * srcWidth + x1] : border;
            int sum = v0 * w[0] + v1 * w[1] + v2 * w[2] + v3 * w[3];
            dst[i] = SaturateByte((sum + (1 << 14)) >> 15);
        }
        return dst;
    }

    /// <summary>
    /// Warp of a 3-plane byte source (R,G,B interleaved) with a constant border
    /// color. Bilinear uses the 32768-scaled integer weights; cubic uses the
    /// quantized 4x4 integer kernel; the dot products accumulate in 32-bit
    /// integers and the result shifts back by 15 bits.
    /// </summary>
    public static byte[] WarpByte3(
        byte[] src, int srcWidth, int srcHeight,
        int[] mapSx, int[] mapSy, int[] mapFrac,
        int dstWidth, int dstHeight,
        bool cubic, byte borderR, byte borderG, byte borderB)
    {
        var dst = new byte[dstWidth * dstHeight * 3];
        Span<int> xs = stackalloc int[4];
        Span<int> ys = stackalloc int[4];
        for (int i = 0; i < dstWidth * dstHeight; i++)
        {
            int fx = mapFrac[i];
            int sx = mapSx[i], sy = mapSy[i];
            int o = i * 3;
            if (cubic)
            {
                int bx = sx - 1, by = sy - 1;
                if (srcWidth >= 4 && srcHeight >= 4 && (uint)bx < (uint)(srcWidth - 3) && (uint)by < (uint)(srcHeight - 3))
                {
                    short[] w = CubicTabI2D[fx];
                    int row0 = (by * srcWidth + bx) * 3;
                    int row1 = row0 + srcWidth * 3;
                    int row2 = row1 + srcWidth * 3;
                    int row3 = row2 + srcWidth * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int sum =
                            src[row0 + c] * w[0] + src[row0 + 3 + c] * w[1] + src[row0 + 6 + c] * w[2] + src[row0 + 9 + c] * w[3];
                        sum += src[row1 + c] * w[4] + src[row1 + 3 + c] * w[5] + src[row1 + 6 + c] * w[6] + src[row1 + 9 + c] * w[7];
                        sum += src[row2 + c] * w[8] + src[row2 + 3 + c] * w[9] + src[row2 + 6 + c] * w[10] + src[row2 + 9 + c] * w[11];
                        sum += src[row3 + c] * w[12] + src[row3 + 3 + c] * w[13] + src[row3 + 6 + c] * w[14] + src[row3 + 9 + c] * w[15];
                        dst[o + c] = SaturateByte((sum + (1 << 14)) >> 15);
                    }
                }
                else if (bx >= srcWidth || bx + 4 <= 0 || by >= srcHeight || by + 4 <= 0)
                {
                    dst[o] = borderR;
                    dst[o + 1] = borderG;
                    dst[o + 2] = borderB;
                }
                else
                {
                    for (int t = 0; t < 4; t++)
                    {
                        xs[t] = BorderInterpolateConstant(bx + t, srcWidth);
                        ys[t] = BorderInterpolateConstant(by + t, srcHeight);
                    }
                    short[] w = CubicTabI2D[fx];
                    for (int c = 0; c < 3; c++)
                    {
                        int cv = c == 0 ? borderR : c == 1 ? borderG : borderB;
                        int sum = cv * (1 << 15);
                        for (int r = 0; r < 4; r++)
                        {
                            if (ys[r] < 0)
                            {
                                continue;
                            }
                            int rowBase = ys[r] * srcWidth * 3;
                            for (int t = 0; t < 4; t++)
                            {
                                if (xs[t] >= 0)
                                {
                                    sum += (src[rowBase + xs[t] * 3 + c] - cv) * w[r * 4 + t];
                                }
                            }
                        }
                        dst[o + c] = SaturateByte((sum + (1 << 14)) >> 15);
                    }
                }
            }
            else
            {
                if (srcWidth >= 2 && srcHeight >= 2 && (uint)sx < (uint)(srcWidth - 1) && (uint)sy < (uint)(srcHeight - 1))
                {
                    short[] w = BilinearTabI[fx];
                    int baseIdx = (sy * srcWidth + sx) * 3;
                    int right = baseIdx + 3;
                    int down = baseIdx + srcWidth * 3;
                    int downRight = down + 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = src[baseIdx + c] * w[0] + src[right + c] * w[1] + src[down + c] * w[2] + src[downRight + c] * w[3];
                        dst[o + c] = SaturateByte((sum + (1 << 14)) >> 15);
                    }
                }
                else if (sx >= srcWidth || sx + 1 < 0 || sy >= srcHeight || sy + 1 < 0)
                {
                    dst[o] = borderR;
                    dst[o + 1] = borderG;
                    dst[o + 2] = borderB;
                }
                else
                {
                    int x0 = BorderInterpolateConstant(sx, srcWidth);
                    int x1 = BorderInterpolateConstant(sx + 1, srcWidth);
                    int y0 = BorderInterpolateConstant(sy, srcHeight);
                    int y1 = BorderInterpolateConstant(sy + 1, srcHeight);
                    short[] w = BilinearTabI[fx];
                    for (int c = 0; c < 3; c++)
                    {
                        int cv = c == 0 ? borderR : c == 1 ? borderG : borderB;
                        int v0 = x0 >= 0 && y0 >= 0 ? src[(y0 * srcWidth + x0) * 3 + c] : cv;
                        int v1 = x1 >= 0 && y0 >= 0 ? src[(y0 * srcWidth + x1) * 3 + c] : cv;
                        int v2 = x0 >= 0 && y1 >= 0 ? src[(y1 * srcWidth + x0) * 3 + c] : cv;
                        int v3 = x1 >= 0 && y1 >= 0 ? src[(y1 * srcWidth + x1) * 3 + c] : cv;
                        int sum = v0 * w[0] + v1 * w[1] + v2 * w[2] + v3 * w[3];
                        dst[o + c] = SaturateByte((sum + (1 << 14)) >> 15);
                    }
                }
            }
        }
        return dst;
    }

    private static byte SaturateByte(int value)
    {
        return value switch
        {
            <= 0 => 0,
            >= 255 => (byte)255,
            _ => (byte)value,
        };
    }

    /// <summary>1D linear weights at the 32 fixed fractions: (1 - x, x), float math.</summary>
    private static readonly float[] LinearTabF1D = BuildLinearTab();

    private static float[] BuildLinearTab()
    {
        var tab = new float[FracSize * 2];
        float scale = 1f / FracSize;
        for (int i = 0; i < FracSize; i++)
        {
            float x = i * scale;
            tab[i * 2] = 1f - x;
            tab[i * 2 + 1] = x;
        }
        return tab;
    }

    /// <summary>2D bilinear float weights per fraction index (fy * 32 + fx), row-major 2x2.</summary>
    private static readonly float[] BilinearTabF = BuildBilinearTabF();

    private static float[] BuildBilinearTabF()
    {
        var tab = new float[FracSize * FracSize * 4];
        for (int i = 0; i < FracSize; i++)
        {
            for (int j = 0; j < FracSize; j++)
            {
                int block = (i * FracSize + j) * 4;
                for (int k1 = 0; k1 < 2; k1++)
                {
                    float vy = LinearTabF1D[i * 2 + k1];
                    for (int k2 = 0; k2 < 2; k2++)
                    {
                        tab[block + k1 * 2 + k2] = vy * LinearTabF1D[j * 2 + k2];
                    }
                }
            }
        }
        return tab;
    }

    private static float[][] BuildCubicTab2D()
    {
        float scale = 1f / FracSize;
        var tab1d = new float[FracSize * 4];
        const float a = -0.75f;
        for (int i = 0; i < FracSize; i++)
        {
            float x = i * scale;
            float x1 = x + 1f;
            tab1d[i * 4 + 0] = ((a * x1 - 5f * a) * x1 + 8f * a) * x1 - 4f * a;
            tab1d[i * 4 + 1] = ((a + 2f) * x - (a + 3f)) * x * x + 1f;
            float u = 1f - x;
            tab1d[i * 4 + 2] = ((a + 2f) * u - (a + 3f)) * u * u + 1f;
            tab1d[i * 4 + 3] = 1f - tab1d[i * 4 + 0] - tab1d[i * 4 + 1] - tab1d[i * 4 + 2];
        }
        var tab2d = new float[FracSize * FracSize][];
        for (int i = 0; i < FracSize; i++)
        {
            for (int j = 0; j < FracSize; j++)
            {
                var block = new float[16];
                for (int k1 = 0; k1 < 4; k1++)
                {
                    float vy = tab1d[i * 4 + k1];
                    for (int k2 = 0; k2 < 4; k2++)
                    {
                        block[k1 * 4 + k2] = vy * tab1d[j * 4 + k2];
                    }
                }
                tab2d[i * FracSize + j] = block;
            }
        }
        return tab2d;
    }

    private static readonly float[][] CubicTabF2D = BuildCubicTab2D();

    /// <summary>
    /// 2D cubic integer weights per fraction index, scaled to 32768, with the
    /// block-sum correction applied to the central taps when rounding leaves the
    /// block total off scale.
    /// </summary>
    private static readonly short[][] CubicTabI2D = BuildCubicTabI();

    private static short[][] BuildCubicTabI()
    {
        float scale = 1f / FracSize;
        var tab1d = new float[FracSize * 4];
        const float a = -0.75f;
        for (int i = 0; i < FracSize; i++)
        {
            float x = i * scale;
            float x1 = x + 1f;
            tab1d[i * 4 + 0] = ((a * x1 - 5f * a) * x1 + 8f * a) * x1 - 4f * a;
            tab1d[i * 4 + 1] = ((a + 2f) * x - (a + 3f)) * x * x + 1f;
            float u = 1f - x;
            tab1d[i * 4 + 2] = ((a + 2f) * u - (a + 3f)) * u * u + 1f;
            tab1d[i * 4 + 3] = 1f - tab1d[i * 4 + 0] - tab1d[i * 4 + 1] - tab1d[i * 4 + 2];
        }
        var blocks = new short[FracSize * FracSize][];
        for (int i = 0; i < FracSize; i++)
        {
            for (int j = 0; j < FracSize; j++)
            {
                var block = new short[16];
                int isum = 0;
                for (int k1 = 0; k1 < 4; k1++)
                {
                    float vy = tab1d[i * 4 + k1];
                    for (int k2 = 0; k2 < 4; k2++)
                    {
                        float v = vy * tab1d[j * 4 + k2];
                        isum += block[k1 * 4 + k2] = (short)SaturateShort((int)Math.Round(v * 32768.0, MidpointRounding.ToEven));
                    }
                }
                if (isum != 32768)
                {
                    int diff = isum - 32768;
                    int Mk1 = 2, Mk2 = 2, mk1 = 2, mk2 = 2;
                    for (int k1 = 2; k1 < 4; k1++)
                    {
                        for (int k2 = 2; k2 < 4; k2++)
                        {
                            if (block[k1 * 4 + k2] < block[mk1 * 4 + mk2])
                            {
                                mk1 = k1;
                                mk2 = k2;
                            }
                            else if (block[k1 * 4 + k2] > block[Mk1 * 4 + Mk2])
                            {
                                Mk1 = k1;
                                Mk2 = k2;
                            }
                        }
                    }
                    if (diff < 0)
                    {
                        block[Mk1 * 4 + Mk2] = (short)(block[Mk1 * 4 + Mk2] - diff);
                    }
                    else
                    {
                        block[mk1 * 4 + mk2] = (short)(block[mk1 * 4 + mk2] - diff);
                    }
                }
                blocks[i * FracSize + j] = block;
            }
        }
        return blocks;
    }

    /// <summary>
    /// 2D bilinear integer weights per fraction index, scaled to 32768, with the
    /// block-sum correction that adjusts the center tap when rounding leaves the
    /// block total off scale.
    /// </summary>
    private static readonly short[][] BilinearTabI = BuildBilinearTabI();

    private static short[][] BuildBilinearTabI()
    {
        // One extra slot beyond the table so the correction scan of the final
        // block (which never triggers, sums being exact there) stays in bounds.
        var flat = new short[FracSize * FracSize * 4 + 4];
        for (int i = 0; i < FracSize; i++)
        {
            for (int j = 0; j < FracSize; j++)
            {
                int block = (i * FracSize + j) * 4;
                int isum = 0;
                for (int k1 = 0; k1 < 2; k1++)
                {
                    float vy = LinearTabF1D[i * 2 + k1];
                    for (int k2 = 0; k2 < 2; k2++)
                    {
                        float v = vy * LinearTabF1D[j * 2 + k2];
                        isum += flat[block + k1 * 2 + k2] = (short)SaturateShort((int)Math.Round(v * 32768.0, MidpointRounding.ToEven));
                    }
                }
                if (isum != 32768)
                {
                    int diff = isum - 32768;
                    // Scan the 2x2 center window; positions past the block fall
                    // into the next block, whose values are overwritten later.
                    int mk1 = 1, mk2 = 1, Mk1 = 1, Mk2 = 1;
                    for (int k1 = 1; k1 < 3; k1++)
                    {
                        for (int k2 = 1; k2 < 3; k2++)
                        {
                            int idx = block + k1 * 2 + k2;
                            if (flat[idx] < flat[block + mk1 * 2 + mk2])
                            {
                                mk1 = k1;
                                mk2 = k2;
                            }
                            else if (flat[idx] > flat[block + Mk1 * 2 + Mk2])
                            {
                                Mk1 = k1;
                                Mk2 = k2;
                            }
                        }
                    }
                    if (diff < 0)
                    {
                        flat[block + Mk1 * 2 + Mk2] = (short)(flat[block + Mk1 * 2 + Mk2] - diff);
                    }
                    else
                    {
                        flat[block + mk1 * 2 + mk2] = (short)(flat[block + mk1 * 2 + mk2] - diff);
                    }
                }
            }
        }
        var blocks = new short[FracSize * FracSize][];
        for (int t = 0; t < FracSize * FracSize; t++)
        {
            var block = new short[4];
            for (int k = 0; k < 4; k++)
            {
                block[k] = flat[t * 4 + k];
            }
            blocks[t] = block;
        }
        return blocks;
    }
}
