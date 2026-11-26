using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GiantCreatureFramework.Agent.Imaging;
using GiantCreatureFramework.Agent.Injection;
using RimWorld;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Authoring;

/// <summary>
/// Exposes the 14 giant-creature authoring tools backed by 1.7/AgentSkills/tools.json:
/// list_manuals, load_manual, list_workspace, read_creature_xml, write_creature_xml,
/// generate_locomotion_anim, generate_image, extract_sheet, warp_bone, preview_sheet,
/// preview_anim_strip, validate_creature, inject_creature, spawn_creature.
/// </summary>
public sealed class AuthoringToolProvider : IAgentToolProvider
{
    private static readonly string[] ToolNames =
    [
        "list_manuals",
        "load_manual",
        "list_workspace",
        "read_creature_xml",
        "write_creature_xml",
        "generate_locomotion_anim",
        "generate_image",
        "extract_sheet",
        "warp_bone",
        "preview_sheet",
        "preview_anim_strip",
        "validate_creature",
        "inject_creature",
        "spawn_creature"
    ];

    private readonly string? _creaturesRootOverride;
    private readonly ImageGenerationClient? _imageClientOverride;

    public AuthoringToolProvider() : this(null, null)
    {
    }

    internal AuthoringToolProvider(
        string? creaturesRootOverride,
        ImageGenerationClient? imageClientOverride = null)
    {
        _creaturesRootOverride = creaturesRootOverride;
        _imageClientOverride = imageClientOverride;
    }

    internal string ResolveCreaturesRootPath() =>
        _creaturesRootOverride ?? AgentContentRoot.RootPath;

    private CreatureWorkspace OpenWorkspace() =>
        new(ResolveCreaturesRootPath());

    public IReadOnlyList<JsonObject> GetSchemas()
    {
        var schemas = AgentSkillStore.Current.ToolSchemas;
        var list = new List<JsonObject>(ToolNames.Length);
        foreach (var name in ToolNames)
        {
            if (!schemas.TryGetValue(name, out var schema))
            {
                throw new KeyNotFoundException($"Missing tool schema '{name}' in tools.json.");
            }
            list.Add((JsonObject)schema.DeepClone());
        }
        return list;
    }

    public bool CanExecute(string toolName) => ToolNames.Contains(toolName, StringComparer.Ordinal);

    public string? GetDisplayLabelKey(string toolName) => toolName switch
    {
        "list_manuals" => "GCFAgent_ToolLabel_ListManuals",
        "load_manual" => "GCFAgent_ToolLabel_LoadManual",
        "list_workspace" => "GCFAgent_ToolLabel_ListWorkspace",
        "read_creature_xml" => "GCFAgent_ToolLabel_ReadCreatureXml",
        "write_creature_xml" => "GCFAgent_ToolLabel_WriteCreatureXml",
        "generate_locomotion_anim" => "GCFAgent_ToolLabel_GenerateLocomotionAnim",
        "generate_image" => "GCFAgent_ToolLabel_GenerateImage",
        "extract_sheet" => "GCFAgent_ToolLabel_ExtractSheet",
        "warp_bone" => "GCFAgent_ToolLabel_WarpBone",
        "preview_sheet" => "GCFAgent_ToolLabel_PreviewSheet",
        "preview_anim_strip" => "GCFAgent_ToolLabel_PreviewAnimStrip",
        "validate_creature" => "GCFAgent_ToolLabel_ValidateCreature",
        "inject_creature" => "GCFAgent_ToolLabel_InjectCreature",
        "spawn_creature" => "GCFAgent_ToolLabel_SpawnCreature",
        _ => null
    };

    public int? GetMaxResultCharsOverride(string toolName) => toolName switch
    {
        "load_manual" => 0,
        "read_creature_xml" => 0,
        _ => null
    };

    public async Task<string> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserImg2ImgGuard.EnforceToolExecutionGate(toolName);

