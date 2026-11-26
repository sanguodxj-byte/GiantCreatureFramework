namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>Deep-interior quality ratios of a warped sprite, in percent.</summary>
public readonly record struct InteriorQualityMetrics(
    double InteriorBlackPct,
    double InteriorSeamPct,
    double CleanFacetPct,
    int DeepInteriorPx);

/// <summary>A named 2D point in source sprite pixel space.</summary>
public sealed record NamedPoint(string Name, double X, double Y);

/// <summary>Result of aligning one sprite part onto a fixed canvas.</summary>
public sealed record BoneWarpResult
{
    public required RgbaImage Image { get; init; }

    /// <summary>Horizontally mirrored counterpart; null unless requested.</summary>
    public RgbaImage? MirrorImage { get; init; }

    public required (double X, double Y) PivotPixel { get; init; }

    public (double X, double Y)? MirrorPivotPixel { get; init; }

    public required double OutlineTargetPx { get; init; }

    public required IReadOnlyDictionary<string, (double X, double Y)> Sockets { get; init; }

    public IReadOnlyDictionary<string, (double X, double Y)> MirrorSockets { get; init; }
        = new Dictionary<string, (double X, double Y)>();

    public required InteriorQualityMetrics Quality { get; init; }

    public InteriorQualityMetrics? MirrorQuality { get; init; }
}

/// <summary>
/// Affine alignment of a cropped sprite onto a fixed square canvas with separate
/// RGB/alpha resampling, followed by optional silhouette ink injection: an
/// inward dark contour whose local thickness follows the sprite's own stroke
/// density, with bright saturated cores kept thin. Also provides the transform
/// for arbitrary source-space points (pivots and sockets) and deep-interior
/// quality measurements.
/// </summary>
public static class BoneWarper
{
    public const double DefaultDstPivotX = 256.0;
    public const double DefaultDstPivotY = 400.0;
    public const string DefaultOutlineHex = "#141218";

    public static BoneWarpResult WarpBone(
        RgbaImage source,
        (double X, double Y) srcPivot,
        (double X, double Y)? dstPivot = null,
        (double X, double Y)? scale = null,
        double angleDeg = 0.0,
        int canvasSize = 512,
        IReadOnlyList<NamedPoint>? sockets = null,
        double drawSize = 3.5,
        double? outlinePx = null,
        string outlineHex = DefaultOutlineHex,
        bool emitMirror = false,
        bool deseamInterior = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(outlineHex);
        if (canvasSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(canvasSize));
        }
        (double, double) dst = dstPivot ?? (DefaultDstPivotX, DefaultDstPivotY);
        (double, double) sc = scale ?? (1.0, 1.0);
        var ink = ParseHexColor(outlineHex);
        double targetOutline = outlinePx ?? ComputeTargetOutlinePx(drawSize, canvasSize);

        RgbaImage warped = WarpToCanvas(
            source, srcPivot, dst, canvasSize, angleDeg, sc.Item1, sc.Item2, flipX: false);
        if (targetOutline > 0 || deseamInterior)
        {
            warped = ApplyRimworldOutline(warped, targetOutline, ink, deseamInterior: deseamInterior);
        }

        var socketMap = new Dictionary<string, (double, double)>();
        if (sockets != null)
        {
            foreach (var s in sockets)
            {
                socketMap[s.Name] = TransformPoint((s.X, s.Y), srcPivot, dst, angleDeg, sc.Item1, sc.Item2, false, source.Width);
            }
        }

        var result = new BoneWarpResult
        {
            Image = warped,
            PivotPixel = (Math.Round(dst.Item1, 1, MidpointRounding.ToEven), Math.Round(dst.Item2, 1, MidpointRounding.ToEven)),
            OutlineTargetPx = Math.Round(targetOutline, 2, MidpointRounding.ToEven),
            Sockets = socketMap,
            Quality = AuditInteriorQuality(warped),
        };

