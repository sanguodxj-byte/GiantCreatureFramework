namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// Inward Euclidean-approximating distance field over a binary mask, computed as
/// a two-pass chamfer with a 5x5 neighborhood. Weights are 1.0 for
/// horizontal/vertical steps, 1.4 for diagonal steps and 2.1969 for the long
/// (2,1) knight steps, held in 16.16 fixed point; zero-mask pixels are the
/// sources at distance 0. The border is treated as an infinite wall so the field
/// measures distance to the nearest zero pixel inside the raster only.
/// </summary>
internal static class DistanceField
{
    private const int Shift = 16;

    // Step weights in 16.16 fixed point, rounded from the 32-bit float metrics
    // 1.0, 1.4 and 2.1969 (91750.398 -> 91750, 143976.031 -> 143976).
    private const uint HorizontalWeight = 65536;
    private const uint DiagonalWeight = 91750;
    private const uint LongWeight = 143976;
    private const uint MaxDistance = uint.MaxValue - LongWeight;
    private const float Scale = 1f / (1 << Shift);

    public static float[] Compute(byte[] mask, int width, int height)
    {
        uint[] temp = ForwardPass(mask, width, height, null);
        var result = new float[mask.Length];
        BackwardPass(temp, width, height, result, null);
        return result;
    }

    /// <summary>
    /// Same field, also propagating source labels: every zero pixel receives a
    /// unique sequential id in raster order and each other pixel adopts the id of
    /// the nearest source; ties keep the id of the first neighbor that reached the
    /// minimum in the fixed scan order of the pass that improved the pixel.
    /// </summary>
    public static (float[] Distance, int[] Labels) ComputeLabeled(byte[] mask, int width, int height)
    {
        var labels = new int[mask.Length];
        int next = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
            {
                labels[i] = ++next;
            }
        }
        uint[] temp = ForwardPass(mask, width, height, labels);
        var result = new float[mask.Length];
        BackwardPass(temp, width, height, result, labels);
        return (result, labels);
    }

    private static uint[] ForwardPass(byte[] mask, int width, int height, int[]? labels)
    {
        // Padded by two border columns and rows on every side, all at max distance.
        int stride = width + 4;
        var temp = new uint[stride * (height + 4)];
        for (int i = 0; i < temp.Length; i++)
        {
            temp[i] = MaxDistance;
        }

        for (int y = 0; y < height; y++)
        {
            int paddedBase = (y + 2) * stride + 2;
            int imageBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = paddedBase + x;
                if (mask[imageBase + x] == 0)
                {
                    temp[i] = 0;
                    continue;
                }
                uint best = MaxDistance;
                int bestLabel = 0;
                uint t;

                t = temp[i - stride * 2 - 1] + LongWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width * 2 - 1]; }
                t = temp[i - stride * 2 + 1] + LongWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width * 2 + 1]; }
                t = temp[i - stride - 2] + LongWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width - 2]; }
                t = temp[i - stride - 1] + DiagonalWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width - 1]; }
                t = temp[i - stride] + HorizontalWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width]; }
                t = temp[i - stride + 1] + DiagonalWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width + 1]; }
                t = temp[i - stride + 2] + LongWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - width + 2]; }
                t = temp[i - 1] + HorizontalWeight;
                if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x - 1]; }

                temp[i] = best > MaxDistance ? MaxDistance : best;
                if (labels != null)
                {
                    labels[imageBase + x] = bestLabel;
                }
            }
        }
        return temp;
    }

    private static void BackwardPass(uint[] temp, int width, int height, float[] result, int[]? labels)
    {
        int stride = width + 4;
        for (int y = height - 1; y >= 0; y--)
        {
            int paddedBase = (y + 2) * stride + 2;
            int imageBase = y * width;
            for (int x = width - 1; x >= 0; x--)
            {
                int i = paddedBase + x;
                uint best = temp[i];
                int bestLabel = labels?[imageBase + x] ?? 0;
                if (best > HorizontalWeight)
                {
                    uint t;

                    t = temp[i + stride * 2 + 1] + LongWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width * 2 + 1]; }
                    t = temp[i + stride * 2 - 1] + LongWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width * 2 - 1]; }
                    t = temp[i + stride + 2] + LongWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width + 2]; }
                    t = temp[i + stride + 1] + DiagonalWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width + 1]; }
                    t = temp[i + stride] + HorizontalWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width]; }
                    t = temp[i + stride - 1] + DiagonalWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width - 1]; }
                    t = temp[i + stride - 2] + LongWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + width - 2]; }
                    t = temp[i + 1] + HorizontalWeight;
                    if (best > t) { best = t; if (labels != null) bestLabel = labels[imageBase + x + 1]; }
                    temp[i] = best;
                    if (labels != null)
                    {
                        labels[imageBase + x] = bestLabel;
                    }
                }
                result[imageBase + x] = (float)best * Scale;
            }
        }
    }
}
