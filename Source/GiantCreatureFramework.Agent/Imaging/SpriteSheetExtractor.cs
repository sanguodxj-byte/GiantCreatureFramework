namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>Bounding box of a column region inside the sheet, end-exclusive.</summary>
public sealed record SheetBoundingBox(int X0, int Y0, int X1, int Y1);

/// <summary>One extracted sprite: tightly cropped RGBA plus the boxes it came from.</summary>
public sealed record ExtractedSheetPart
{
    public required int Index { get; init; }

    /// <summary>Column bounding box inside the sheet, already padded, end-exclusive.</summary>
    public required SheetBoundingBox BoundingBox { get; init; }

    /// <summary>Offset of the tight crop inside the padded column box.</summary>
    public required (int X, int Y) CropOffset { get; init; }

    public required RgbaImage Image { get; init; }
}

/// <summary>
/// Splits a green-screen mother sheet into horizontally separated sprites and
/// turns each one into a clean RGBA cutout. Background separation floods the
/// green mask from the raster border over 8-connected green pixels; green
/// regions not reached by the flood are interior cavities and are either kept
/// transparent or filled, depending on the options. Strong-green fringe pixels
/// on the 1-pixel outer edge band are peeled, green spill is capped on every
/// foreground pixel, the outer edge band is darkened toward ink, tiny loose
/// islands are removed, and opaque RGB values are propagated outward into the
/// transparent margin so bilinear sampling never reads pure black or green.
/// </summary>
public static class SpriteSheetExtractor
{
    private const int CavityFillR = 58;
    private const int CavityFillG = 54;
    private const int CavityFillB = 64;