        switch (toolName)
        {
            case "list_manuals":
                return ExecuteListManuals();

            case "load_manual":
                return ExecuteLoadManual(arguments);

            case "list_workspace":
                return ExecuteListWorkspace(arguments);

            case "read_creature_xml":
                return ExecuteReadCreatureXml(arguments);

            case "write_creature_xml":
                return ExecuteWriteCreatureXml(arguments);

            case "generate_locomotion_anim":
                return await Task.Run(() => ExecuteGenerateLocomotionAnim(arguments), cancellationToken).ConfigureAwait(false);

            case "generate_image":
                return await ExecuteGenerateImageAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "extract_sheet":
                return await Task.Run(() => ExecuteExtractSheet(arguments, cancellationToken), cancellationToken).ConfigureAwait(false);

            case "warp_bone":
                return await Task.Run(() => ExecuteWarpBone(arguments, cancellationToken), cancellationToken).ConfigureAwait(false);

            case "preview_sheet":
                return await Task.Run(() => ExecutePreviewSheet(arguments, cancellationToken), cancellationToken).ConfigureAwait(false);

            case "preview_anim_strip":
                return await Task.Run(() => ExecutePreviewAnimStrip(arguments, cancellationToken), cancellationToken).ConfigureAwait(false);

            case "validate_creature":
                return await ExecuteValidateCreatureAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "inject_creature":
                return await ExecuteInjectCreatureAsync(arguments, cancellationToken).ConfigureAwait(false);

            case "spawn_creature":
                return await ExecuteSpawnCreatureAsync(arguments, cancellationToken).ConfigureAwait(false);

            default:
                throw new ArgumentException(AgentProtocol.Get("Registry_UnknownToolPrefix") + toolName);
        }
    }

    private static string ExecuteListManuals() =>
        ModManualCatalog.ListManualsJson();

    private static string ExecuteLoadManual(JsonElement args) =>
        ModManualCatalog.LoadManualJson(GetString(args, "manualName"));

    private string ExecuteListWorkspace(JsonElement args)
    {
        var ws = OpenWorkspace();
        var creatureName = GetOptionalString(args, "creatureName")?.Trim();
        if (string.IsNullOrWhiteSpace(creatureName))
        {
            var all = ws.ListAllCreatures();
            return JsonSerializer.Serialize(new
            {
                creatureCount = all.Count,
                creatures = all.Select(c => new
                {
                    creatureName = c.CreatureName,
                    defFileCount = c.DefFileCount,
                    textureFileCount = c.TextureFileCount,
                    workFileCount = c.WorkFileCount
                }).ToArray()
            }, Evidence.UnescapedJsonOptions);
        }

        var listing = ws.ListCreatureFiles(creatureName);
        return JsonSerializer.Serialize(new
        {
            creatureName = listing.CreatureName,
            defFiles = listing.DefFiles,
            textureFiles = listing.TextureFiles,
            workFiles = listing.WorkFiles
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecuteReadCreatureXml(JsonElement args)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var relPath = GetString(args, "relativeXmlPath");
        var content = ws.ReadCreatureXmlWithLineNumbers(creatureName, relPath, out var canonicalRel, out var lineCount);
        return JsonSerializer.Serialize(new
        {
            creatureName = creatureName.Trim(),
            relativeXmlPath = canonicalRel,
            lineCount,
            xmlWithLineNumbers = content
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecuteWriteCreatureXml(JsonElement args)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var relPath = GetString(args, "relativeXmlPath");
        var xmlContent = GetString(args, "xmlContent");
        var (_, savedRel, defCount) = ws.WriteCreatureXml(creatureName, relPath, xmlContent);
        return JsonSerializer.Serialize(new
        {
            written = true,
            creatureName = creatureName.Trim(),
            relativeXmlPath = savedRel,
            defElementCount = defCount
        }, Evidence.UnescapedJsonOptions);
    }

    private async Task<string> ExecuteGenerateImageAsync(JsonElement args, CancellationToken cancellationToken)
    {
        if (_creaturesRootOverride == null && !GiantCreatureAgentMod.IsTerminalEnabled)
        {
            throw new OperationCanceledException("Authoring terminal is disabled.");
        }

        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var prompt = GetString(args, "prompt");
        var outputPath = GetString(args, "outputPath");
        var size = GetOptionalString(args, "size") ?? "1024x1024";

        // 1. Strict 4-section built-in Style 1 prompt validation & poison-word interception
        BuiltInPromptGuard.ValidateOrThrow(prompt);

        // 2. Sandbox output & strictly provenance-verified self-generated reference images only
        var creatureDir = ws.GetCreatureDir(creatureName, createIfMissing: true);
        var outFull = ws.ResolvePngPath(creatureName, outputPath, requireUnderTextures: false, createParentDirs: true);
        var refs = new List<ImageReferenceFile>();
        if (args.TryGetProperty("referencePaths", out var refArr) && refArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refArr.EnumerateArray())
            {
                var rp = item.GetString();
                if (string.IsNullOrWhiteSpace(rp)) continue;
                var refFull = ws.ResolvePngPath(creatureName, rp, requireUnderTextures: false, createParentDirs: false);
                if (!File.Exists(refFull))
                {
                    throw new FileNotFoundException($"Workspace reference image not found: {rp}", refFull);
                }
                var relRef = ws.ToRelativeWorkspacePath(creatureName, refFull);
                var refBytes = await File.ReadAllBytesAsync(refFull, cancellationToken).ConfigureAwait(false);
                UserImg2ImgGuard.VerifySelfGeneratedPngOrThrow(creatureDir, relRef, refBytes, requireUnderWorkDir: true);
                refs.Add(new ImageReferenceFile(Path.GetFileName(refFull), refBytes));
            }
        }

        var settings = GiantCreatureAgentMod.Settings;
        var imgConfig = new ImageEndpointConfig(
            BaseUrl: settings.ImageApiEndpoint,
            ApiKey: settings.ImageApiKey,
            Model: settings.ImageModelName,
            TimeoutSeconds: settings.ImageRequestTimeoutSeconds);

        var client = _imageClientOverride ?? new ImageGenerationClient(
            nonPngDecoder: bytes => DecodeNonPngOnMainThreadAsync(bytes, cancellationToken));

        var payload = await client.GenerateAsync(
            imgConfig,
            prompt,
            size,
            refs.Count > 0 ? refs : null,
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllBytesAsync(outFull, payload.PngBytes, cancellationToken).ConfigureAwait(false);

        var relOut = ws.ToRelativeWorkspacePath(creatureName, outFull);
        UserImg2ImgGuard.RecordSelfGeneratedPng(creatureDir, relOut, payload.PngBytes, payload.UsedEdits ? "edits" : "generations");
        var inspectionDataUrl = BuildInspectionDataUrl(PngCodec.Decode(payload.PngBytes), maxDim: 768);
        return JsonSerializer.Serialize(new
        {
            generated = true,
            creatureName = creatureName.Trim(),
            outputPath = relOut,
            width = payload.Width,
            height = payload.Height,
            usedEdits = payload.UsedEdits,
            imageUsage = payload.Usage == null ? null : new
            {
                inputTokens = payload.Usage.InputTokens,
                outputTokens = payload.Usage.OutputTokens,
                imageOutputTokens = payload.Usage.ImageOutputTokens,
                totalTokens = payload.Usage.TotalTokens
            },
            _attachedImageDataUrl = inspectionDataUrl,
            _attachedImageLabel = $"{creatureName.Trim()}/{relOut}"
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecuteExtractSheet(JsonElement args, CancellationToken cancellationToken)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var inputPath = GetString(args, "inputPath");
        var outputDir = GetString(args, "outputDir");
        var columns = GetInt(args, "columns", 0);
        var fillAllHoles = GetBool(args, "fillAllHoles", false);
        var previewRelArg = GetOptionalString(args, "previewPath");

        var creatureDir = ws.GetCreatureDir(creatureName, createIfMissing: false);
        var inFull = ws.ResolvePngPath(creatureName, inputPath);
        if (!File.Exists(inFull))
        {
            throw new FileNotFoundException($"Sheet PNG not found: {inputPath}", inFull);
        }
        var inBytes = File.ReadAllBytes(inFull);
        UserImg2ImgGuard.VerifySelfGeneratedPngOrThrow(creatureDir, ws.ToRelativeWorkspacePath(creatureName, inFull), inBytes, requireUnderWorkDir: true);

        var outDirFull = ws.ResolveWorkspacePath(creatureName, outputDir, createParentDirs: true);
        Directory.CreateDirectory(outDirFull);

        var defaultPreviewRel = outputDir.Replace('\\', '/').TrimEnd('/') + "/preview.png";
        var previewFull = ws.ResolvePngPath(
            creatureName,
            string.IsNullOrWhiteSpace(previewRelArg) ? defaultPreviewRel : previewRelArg!,
            requireUnderTextures: false,
            createParentDirs: true);

        cancellationToken.ThrowIfCancellationRequested();
        var sheet = PngCodec.Decode(inBytes);
        var extracted = SpriteSheetExtractor.ExtractSheet(
            sheet,
            columns: columns > 0 ? columns : null,
            fillAllHoles: fillAllHoles);

        var partInfos = new List<object>(extracted.Count);
        var partImages = new List<RgbaImage>(Math.Max(1, extracted.Count));
        var partLabels = new List<string>(Math.Max(1, extracted.Count));

        foreach (var part in extracted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partFile = Path.Combine(outDirFull, $"part_{part.Index}.png");
            var partPngBytes = PngCodec.Encode(part.Image);
            File.WriteAllBytes(partFile, partPngBytes);
            var relPart = ws.ToRelativeWorkspacePath(creatureName, partFile);
            UserImg2ImgGuard.RecordSelfGeneratedPng(creatureDir, relPart, partPngBytes, "extracted_part");
            partImages.Add(part.Image);
            partLabels.Add($"part_{part.Index} ({part.Image.Width}x{part.Image.Height})");
            partInfos.Add(new
            {
                index = part.Index,
                path = relPart,
                width = part.Image.Width,
                height = part.Image.Height,
                columnBox = new
                {
                    x0 = part.BoundingBox.X0,
                    y0 = part.BoundingBox.Y0,
                    x1 = part.BoundingBox.X1,
                    y1 = part.BoundingBox.Y1
                },
                cropOffset = new
                {
                    x = part.CropOffset.X,
                    y = part.CropOffset.Y
                }
            });
        }

        var previewImg = partImages.Count > 0
            ? PreviewRenderer.RenderCheckerboardPreview(partImages, partLabels, tileSize: 256, cols: Math.Clamp(partImages.Count, 1, 4))
            : PreviewRenderer.RenderCheckerboardPreview([sheet], ["empty_sheet"], tileSize: 256, cols: 1);
        var previewBytes = PngCodec.Encode(previewImg);
        File.WriteAllBytes(previewFull, previewBytes);
        var relPreview = ws.ToRelativeWorkspacePath(creatureName, previewFull);

        return JsonSerializer.Serialize(new
        {
            extracted = true,
            creatureName = creatureName.Trim(),
            inputPath = ws.ToRelativeWorkspacePath(creatureName, inFull),
            partCount = extracted.Count,
            parts = partInfos,
            previewPath = relPreview,
            _attachedImageDataUrl = BuildInspectionDataUrl(previewImg, maxDim: 860, preEncodedBytes: previewBytes),
            _attachedImageLabel = $"{creatureName.Trim()}/{relPreview}"
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecuteWarpBone(JsonElement args, CancellationToken cancellationToken)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var inputPath = GetString(args, "inputPath");
        var outputPath = GetString(args, "outputPath");
        var mirrorOutputPath = GetOptionalString(args, "mirrorOutputPath");
        var srcPivotStr = GetString(args, "srcPivot");
        var dstPivotStr = GetOptionalString(args, "dstPivot") ?? "256,400";
        var scaleStr = GetOptionalString(args, "scale") ?? "1.0,1.0";
        var angleDeg = GetInt(args, "angleDeg", 0);
        var drawSizeStr = GetString(args, "drawSize");
        var outlineHex = GetOptionalString(args, "outlineHex") ?? "#14161A";
        var maxBlackStr = GetOptionalString(args, "maxInteriorBlackPct") ?? "1.0";

        var creatureDir = ws.GetCreatureDir(creatureName, createIfMissing: false);
        var inFull = ws.ResolvePngPath(creatureName, inputPath);
        if (!File.Exists(inFull))
        {
            throw new FileNotFoundException($"Source sprite not found: {inputPath}", inFull);
        }
        var inBytes = File.ReadAllBytes(inFull);
        UserImg2ImgGuard.VerifySelfGeneratedPngOrThrow(creatureDir, ws.ToRelativeWorkspacePath(creatureName, inFull), inBytes, requireUnderWorkDir: true);

        var outFull = ws.ResolvePngPath(creatureName, outputPath, requireUnderTextures: true, createParentDirs: true);
        var mirrorFull = !string.IsNullOrWhiteSpace(mirrorOutputPath)
            ? ws.ResolvePngPath(creatureName, mirrorOutputPath!, requireUnderTextures: true, createParentDirs: true)
            : null;

        var srcPivot = ParsePair(srcPivotStr, nameof(srcPivotStr));
        var dstPivot = ParsePair(dstPivotStr, nameof(dstPivotStr));
        var scale = ParseScale(scaleStr);
        var drawSizeMax = ParseMaxDrawSize(drawSizeStr);
        var maxInteriorBlackPct = ParseDouble(maxBlackStr, 1.0);

        var namedSockets = new List<NamedPoint>();
        if (args.TryGetProperty("sockets", out var sockArr) && sockArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sockArr.EnumerateArray())
            {
                var raw = item.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var colon = raw.IndexOf(':');
                if (colon <= 0 || colon >= raw.Length - 1)
                {
                    throw new ArgumentException($"Invalid socket specification '{raw}'; expected 'name:x,y'.");
                }
                var sName = raw[..colon].Trim();
                var (sx, sy) = ParsePair(raw[(colon + 1)..], "sockets");
                namedSockets.Add(new NamedPoint(sName, sx, sy));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var srcImg = PngCodec.Decode(inBytes);
        var result = BoneWarper.WarpBone(
            srcImg,
            srcPivot,
            dstPivot,
            scale,
            angleDeg,
            canvasSize: 512,
            sockets: namedSockets,
            drawSize: drawSizeMax,
            outlineHex: outlineHex,
            emitMirror: mirrorFull != null);

        if (maxInteriorBlackPct >= 0.0 &&
            result.Quality.DeepInteriorPx >= 200 &&
            result.Quality.InteriorBlackPct > maxInteriorBlackPct)
        {
            throw new InvalidOperationException(AgentProtocol.Format(
                "Authoring_InteriorBlackExceeded",
                result.Quality.InteriorBlackPct.ToString("F2", CultureInfo.InvariantCulture),
                maxInteriorBlackPct.ToString("F2", CultureInfo.InvariantCulture)));
        }

        var outBytes = PngCodec.Encode(result.Image);
        File.WriteAllBytes(outFull, outBytes);
        var relOutPath = ws.ToRelativeWorkspacePath(creatureName, outFull);
        UserImg2ImgGuard.RecordSelfGeneratedPng(creatureDir, relOutPath, outBytes, "warped_bone");
        string? mirrorTexPath = null;
        string? mirrorRelPath = null;
        if (mirrorFull != null && result.MirrorImage != null)
        {
            var mirrorBytes = PngCodec.Encode(result.MirrorImage);
            File.WriteAllBytes(mirrorFull, mirrorBytes);
            mirrorTexPath = ws.ToRimWorldTexPath(creatureName, mirrorFull);
            mirrorRelPath = ws.ToRelativeWorkspacePath(creatureName, mirrorFull);
            UserImg2ImgGuard.RecordSelfGeneratedPng(creatureDir, mirrorRelPath, mirrorBytes, "warped_bone_mirror");
        }

        int bx0 = 512, by0 = 512, bx1 = -1, by1 = -1, clippedEdgePx = 0;
        for (int py = 0; py < result.Image.Height; py++)
        {
            for (int px = 0; px < result.Image.Width; px++)
            {
                if (result.Image.GetA(px, py) >= 32)
                {
                    if (px < bx0) bx0 = px;
                    if (py < by0) by0 = py;
                    if (px > bx1) bx1 = px;
                    if (py > by1) by1 = py;
                    if (px == 0 || py == 0 || px == result.Image.Width - 1 || py == result.Image.Height - 1)
                    {
                        clippedEdgePx++;
                    }
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            warped = true,
            creatureName = creatureName.Trim(),
            outputPath = relOutPath,
            texPath = ws.ToRimWorldTexPath(creatureName, outFull),
            mirrorOutputPath = mirrorRelPath,
            mirrorTexPath,
            canvasSize = 512,
            opaqueBounds = bx1 >= bx0 ? new
            {
                x0 = bx0,
                y0 = by0,
                x1 = bx1,
                y1 = by1,
                widthPx = bx1 - bx0 + 1,
                heightPx = by1 - by0 + 1
            } : null,
            clippedEdgePixels = clippedEdgePx,
            outlineTargetPx = result.OutlineTargetPx,
            pivotPixel = new
            {
                x = result.PivotPixel.X,
                y = result.PivotPixel.Y,
                xml = FormatXmlVec2(result.PivotPixel)
            },
            mirrorPivotPixel = result.MirrorPivotPixel.HasValue
                ? new
                {
                    x = result.MirrorPivotPixel.Value.X,
                    y = result.MirrorPivotPixel.Value.Y,
                    xml = FormatXmlVec2(result.MirrorPivotPixel.Value)
                }
                : null,
            sockets = result.Sockets.ToDictionary(
                kv => kv.Key,
                kv => new { x = kv.Value.X, y = kv.Value.Y, xml = FormatXmlVec2(kv.Value) },
                StringComparer.Ordinal),
            mirrorSockets = result.MirrorSockets.ToDictionary(
                kv => kv.Key,
                kv => new { x = kv.Value.X, y = kv.Value.Y, xml = FormatXmlVec2(kv.Value) },
                StringComparer.Ordinal),
            quality = new
            {
                interiorBlackPct = result.Quality.InteriorBlackPct,
                interiorSeamPct = result.Quality.InteriorSeamPct,
                cleanFacetPct = result.Quality.CleanFacetPct,
                deepInteriorPx = result.Quality.DeepInteriorPx
            }
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecutePreviewSheet(JsonElement args, CancellationToken cancellationToken)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var outputPath = GetString(args, "outputPath");
        var tileSize = Math.Clamp(GetInt(args, "tileSize", 256), 64, 512);

        var images = new List<RgbaImage>();
        var labels = new List<string>();
        if (args.TryGetProperty("inputPaths", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rel = el.GetString();
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var full = ws.ResolvePngPath(creatureName, rel);
                if (!File.Exists(full))
                {
                    throw new FileNotFoundException($"Input PNG not found: {rel}", full);
                }
                images.Add(PngCodec.Decode(File.ReadAllBytes(full)));
                labels.Add(Path.GetFileNameWithoutExtension(full));
            }
        }

        if (images.Count == 0)
        {
            throw new ArgumentException("inputPaths must contain at least one PNG path.");
        }

        var outFull = ws.ResolvePngPath(creatureName, outputPath, requireUnderTextures: false, createParentDirs: true);
        var preview = PreviewRenderer.RenderCheckerboardPreview(
            images,
            labels,
            tileSize: tileSize,
            cols: Math.Clamp(images.Count, 1, 4));
        var encoded = PngCodec.Encode(preview);
        File.WriteAllBytes(outFull, encoded);
        var relOut = ws.ToRelativeWorkspacePath(creatureName, outFull);

        return JsonSerializer.Serialize(new
        {
            rendered = true,
            creatureName = creatureName.Trim(),
            imageCount = images.Count,
            outputPath = relOut,
            width = preview.Width,
            height = preview.Height,
            _attachedImageDataUrl = BuildInspectionDataUrl(preview, maxDim: 860, preEncodedBytes: encoded),
            _attachedImageLabel = $"{creatureName.Trim()}/{relOut}"
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecuteGenerateLocomotionAnim(JsonElement args)
    {
        var ws = OpenWorkspace();
        var req = new LocomotionAnimRequest
        {
            CreatureName = GetString(args, "creatureName"),
            RelativeXmlPath = GetString(args, "relativeXmlPath"),
            AnimDefName = GetString(args, "animDefName"),
            Preset = GetString(args, "preset"),
            RigDefName = GetOptionalString(args, "rigDefName"),
            DurationTicks = GetOptionalInt(args, "durationTicks"),
            AmplitudeScale = GetJsonDouble(args, "amplitudeScale", 1.0),
            ImpactTick = GetOptionalInt(args, "impactTick"),
            TriggerToolTag = GetOptionalString(args, "triggerToolTag"),
            IsolateSubtree = GetBool(args, "isolateSubtree", false),
            WingGroundedFold = GetBool(args, "wingGroundedFold", true),
            RootBoneId = GetOptionalString(args, "rootBoneId"),
            SpineChain = GetOptionalString(args, "spineChain"),
            NeckChain = GetOptionalString(args, "neckChain"),
            TailChain = GetOptionalString(args, "tailChain"),
            LeftWingChain = GetOptionalString(args, "leftWingChain"),
            RightWingChain = GetOptionalString(args, "rightWingChain"),
            LeftForeChain = GetOptionalString(args, "leftForeChain"),
            RightForeChain = GetOptionalString(args, "rightForeChain"),
            LeftHindChain = GetOptionalString(args, "leftHindChain"),
            RightHindChain = GetOptionalString(args, "rightHindChain"),
            MultiLegLeftChains = GetOptionalString(args, "multiLegLeftChains"),
            MultiLegRightChains = GetOptionalString(args, "multiLegRightChains")
        };

        var summary = ProceduralLocomotionGenerator.GenerateAndWrite(ws, req);
        return JsonSerializer.Serialize(new
        {
            generated = true,
            creatureName = summary.CreatureName,
            relativeXmlPath = summary.RelativeXmlPath,
            animDefName = summary.AnimDefName,
            preset = summary.Preset,
            durationTicks = summary.DurationTicks,
            loop = summary.Loop,
            isolatedSubtreeRootBoneId = summary.IsolatedSubtreeRootBoneId,
            trackCount = summary.TrackCount,
            totalKeyframes = summary.TotalKeyframes,
            animatedBoneIds = summary.AnimatedBoneIds,
            warnings = summary.Warnings
        }, Evidence.UnescapedJsonOptions);
    }

    private string ExecutePreviewAnimStrip(JsonElement args, CancellationToken cancellationToken)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var animDefName = GetString(args, "animDefName");
        var rigDefName = GetOptionalString(args, "rigDefName");
        var frameCount = GetInt(args, "frameCount", 6);
        var headingDeg = GetJsonDouble(args, "headingDeg", 180.0);
        var tileSize = GetInt(args, "tileSize", 320);
        var outputPath = GetOptionalString(args, "outputPath");
        var showJoints = GetBool(args, "showJoints", false);

        var report = AnimStripPreviewRenderer.RenderAnimStrip(
            ws,
            creatureName,
            animDefName,
            rigDefName,
            frameCount,
            headingDeg,
            tileSize,
            outputPath,
            showJoints,
            cancellationToken);

        return JsonSerializer.Serialize(new
        {
            rendered = true,
            creatureName = report.CreatureName,
            rigDefName = report.RigDefName,
            animDefName = report.AnimDefName,
            loop = report.Loop,
            durationTicks = report.DurationTicks,
            frameCount = report.FrameCount,
            headingDeg = report.HeadingDeg,
            outputPath = report.OutputPath,
            width = report.ImageWidth,
            height = report.ImageHeight,
            wingspanRangeCells = new { min = report.MinWingspanCells, max = report.MaxWingspanCells },
            lengthRangeCells = new { min = report.MinLengthCells, max = report.MaxLengthCells },
            totalJointGaps = report.TotalJointGaps,
            jointGapDetails = report.JointGapDetails,
            midlineCrossings = report.MidlineCrossings,
            frames = report.Frames.Select(f => new
            {
                tick = f.Tick,
                wingspanCells = f.WingspanCells,
                lengthCells = f.LengthCells,
                jointGapCount = f.JointGapCount
            }).ToArray(),
            _attachedImageDataUrl = BuildInspectionDataUrl(report.StripImage, maxDim: 960),
            _attachedImageLabel = $"{report.CreatureName}/{report.OutputPath}"
        }, Evidence.UnescapedJsonOptions);
    }

    private async Task<string> ExecuteValidateCreatureAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var ws = OpenWorkspace();
        var creatureName = GetString(args, "creatureName");
        var minOutlinePx = GetInt(args, "minOutlinePx", 6);
        var maxBlackRaw = GetOptionalString(args, "maxInteriorBlackPct");
        double? maxInteriorBlackPct = !string.IsNullOrWhiteSpace(maxBlackRaw) &&
            double.TryParse(maxBlackRaw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaxBlack)
            ? parsedMaxBlack
            : null;

        var creatureDir = ws.GetCreatureDir(creatureName, createIfMissing: false);
        if (!Directory.Exists(creatureDir))
        {
            throw new DirectoryNotFoundException($"Creature workspace does not exist: {creatureName}");
        }

        // 1. Offline pure-C# joint, outline, world-continuity, 1:1 BodyDef subtree & deathAnim audit
        var rigReports = await Task.Run(
            () => RigJointAuditor.AuditCreatureWorkspace(
                creatureDir,
                minOutlinePx: minOutlinePx,
                maxInteriorBlackPct: maxInteriorBlackPct),
            cancellationToken).ConfigureAwait(false);

        string? previewRelPath = null;
        string? attachedDataUrl = null;
        var firstPreview = rigReports.FirstOrDefault(r => r.PreviewImage != null)?.PreviewImage;
        if (firstPreview != null)
        {
            var previewFull = ws.ResolvePngPath(creatureName, "Work/validate_preview.png", requireUnderTextures: false, createParentDirs: true);
            var previewBytes = PngCodec.Encode(firstPreview);
            await File.WriteAllBytesAsync(previewFull, previewBytes, cancellationToken).ConfigureAwait(false);
            previewRelPath = ws.ToRelativeWorkspacePath(creatureName, previewFull);
            attachedDataUrl = BuildInspectionDataUrl(firstPreview, maxDim: 860, preEncodedBytes: previewBytes);
        }

        // 2. Runtime dry-run Def XML parse, cross-reference & ConfigErrors check (rolls back cleanly)
        InjectionReport? dryRunReport = null;
        if (_creaturesRootOverride == null && AgentContentRoot.Owner != null)
        {
            var pending = await MainThreadDispatcher.RunOnMainThreadAsync(
                () => GcfDefInjector.InjectAsync(dryRun: true, creatureName.Trim()),
                cancellationToken).ConfigureAwait(false);
            dryRunReport = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var rigFailures = rigReports.SelectMany(r => r.Failures).ToList();
        var defErrors = dryRunReport?.Errors.Select(e => new { code = e.Code, subject = e.Subject, detail = e.Detail }).ToArray() ?? [];
        var defWarnings = dryRunReport?.Warnings.Select(w => new { code = w.Code, subject = w.Subject, detail = w.Detail }).ToArray() ?? [];
        var allPassed = rigReports.Count > 0 && rigFailures.Count == 0 && defErrors.Length == 0;

        var response = new JsonObject
        {
            ["passed"] = allPassed,
            ["creatureName"] = creatureName.Trim(),
            ["previewPath"] = previewRelPath,
            ["rigAudits"] = JsonSerializer.SerializeToNode(rigReports.Select(r => new
            {
                rigDefName = r.RigDefName,
                passed = r.Passed,
                nodeCount = r.NodeCount,
                bridgeCount = r.BridgeCount,
                minOutlinePx = r.MinOutlinePx,
                maxInteriorBlackPct = r.MaxInteriorBlackPct,
                maxInteriorSeamPct = r.MaxInteriorSeamPct,
                matchedBodyDefName = r.MatchedBodyDefName,
                failures = r.Failures
            }), Evidence.UnescapedJsonOptions),
            ["defErrors"] = JsonSerializer.SerializeToNode(defErrors, Evidence.UnescapedJsonOptions),
            ["defWarnings"] = JsonSerializer.SerializeToNode(defWarnings, Evidence.UnescapedJsonOptions)
        };

        if (!string.IsNullOrEmpty(attachedDataUrl))
        {
            response["_attachedImageDataUrl"] = attachedDataUrl;
            response["_attachedImageLabel"] = $"{creatureName.Trim()}/{previewRelPath}";
        }

        return response.ToJsonString(Evidence.UnescapedJsonOptions);
    }

    private async Task<string> ExecuteInjectCreatureAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var creatureName = GetString(args, "creatureName").Trim();
        CreatureWorkspace.ValidateCreatureName(creatureName);

        var pending = await MainThreadDispatcher.RunOnMainThreadAsync(
            () => GcfDefInjector.InjectAsync(dryRun: false, creatureName),
            cancellationToken).ConfigureAwait(false);
        var report = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? primaryError = null;
        if (!report.Committed)
        {
            var first = report.Errors.FirstOrDefault();
            primaryError = first != null
                ? (!string.IsNullOrWhiteSpace(first.Detail) ? first.Detail : $"{first.Code}: {first.Subject}")
                : "Hot-injection rolled back.";
        }

        return JsonSerializer.Serialize(new
        {
            error = primaryError,
            committed = report.Committed,
            rolledBack = report.RolledBack,
            creatureName,
            added = report.Added,
            replaced = report.Replaced,
            implied = report.Implied,
            textures = report.Textures,
            errors = report.Errors.Select(e => new { code = e.Code, subject = e.Subject, detail = e.Detail }).ToArray(),
            warnings = report.Warnings.Select(w => new { code = w.Code, subject = w.Subject, detail = w.Detail }).ToArray()
        }, Evidence.UnescapedJsonOptions);
    }

    private static async Task<string> ExecuteSpawnCreatureAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var pawnKindDefName = GetString(args, "pawnKindDefName").Trim();
        var reqX = GetInt(args, "x", -1);
        var reqZ = GetInt(args, "z", -1);
        var factionMode = (GetOptionalString(args, "faction") ?? "wild").Trim().ToLowerInvariant();

        return await MainThreadDispatcher.RunOnMainThreadAsync(() =>
        {
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null)
            {
                throw new InvalidOperationException(AgentProtocol.Format("Authoring_SpawnFailed", pawnKindDefName, reqX, reqZ));
            }

            var map = Find.CurrentMap;
            var kind = DefDatabase<PawnKindDef>.GetNamedSilentFail(pawnKindDefName);
            if (kind == null)
            {
                throw new InvalidOperationException(AgentProtocol.Format("Authoring_SpawnFailed", pawnKindDefName, reqX, reqZ));
            }

            var rigProps = kind.race?.GetCompProperties<CompProperties_GiantRig>();
            var aqDef = rigProps?.rigDef?.ActiveAquaticLocomotion;
            var hasFlight = rigProps?.rigDef?.ActiveFlightLocomotion?.IsActive == true;

            IntVec3 spawnCell;
            if (aqDef != null && aqDef.IsWaterOnly && !hasFlight)
            {
                IntVec3 preferred = (reqX >= 0 && reqZ >= 0) ? new IntVec3(reqX, 0, reqZ) : map.Center;
                if (reqX >= 0 && reqZ >= 0 && GiantAquaticUtility.IsCellPassableForAquatic(map, preferred, null, null, aqDef))
                {
                    spawnCell = preferred;
                }
                else if (!GiantAquaticUtility.TryFindValidAquaticSpawnCell(map, rigProps!.rigDef, preferred, out spawnCell))
                {
                    throw new InvalidOperationException(
                        $"Cannot spawn WaterOnly unit '{pawnKindDefName}': current map has no eligible water cells matching its <aquaticLocomotion> rules.");
                }
            }
            else if (reqX >= 0 && reqZ >= 0)
            {
                spawnCell = new IntVec3(reqX, 0, reqZ);
            }
            else if (!CellFinder.TryFindRandomCellNear(
                         map.Center,
                         map,
                         20,
                         c => c.InBounds(map) && c.Standable(map) && !c.Fogged(map),
                         out spawnCell))
            {
                spawnCell = map.Center;
            }

            Faction? targetFaction = factionMode switch
            {
                "player" => Faction.OfPlayerSilentFail,
                "hostile" => Faction.OfEntities
                             ?? Find.FactionManager?.FirstFactionOfDef(FactionDefOf.Mechanoid)
                             ?? Find.FactionManager?.AllFactionsListForReading?.FirstOrDefault(f =>
                                 !f.IsPlayer && !f.defeated && Faction.OfPlayerSilentFail != null && f.HostileTo(Faction.OfPlayerSilentFail)),
                _ => null
            };

            var pawn = InjectedCreatureSpawner.Spawn(pawnKindDefName, spawnCell, targetFaction);
            if (pawn == null)
            {
                throw new InvalidOperationException(AgentProtocol.Format("Authoring_SpawnFailed", pawnKindDefName, spawnCell.x, spawnCell.z));
            }

            var comp = GiantFastLookup.TryGetRig(pawn);
            return JsonSerializer.Serialize(new
            {
                spawned = true,
                thingId = pawn.ThingID,
                defName = pawn.def.defName,
                pawnKindDefName = pawn.kindDef?.defName,
                position = new { x = pawn.Position.x, z = pawn.Position.z },
                faction = pawn.Faction?.Name ?? "Wild",
                rigDefName = comp?.RigDef?.defName,
                hitCells = comp?.Footprint?.hitCells.Count ?? 0,
                collisionCells = comp?.Footprint?.collisionCells.Count ?? 0,
                animState = comp?.CurrentAnimState.ToString() ?? "None"
            }, Evidence.UnescapedJsonOptions);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<RgbaImage> DecodeNonPngOnMainThreadAsync(byte[] rawBytes, CancellationToken cancellationToken)
    {
        return MainThreadDispatcher.RunOnMainThreadAsync(() =>
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(tex, rawBytes))
                {
                    throw new InvalidDataException("Unity ImageConversion.LoadImage could not decode image bytes.");
                }
                var w = tex.width;
                var h = tex.height;
                var c32 = tex.GetPixels32();
                var img = new RgbaImage(w, h);
                var dst = img.Pixels;
                for (var y = 0; y < h; y++)
                {
                    var srcRow = (h - 1 - y) * w;
                    var dstRow = y * w * 4;
                    for (var x = 0; x < w; x++)
                    {
                        var c = c32[srcRow + x];
                        var p = dstRow + x * 4;
                        dst[p] = c.r;
                        dst[p + 1] = c.g;
                        dst[p + 2] = c.b;
                        dst[p + 3] = c.a;
                    }
                }
                return img;
            }
            finally
            {
                UnityEngine.Object.Destroy(tex);
            }
        }, cancellationToken);
    }

    private static string BuildInspectionDataUrl(RgbaImage img, int maxDim = 768, byte[]? preEncodedBytes = null)
    {
        if (img.Width <= maxDim && img.Height <= maxDim && preEncodedBytes != null)
        {
            return "data:image/png;base64," + Convert.ToBase64String(preEncodedBytes);
        }

        var working = img;
        if (img.Width > maxDim || img.Height > maxDim)
        {
            var scale = Math.Min(maxDim / (double)img.Width, maxDim / (double)img.Height);
            var nw = Math.Max(1, (int)Math.Round(img.Width * scale));
            var nh = Math.Max(1, (int)Math.Round(img.Height * scale));
            working = PreviewRenderer.LanczosResize(img, nw, nh);
        }

        var png = PngCodec.Encode(working);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private static (double X, double Y) ParsePair(string raw, string paramName)
    {
        var parts = (raw ?? string.Empty).Trim().Trim('(', ')').Split(',');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            throw new ArgumentException($"Invalid coordinate pair for {paramName}: '{raw}'. Expected 'x,y'.");
        }
        return (x, y);
    }

    private static (double X, double Y) ParseScale(string raw)
    {
        var clean = (raw ?? "1.0,1.0").Trim().Trim('(', ')');
        if (!clean.Contains(','))
        {
            if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var uniform) || uniform <= 0.0)
            {
                throw new ArgumentException($"Invalid scale '{raw}'.");
            }
            return (uniform, uniform);
        }
        var (sx, sy) = ParsePair(clean, "scale");
        if (sx <= 0.0 || sy <= 0.0)
        {
            throw new ArgumentException($"Scale components must be positive, got '{raw}'.");
        }
        return (sx, sy);
    }

    private static double ParseMaxDrawSize(string raw)
    {
        var clean = (raw ?? string.Empty).Trim().Trim('(', ')');
        if (clean.Contains(','))
        {
            var (dx, dy) = ParsePair(clean, "drawSize");
            return Math.Max(dx, dy);
        }
        if (!double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0.0)
        {
            throw new ArgumentException($"Invalid drawSize '{raw}'.");
        }
        return d;
    }

    private static double ParseDouble(string? raw, double fallback) =>
        !string.IsNullOrWhiteSpace(raw) && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string FormatXmlVec2((double X, double Y) pt) =>
        string.Create(CultureInfo.InvariantCulture, $"({pt.X:0.#}, {pt.Y:0.#})");

    private static string GetString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static string? GetOptionalString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int GetInt(JsonElement args, string name, int fallback) =>
        args.TryGetProperty(name, out var el) && el.TryGetInt32(out var val) ? val : fallback;

    private static int? GetOptionalInt(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var val) ? val : null;

    private static double GetJsonDouble(JsonElement args, string name, double fallback)
    {
        if (!args.TryGetProperty(name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)) return sd;
        return fallback;
    }

    private static bool GetBool(JsonElement args, string name, bool fallback) =>
        args.TryGetProperty(name, out var el) && (el.ValueKind is JsonValueKind.True or JsonValueKind.False) ? el.GetBoolean() : fallback;
}
