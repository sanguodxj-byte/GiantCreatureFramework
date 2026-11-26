namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// Deterministic image-analysis primitives shared by the extraction, warping and
/// preview pipelines: 8-connected component labeling with per-component area and
/// centroid, binary and grayscale morphology, and the 3x3 box sum filter with
/// reflected (edge-sample-duplicated) borders.
/// </summary>
internal static class Labeling
{
    /// <summary>
    /// Labels 8-connected non-zero regions of the mask. Region ids start at 1 and
    /// are assigned in raster order of each region's first pixel; zero pixels keep
    /// label 0. Areas and centroid coordinate sums are indexed by region id.
    /// </summary>
    public static (int[] Labels, int Count, int[] Areas, long[] SumX, long[] SumY) Label8(byte[] mask, int width, int height)
    {
        var labels = new int[width * height];
        var areas = new List<int> { 0 };
        var sumX = new List<long> { 0 };
        var sumY = new List<long> { 0 };
        var queue = new int[width * height];
        int nextLabel = 0;

        for (int start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || labels[start] != 0)
            {
                continue;
            }
            nextLabel++;
            int area = 0;
            long sx = 0, sy = 0;
            int head = 0, tail = 0;
            queue[tail++] = start;
            labels[start] = nextLabel;
            while (head < tail)
            {
                int idx = queue[head++];
                int y = idx / width;
                int x = idx - y * width;
                area++;
                sx += x;
                sy += y;
                int y0 = y > 0 ? y - 1 : 0;
                int y1 = y < height - 1 ? y + 1 : height - 1;
                int x0 = x > 0 ? x - 1 : 0;
                int x1 = x < width - 1 ? x + 1 : width - 1;
                for (int ny = y0; ny <= y1; ny++)
                {
                    int rowBase = ny * width;
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int nIdx = rowBase + nx;
                        if (mask[nIdx] != 0 && labels[nIdx] == 0)
                        {
                            labels[nIdx] = nextLabel;
                            queue[tail++] = nIdx;
                        }
                    }
                }
            }
            areas.Add(area);
            sumX.Add(sx);
            sumY.Add(sy);
        }

        return (labels, nextLabel, areas.ToArray(), sumX.ToArray(), sumY.ToArray());
    }
}

