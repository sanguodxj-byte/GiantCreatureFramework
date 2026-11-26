using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework
{
    /// <summary>
    /// Compact 1-bit-per-pixel alpha occupancy mask + tight UV bounds extracted from a top-down PNG sprite.
    /// Row 0 is bottom (v = 0), Row height-1 is top (v = 1), matching Unity UV space.
    /// </summary>
    public sealed class TextureAlphaMask
    {
        private const int TileShift = 3; // 8x8 pixel tiles for O(1) 2D integral classification
        private const int TileSize = 1 << TileShift;

        public readonly int width;
        public readonly int height;
        public readonly byte alphaThreshold;
        private readonly ulong[] bits;
        private readonly int tilesX;
        private readonly int tilesY;
        private readonly int integralStride;
        private readonly int[] anyOpaqueIntegral;
        private readonly int[] allOpaqueIntegral;

        public readonly float minU;
        public readonly float maxU;
        public readonly float minV;
        public readonly float maxV;
        public readonly float widthF;
        public readonly float heightF;
        public readonly float minPx;
        public readonly float maxPx;
        public readonly float minPy;
        public readonly float maxPy;
        public readonly int opaquePixelCount;

        public bool HasAnyOpaquePixels => opaquePixelCount > 0;

        public TextureAlphaMask(int width, int height, byte alphaThreshold, ReadOnlySpan<byte> bottomUpAlpha)
        {
            this.width = width;
            this.height = height;
            this.widthF = width;
            this.heightF = height;
            this.alphaThreshold = alphaThreshold;

            int totalPixels = width * height;
            bits = new ulong[(totalPixels + 63) >> 6];

            int minX = width;
            int maxX = -1;
            int minY = height;
            int maxY = -1;
            int count = 0;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    if (bottomUpAlpha[idx] >= alphaThreshold)
                    {
                        bits[idx >> 6] |= 1UL << (idx & 63);
                        count++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            opaquePixelCount = count;
            if (count > 0)
            {
                minPx = minX;
                maxPx = maxX + 1;
                minPy = minY;
                maxPy = maxY + 1;
                minU = minPx / widthF;
                maxU = maxPx / widthF;
                minV = minPy / heightF;
                maxV = maxPy / heightF;
            }
            else
            {
                minPx = 0f;
                maxPx = 0f;
                minPy = 0f;
                maxPy = 0f;
                minU = 0f;
                maxU = 0f;
                minV = 0f;
                maxV = 0f;
            }

            tilesX = (width + TileSize - 1) >> TileShift;
            tilesY = (height + TileSize - 1) >> TileShift;
            integralStride = tilesX + 1;
            int integralLen = integralStride * (tilesY + 1);
            anyOpaqueIntegral = new int[integralLen];
            allOpaqueIntegral = new int[integralLen];

            if (count > 0)
            {
                for (int ty = 0; ty < tilesY; ty++)
                {
                    int pyStart = ty << TileShift;
                    int pyEnd = Math.Min(height, pyStart + TileSize);
                    bool fullTileH = (pyEnd - pyStart) == TileSize;
                    int rowBase = (ty + 1) * integralStride;
                    int prevRowBase = ty * integralStride;
                    int rowAnySum = 0;
                    int rowAllSum = 0;

                    for (int tx = 0; tx < tilesX; tx++)
                    {
                        int pxStart = tx << TileShift;
                        int pxEnd = Math.Min(width, pxStart + TileSize);
                        bool fullTile = fullTileH && ((pxEnd - pxStart) == TileSize);
                        int tileOpaque = 0;

                        for (int py = pyStart; py < pyEnd; py++)
                        {
                            int pRow = py * width;
                            for (int px = pxStart; px < pxEnd; px++)
                            {
                                int pIdx = pRow + px;
                                if ((bits[pIdx >> 6] & (1UL << (pIdx & 63))) != 0)
                                {
                                    tileOpaque++;
                                }
                            }
                        }

                        if (tileOpaque > 0)
                        {
                            rowAnySum++;
                            if (fullTile && tileOpaque == (TileSize * TileSize))
                            {
                                rowAllSum++;
                            }
                        }

                        anyOpaqueIntegral[rowBase + tx + 1] = anyOpaqueIntegral[prevRowBase + tx + 1] + rowAnySum;
                        allOpaqueIntegral[rowBase + tx + 1] = allOpaqueIntegral[prevRowBase + tx + 1] + rowAllSum;
                    }
                }
            }
        }

        /// <summary>
        /// O(1) conservative UV-box classifier backed by 8x8 tile summed-area tables:
        /// returns 0 when provably 100% transparent, 2 when provably 100% opaque, or 1 when mixed/boundary.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int ClassifyUVRect(float uMin, float uMax, float vMin, float vMax)
        {
            const float Eps = 2e-5f;
            if (opaquePixelCount == 0 || uMax + Eps < minU || uMin - Eps >= maxU || vMax + Eps < minV || vMin - Eps >= maxV)
            {
                return 0;
            }

            int px0 = Math.Max(0, (int)((uMin - Eps) * width));
            int px1 = Math.Min(width - 1, (int)((uMax + Eps) * width));
            int py0 = Math.Max(0, (int)((vMin - Eps) * height));
            int py1 = Math.Min(height - 1, (int)((vMax + Eps) * height));
            if (px0 > px1 || py0 > py1)
            {
                return 0;
            }

            int tx0 = px0 >> TileShift;
            int tx1 = px1 >> TileShift;
            int ty0 = py0 >> TileShift;
            int ty1 = py1 >> TileShift;

            int r0 = ty0 * integralStride;
            int r1 = (ty1 + 1) * integralStride;
            int c0 = tx0;
            int c1 = tx1 + 1;

            int anyTiles = anyOpaqueIntegral[r1 + c1] - anyOpaqueIntegral[r0 + c1] - anyOpaqueIntegral[r1 + c0] + anyOpaqueIntegral[r0 + c0];
            if (anyTiles == 0)
            {
                return 0;
            }

            if (uMin - Eps >= minU && uMax + Eps < maxU && vMin - Eps >= minV && vMax + Eps < maxV)
            {
                int allTiles = allOpaqueIntegral[r1 + c1] - allOpaqueIntegral[r0 + c1] - allOpaqueIntegral[r1 + c0] + allOpaqueIntegral[r0 + c0];
                if (allTiles == (tx1 - tx0 + 1) * (ty1 - ty0 + 1))
                {
                    return 2;
                }
            }

            return 1;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool IsOpaqueUV(float u, float v)
        {
            if (u < minU || u >= maxU || v < minV || v >= maxV)
            {
                return false;
            }
            int x = (int)(u * width);
            int y = (int)(v * height);
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return false;
            }
            int idx = y * width + x;
            return (bits[idx >> 6] & (1UL << (idx & 63))) != 0;
        }

        /// <summary>
        /// Evaluates a 4x4 sub-cell sample grid directly in pre-scaled pixel space (caller hoists w/h multiply per bone),
        /// using precomputed [minPx, maxPx) x [minPy, maxPy) tight bounds so every passing coordinate is strictly in [0, w) x [0, h).
        /// </summary>
        public ushort SampleCell4x4Pixels(
            float rpx,
            float rpy,
            float dpxX,
            float dpyX,
            float dpxZ,
            float dpyZ)
        {
            int w = width;
            ulong[] b = bits;
            float loX = minPx;
            float hiX = maxPx;
            float loY = minPy;
            float hiY = maxPy;

            ushort cellMask = 0;
            int bitIdx = 0;
            for (int sz = 0; sz < 4; sz++)
            {
                float px = rpx;
                float py = rpy;
                for (int sx = 0; sx < 4; sx++)
                {
                    if (px >= loX && px < hiX && py >= loY && py < hiY)
                    {
                        int idx = ((int)py) * w + ((int)px);
                        if ((b[idx >> 6] & (1UL << (idx & 63))) != 0UL)
                        {
                            cellMask |= (ushort)(1 << bitIdx);
                        }
                    }
                    px += dpxX;
                    py += dpyX;
                    bitIdx++;
                }
                rpx += dpxZ;
                rpy += dpyZ;
            }
            return cellMask;
        }

        public bool IsOpaquePixelBottomUp(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return false;
            }
            int idx = y * width + x;
            return (bits[idx >> 6] & (1UL << (idx & 63))) != 0;
        }

        /// <summary>
        /// Pure C# PNG decoder returning bottom-up Unity Color32[] pixels (row 0 = v=0 bottom, row height-1 = v=1 top).
        /// Supports 8-bit RGBA (6), RGB (2), Gray+Alpha (4), Gray (0), and Indexed (3 with PLTE/tRNS).
        /// Works identically in RimWorld runtime and standalone headless .NET 10 tests without GPU calls.
        /// </summary>
        public static Color32[] DecodePngRgbaBottomUp(ReadOnlySpan<byte> pngBytes, out int width, out int height)
        {
            ReadOnlySpan<byte> sig = stackalloc byte[8] { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (pngBytes.Length < 33 || !pngBytes.Slice(0, 8).SequenceEqual(sig))
            {
                throw new InvalidDataException("Invalid PNG signature.");
            }

            width = 0;
            height = 0;
            byte bitDepth = 0;
            byte colorType = 0;
            byte interlace = 0;
            byte[] paletteRgb = null;
            byte[] trnsAlpha = null;

            using var idatStream = new MemoryStream();
            int pos = 8;
            while (pos + 12 <= pngBytes.Length)
            {
                int chunkLen = BinaryPrimitives.ReadInt32BigEndian(pngBytes.Slice(pos, 4));
                uint chunkType = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos + 4, 4));
                pos += 8;
                if (chunkLen < 0 || pos + chunkLen + 4 > pngBytes.Length)
                {
                    break;
                }
                ReadOnlySpan<byte> chunkData = pngBytes.Slice(pos, chunkLen);
                pos += chunkLen + 4; // skip CRC

                switch (chunkType)
                {
                    case 0x49484452u: // IHDR
                        width = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(0, 4));
                        height = BinaryPrimitives.ReadInt32BigEndian(chunkData.Slice(4, 4));
                        bitDepth = chunkData[8];
                        colorType = chunkData[9];
                        interlace = chunkData[12];
                        break;
                    case 0x504C5445u: // PLTE
                        paletteRgb = chunkData.ToArray();
                        break;
                    case 0x74524E53u: // tRNS
                        trnsAlpha = chunkData.ToArray();
                        break;
                    case 0x49444154u: // IDAT
                        idatStream.Write(chunkData);
                        break;
                    case 0x49454E44u: // IEND
                        pos = pngBytes.Length;
                        break;
                }
            }

            if (width <= 0 || height <= 0 || bitDepth != 8 || interlace != 0)
            {
                throw new NotSupportedException($"PNG format unsupported for direct RGBA decode ({width}x{height}, bitDepth={bitDepth}, interlace={interlace}).");
            }

            int bpp = colorType switch
            {
                6 => 4, // RGBA
                2 => 3, // RGB
                4 => 2, // Gray + Alpha
                0 => 1, // Gray
                3 => 1, // Indexed
                _ => throw new NotSupportedException($"PNG colorType {colorType} unsupported.")
            };

            idatStream.Position = 0;
            int stride = width * bpp;
            byte[] rawScanlines = new byte[(stride + 1) * height];
            using (var zlib = new ZLibStream(idatStream, CompressionMode.Decompress))
            {
                int totalRead = 0;
                while (totalRead < rawScanlines.Length)
                {
                    int r = zlib.Read(rawScanlines, totalRead, rawScanlines.Length - totalRead);
                    if (r <= 0) break;
                    totalRead += r;
                }
            }

            byte[] prevRow = new byte[stride];
            byte[] curRow = new byte[stride];
            var bottomUp = new Color32[width * height];

            for (int yTop = 0; yTop < height; yTop++)
            {
                int srcLineStart = yTop * (stride + 1);
                byte filter = rawScanlines[srcLineStart];
                ReadOnlySpan<byte> raw = rawScanlines.AsSpan(srcLineStart + 1, stride);

                for (int i = 0; i < stride; i++)
                {
                    byte xByte = raw[i];
                    byte a = i >= bpp ? curRow[i - bpp] : (byte)0;
                    byte b = prevRow[i];
                    byte c = i >= bpp ? prevRow[i - bpp] : (byte)0;

                    curRow[i] = filter switch
                    {
                        0 => xByte,
                        1 => (byte)(xByte + a),
                        2 => (byte)(xByte + b),
                        3 => (byte)(xByte + ((a + b) >> 1)),
                        4 => (byte)(xByte + PaethPredictor(a, b, c)),
                        _ => xByte
                    };
                }

                int yBottom = height - 1 - yTop;
                int dstRowOffset = yBottom * width;
                switch (colorType)
                {
                    case 6: // RGBA
                        for (int x = 0; x < width; x++)
                        {
                            int o = x * 4;
                            bottomUp[dstRowOffset + x] = new Color32(curRow[o], curRow[o + 1], curRow[o + 2], curRow[o + 3]);
                        }
                        break;
                    case 2: // RGB
                        for (int x = 0; x < width; x++)
                        {
                            int o = x * 3;
                            bottomUp[dstRowOffset + x] = new Color32(curRow[o], curRow[o + 1], curRow[o + 2], 255);
                        }
                        break;
                    case 4: // Gray + Alpha
                        for (int x = 0; x < width; x++)
                        {
                            int o = x * 2;
                            byte g = curRow[o];
                            bottomUp[dstRowOffset + x] = new Color32(g, g, g, curRow[o + 1]);
                        }
                        break;
                    case 3: // Indexed
                        for (int x = 0; x < width; x++)
                        {
                            int idx = curRow[x];
                            byte r = 0, g = 0, b = 0;
                            if (paletteRgb != null && idx * 3 + 2 < paletteRgb.Length)
                            {
                                r = paletteRgb[idx * 3];
                                g = paletteRgb[idx * 3 + 1];
                                b = paletteRgb[idx * 3 + 2];
                            }
                            byte al = (trnsAlpha != null && idx < trnsAlpha.Length) ? trnsAlpha[idx] : (byte)255;
                            bottomUp[dstRowOffset + x] = new Color32(r, g, b, al);
                        }
                        break;
                    default: // Gray (0)
                        for (int x = 0; x < width; x++)
                        {
                            byte g = curRow[x];
                            bottomUp[dstRowOffset + x] = new Color32(g, g, g, 255);
                        }
                        break;
                }

                (prevRow, curRow) = (curRow, prevRow);
            }

            return bottomUp;
        }

        public static Color32[] DecodePngRgbaBottomUpFromFile(string filePath, out int width, out int height)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            return DecodePngRgbaBottomUp(bytes, out width, out height);
        }

        /// <summary>
        /// Pure C# PNG decoder supporting standard 8-bit RGBA (colorType 6), RGB (2), Grayscale+Alpha (4), and Grayscale (0).
        /// Works identically in RimWorld runtime and standalone headless .NET 10 tests without GPU calls.
        /// </summary>
        public static TextureAlphaMask FromPngBytes(ReadOnlySpan<byte> pngBytes, byte alphaThreshold = 32)
        {
            Color32[] rgba = DecodePngRgbaBottomUp(pngBytes, out int width, out int height);
            byte[] bottomUpAlpha = new byte[rgba.Length];
            for (int i = 0; i < rgba.Length; i++)
            {
                bottomUpAlpha[i] = rgba[i].a;
            }
            return new TextureAlphaMask(width, height, alphaThreshold, bottomUpAlpha);
        }

        public static TextureAlphaMask FromPngFile(string filePath, byte alphaThreshold = 32)
        {
            byte[] bytes = File.ReadAllBytes(filePath);
            return FromPngBytes(bytes, alphaThreshold);
        }

        /// <summary>
        /// Safely reads bottom-up RGBA32 pixels from a Texture2D, falling back to a main-thread
        /// RenderTexture blit + ReadPixels copy whenever the source texture is GPU-only (!isReadable),
        /// such as .dds textures, AssetBundle textures, or 16-bit PNGs loaded with makeNoLongerReadable.
        /// </summary>
        public static Color32[] ReadPixels32Safe(Texture2D tex)
        {
            if (tex == null)
            {
                throw new ArgumentNullException(nameof(tex));
            }

            int w = tex.width;
            int h = tex.height;
            if (w <= 0 || h <= 0)
            {
                return Array.Empty<Color32>();
            }

            if (tex.isReadable)
            {
                try
                {
                    return tex.GetPixels32();
                }
                catch (Exception) when (UnityData.IsInMainThread)
                {
                    // Fall through to GPU RenderTexture readback if direct CPU access failed.
                }
            }

            if (!UnityData.IsInMainThread)
            {
                throw new InvalidOperationException($"Texture '{tex.name}' is not readable off the main thread.");
            }

            RenderTexture previousActive = RenderTexture.active;
            RenderTexture tempRt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Texture2D readableCopy = null;
            try
            {
                Graphics.Blit(tex, tempRt);
                RenderTexture.active = tempRt;
                readableCopy = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: false);
                readableCopy.ReadPixels(new Rect(0f, 0f, w, h), 0, 0);
                readableCopy.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return readableCopy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (readableCopy != null)
                {
                    UnityEngine.Object.DestroyImmediate(readableCopy);
                }
                if (tempRt != null)
                {
                    RenderTexture.ReleaseTemporary(tempRt);
                }
            }
        }

        public static TextureAlphaMask FromTexture2D(Texture2D tex, byte alphaThreshold = 32)
        {
            Color32[] pixels = ReadPixels32Safe(tex);
            int w = tex.width;
            int h = tex.height;
            byte[] alpha = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                alpha[i] = pixels[i].a;
            }
            return new TextureAlphaMask(w, h, alphaThreshold, alpha);
        }

        private static int PaethPredictor(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }
    }

    public static class GiantAlphaMaskCache
    {
        private readonly record struct MaskKey(string TexPath, byte AlphaThreshold);
        private static readonly ConcurrentDictionary<MaskKey, TextureAlphaMask> Cache = new();
        private static readonly ConcurrentDictionary<MaskKey, byte> MissingCache = new();

        public static TextureAlphaMask GetOrLoad(string texPath, byte alphaThreshold = 32)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return null;
            }

            var key = new MaskKey(texPath, alphaThreshold);
            if (Cache.TryGetValue(key, out TextureAlphaMask existing))
            {
                return existing;
            }
            if (MissingCache.ContainsKey(key))
            {
                return null;
            }

            TextureAlphaMask loaded = TryLoadFromModDisk(texPath, alphaThreshold);
            if (loaded == null && UnityData.IsInMainThread)
            {
                Texture2D tex = ContentFinder<Texture2D>.Get(texPath, reportFailure: false);
                if (tex != null)
                {
                    try
                    {
                        loaded = TextureAlphaMask.FromTexture2D(tex, alphaThreshold);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[GiantCreatureFramework] Fallback GetPixels32 failed for '{texPath}': {ex.Message}");
                    }
                }
            }

            if (loaded != null)
            {
                Cache[key] = loaded;
                MissingCache.TryRemove(key, out _);
            }
            else if (UnityData.IsInMainThread)
            {
                // Only cache negative miss once both disk and Unity ContentFinder fallback have been tried on main thread.
                MissingCache[key] = 1;
            }
            return loaded;
        }

        public static void RegisterMask(string texPath, byte alphaThreshold, TextureAlphaMask mask)
        {
            if (!string.IsNullOrEmpty(texPath) && mask != null)
            {
                var key = new MaskKey(texPath, alphaThreshold);
                Cache[key] = mask;
                MissingCache.TryRemove(key, out _);
            }
        }

        public static void Invalidate(string texPath)
        {
            if (string.IsNullOrEmpty(texPath))
            {
                return;
            }
            foreach (MaskKey key in Cache.Keys)
            {
                if (string.Equals(key.TexPath, texPath, StringComparison.Ordinal))
                {
                    Cache.TryRemove(key, out _);
                }
            }
            foreach (MaskKey key in MissingCache.Keys)
            {
                if (string.Equals(key.TexPath, texPath, StringComparison.Ordinal))
                {
                    MissingCache.TryRemove(key, out _);
                }
            }
        }

        public static bool TryResolveModTexturePngPath(string texPath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrEmpty(texPath))
            {
                return false;
            }
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
            if (mods == null)
            {
                return false;
            }

            string relPng = texPath.Replace('/', Path.DirectorySeparatorChar) + ".png";
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                ModContentPack mod = mods[i];
                if (mod == null) continue;
                List<string> folders = mod.foldersToLoadDescendingOrder;
                if (folders != null && folders.Count > 0)
                {
                    for (int f = 0; f < folders.Count; f++)
                    {
                        string candidate = Path.Combine(folders[f], "Textures", relPng);
                        if (File.Exists(candidate))
                        {
                            fullPath = candidate;
                            return true;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(mod.RootDir))
                {
                    string candidate = Path.Combine(mod.RootDir, "Textures", relPng);
                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        public static bool TryLoadRgbaPixels(string texPath, out Color32[] pixels, out int width, out int height)
        {
            pixels = null;
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(texPath))
            {
                return false;
            }

            if (TryResolveModTexturePngPath(texPath, out string pngPath))
            {
                try
                {
                    pixels = TextureAlphaMask.DecodePngRgbaBottomUpFromFile(pngPath, out width, out height);
                    if (pixels != null && width > 0 && height > 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[GiantCreatureFramework] Direct PNG RGBA decode failed on '{pngPath}': {ex.Message}");
                }
            }

            if (UnityData.IsInMainThread)
            {
                Texture2D tex = ContentFinder<Texture2D>.Get(texPath, reportFailure: false);
                if (tex != null)
                {
                    try
                    {
                        pixels = TextureAlphaMask.ReadPixels32Safe(tex);
                        width = tex.width;
                        height = tex.height;
                        return pixels != null && width > 0 && height > 0;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[GiantCreatureFramework] Fallback GPU/CPU RGBA readback failed for '{texPath}': {ex.Message}");
                    }
                }
            }

            return false;
        }

        private static TextureAlphaMask TryLoadFromModDisk(string texPath, byte alphaThreshold)
        {
            if (TryResolveModTexturePngPath(texPath, out string candidate))
            {
                try
                {
                    return TextureAlphaMask.FromPngFile(candidate, alphaThreshold);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[GiantCreatureFramework] Direct PNG alpha decode failed on '{candidate}': {ex.Message}");
                }
            }
            return null;
        }
    }
}