    public static IReadOnlyList<ExtractedSheetPart> ExtractSheet(
        RgbaImage sheet,
        int? columns = null,
        int padPixels = 10,
        int greenMin = 115,
        int excessMin = 32,
        bool fillAllHoles = false,
        int minHoleArea = 180,
        int minIslandArea = 40,
        int cropAlphaThreshold = 8,
        int bleedIterations = 8)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (columns.HasValue && columns.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Column count must be at least 1.");
        }
        if (padPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(padPixels));
        }

        int w = sheet.Width;
        int h = sheet.Height;
        byte[] px = sheet.Pixels;

        var foreground = new byte[w * h];
        for (int i = 0, p = 0; i < foreground.Length; i++, p += 4)
        {
            int r = px[p];
            int g = px[p + 1];
            int b = px[p + 2];
            int excess = g - Math.Max(r, b);
            foreground[i] = (g > greenMin && excess > excessMin) ? (byte)0 : (byte)1;
        }

        var columnBoxes = columns.HasValue && columns.Value > 1
            ? SplitFixedColumns(foreground, w, h, columns.Value)
            : SplitAutoSpans(foreground, w, h);

        var results = new List<ExtractedSheetPart>();
        int index = 0;
        foreach (var (x0, x1) in columnBoxes)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * w;
                for (int x = x0; x < x1; x++)
                {
                    if (foreground[rowBase + x] != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0)
            {
                continue;
            }
            int bx0 = Math.Max(0, minX - padPixels);
            int bx1 = Math.Min(w, maxX + 1 + padPixels);
            int by0 = Math.Max(0, minY - padPixels);
            int by1 = Math.Min(h, maxY + 1 + padPixels);

            var columnRgba = sheet.Crop(bx0, by0, bx1, by1);
            RgbaImage extracted = ChromaKeyExtract(
                columnRgba,
                fillAllHoles,
                keepHolesTransparent: !fillAllHoles,
                greenMin,
                excessMin,
                minHoleArea,
                minIslandArea);

            if (!columns.HasValue && TrySplitMajorIslands(extracted, bx0, by0, w, h, padPixels, cropAlphaThreshold, minIslandArea, bleedIterations, ref index, results))
            {
                continue;
            }

            var (cropped, offset) = TightCrop(extracted, cropAlphaThreshold);
            RgbaImage bled = BleedRgbIntoTransparent(cropped, bleedIterations);
            results.Add(new ExtractedSheetPart
            {
                Index = index,
                BoundingBox = new SheetBoundingBox(bx0, by0, bx1, by1),
                CropOffset = offset,
                Image = bled,
            });
            index++;
        }
        return results;
    }

    private static bool TrySplitMajorIslands(
        RgbaImage extracted,
        int baseX0,
        int baseY0,
        int sheetW,
        int sheetH,
        int padPixels,
        int cropAlphaThreshold,
        int minIslandArea,
        int bleedIterations,
        ref int nextIndex,
        List<ExtractedSheetPart> results)
    {
        int cw = extracted.Width;
        int ch = extracted.Height;
        int n = cw * ch;
        byte[] px = extracted.Pixels;
        var alphaMask = new byte[n];
        for (int i = 0; i < n; i++)
        {
            alphaMask[i] = px[i * 4 + 3] > cropAlphaThreshold ? (byte)1 : (byte)0;
        }

        // Dilate by 6 px (13x13) so tiny sub-12px details/floating horns stay grouped with their host part,
        // while distinct modular parts separated by >= 13 px of #00FF00 background split cleanly even when
        // stacked 2x2 vertically or overhanging horizontally across column X bounds.
        byte[] bridged = Morphology.DilateBinary(alphaMask, cw, ch, kernelSize: 13, iterations: 1);
        var (labels, labelCount, _, _, _) = Labeling.Label8(bridged, cw, ch);
        if (labelCount <= 1)
        {
            return false;
        }

        int minMajorPixels = Math.Max(minIslandArea * 10, 600);
        var opaqueCounts = new int[labelCount + 1];
        var sumX = new long[labelCount + 1];
        var sumY = new long[labelCount + 1];
        var minX = new int[labelCount + 1];
        var minY = new int[labelCount + 1];
        var maxX = new int[labelCount + 1];
        var maxY = new int[labelCount + 1];
        Array.Fill(minX, int.MaxValue);
        Array.Fill(minY, int.MaxValue);
        Array.Fill(maxX, -1);
        Array.Fill(maxY, -1);

        for (int y = 0; y < ch; y++)
        {
            int rowBase = y * cw;
            for (int x = 0; x < cw; x++)
            {
                int i = rowBase + x;
                if (alphaMask[i] == 0) continue;
                int lbl = labels[i];
                if (lbl <= 0) continue;
                opaqueCounts[lbl]++;
                sumX[lbl] += x;
                sumY[lbl] += y;
                if (x < minX[lbl]) minX[lbl] = x;
                if (x > maxX[lbl]) maxX[lbl] = x;
                if (y < minY[lbl]) minY[lbl] = y;
                if (y > maxY[lbl]) maxY[lbl] = y;
            }
        }

        var majors = new List<int>();
        for (int lbl = 1; lbl <= labelCount; lbl++)
        {
            if (opaqueCounts[lbl] >= minMajorPixels && maxX[lbl] >= 0)
            {
                majors.Add(lbl);
            }
        }

        if (majors.Count <= 1)
        {
            return false;
        }

        double xBand = Math.Max(32.0, cw * 0.22);
        majors.Sort((a, b) =>
        {
            double cxa = (double)sumX[a] / opaqueCounts[a];
            double cxb = (double)sumX[b] / opaqueCounts[b];
            if (Math.Abs(cxa - cxb) > xBand)
            {
                return cxa.CompareTo(cxb);
            }
            double cya = (double)sumY[a] / opaqueCounts[a];
            double cyb = (double)sumY[b] / opaqueCounts[b];
            return cya.CompareTo(cyb);
        });

        foreach (int lbl in majors)
        {
            int ix0 = minX[lbl];
            int iy0 = minY[lbl];
            int ix1 = maxX[lbl] + 1;
            int iy1 = maxY[lbl] + 1;
            int iw = ix1 - ix0;
            int ih = iy1 - iy0;
            var isolated = new RgbaImage(iw, ih);
            byte[] dst = isolated.Pixels;
            for (int sy = iy0; sy < iy1; sy++)
            {
                int srcRow = sy * cw;
                int dstRow = (sy - iy0) * iw;
                for (int sx = ix0; sx < ix1; sx++)
                {
                    int si = srcRow + sx;
                    if (labels[si] != lbl) continue;
                    int sp = si * 4;
                    int dp = (dstRow + (sx - ix0)) * 4;
                    dst[dp] = px[sp];
                    dst[dp + 1] = px[sp + 1];
                    dst[dp + 2] = px[sp + 2];
                    dst[dp + 3] = px[sp + 3];
                }
            }

            RgbaImage bled = BleedRgbIntoTransparent(isolated, bleedIterations);
            int sheetMinX = baseX0 + ix0;
            int sheetMinY = baseY0 + iy0;
            int sheetMaxX = baseX0 + ix1;
            int sheetMaxY = baseY0 + iy1;
            int pbx0 = Math.Max(0, sheetMinX - padPixels);
            int pby0 = Math.Max(0, sheetMinY - padPixels);
            int pbx1 = Math.Min(sheetW, sheetMaxX + padPixels);
            int pby1 = Math.Min(sheetH, sheetMaxY + padPixels);

            results.Add(new ExtractedSheetPart
            {
                Index = nextIndex,
                BoundingBox = new SheetBoundingBox(pbx0, pby0, pbx1, pby1),
                CropOffset = (sheetMinX - pbx0, sheetMinY - pby0),
                Image = bled,
            });
            nextIndex++;
        }

        return true;
    }

    private static List<(int X0, int X1)> SplitFixedColumns(byte[] foreground, int w, int h, int columns)
    {
        var activity = new float[w];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                activity[x] += foreground[rowBase + x];
            }
        }

        double step = w / (double)columns;
        var cuts = new List<int> { 0 };
        for (int idx = 1; idx < columns; idx++)
        {
            int lo = Math.Max(1, (int)((idx - 0.35) * step));
            int hi = Math.Min(w - 1, (int)((idx + 0.35) * step));
            if (lo >= hi)
            {
                throw new ArgumentException(
                    $"Sheet width {w} is too small for {columns} columns.");
            }
            int best = lo;
            float bestValue = activity[lo];
            for (int x = lo + 1; x < hi; x++)
            {
                if (activity[x] < bestValue)
                {
                    bestValue = activity[x];
                    best = x;
                }
            }
            cuts.Add(best);
        }
        cuts.Add(w);

        var boxes = new List<(int, int)>();
        for (int i = 0; i < columns; i++)
        {
            boxes.Add((cuts[i], cuts[i + 1]));
        }
        return boxes;
    }

    private static List<(int X0, int X1)> SplitAutoSpans(byte[] foreground, int w, int h)
    {
        int threshold = Math.Max(2, (int)(h * 0.005));
        var spans = new List<(int Start, int End)>();
        bool inSpan = false;
        int start = 0;
        for (int x = 0; x < w; x++)
        {
            int count = 0;
            for (int y = 0; y < h; y++)
            {
                count += foreground[y * w + x];
            }
            bool active = count > threshold;
            if (active && !inSpan)
            {
                inSpan = true;
                start = x;
            }
            else if (!active && inSpan)
            {
                inSpan = false;
                spans.Add((start, x));
            }
        }
        if (inSpan)
        {
            spans.Add((start, w));
        }

        var merged = new List<(int Start, int End)>();
        foreach (var (s0, s1) in spans)
        {
            if (merged.Count > 0 && s0 - merged[^1].End < 8)
            {
                merged[^1] = (merged[^1].Start, s1);
            }
            else if (s1 - s0 >= 12)
            {
                merged.Add((s0, s1));
            }
        }
        return merged;
    }

    /// <summary>
    /// Separates a green-screen crop into an RGBA cutout using exterior flood
    /// separation, cavity handling, fringe peeling, spill capping, edge-band
    /// darkening and loose-island removal, in that order.
    /// </summary>
    internal static RgbaImage ChromaKeyExtract(
        RgbaImage crop,
        bool fillAllHoles,
        bool keepHolesTransparent,
        int greenMin,
        int excessMin,
        int minHoleArea,
        int minIslandArea)
    {
        int w = crop.Width;
        int h = crop.Height;
        byte[] src = crop.Pixels;
        int n = w * h;

        var greenness = new short[n];
        int pw = w + 2;
        var paddedMask = new byte[pw * (h + 2)];
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int paddedRow = (y + 1) * pw + 1;
            for (int x = 0; x < w; x++)
            {
                int i = rowBase + x;
                int p = i * 4;
                int r = src[p], g = src[p + 1], b = src[p + 2];
                int excess = g - Math.Max(r, b);
                greenness[i] = (short)excess;
                paddedMask[paddedRow + x] = (byte)((g > greenMin && excess > excessMin) ? 1 : 0);
            }
        }
        // Solid border ring guarantees every outside green zone shares one region.
        for (int x = 0; x < pw; x++)
        {
            paddedMask[x] = 1;
            paddedMask[(h + 1) * pw + x] = 1;
        }
        for (int y = 0; y < h + 2; y++)
        {
            paddedMask[y * pw] = 1;
            paddedMask[y * pw + pw - 1] = 1;
        }

        var (labels, labelCount, areas, sumX, sumY) = Labeling.Label8(paddedMask, pw, h + 2);
        int exteriorLabel = labels[0];

        var alpha = new byte[n];
        for (int i = 0; i < n; i++)
        {
            alpha[i] = labels[(i / w + 1) * pw + (i % w + 1)] == exteriorLabel ? (byte)0 : (byte)255;
        }

        // Collect interior pixel indices per non-exterior green region.
        var regionPixels = new List<int>[labelCount + 1];
        for (int lbl = 1; lbl <= labelCount; lbl++)
        {
            if (lbl != exteriorLabel)
            {
                regionPixels[lbl] = new List<int>();
            }
        }
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w;
            int paddedRowBase = (y + 1) * pw + 1;
            for (int x = 0; x < w; x++)
            {
                int lbl = labels[paddedRowBase + x];
                if (lbl != 0 && lbl != exteriorLabel)
                {
                    regionPixels[lbl].Add(rowBase + x);
                }
            }
        }

        var outR = new byte[n];
        var outG = new byte[n];
        var outB = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            outR[i] = src[p];
            outG[i] = src[p + 1];
            outB[i] = src[p + 2];
        }

        byte[]? combinedHoleMask = null;
        for (int lbl = 1; lbl <= labelCount; lbl++)
        {
            if (lbl == exteriorLabel)
            {
                continue;
            }
            var pixels = regionPixels[lbl];
            int area = areas[lbl];
            if (fillAllHoles)
            {
                combinedHoleMask ??= new byte[n];
                foreach (int idx in pixels)
                {
                    combinedHoleMask[idx] = 1;
                }
            }
            else if (keepHolesTransparent || area < minHoleArea)
            {
                foreach (int idx in pixels)
                {
                    alpha[idx] = 0;
                }
            }
        }
        if (combinedHoleMask != null)
        {
            byte[] dilated = Morphology.DilateBinary(combinedHoleMask, w, h, 5, 1);
            for (int i = 0; i < n; i++)
            {
                if (dilated[i] != 0)
                {
                    alpha[i] = 255;
                    if (greenness[i] > 10)
                    {
                        outR[i] = CavityFillR;
                        outG[i] = CavityFillG;
                        outB[i] = CavityFillB;
                    }
                }
            }
        }

        // 1px outer band: foreground touching a dilated transparent zone.
        var transparentMask = new byte[n];
        for (int i = 0; i < n; i++)
        {
            transparentMask[i] = alpha[i] == 0 ? (byte)1 : (byte)0;
        }
        byte[] transparentDilated = Morphology.DilateBinary(transparentMask, w, h, 3, 1);
        var edgeBand = new byte[n];
        for (int i = 0; i < n; i++)
        {
            edgeBand[i] = (alpha[i] > 0 && transparentDilated[i] != 0) ? (byte)1 : (byte)0;
        }

        for (int i = 0; i < n; i++)
        {
            if (edgeBand[i] != 0 && greenness[i] > 24)
            {
                alpha[i] = 0;
            }
        }

        bool anyDespill = false;
        for (int i = 0; i < n; i++)
        {
            if (alpha[i] > 0 && greenness[i] > 4)
            {
                anyDespill = true;
                break;
            }
        }
        if (anyDespill)
        {
            for (int i = 0; i < n; i++)
            {
                // The spill-cap and edge darkening read the CURRENT channel
                // values (cavity fills included); only the threshold masks use
                // the original greenness.
                float rCurrent = outR[i];
                float gCurrent = outG[i];
                float bCurrent = outB[i];
                float cap = Math.Max(rCurrent, bCurrent) + 2.0f;
                float gNew = (alpha[i] > 0 && greenness[i] > 4) ? Math.Min(gCurrent, cap) : gCurrent;
                bool darkFringe = edgeBand[i] != 0 && greenness[i] > 8 && alpha[i] > 0;
                if (darkFringe)
                {
                    outB[i] = TruncateToByte(Math.Clamp(bCurrent * 0.45f, 20f, 255f));
                    outG[i] = TruncateToByte(Math.Clamp(gNew * 0.35f, 18f, 255f));
                    outR[i] = TruncateToByte(Math.Clamp(rCurrent * 0.45f, 22f, 255f));
                    if (greenness[i] > 16)
                    {
                        alpha[i] = 140;
                    }
                }
                else
                {
                    outG[i] = TruncateToByte(gNew);
                }
            }
        }

        // Remove loose islands below the area floor from the visible mask in a single O(n) pass.
        var visibleMask = new byte[n];
        for (int i = 0; i < n; i++)
        {
            visibleMask[i] = alpha[i] > 20 ? (byte)1 : (byte)0;
        }
        var (visibleLabels, visibleCount, visibleAreas, _, _) = Labeling.Label8(visibleMask, w, h);
        if (visibleCount > 0)
        {
            bool[] removeLabel = new bool[visibleCount + 1];
            bool anyToRemove = false;
            for (int lbl = 1; lbl <= visibleCount; lbl++)
            {
                if (visibleAreas[lbl] < minIslandArea)
                {
                    removeLabel[lbl] = true;
                    anyToRemove = true;
                }
            }
            if (anyToRemove)
            {
                for (int i = 0; i < n; i++)
                {
                    int lbl = visibleLabels[i];
                    if (lbl > 0 && removeLabel[lbl])
                    {
                        alpha[i] = 0;
                    }
                }
            }
        }

        var result = new RgbaImage(w, h);
        byte[] rp = result.Pixels;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            rp[p] = outR[i];
            rp[p + 1] = outG[i];
            rp[p + 2] = outB[i];
            rp[p + 3] = alpha[i];
        }
        return result;
    }

    private static byte TruncateToByte(float value)
    {
        // Float-to-byte conversion truncates toward zero; values are pre-clamped.
        return (byte)value;
    }

    /// <summary>Crops to the bounding box of pixels whose alpha exceeds the threshold.</summary>
    internal static (RgbaImage Image, (int X, int Y) Offset) TightCrop(RgbaImage image, int alphaThreshold)
    {
        int w = image.Width;
        int h = image.Height;
        byte[] px = image.Pixels;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            int rowBase = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (px[rowBase + x * 4 + 3] > alphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }
        if (maxX < 0)
        {
            return (image.Clone(), (0, 0));
        }
        return (image.Crop(minX, minY, maxX + 1, maxY + 1), (minX, minY));
    }

    /// <summary>
    /// Propagates the RGB of pixels with alpha &gt; 24 outward into transparent
    /// margin pixels through repeated 3x3 averaging, leaving alpha untouched.
    /// </summary>
    internal static RgbaImage BleedRgbIntoTransparent(RgbaImage image, int iterations)
    {
        int w = image.Width;
        int h = image.Height;
        int n = w * h;
        byte[] src = image.Pixels;
        var known = new byte[n];
        var rgbR = new float[n];
        var rgbG = new float[n];
        var rgbB = new float[n];
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            known[i] = src[p + 3] > 24 ? (byte)1 : (byte)0;
            rgbR[i] = src[p];
            rgbG[i] = src[p + 1];
            rgbB[i] = src[p + 2];
        }

        var fill = new byte[n];
        var knownFloat = new float[n];
        var maskedPlane = new float[n];
        for (int it = 0; it < iterations; it++)
        {
            byte[] dilatedKnown = Morphology.DilateBinary(known, w, h, 3, 1);
            bool anyFill = false;
            for (int i = 0; i < n; i++)
            {
                byte f = (known[i] == 0 && dilatedKnown[i] != 0) ? (byte)1 : (byte)0;
                fill[i] = f;
                if (f != 0)
                {
                    anyFill = true;
                }
            }
            if (!anyFill)
            {
                break;
            }

            for (int i = 0; i < n; i++)
            {
                knownFloat[i] = known[i];
            }
            float[] cnt = Filters.BoxSum3x3(knownFloat, w, h);

            for (int c = 0; c < 3; c++)
            {
                float[] ch = c == 0 ? rgbR : (c == 1 ? rgbG : rgbB);
                for (int i = 0; i < n; i++)
                {
                    maskedPlane[i] = known[i] != 0 ? ch[i] : 0f;
                }
                float[] sums = Filters.BoxSum3x3(maskedPlane, w, h);
                for (int i = 0; i < n; i++)
                {
                    if (fill[i] != 0 && cnt[i] > 0f)
                    {
                        ch[i] = sums[i] / cnt[i];
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (fill[i] != 0 && cnt[i] > 0f)
                {
                    known[i] = 1;
                }
            }
        }

        var result = new RgbaImage(w, h);
        byte[] rp = result.Pixels;
        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            rp[p] = TruncateToByte(Math.Clamp(rgbR[i], 0f, 255f));
            rp[p + 1] = TruncateToByte(Math.Clamp(rgbG[i], 0f, 255f));
            rp[p + 2] = TruncateToByte(Math.Clamp(rgbB[i], 0f, 255f));
            rp[p + 3] = src[p + 3];
        }
        return result;
    }
}