internal static class Morphology
{
    /// <summary>
    /// Maximum filter over a k x k all-ones square applied to a 0/1 mask; pixels
    /// outside the raster never contribute to the maximum. Repeated for the given
    /// iteration count.
    /// </summary>
    public static byte[] DilateBinary(byte[] mask, int width, int height, int kernelSize, int iterations)
    {
        if ((kernelSize & 1) == 0)
        {
            throw new ArgumentException("Kernel size must be odd.");
        }
        byte[] current = (byte[])mask.Clone();
        byte[] next = new byte[mask.Length];
        int radius = kernelSize / 2;
        for (int it = 0; it < iterations; it++)
        {
            for (int y = 0; y < height; y++)
            {
                int y0 = Math.Max(0, y - radius);
                int y1 = Math.Min(height - 1, y + radius);
                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - radius);
                    int x1 = Math.Min(width - 1, x + radius);
                    byte hit = 0;
                    for (int ny = y0; ny <= y1 && hit == 0; ny++)
                    {
                        int rowBase = ny * width;
                        for (int nx = x0; nx <= x1; nx++)
                        {
                            if (current[rowBase + nx] != 0)
                            {
                                hit = 1;
                                break;
                            }
                        }
                    }
                    next[y * width + x] = hit;
                }
            }
            (current, next) = (next, current);
        }
        return current;
    }

    /// <summary>
    /// Builds a disk-shaped structuring element of the given odd size: a row at
    /// vertical offset dy is filled from (radius - halfWidth) to
    /// (radius + halfWidth) inclusive, where halfWidth is the radius-scaled
    /// circle chord rounded to the nearest integer.
    /// </summary>
    public static bool[,] DiskElement(int size)
    {
        if ((size & 1) == 0 || size < 1)
        {
            throw new ArgumentException("Structuring element size must be odd and positive.");
        }
        int radius = size / 2;
        double invRadiusSq = radius > 0 ? 1.0 / ((double)radius * radius) : 0.0;
        var element = new bool[size, size];
        for (int i = 0; i < size; i++)
        {
            int dy = i - radius;
            int halfWidth = 0;
            if (Math.Abs(dy) <= radius)
            {
                halfWidth = RoundHalfToEven(radius * Math.Sqrt((radius * radius - dy * dy) * invRadiusSq));
            }
            int j1 = Math.Max(radius - halfWidth, 0);
            int j2 = Math.Min(radius + halfWidth + 1, size);
            for (int j = j1; j < j2; j++)
            {
                element[i, j] = true;
            }
        }
        return element;
    }

    private static int RoundHalfToEven(double value)
    {
        // Matches C library rounding of double to int (nearest, ties to even).
        return (int)Math.Round(value, MidpointRounding.ToEven);
    }

    /// <summary>Maximum filter (grayscale dilation) with a disk element; outside pixels never contribute.</summary>
    public static float[] GrayDilate(float[] image, int width, int height, bool[,] element)
    {
        return GrayMorph(image, width, height, element, takeMax: true);
    }

    /// <summary>Minimum filter (grayscale erosion) with a disk element; outside pixels never contribute.</summary>
    public static float[] GrayErode(float[] image, int width, int height, bool[,] element)
    {
        return GrayMorph(image, width, height, element, takeMax: false);
    }

    private static float[] GrayMorph(float[] image, int width, int height, bool[,] element, bool takeMax)
    {
        int size = element.GetLength(0);
        int radius = size / 2;
        var offsets = new List<(int Dx, int Dy)>();
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (element[dy + radius, dx + radius])
                {
                    offsets.Add((dx, dy));
                }
            }
        }
        var result = new float[image.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float extreme = takeMax ? float.NegativeInfinity : float.PositiveInfinity;
                for (int o = 0; o < offsets.Count; o++)
                {
                    int nx = x + offsets[o].Dx;
                    int ny = y + offsets[o].Dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }
                    float v = image[ny * width + nx];
                    if (takeMax ? v > extreme : v < extreme)
                    {
                        extreme = v;
                    }
                }
                result[y * width + x] = extreme;
            }
        }
        return result;
    }
}

internal static class Filters
{
    /// <summary>
    /// Sum of the 3x3 neighborhood for every pixel, float accumulation, with
    /// mirrored borders that reflect without repeating the edge sample
    /// (column -1 reads column 1, column width reads column width - 2).
    /// </summary>
    public static float[] BoxSum3x3(float[] image, int width, int height)
    {
        var result = new float[image.Length];
        for (int y = 0; y < height; y++)
        {
            int ym = Reflect101(y - 1, height);
            int yp = Reflect101(y + 1, height);
            int rowBase = y * width;
            int rowPrev = ym * width;
            int rowNext = yp * width;
            for (int x = 0; x < width; x++)
            {
                int xm = Reflect101(x - 1, width);
                int xp = Reflect101(x + 1, width);
                float sum =
                    image[rowPrev + xm] + image[rowPrev + x] + image[rowPrev + xp] +
                    image[rowBase + xm] + image[rowBase + x] + image[rowBase + xp] +
                    image[rowNext + xm] + image[rowNext + x] + image[rowNext + xp];
                result[rowBase + x] = sum;
            }
        }
        return result;
    }

    /// <summary>Mirror without repeating the edge sample: -1 maps to 1, size maps to size - 2.</summary>
    public static int Reflect101(int index, int size)
    {
        if (size == 1)
        {
            return 0;
        }
        if (index < 0)
        {
            return -index;
        }
        if (index >= size)
        {
            return 2 * size - 2 - index;
        }
        return index;
    }
}
