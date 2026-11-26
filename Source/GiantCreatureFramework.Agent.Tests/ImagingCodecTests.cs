using System.Buffers.Binary;
using System.IO.Compression;
using FluentAssertions;
using GiantCreatureFramework.Agent.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GiantCreatureFramework.Agent.Tests;

/// <summary>
/// Self-contained codec unit tests: byte-exact re-encode/decode round trips over
/// synthetic RGBA rasters, in-memory PNG color-type decoding, rejection of
/// unsupported layouts (16-bit and interlaced) with explicit exceptions, and
/// invalid-argument validation.
/// </summary>
[TestClass]
public class ImagingCodecTests
{
    [TestMethod]
    public void Decode_Encode_Roundtrip_IsByteExact_OnSyntheticImages()
    {
        var cases = new (string Name, RgbaImage Image)[]
        {
            ("1x1_transparent", BuildSyntheticRgba(1, 1, seed: 1)),
            ("7x5_odd_dimensions", BuildSyntheticRgba(7, 5, seed: 2)),
            ("64x48_gradient_and_alpha", BuildSyntheticRgba(64, 48, seed: 3)),
            ("128x128_checker_and_edges", BuildSyntheticRgba(128, 128, seed: 4)),
        };

        foreach (var (name, image) in cases)
        {
            byte[] encoded = PngCodec.Encode(image);
            RgbaImage decoded = PngCodec.Decode(encoded);
            decoded.Width.Should().Be(image.Width, name);
            decoded.Height.Should().Be(image.Height, name);
            decoded.Pixels.Should().Equal(image.Pixels, "round trip must be byte exact for {0}", name);
        }
    }

    [TestMethod]
    public void Decode_SupportsGrayRgbPaletteAndGrayAlpha_InMemory()
    {
        // 1. Grayscale (color type 0, 8-bit) with tRNS transparent value = 0
        byte[] grayPng = BuildMinimalPng(
            width: 2, height: 1, bitDepth: 8, colorType: 0, interlace: 0,
            rawScanlines: new byte[] { 0, 0, 200 },
            trns: new byte[] { 0, 0 });
        RgbaImage grayImg = PngCodec.Decode(grayPng);
        grayImg.Pixels.Should().Equal(new byte[] { 0, 0, 0, 0, 200, 200, 200, 255 });

        // 2. RGB (color type 2, 8-bit) with tRNS transparent RGB = (10, 20, 30)
        byte[] rgbPng = BuildMinimalPng(
            width: 2, height: 1, bitDepth: 8, colorType: 2, interlace: 0,
            rawScanlines: new byte[] { 0, 10, 20, 30, 40, 50, 60 },
            trns: new byte[] { 0, 10, 0, 20, 0, 30 });
        RgbaImage rgbImg = PngCodec.Decode(rgbPng);
        rgbImg.Pixels.Should().Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 255 });

        // 3. Indexed palette (color type 3, 8-bit) with PLTE + tRNS
        byte[] palPng = BuildMinimalPng(
            width: 2, height: 1, bitDepth: 8, colorType: 3, interlace: 0,
            rawScanlines: new byte[] { 0, 0, 1 },
            plte: new byte[] { 255, 0, 0, 0, 128, 255 },
            trns: new byte[] { 64 });
        RgbaImage palImg = PngCodec.Decode(palPng);
        palImg.Pixels.Should().Equal(new byte[] { 255, 0, 0, 64, 0, 128, 255, 255 });

        // 4. Gray+Alpha (color type 4, 8-bit)
        byte[] gaPng = BuildMinimalPng(
            width: 2, height: 1, bitDepth: 8, colorType: 4, interlace: 0,
            rawScanlines: new byte[] { 0, 90, 128, 240, 255 });
        RgbaImage gaImg = PngCodec.Decode(gaPng);
        gaImg.Pixels.Should().Equal(new byte[] { 90, 90, 90, 128, 240, 240, 240, 255 });
    }

    [TestMethod]
    public void Decode_Rejects16BitAndInterlaced_WithExplicitException()
    {
        byte[] gray16 = BuildMinimalPng(
            width: 2, height: 2, bitDepth: 16, colorType: 0, interlace: 0,
            rawScanlines: new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        byte[] interlaced = BuildMinimalPng(
            width: 2, height: 2, bitDepth: 8, colorType: 6, interlace: 1,
            rawScanlines: new byte[] { 0, 255, 0, 0, 255 });

        Action decode16 = () => PngCodec.Decode(gray16);
        Action decodeInterlaced = () => PngCodec.Decode(interlaced);
        decode16.Should().Throw<NotSupportedException>().WithMessage("*16-bit*");
        decodeInterlaced.Should().Throw<NotSupportedException>().WithMessage("*Interlaced*");
    }

    [TestMethod]
    public void Encode_RejectsNullAndEmptyInput()
    {
        Action encodeNull = () => PngCodec.Encode(null!);
        encodeNull.Should().Throw<ArgumentNullException>();
        Action decodeTruncated = () => PngCodec.Decode(new byte[] { 1, 2, 3 });
        decodeTruncated.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void ExtractSheet_RejectsInvalidArguments()
    {
        var image = new RgbaImage(16, 16);
        Action zeroColumns = () => SpriteSheetExtractor.ExtractSheet(image, columns: 0);
        zeroColumns.Should().Throw<ArgumentOutOfRangeException>();
        Action negativePad = () => SpriteSheetExtractor.ExtractSheet(image, padPixels: -1);
        negativePad.Should().Throw<ArgumentOutOfRangeException>();
        Action badHex = () => BoneWarper.ParseHexColor("#12345");
        badHex.Should().Throw<FormatException>();
        Action smallCanvas = () => BoneWarper.WarpBone(image, (8, 8), canvasSize: -4);
        smallCanvas.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static RgbaImage BuildSyntheticRgba(int width, int height, int seed)
    {
        var image = new RgbaImage(width, height);
        byte[] px = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                px[o] = (byte)((x * 17 + y * 31 + seed * 53) & 0xFF);
                px[o + 1] = (byte)((x * 29 + y * 13 + seed * 19) & 0xFF);
                px[o + 2] = (byte)((x * 7 + y * 43 + seed * 97) & 0xFF);
                px[o + 3] = (x + y + seed) % 5 == 0 ? (byte)0 : (byte)((x * 11 + y * 23 + seed * 37) & 0xFF);
            }
        }
        return image;
    }

    private static byte[] BuildMinimalPng(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte interlace,
        byte[] rawScanlines,
        byte[]? plte = null,
        byte[]? trns = null)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = bitDepth;
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = interlace;
        WriteChunk(ms, "IHDR", ihdr);

        if (plte != null)
        {
            WriteChunk(ms, "PLTE", plte);
        }
        if (trns != null)
        {
            WriteChunk(ms, "tRNS", trns);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest))
        {
            zlib.Write(rawScanlines, 0, rawScanlines.Length);
        }
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        ms.Write(len);

        var typeAndData = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typeAndData, 4, data.Length);
        ms.Write(typeAndData);

        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(typeAndData));
        ms.Write(crc);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
        }
        return ~crc;
    }
}

