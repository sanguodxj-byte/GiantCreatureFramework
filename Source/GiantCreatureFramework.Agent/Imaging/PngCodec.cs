using System.Buffers.Binary;
using System.IO.Compression;

namespace GiantCreatureFramework.Agent.Imaging;

/// <summary>
/// Self-contained PNG codec producing and consuming 8-bit RGBA rasters.
/// Decoding supports color types 0 (gray), 2 (RGB), 3 (palette, with optional
/// tRNS), 4 (gray+alpha) and 6 (RGBA) at bit depths 1, 2, 4 and 8, non-interlaced.
/// Encoding always emits 8-bit RGBA, non-interlaced, zlib-compressed IDAT.
/// 16-bit depths and interlaced scans are rejected with explicit exceptions.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    private const uint IhdrType = 0x49484452;
    private const uint PlteType = 0x504C5445;
    private const uint TrnsType = 0x74524E53;
    private const uint IdatType = 0x49444154;
    private const uint IendType = 0x49454E44;

    public static RgbaImage Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 33 || !StartsWith(png, Signature))
        {
            throw new InvalidDataException("Invalid PNG signature.");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        var idat = new List<byte[]>();
        bool seenIend = false;

        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int chunkLen = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos, 4));
            uint chunkType = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 4, 4));
            if (chunkLen < 0 || pos + 12 + chunkLen > png.Length)
            {
                throw new InvalidDataException("PNG chunk length overruns the file.");
            }
            var data = new byte[chunkLen];
            Buffer.BlockCopy(png, pos + 8, data, 0, chunkLen);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + chunkLen, 4));
            if (Crc32(png, pos + 4, chunkLen + 4) != storedCrc)
            {
                throw new InvalidDataException($"PNG chunk CRC mismatch (type 0x{chunkType:X8}).");
            }
            pos += 12 + chunkLen;

            switch (chunkType)
            {
                case IhdrType:
                    if (chunkLen != 13)
                    {
                        throw new InvalidDataException($"Invalid PNG IHDR chunk length {chunkLen} (expected 13).");
                    }
                    width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4, 4));
                    bitDepth = data[8];
                    colorType = data[9];
                    int compression = data[10];
                    int filterMethod = data[11];
                    interlace = data[12];
                    if (compression != 0 || filterMethod != 0)
                    {
                        throw new NotSupportedException("Only standard PNG compression and filter methods are supported.");
                    }
                    break;
                case PlteType:
                    palette = data;
                    break;
                case TrnsType:
                    trns = data;
                    break;
                case IdatType:
                    idat.Add(data);
                    break;
                case IendType:
                    seenIend = true;
                    break;
            }
            if (seenIend)
            {
                break;
            }
        }

        if (!seenIend)
        {
            throw new InvalidDataException("PNG data stream ended before IEND.");
        }
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"PNG image size {width}x{height} is not positive.");
        }
        if (interlace != 0)
        {
            throw new NotSupportedException("Interlaced (Adam7) PNG scans are not supported.");
        }
        if (bitDepth == 16)
        {
            throw new NotSupportedException("16-bit PNG samples are not supported.");
        }
        if ((long)width * height > int.MaxValue / 4)
        {
            throw new InvalidDataException("PNG image is too large for a 32-bit pixel buffer.");
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"PNG color type {colorType} is not supported."),
        };

        switch (colorType)
        {
            case 0:
                if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8)
                {
                    throw new NotSupportedException($"Bit depth {bitDepth} is not supported for grayscale PNG.");
                }
                break;
            case 3:
                if (bitDepth != 1 && bitDepth != 2 && bitDepth != 4 && bitDepth != 8)
                {
                    throw new NotSupportedException($"Bit depth {bitDepth} is not supported for palette PNG.");
                }
                if (palette == null || palette.Length % 3 != 0)
                {
                    throw new InvalidDataException("Palette PNG is missing a valid PLTE chunk.");
                }
                break;
            case 2:
            case 4:
            case 6:
                if (bitDepth != 8)
                {
                    throw new NotSupportedException($"Bit depth {bitDepth} is not supported for this PNG color type.");
                }
                break;
        }

        int bitsPerPixel = channels * bitDepth;
        long strideLong = ((long)width * bitsPerPixel + 7L) / 8L;
        long rawSizeLong = (strideLong + 1L) * height;
        if (strideLong <= 0 || strideLong > int.MaxValue - 1 || rawSizeLong <= 0 || rawSizeLong > int.MaxValue)
        {
            throw new InvalidDataException("PNG image scanline buffer exceeds supported 32-bit bounds.");
        }
        int stride = (int)strideLong;
        int filterBpp = Math.Max(1, (bitsPerPixel + 7) / 8);

        byte[] raw = InflateIdat(idat, (int)rawSizeLong);
        var unfiltered = new byte[stride];
        var previous = new byte[stride];
        var result = new RgbaImage(width, height);
        byte[] rp = result.Pixels;

        for (int y = 0; y < height; y++)
        {
            int lineStart = y * (stride + 1);
            int filter = raw[lineStart];
            Buffer.BlockCopy(raw, lineStart + 1, unfiltered, 0, stride);
            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (int i = filterBpp; i < stride; i++)
                    {
                        unfiltered[i] = (byte)(unfiltered[i] + unfiltered[i - filterBpp]);
                    }
                    break;
                case 2:
                    for (int i = 0; i < stride; i++)
                    {
                        unfiltered[i] = (byte)(unfiltered[i] + previous[i]);
                    }
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= filterBpp ? unfiltered[i - filterBpp] : 0;
                        int up = previous[i];
                        unfiltered[i] = (byte)(unfiltered[i] + ((left + up) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= filterBpp ? unfiltered[i - filterBpp] : 0;
                        int b = previous[i];
                        int c = i >= filterBpp ? previous[i - filterBpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a);
                        int pb = Math.Abs(p - b);
                        int pc = Math.Abs(p - c);
                        int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        unfiltered[i] = (byte)(unfiltered[i] + predictor);
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown PNG row filter {filter}.");
            }

            DecodeRowIntoRgba(colorType, bitDepth, unfiltered, palette, trns, rp, y * width * 4, width);
            Buffer.BlockCopy(unfiltered, 0, previous, 0, stride);
        }

        return result;
    }

    private static byte[] InflateIdat(List<byte[]> idat, int expectedSize)
    {
        if (idat.Count == 0)
        {
            throw new InvalidDataException("PNG file contains no IDAT chunk.");
        }
        using var packed = new MemoryStream();
        foreach (var chunk in idat)
        {
            packed.Write(chunk, 0, chunk.Length);
        }
        packed.Position = 0;
        using var zlib = new ZLibStream(packed, CompressionMode.Decompress);
        var raw = new byte[expectedSize];
        int total = 0;
        while (total < expectedSize)
        {
            int read = zlib.Read(raw, total, expectedSize - total);
            if (read <= 0)
            {
                break;
            }
            total += read;
        }
        if (total < expectedSize)
        {
            throw new InvalidDataException("PNG IDAT stream is truncated.");
        }
        return raw;
    }

    private static void DecodeRowIntoRgba(
        int colorType, int bitDepth, byte[] row, byte[]? palette, byte[]? trns, byte[] dst, int dstOffset, int width)
    {
        switch (colorType)
        {
            case 0:
            {
                // The tRNS gray value stores the significant bits in the low positions.
                int trnsGray = trns != null && trns.Length >= 2
                    ? ((trns[0] << 8) | trns[1]) & ((1 << bitDepth) - 1)
                    : -1;
                int maxValue = (1 << bitDepth) - 1;
                for (int x = 0; x < width; x++)
                {
                    int sample = ReadSample(row, bitDepth, x);
                    byte gray = bitDepth == 8 ? (byte)sample : (byte)(sample * 255 / maxValue);
                    int o = dstOffset + x * 4;
                    dst[o] = gray;
                    dst[o + 1] = gray;
                    dst[o + 2] = gray;
                    dst[o + 3] = sample == trnsGray ? (byte)0 : (byte)255;
                }
                break;
            }
            case 2:
            {
                // The tRNS color value stores the significant bits in the low positions.
                bool hasTrns = trns != null && trns.Length >= 6;
                int trnsR = 0, trnsG = 0, trnsB = 0;
                if (hasTrns)
                {
                    trnsR = ((trns![0] << 8) | trns[1]) & 0xFF;
                    trnsG = ((trns[2] << 8) | trns[3]) & 0xFF;
                    trnsB = ((trns[4] << 8) | trns[5]) & 0xFF;
                }
                for (int x = 0; x < width; x++)
                {
                    int o = dstOffset + x * 4;
                    int i = x * 3;
                    dst[o] = row[i];
                    dst[o + 1] = row[i + 1];
                    dst[o + 2] = row[i + 2];
                    dst[o + 3] = hasTrns && row[i] == trnsR && row[i + 1] == trnsG && row[i + 2] == trnsB
                        ? (byte)0
                        : (byte)255;
                }
                break;
            }
            case 3:
            {
                if (palette == null)
                {
                    throw new InvalidDataException("Palette PNG is missing a valid PLTE chunk.");
                }
                int paletteEntries = palette.Length / 3;
                for (int x = 0; x < width; x++)
                {
                    int index = ReadSample(row, bitDepth, x);
                    if (index >= paletteEntries)
                    {
                        throw new InvalidDataException($"Palette index {index} is out of range.");
                    }
                    int o = dstOffset + x * 4;
                    int p = index * 3;
                    dst[o] = palette[p];
                    dst[o + 1] = palette[p + 1];
                    dst[o + 2] = palette[p + 2];
                    dst[o + 3] = trns != null && index < trns.Length ? trns[index] : (byte)255;
                }
                break;
            }
            case 4:
                for (int x = 0; x < width; x++)
                {
                    int o = dstOffset + x * 4;
                    int i = x * 2;
                    dst[o] = row[i];
                    dst[o + 1] = row[i];
                    dst[o + 2] = row[i];
                    dst[o + 3] = row[i + 1];
                }
                break;
            case 6:
                Buffer.BlockCopy(row, 0, dst, dstOffset, width * 4);
                break;
        }
    }

    /// <summary>Reads the x-th sample of the row; sub-byte depths are packed MSB-first.</summary>
    private static int ReadSample(byte[] row, int bitDepth, int x)
    {
        switch (bitDepth)
        {
            case 8:
                return row[x];
            case 1:
                return (row[x >> 3] >> (7 - (x & 7))) & 1;
            case 2:
                return (row[x >> 2] >> (6 - ((x & 3) << 1))) & 3;
            case 4:
                return (row[x >> 1] >> (4 - ((x & 1) << 2))) & 15;
            default:
                throw new NotSupportedException($"Bit depth {bitDepth} is not supported.");
        }
    }

    public static byte[] Encode(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.Width;
        int height = image.Height;
        long strideLong = (long)width * 4L;
        long rawSizeLong = (strideLong + 1L) * height;
        if (strideLong <= 0 || strideLong > int.MaxValue - 1 || rawSizeLong <= 0 || rawSizeLong > int.MaxValue)
        {
            throw new InvalidDataException("PNG image is too large to encode.");
        }
        int stride = (int)strideLong;

        using var output = new MemoryStream();
        output.Write(Signature, 0, 8);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // color type RGBA
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // non-interlaced
        WriteChunk(output, IhdrType, ihdr);

        var rawRows = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            rawRows[y * (stride + 1)] = 0; // filter none
            Buffer.BlockCopy(image.Pixels, y * stride, rawRows, y * (stride + 1) + 1, stride);
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal))
        {
            zlib.Write(rawRows, 0, rawRows.Length);
        }
        WriteChunk(output, IdatType, compressed.ToArray());
        WriteChunk(output, IendType, Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(MemoryStream output, uint type, byte[] data)
    {
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, data.Length);
        output.Write(lenBytes, 0, 4);
        var typeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(typeBytes, type);
        output.Write(typeBytes, 0, 4);
        output.Write(data, 0, data.Length);
        uint crc = Crc32Continue(data, 0, data.Length, Crc32Continue(typeBytes, 0, 4, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes, 0, 4);
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static uint Crc32(byte[] data, int offset, int count)
    {
        return Crc32Continue(data, offset, count, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
    }

    private static uint Crc32Continue(byte[] data, int offset, int count, uint runningCrc)
    {
        uint crc = runningCrc;
        for (int i = 0; i < count; i++)
        {
            crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }
}