        if (emitMirror)
        {
            RgbaImage mirrored = WarpToCanvas(
                source, srcPivot, dst, canvasSize, -angleDeg, sc.Item1, sc.Item2, flipX: true);
            if (targetOutline > 0 || deseamInterior)
            {
                mirrored = ApplyRimworldOutline(mirrored, targetOutline, ink, deseamInterior: deseamInterior);
            }
            var mirrorSockets = new Dictionary<string, (double, double)>();
            if (sockets != null)
            {
                foreach (var s in sockets)
                {
                    mirrorSockets[s.Name] = TransformPoint((s.X, s.Y), srcPivot, dst, -angleDeg, sc.Item1, sc.Item2, true, source.Width);
                }
            }
            result = result with
            {
                MirrorImage = mirrored,
                MirrorPivotPixel = result.PivotPixel,
                MirrorSockets = mirrorSockets,
                MirrorQuality = AuditInteriorQuality(mirrored),
            };
        }
        return result;
    }

    /// <summary>
    /// World-balanced outer outline stroke width: 11.5 px at drawSize 3.5,
    /// falling with the fourth-fifth root of draw size, clamped to 8.5..13.5 px
    /// and scaled with the canvas resolution relative to 512.
    /// </summary>
    public static double ComputeTargetOutlinePx(double drawSizeMax, int canvasRes = 512)
    {
        double scale = canvasRes / 512.0;
        double raw = 11.5 * Math.Pow(3.5 / Math.Max(0.8, drawSizeMax), 0.45);
        return Math.Clamp(raw, 8.5, 13.5) * scale;
    }

    /// <summary>Parses a 6-digit hex color with optional leading '#' into RGB bytes.</summary>
    public static (byte R, byte G, byte B) ParseHexColor(string hex)
    {
        string cleaned = hex.Trim().TrimStart('#');
        if (cleaned.Length != 6)
        {
            throw new FormatException($"Expected a 6-digit hex color, got: {hex}");
        }
        return (Convert.ToByte(cleaned[..2], 16), Convert.ToByte(cleaned[2..4], 16), Convert.ToByte(cleaned[4..6], 16));
    }

    /// <summary>
    /// Maps a source-space point through the same affine used by
    /// <see cref="WarpToCanvas"/>, rounding to 0.1 px.
    /// </summary>
    public static (double X, double Y) TransformPoint(
        (double X, double Y) point,
        (double X, double Y) srcAnchor,
        (double X, double Y) dstAnchor,
        double angleDeg,
        double scaleX,
        double scaleY,
        bool flipX,
        int originalWidth)
    {
        double px = point.X, py = point.Y;
        double sx;
        if (flipX)
        {
            px = originalWidth - 1.0 - px;
            sx = originalWidth - 1.0 - srcAnchor.X;
        }
        else
        {
            sx = srcAnchor.X;
        }
        double sy = srcAnchor.Y;
        double dx = dstAnchor.X, dy = dstAnchor.Y;
        double rad = angleDeg * Math.PI / 180.0;
        double ca = Math.Cos(rad), sa = Math.Sin(rad);
        double lx = (px - sx) * scaleX;
        double ly = (py - sy) * scaleY;
        double nx = dx + ca * lx - sa * ly;
        double ny = dy + sa * lx + ca * ly;
        return (Math.Round(nx, 1, MidpointRounding.ToEven), Math.Round(ny, 1, MidpointRounding.ToEven));
    }

    private const byte InkR = 20;
    private const byte InkG = 18;
    private const byte InkB = 24;

    /// <summary>
    /// Affine warp of a sprite onto a square canvas. RGB is resampled from a
    /// float image with cubic (or bilinear while downsizing below 0.95) against
    /// a dark ink border; alpha is resampled bilinearly against a zero border.
    /// Nearly transparent results are snapped to fully transparent, then RGB is
    /// propagated into the transparent margin.
    /// </summary>
    internal static RgbaImage WarpToCanvas(
        RgbaImage rgba,
        (double X, double Y) srcAnchor,
        (double X, double Y) dstAnchor,
        int canvasSize,
        double angleDeg,
        double scaleX,
        double scaleY,
        bool flipX,
        int bleedIterations = 8)
    {
        RgbaImage source = flipX ? rgba.FlipHorizontal() : rgba;
        double srcAx = flipX ? rgba.Width - 1.0 - srcAnchor.X : srcAnchor.X;
        double srcAy = srcAnchor.Y;

        var forward = AffineWarp.BuildForwardMatrix(srcAx, srcAy, dstAnchor.X, dstAnchor.Y, angleDeg, scaleX, scaleY);
        double[] invM = AffineWarp.InvertForwardMatrix(forward);
        var (mapSx, mapSy, mapFrac) = AffineWarp.BuildMap(invM, canvasSize, canvasSize);

        bool downscale = Math.Max(scaleX, scaleY) < 0.95;

        // RGB planes from an RGB-bleed pass so interpolation never reads black.
        // Both planes stay 8-bit: the reference pipeline warps bytes with the
        // 15-bit fixed-point kernels, not floats.
        RgbaImage bled = SpriteSheetExtractor.BleedRgbIntoTransparent(source, 3);
        int sw = source.Width, sh = source.Height;
        int sn = sw * sh;
        var rgbPlanes = new byte[sn * 3];
        var alphaPlane = new byte[sn];
        byte[] sp = bled.Pixels;
        for (int i = 0; i < sn; i++)
        {
            rgbPlanes[i * 3] = sp[i * 4];
            rgbPlanes[i * 3 + 1] = sp[i * 4 + 1];
            rgbPlanes[i * 3 + 2] = sp[i * 4 + 2];
            alphaPlane[i] = sp[i * 4 + 3];
        }

        byte[] warpedRgb = AffineWarp.WarpByte3(
            rgbPlanes, sw, sh, mapSx, mapSy, mapFrac, canvasSize, canvasSize,
            cubic: !downscale, InkR, InkG, InkB);
        byte[] warpedAlpha = AffineWarp.WarpByte1(
            alphaPlane, sw, sh, mapSx, mapSy, mapFrac, canvasSize, canvasSize, 0);

        var composed = new RgbaImage(canvasSize, canvasSize);
        byte[] cp = composed.Pixels;
        int cn = canvasSize * canvasSize;
        for (int i = 0; i < cn; i++)
        {
            byte a = warpedAlpha[i] < 10 ? (byte)0 : warpedAlpha[i];
            cp[i * 4] = warpedRgb[i * 3];
            cp[i * 4 + 1] = warpedRgb[i * 3 + 1];
            cp[i * 4 + 2] = warpedRgb[i * 3 + 2];
            cp[i * 4 + 3] = a;
        }
        return SpriteSheetExtractor.BleedRgbIntoTransparent(composed, bleedIterations);
    }

    private static byte Truncate(float value)
    {
        // Float-to-byte conversion truncates toward zero; call sites pre-clamp
        // into the byte range so no wrap can occur.
        return (byte)value;
    }

    /// <summary>
    /// Injects an inward silhouette contour of the given ink color and target
    /// width over pixels with alpha &gt;= 96, hardening the contour band to full
    /// opacity without expanding the alpha &gt; 32 footprint outward, then
    /// re-propagates RGB into the transparent margin.
    /// </summary>
    internal static RgbaImage ApplyRimworldOutline(
        RgbaImage rgba,
        double targetPx,
        (byte R, byte G, byte B) ink,
        bool openBottom = false,
        int bleedIterations = 8,
        bool deseamInterior = false)
    {
        RgbaImage srcRgba = deseamInterior
            ? DeseamInteriorInk(rgba, Math.Max(6.0, targetPx), openBottom)
            : rgba;

        if (targetPx <= 0.0)
        {
            return SpriteSheetExtractor.BleedRgbIntoTransparent(srcRgba, bleedIterations);
        }

        int w = srcRgba.Width;
        int h = srcRgba.Height;
        int n = w * h;
        byte[] sp = srcRgba.Pixels;
        var fg = new byte[n];
        var alpha = new byte[n];
        bool anyFg = false;
        for (int i = 0; i < n; i++)
        {
            alpha[i] = sp[i * 4 + 3];
            fg[i] = alpha[i] >= 96 ? (byte)1 : (byte)0;
            anyFg |= fg[i] != 0;
        }
        if (!anyFg)
        {
            return srcRgba.Clone();
        }

        float[] distIn = ComputeInwardDistance(fg, w, h, openBottom);

        int kRad = (int)Math.Max(5, Math.Round(targetPx * 1.6, MidpointRounding.ToEven)) | 1;
        var disk = Morphology.DiskElement(kRad);
        float[] localHalfFg = Morphology.GrayDilate(distIn, w, h, disk);

        var outImage = srcRgba.Clone();
        byte[] op = outImage.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (fg[i] == 0)
                {
                    int q = i * 4;
                    op[q] = ink.R;
                    op[q + 1] = ink.G;
                    op[q + 2] = ink.B;
                    continue;
                }
                float d = distIn[i];
                float half = localHalfFg[i];
                float wIn = Math.Min((float)targetPx, Math.Max(6.0f, 0.56f * half));
                wIn = Math.Min(wIn, 0.72f * half);

                int p = i * 4;
                float r = op[p], g = op[p + 1], b = op[p + 2];
                float cmax = Math.Max(r, Math.Max(g, b));
                float cmin = Math.Min(r, Math.Min(g, b));
                float sat = (cmax - cmin) / Math.Max(cmax, 1.0f);
                if (cmax > 185.0f && sat > 0.42f)
                {
                    wIn = Math.Min(wIn, 4.5f);
                }

                float inBlend = Math.Clamp((wIn + 1.0f - d) / 1.0f, 0.0f, 1.0f);
                op[p] = Truncate(r * (1.0f - inBlend) + ink.R * inBlend);
                op[p + 1] = Truncate(g * (1.0f - inBlend) + ink.G * inBlend);
                op[p + 2] = Truncate(b * (1.0f - inBlend) + ink.B * inBlend);
                if (d <= wIn + 1.0f)
                {
                    alpha[i] = 255;
                }
            }
        }
        for (int i = 0; i < n; i++)
        {
            op[i * 4 + 3] = alpha[i];
        }
        return SpriteSheetExtractor.BleedRgbIntoTransparent(outImage, bleedIterations);
    }

    /// <summary>
    /// Removes thin interior dark seam lines by propagating the color of the
    /// nearest clean interior pixel (nearest-source Voronoi over the seam-free
    /// zone), blending a 3x3 local mean, and lifting remaining over-dark interior
    /// surfaces; alpha and bright emissive cores are untouched.
    /// </summary>
    internal static RgbaImage DeseamInteriorInk(
        RgbaImage rgba,
        double targetOutlinePx,
        bool openBottom = false,
        double minInteriorLum = 44.0)
    {
        int w = rgba.Width;
        int h = rgba.Height;
        int n = w * h;
        var outImage = rgba.Clone();
        byte[] op = outImage.Pixels;
        var fg = new byte[n];
        bool anyFg = false;
        for (int i = 0; i < n; i++)
        {
            fg[i] = op[i * 4 + 3] >= 96 ? (byte)1 : (byte)0;
            anyFg |= fg[i] != 0;
        }
        if (!anyFg)
        {
            return outImage;
        }

        float[] distIn = ComputeInwardDistance(fg, w, h, openBottom);
        int kRad = (int)Math.Max(5, Math.Round(targetOutlinePx * 1.6, MidpointRounding.ToEven)) | 1;
        var disk = Morphology.DiskElement(kRad);
        float[] localHalfFg = Morphology.GrayDilate(distIn, w, h, disk);

        var workZone = new byte[n];
        var strictInterior = new byte[n];
        bool anyWork = false;
        for (int i = 0; i < n; i++)
        {
            if (fg[i] == 0)
            {
                continue;
            }
            float d = distIn[i];
            float wIn = Math.Min((float)targetOutlinePx, Math.Max(6.0f, 0.56f * localHalfFg[i]));
            wIn = Math.Min(wIn, 0.72f * localHalfFg[i]);
            if (d > Math.Max(3.5f, wIn - 1.5f))
            {
                workZone[i] = 1;
                anyWork = true;
            }
            if (d > wIn + 1.0f)
            {
                strictInterior[i] = 1;
            }
        }
        if (!anyWork)
        {
            return outImage;
        }

        var rgb = new float[n * 3];
        var lum = new float[n];
        for (int i = 0; i < n; i++)
        {
            float r = op[i * 4], g = op[i * 4 + 1], b = op[i * 4 + 2];
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
            lum[i] = 0.299f * r + 0.587f * g + 0.114f * b;
        }
        var emissive = new byte[n];
        for (int i = 0; i < n; i++)
        {
            float r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            float cmax = Math.Max(r, Math.Max(g, b));
            float cmin = Math.Min(r, Math.Min(g, b));
            float sat = (cmax - cmin) / Math.Max(cmax, 1.0f);
            emissive[i] = cmax > 155.0f && sat > 0.36f ? (byte)1 : (byte)0;
        }

        var closedLum = Morphology.GrayErode(
            Morphology.GrayDilate(lum, w, h, Morphology.DiskElement(9)), w, h, Morphology.DiskElement(9));

        var darkInk = new byte[n];
        for (int i = 0; i < n; i++)
        {
            float valleyDepth = closedLum[i] - lum[i];
            darkInk[i] = workZone[i] != 0 && emissive[i] == 0 &&
                (lum[i] < 40.0f || (lum[i] < 68.0f && valleyDepth > 14.0f)) ? (byte)1 : (byte)0;
        }
        float[] distDark = DistanceField.Compute(darkInk, w, h);
        float[] maxHalfDark = Morphology.GrayDilate(distDark, w, h, Morphology.DiskElement(11));

        var seamCore = new byte[n];
        for (int i = 0; i < n; i++)
        {
            seamCore[i] = darkInk[i] != 0 && maxHalfDark[i] <= 4.0f ? (byte)1 : (byte)0;
        }
        byte[] seamDilated = Morphology.DilateBinary(seamCore, w, h, 3, 1);
        var seamMask = new byte[n];
        bool anySeam = false;
        for (int i = 0; i < n; i++)
        {
            seamMask[i] = workZone[i] != 0 && emissive[i] == 0 && seamDilated[i] != 0 &&
                (lum[i] < 48.0f || lum[i] < closedLum[i] - 5.0f) ? (byte)1 : (byte)0;
            anySeam |= seamMask[i] != 0;
        }

        var seedMask = new byte[n];
        bool anySeed = false;
        for (int i = 0; i < n; i++)
        {
            seedMask[i] = strictInterior[i] != 0 && seamMask[i] == 0 && lum[i] >= 40.0f && emissive[i] == 0 ? (byte)1 : (byte)0;
            anySeed |= seedMask[i] != 0;
        }
        if (!anySeed)
        {
            anySeed = false;
            for (int i = 0; i < n; i++)
            {
                seedMask[i] = strictInterior[i] != 0 && seamMask[i] == 0 && lum[i] >= 38.0f ? (byte)1 : (byte)0;
                anySeed |= seedMask[i] != 0;
            }
        }
        if (!anySeed)
        {
            anySeed = false;
            for (int i = 0; i < n; i++)
            {
                seedMask[i] = strictInterior[i] != 0 && seamMask[i] == 0 ? (byte)1 : (byte)0;
                anySeed |= seedMask[i] != 0;
            }
        }

        var work = (float[])rgb.Clone();
        if (anySeed && anySeam)
        {
            var sourceMask = new byte[n];
            for (int i = 0; i < n; i++)
            {
                sourceMask[i] = seedMask[i] != 0 ? (byte)0 : (byte)1;
            }
            (_, int[] vorLabels) = DistanceField.ComputeLabeled(sourceMask, w, h);

            // Label 0 maps to black; label k maps to the k-th seed in raster order.
            int seedCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (seedMask[i] != 0)
                {
                    seedCount++;
                }
            }
            var lut = new float[(seedCount + 1) * 3];
            int seedIdx = 0;
            for (int i = 0; i < n; i++)
            {
                if (seedMask[i] != 0)
                {
                    lut[(seedIdx + 1) * 3] = rgb[i * 3];
                    lut[(seedIdx + 1) * 3 + 1] = rgb[i * 3 + 1];
                    lut[(seedIdx + 1) * 3 + 2] = rgb[i * 3 + 2];
                    seedIdx++;
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (seamMask[i] != 0)
                {
                    int label = vorLabels[i];
                    int baseIdx = label * 3;
                    work[i * 3] = lut[baseIdx];
                    work[i * 3 + 1] = lut[baseIdx + 1];
                    work[i * 3 + 2] = lut[baseIdx + 2];
                }
            }

            var fgFloat = new float[n];
            for (int i = 0; i < n; i++)
            {
                fgFloat[i] = fg[i];
            }
            float[] cnt = Filters.BoxSum3x3(fgFloat, w, h);
            for (int c = 0; c < 3; c++)
            {
                var plane = new float[n];
                for (int i = 0; i < n; i++)
                {
                    plane[i] = work[i * 3 + c] * fg[i];
                }
                float[] sums = Filters.BoxSum3x3(plane, w, h);
                for (int i = 0; i < n; i++)
                {
                    if (seamMask[i] != 0)
                    {
                        float mean = sums[i] / Math.Max(cnt[i], 1.0f);
                        work[i * 3 + c] = 0.68f * work[i * 3 + c] + 0.32f * mean;
                    }
                }
            }
        }

        var lum2 = new float[n];
        for (int i = 0; i < n; i++)
        {
            lum2[i] = 0.299f * work[i * 3] + 0.587f * work[i * 3 + 1] + 0.114f * work[i * 3 + 2];
        }
        float softCeil = (float)minInteriorLum + 14.0f;
        for (int i = 0; i < n; i++)
        {
            if (workZone[i] != 0 && emissive[i] == 0 && lum2[i] < softCeil)
            {
                float targetLum = (float)minInteriorLum + (lum2[i] / softCeil) * (softCeil - (float)minInteriorLum);
                float delta = Math.Max(0.0f, targetLum - lum2[i]);
                work[i * 3] = Math.Clamp(work[i * 3] + delta, 0.0f, 255.0f);
                work[i * 3 + 1] = Math.Clamp(work[i * 3 + 1] + delta, 0.0f, 255.0f);
                work[i * 3 + 2] = Math.Clamp(work[i * 3 + 2] + delta, 0.0f, 255.0f);
            }
        }

        for (int i = 0; i < n; i++)
        {
            op[i * 4] = Truncate(work[i * 3]);
            op[i * 4 + 1] = Truncate(work[i * 3 + 1]);
            op[i * 4 + 2] = Truncate(work[i * 3 + 2]);
        }
        return outImage;
    }

    /// <summary>
    /// Inward distance to the nearest non-foreground pixel (or raster border),
    /// over the alpha &gt;= threshold mask; bottom row replication keeps the
    /// root edge of open-bottom sprites from counting as a wall.
    /// </summary>
    private static float[] ComputeInwardDistance(byte[] fg, int w, int h, bool openBottom)
    {
        int pw = w + 2;
        int ph = h + 2;
        var padded = new byte[pw * ph];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                padded[(y + 1) * pw + x + 1] = fg[y * w + x];
            }
        }
        if (openBottom)
        {
            for (int x = 0; x < w; x++)
            {
                padded[(h + 1) * pw + x + 1] = fg[(h - 1) * w + x];
            }
        }
        float[] paddedDist = DistanceField.Compute(padded, pw, ph);
        var inner = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                inner[y * w + x] = paddedDist[(y + 1) * pw + x + 1];
            }
        }
        return inner;
    }

    /// <summary>
    /// Measures the fraction of deep-interior pixels (well inside the outer
    /// contour) whose luminance is near-black.
    /// </summary>
    public static (double Ratio, int DeepInteriorPx) MeasureInteriorBlackRatio(
        RgbaImage rgba, bool openBottom = false, double minDistPx = 15.5, double blackLumMax = 34.0)
    {
        var distances = ComputeInwardDistanceAndLum(rgba, openBottom);
        if (distances.Fg is null || distances.DistIn is null || distances.Lum is null)
        {
            return (0.0, 0);
        }
        byte[] fg = distances.Fg;
        float[] distIn = distances.DistIn;
        float[] lum = distances.Lum;
        int n = rgba.Width * rgba.Height;
        int deepCount = 0;
        int darkCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (fg[i] != 0 && distIn[i] >= minDistPx)
            {
                deepCount++;
                if (lum[i] < blackLumMax)
                {
                    darkCount++;
                }
            }
        }
        if (deepCount == 0)
        {
            return (0.0, 0);
        }
        return ((double)darkCount / deepCount, deepCount);
    }

    /// <summary>
    /// Measures the fraction of deep-interior pixels belonging to thin dark or
    /// low-contrast valley seam lines (a pixel darker than both opposing sides
    /// sampled 2..4 px away along four directions) or to near-black pixels.
    /// </summary>
    public static (double Ratio, int DeepInteriorPx) MeasureInteriorSeamRatio(
        RgbaImage rgba,
        bool openBottom = false,
        double minDistPx = 16.0,
        double safeDistPx = 12.5,
        double valleyDropMin = 16.0,
        double blackLumMax = 34.0)
    {
        var distances = ComputeInwardDistanceAndLum(rgba, openBottom);
        if (distances.Fg is null || distances.DistIn is null || distances.Lum is null)
        {
            return (0.0, 0);
        }
        byte[] fg = distances.Fg;
        float[] distIn = distances.DistIn;
        float[] lum = distances.Lum;
        var (deep, seam) = ComputeValleySeamMasks(
            fg, distIn, lum, rgba.Width, rgba.Height, minDistPx, safeDistPx, valleyDropMin, blackLumMax);
        int deepCount = 0;
        int seamCount = 0;
        int n = rgba.Width * rgba.Height;
        for (int i = 0; i < n; i++)
        {
            if (deep[i] != 0)
            {
                deepCount++;
            }
            if (seam[i] != 0)
            {
                seamCount++;
            }
        }
        if (deepCount == 0)
        {
            return (0.0, 0);
        }
        return ((double)seamCount / deepCount, deepCount);
    }

    /// <summary>
    /// Measures the fraction of deep-interior pixels forming crisp tonal facet
    /// transitions (luminance gradient at or above the floor) outside seam lines.
    /// </summary>
    public static double MeasureCleanFacetRatio(
        RgbaImage rgba,
        bool openBottom = false,
        double minDistPx = 16.0,
        double safeDistPx = 12.5,
        double valleyDropMin = 16.0,
        double blackLumMax = 34.0,
        double gradMin = 7.5)
    {
        var distances = ComputeInwardDistanceAndLum(rgba, openBottom);
        if (distances.Fg is null || distances.DistIn is null || distances.Lum is null)
        {
            return 0.0;
        }
        byte[] fg = distances.Fg;
        float[] distIn = distances.DistIn;
        float[] lum = distances.Lum;
        int w = rgba.Width;
        int h = rgba.Height;
        var (deep, seam) = ComputeValleySeamMasks(
            fg, distIn, lum, w, h, minDistPx, safeDistPx, valleyDropMin, blackLumMax);
        int deepCount = 0;
        int facetCount = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (deep[i] == 0)
                {
                    continue;
                }
                deepCount++;
                float grad = 0;
                if (x >= 1)
                {
                    grad += Math.Abs(lum[i] - lum[i - 1]);
                }
                if (y >= 1)
                {
                    grad += Math.Abs(lum[i] - lum[i - w]);
                }
                if (grad >= gradMin && seam[i] == 0)
                {
                    facetCount++;
                }
            }
        }
        if (deepCount == 0)
        {
            return 0.0;
        }
        return (double)facetCount / deepCount;
    }

    internal static InteriorQualityMetrics AuditInteriorQuality(RgbaImage rgba)
    {
        (double blackRatio, int deepCnt) = MeasureInteriorBlackRatio(rgba);
        (double seamRatio, _) = MeasureInteriorSeamRatio(rgba);
        double facetRatio = MeasureCleanFacetRatio(rgba);
        return new InteriorQualityMetrics(
            Math.Round(blackRatio * 100.0, 2, MidpointRounding.ToEven),
            Math.Round(seamRatio * 100.0, 2, MidpointRounding.ToEven),
            Math.Round(facetRatio * 100.0, 2, MidpointRounding.ToEven),
            deepCnt);
    }

    private static (byte[]? Fg, float[]? DistIn, float[]? Lum) ComputeInwardDistanceAndLum(RgbaImage rgba, bool openBottom)
    {
        int w = rgba.Width;
        int h = rgba.Height;
        int n = w * h;
        byte[] px = rgba.Pixels;
        var fg = new byte[n];
        bool anyFg = false;
        for (int i = 0; i < n; i++)
        {
            fg[i] = px[i * 4 + 3] >= 128 ? (byte)1 : (byte)0;
            anyFg |= fg[i] != 0;
        }
        if (!anyFg)
        {
            return (null, null, null);
        }
        float[] distIn = ComputeInwardDistance(fg, w, h, openBottom);
        var lum = new float[n];
        for (int i = 0; i < n; i++)
        {
            lum[i] = 0.299f * px[i * 4] + 0.587f * px[i * 4 + 1] + 0.114f * px[i * 4 + 2];
        }
        return (fg, distIn, lum);
    }

    private static (byte[] Deep, byte[] Seam) ComputeValleySeamMasks(
        byte[] fg, float[] distIn, float[] lum, int w, int h,
        double minDistPx, double safeDistPx, double valleyDropMin, double blackLumMax)
    {
        int n = w * h;
        var deep = new byte[n];
        bool anyDeep = false;
        for (int i = 0; i < n; i++)
        {
            deep[i] = fg[i] != 0 && distIn[i] >= minDistPx ? (byte)1 : (byte)0;
            anyDeep |= deep[i] != 0;
        }
        var seam = new byte[n];
        if (!anyDeep)
        {
            return (deep, seam);
        }

        var safe = new byte[n];
        for (int i = 0; i < n; i++)
        {
            safe[i] = fg[i] != 0 && distIn[i] >= safeDistPx ? (byte)1 : (byte)0;
            if (deep[i] != 0 && lum[i] < blackLumMax)
            {
                seam[i] = 1;
            }
        }

        if (h > 8 && w > 8)
        {
            var valley = new byte[n];
            foreach (var (dy, dx) in new[] { (0, 1), (1, 0), (1, 1), (1, -1) })
            {
                for (int d = 2; d <= 4; d++)
                {
                    // The seam test walks the interior window [4, h-4) x [4, w-4)
                    // and compares each pixel against both sides offset by
                    // d * (dy, dx), requiring both sides to sit in the safe zone.
                    for (int y = 4; y < h - 4; y++)
                    {
                        int yy1 = y - dy * d;
                        int yy2 = y + dy * d;
                        if (yy1 < 0 || yy1 >= h || yy2 < 0 || yy2 >= h)
                        {
                            continue;
                        }
                        for (int x = 4; x < w - 4; x++)
                        {
                            int xx1 = x - dx * d;
                            int xx2 = x + dx * d;
                            if (xx1 < 0 || xx1 >= w || xx2 < 0 || xx2 >= w)
                            {
                                continue;
                            }
                            int c = y * w + x;
                            int i1 = yy1 * w + xx1;
                            int i2 = yy2 * w + xx2;
                            if (safe[i1] != 0 && safe[i2] != 0)
                            {
                                float drop = Math.Min(lum[i1], lum[i2]) - lum[c];
                                if (drop >= valleyDropMin)
                                {
                                    valley[c] = 1;
                                }
                            }
                        }
                    }
                }
            }
            for (int y = 4; y < h - 4; y++)
            {
                for (int x = 4; x < w - 4; x++)
                {
                    int i = y * w + x;
                    if (deep[i] != 0 && valley[i] != 0)
                    {
                        seam[i] = 1;
                    }
                }
            }
        }
        return (deep, seam);
    }
}