/// <summary>
/// Performance smoke checks on large inputs; asserts generous upper bounds and
/// logs measured wall-clock times to console output.
/// </summary>
[TestClass]
public class ImagingPerformanceTests
{
    [TestMethod]
    public void ExtractSheet_And_Warp_On_2048_Sheet_CompleteWithinBudget()
    {
        RgbaImage sheet = BuildSyntheticSheet(2048, 2048);

        var extractWatch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<ExtractedSheetPart> parts = SpriteSheetExtractor.ExtractSheet(sheet, columns: 4);
        extractWatch.Stop();

        parts.Count.Should().Be(4);
        parts.Should().OnlyContain(p => p.Image.Width > 0 && p.Image.Height > 0);

        var warpWatch = System.Diagnostics.Stopwatch.StartNew();
        ExtractedSheetPart part = parts[0];
        var pivot = ((double)part.Image.Width / 2, part.Image.Height * 0.8);
        BoneWarpResult warped = BoneWarper.WarpBone(
            part.Image, pivot, (256.0, 400.0), (0.9, 1.1), 15.0, 512,
            new[] { new NamedPoint("tip", 10.0, 20.0) }, emitMirror: true);
        warpWatch.Stop();

        warped.Image.Width.Should().Be(512);
        warped.MirrorImage.Should().NotBeNull();

        Console.WriteLine($"[PERF] extract_2048x2048_ms={extractWatch.ElapsedMilliseconds}, warp_512_mirror_ms={warpWatch.ElapsedMilliseconds}");

        extractWatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "2048x2048 extract");
        warpWatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "single warp with mirror");
    }

    /// <summary>Deterministic 4-column green-screen sheet with outlined blobs, holes and fringe pixels.</summary>
    private static RgbaImage BuildSyntheticSheet(int width, int height)
    {
        var sheet = new RgbaImage(width, height);
        byte[] px = sheet.Pixels;
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4] = 0;
            px[i * 4 + 1] = 255;
            px[i * 4 + 2] = 0;
            px[i * 4 + 3] = 255;
        }
        int cell = width / 4;
        for (int c = 0; c < 4; c++)
        {
            int cx = c * cell + cell / 2;
            int cy = height / 2;
            int rx = cell * 2 / 5;
            int ry = height * 2 / 5;
            FillEllipse(px, width, height, cx, cy, rx, ry, 20, 18, 24);
            FillEllipse(px, width, height, cx, cy, rx - 10, ry - 10, 96, 120, 150);
            if (c % 2 == 0)
            {
                FillEllipse(px, width, height, cx, cy, rx / 3, rx / 3, 0, 255, 0);
            }
            FillRectangle(px, width, height, cx - rx / 5, cy - ry / 5, rx / 2, ry / 2, 235, 212, 180);
        }
        return sheet;
    }

    private static void FillEllipse(byte[] px, int w, int h, int cx, int cy, int rx, int ry, byte r, byte g, byte b)
    {
        for (int y = Math.Max(0, cy - ry); y < Math.Min(h, cy + ry + 1); y++)
        {
            for (int x = Math.Max(0, cx - rx); x < Math.Min(w, cx + rx + 1); x++)
            {
                double dx = (x - cx) / (double)rx;
                double dy = (y - cy) / (double)ry;
                if (dx * dx + dy * dy <= 1.0)
                {
                    int p = (y * w + x) * 4;
                    px[p] = r;
                    px[p + 1] = g;
                    px[p + 2] = b;
                    px[p + 3] = 255;
                }
            }
        }
    }

    private static void FillRectangle(byte[] px, int w, int h, int x0, int y0, int rw, int rh, byte r, byte g, byte b)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(h, y0 + rh); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(w, x0 + rw); x++)
            {
                int p = (y * w + x) * 4;
                px[p] = r;
                px[p + 1] = g;
                px[p + 2] = b;
                px[p + 3] = 255;
            }
        }
    }
}
