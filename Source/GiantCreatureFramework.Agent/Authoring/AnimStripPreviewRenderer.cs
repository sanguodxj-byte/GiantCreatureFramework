using System.Globalization;
using System.Xml.Linq;
using GiantCreatureFramework.Agent.Imaging;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record AnimStripFrameDiagnostic(
    double Tick,
    double WingspanCells,
    double LengthCells,
    int JointGapCount);

public sealed record AnimStripReport(
    string CreatureName,
    string RigDefName,
    string AnimDefName,
    bool Loop,
    double DurationTicks,
    int FrameCount,
    double HeadingDeg,
    string OutputPath,
    int ImageWidth,
    int ImageHeight,
    double MinWingspanCells,
    double MaxWingspanCells,
    double MinLengthCells,
    double MaxLengthCells,
    int TotalJointGaps,
    IReadOnlyList<string> JointGapDetails,
    IReadOnlyList<string> MidlineCrossings,
    IReadOnlyList<AnimStripFrameDiagnostic> Frames,
    RgbaImage StripImage);

/// <summary>
/// Pure-C# multi-frame 3D skeletal animation contact-sheet renderer and dynamic continuity auditor.
/// Evaluates the exact same Catmull-Rom/Easing interpolation, coupled sagittal pitch foreshortening,
/// and 3D coronal flap foreshortening as SkeletalKinematics.SolveWorldPoses across evenly spaced frames
/// on a unified camera scale so the in-game Agent can visually and mathematically verify locomotion,
/// wing folding, and flap cycles prior to inject_creature.
/// </summary>
public static class AnimStripPreviewRenderer
{
    public static readonly HashSet<string> ValidEasings = new(StringComparer.Ordinal)
    {
        "Linear",
        "SmoothStep",
        "EaseInQuad",
        "EaseOutQuad",
        "EaseInCubic",
        "EaseOutCubic",
        "EaseOutBack",
        "Sine",
        "CatmullRom"
    };

    private sealed class Variant3D
    {
        public required RgbaImage Img { get; init; }
        public RgbaImage? SilImg { get; init; }
        public required int Res { get; init; }
        public required (double X, double Y) Ds { get; init; }
        public required (double U, double V) Piv { get; init; }
        public (double U, double V) Sock { get; set; }
        public string? RawSockPx { get; init; }
        public string? RawSockUv { get; init; }
        public required double LayerOffset { get; init; }
        public double SilLayer { get; init; } = -9.2;
        public bool HasSelfOverride { get; init; }
    }

    private sealed class Node3D
    {
        public required string Id { get; init; }
        public string? ParentId { get; init; }
        public int ParentIndex { get; set; } = -1;
        public int FirstArticulatedChildIndex { get; set; } = -1;
        public required Variant3D BaseVar { get; init; }
        public Variant3D? EastVar { get; set; }
        public Variant3D? WestVar { get; set; }
        public bool HasSelfVariant { get; set; }
        public string? SectorSourceBoneId { get; init; }
        public int SectorSourceIndex { get; set; } = -1;
        public double LateralHalfDeg { get; init; } = 45.0;
        public double BaseAngle { get; init; }
        public (double X, double Z) LocalOffset { get; init; }
        public (double X, double Y) BaseScale { get; init; } = (1.0, 1.0);
        public double SagittalDropCells { get; init; }
        public bool IsCoronalFlapHinge { get; init; }
        public double BaseCoronalFlap { get; init; }
        public bool IsRigid { get; init; }
        public int AlphaThreshold { get; init; } = 32;
        public bool IsSuppressedBySkinnedMesh { get; set; }
    }

    private sealed class SkinnedMeshSimple
    {
        public required string MeshId { get; init; }
        public required RgbaImage Img { get; init; }
        public RgbaImage? SilImg { get; init; }
        public string? AnchorBoneId { get; init; }
        public (double X, double Y) DrawSize { get; init; }
        public (double U, double V) PivotUv { get; init; } = (0.5, 0.5);
        public double BaseAngle { get; init; }
        public double LayerOffset { get; init; }
        public double SilLayer { get; init; } = -9.2;
        public bool HasWorldBoundsXZ { get; init; }
        public (double X, double Z) WorldMinXZ { get; init; }
        public (double X, double Z) WorldMaxXZ { get; init; }
        public List<string> BoneIds { get; init; } = new();
        public List<(string BoneId, (double X, double Z) A, (double X, double Z) B, double Radius, double Mult)> Capsules { get; init; } = new();
    }

    private sealed class ParsedRig3D
    {
        public required string DefName { get; init; }
        public required bool UseCoupledSagittal { get; init; }
        public required List<Node3D> SortedNodes { get; init; }
        public required Dictionary<string, int> NodeIndex { get; init; }
        public required List<SkinnedMeshSimple> SkinnedMeshes { get; init; }
    }

    private sealed class AnimKeyframe3D
    {
        public double Tick { get; init; }
        public double Angle { get; init; }
        public double SagittalPitch { get; init; }
        public double CoronalFlap { get; init; }
        public (double X, double Z) Offset { get; init; }
        public string Easing { get; init; } = "SmoothStep";
    }

    private sealed class ParsedAnim3D
    {
        public required string DefName { get; init; }
        public required bool Loop { get; init; }
        public required double DurationTicks { get; init; }
        public string? IsolatedSubtreeRootBoneId { get; init; }
        public required Dictionary<string, List<AnimKeyframe3D>> Tracks { get; init; }
    }

    private readonly record struct LocalPose3D(
        (double X, double Z) Offset,
        double Angle,
        double SagittalPitch,
        double CoronalFlap)
    {
        public static LocalPose3D Identity => new((0.0, 0.0), 0.0, 0.0, 0.0);
    }

    private sealed class WorldPose3D
    {
        public required Node3D Node { get; init; }
        public required Variant3D ActiveVar { get; init; }
        public required string ActiveSector { get; init; }
        public required (double X, double Z) JointWorldXZ { get; init; }
        public required (double X, double Z) FlatJointWorldXZ { get; init; }
        public required double WorldAngleDeg { get; init; }
        public required double CosTheta { get; init; }
        public required double SinTheta { get; init; }
        public required (double X, double Y) WorldSize { get; init; }
        public required (double U, double V) PivotUv { get; init; }
        public required double LayerOffset { get; init; }
        public required bool HasCoronalFlap { get; init; }
        public required (double X, double Z) FlapHingeXZ { get; init; }
        public required (double X, double Z) FlapNormalXZ { get; init; }
        public required double FlapCosPsi { get; init; }
        public required bool HasSagittalShear { get; init; }
        public required (double X, double Z) SagGradXZ { get; init; }
        public required (double X, double Z) SagForwardAxisXZ { get; init; }
        public required double SagDet { get; init; }
        public required double OwnSagittalForwardCells { get; init; }

        public (double X, double Z) UVToWorldXZFlat(double u, double v)
        {
            var hasProj = (HasCoronalFlap && Math.Abs(FlapCosPsi - 1.0) > 1e-5) || HasSagittalShear;
            var refJ = hasProj ? FlatJointWorldXZ : JointWorldXZ;
            var lx = (u - PivotUv.U) * WorldSize.X;
            var lz = (v - PivotUv.V) * WorldSize.Y;
            return (refJ.X + (lx * CosTheta + lz * SinTheta), refJ.Z + (-lx * SinTheta + lz * CosTheta));
        }

        public (double X, double Z) UVToWorldXZ(double u, double v)
        {
            var pt = UVToWorldXZFlat(u, v);
            if (HasSagittalShear)
            {
                var dSag = SagGradXZ.X * (pt.X - FlatJointWorldXZ.X) + SagGradXZ.Z * (pt.Z - FlatJointWorldXZ.Z);
                pt = (pt.X + dSag * SagForwardAxisXZ.X, pt.Z + dSag * SagForwardAxisXZ.Z);
            }
            if (HasCoronalFlap && Math.Abs(FlapCosPsi - 1.0) > 1e-5)
            {
                var dLat = (pt.X - FlapHingeXZ.X) * FlapNormalXZ.X + (pt.Z - FlapHingeXZ.Z) * FlapNormalXZ.Z;
                var shift = (FlapCosPsi - 1.0) * dLat;
                pt = (pt.X + shift * FlapNormalXZ.X, pt.Z + shift * FlapNormalXZ.Z);
            }
            return pt;
        }

        public bool TryWorldToUV(double wx, double wz, out double u, out double v)
        {
            if (WorldSize.X <= 1e-5 || WorldSize.Y <= 1e-5)
            {
                u = 0.0;
                v = 0.0;
                return false;
            }

            var evalX = wx;
            var evalZ = wz;
            var hasCor = HasCoronalFlap && Math.Abs(FlapCosPsi - 1.0) > 1e-5;
            var refJ = (hasCor || HasSagittalShear) ? FlatJointWorldXZ : JointWorldXZ;
            if (hasCor)
            {
                var safeCos = Math.Max(0.05, FlapCosPsi);
                var dLat = (evalX - FlapHingeXZ.X) * FlapNormalXZ.X + (evalZ - FlapHingeXZ.Z) * FlapNormalXZ.Z;
                var invShift = ((1.0 / safeCos) - 1.0) * dLat;
                evalX += invShift * FlapNormalXZ.X;
                evalZ += invShift * FlapNormalXZ.Z;
            }
            if (HasSagittalShear)
            {
                var relX = evalX - FlatJointWorldXZ.X;
                var relZ = evalZ - FlatJointWorldXZ.Z;
                var gDot = SagGradXZ.X * relX + SagGradXZ.Z * relZ;
                var invSag = gDot / Math.Max(0.05, SagDet);
                evalX -= invSag * SagForwardAxisXZ.X;
                evalZ -= invSag * SagForwardAxisXZ.Z;
            }

            var dx = evalX - refJ.X;
            var dz = evalZ - refJ.Z;
            var lx = dx * CosTheta - dz * SinTheta;
            var lz = dx * SinTheta + dz * CosTheta;
            u = PivotUv.U + lx / WorldSize.X;
            v = PivotUv.V + lz / WorldSize.Y;
            return u >= 0.0 && u <= 1.0 && v >= 0.0 && v <= 1.0;
        }
    }

    public static AnimStripReport RenderAnimStrip(
        CreatureWorkspace workspace,
        string creatureName,
        string animDefName,
        string? rigDefName = null,
        int frameCount = 6,
        double headingDeg = 180.0,
        int tileSize = 320,
        string? outputPath = null,
        bool showJoints = false,
        CancellationToken cancellationToken = default)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        var cleanCreature = creatureName.Trim();
        CreatureWorkspace.ValidateCreatureName(cleanCreature);

        var cleanAnim = animDefName.Trim();
        if (string.IsNullOrEmpty(cleanAnim))
        {
            throw new ArgumentException("animDefName must not be empty.");
        }

        var safeFrames = Math.Clamp(frameCount, 2, 12);
        var safeTile = Math.Clamp(tileSize, 160, 512);
        var creatureDir = workspace.GetCreatureDir(cleanCreature, createIfMissing: false);
        if (!Directory.Exists(creatureDir))
        {
            throw new DirectoryNotFoundException($"Creature workspace does not exist: {cleanCreature}");
        }

        var defsDir = Path.Combine(creatureDir, "Defs");
        var texturesDir = Path.Combine(creatureDir, "Textures");
        var (rig, anim) = LoadRigAndAnim(defsDir, texturesDir, rigDefName, cleanAnim);

        var sampledTicks = new List<double>(safeFrames);
        if (anim.Loop)
        {
            for (int i = 0; i < safeFrames; i++)
            {
                sampledTicks.Add(Math.Round(i * anim.DurationTicks / safeFrames, 2));
            }
        }
        else
        {
            for (int i = 0; i < safeFrames; i++)
            {
                double frac = safeFrames == 1 ? 0.0 : i / (double)(safeFrames - 1);
                sampledTicks.Add(Math.Round(frac * anim.DurationTicks, 2));
            }
        }

        HashSet<int>? isolatedIndices = null;
        if (!string.IsNullOrEmpty(anim.IsolatedSubtreeRootBoneId) &&
            rig.NodeIndex.TryGetValue(anim.IsolatedSubtreeRootBoneId, out var isoRootIdx))
        {
            isolatedIndices = CollectSubtreeIndices(rig, isoRootIdx);
        }

        var solvedFrames = new List<(double Tick, WorldPose3D[] Poses)>(safeFrames);
        double globalMinX = double.MaxValue, globalMaxX = double.MinValue;
        double globalMinZ = double.MaxValue, globalMaxZ = double.MinValue;

        var headRad = headingDeg * Math.PI / 180.0;
        var fwdAxis = (X: Math.Sin(headRad), Z: Math.Cos(headRad));
        var rightAxis = (X: Math.Cos(headRad), Z: -Math.Sin(headRad));

        var frameDiagnostics = new List<AnimStripFrameDiagnostic>(safeFrames);
        var jointGapDetails = new List<string>();
        var midlineCrossings = new List<string>();
        int totalJointGaps = 0;
        double minSpanW = double.MaxValue, maxSpanW = 0.0;
        double minSpanL = double.MaxValue, maxSpanL = 0.0;

        foreach (var tick in sampledTicks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPoses = SampleAnimPosesAt(rig, anim, tick, isolatedIndices);
            var worldPoses = SolveWorldPoses3D(rig, headingDeg, localPoses);
            solvedFrames.Add((tick, worldPoses));

            double fMinX = double.MaxValue, fMaxX = double.MinValue;
            double fMinZ = double.MaxValue, fMaxZ = double.MinValue;
            double fMinLat = double.MaxValue, fMaxLat = double.MinValue;
            double fMinLong = double.MaxValue, fMaxLong = double.MinValue;

            for (int i = 0; i < worldPoses.Length; i++)
            {
                var wp = worldPoses[i];
                foreach (var (u, v) in CornerUvs)
                {
                    var pt = wp.UVToWorldXZ(u, v);
                    if (pt.X < fMinX) fMinX = pt.X;
                    if (pt.X > fMaxX) fMaxX = pt.X;
                    if (pt.Z < fMinZ) fMinZ = pt.Z;
                    if (pt.Z > fMaxZ) fMaxZ = pt.Z;

                    var lat = pt.X * rightAxis.X + pt.Z * rightAxis.Z;
                    var lon = pt.X * fwdAxis.X + pt.Z * fwdAxis.Z;
                    if (lat < fMinLat) fMinLat = lat;
                    if (lat > fMaxLat) fMaxLat = lat;
                    if (lon < fMinLong) fMinLong = lon;
                    if (lon > fMaxLong) fMaxLong = lon;
                }
            }

            if (fMinX < globalMinX) globalMinX = fMinX;
            if (fMaxX > globalMaxX) globalMaxX = fMaxX;
            if (fMinZ < globalMinZ) globalMinZ = fMinZ;
            if (fMaxZ > globalMaxZ) globalMaxZ = fMaxZ;

            double spanW = fMaxLat >= fMinLat ? fMaxLat - fMinLat : 0.0;
            double spanL = fMaxLong >= fMinLong ? fMaxLong - fMinLong : 0.0;
            if (spanW < minSpanW) minSpanW = spanW;
            if (spanW > maxSpanW) maxSpanW = spanW;
            if (spanL < minSpanL) minSpanL = spanL;
            if (spanL > maxSpanL) maxSpanL = spanL;

            int frameGaps = 0;
            var rootJoint = worldPoses[0].JointWorldXZ;
            var rootLat = rootJoint.X * rightAxis.X + rootJoint.Z * rightAxis.Z;

            for (int i = 0; i < worldPoses.Length; i++)
            {
                var n = rig.SortedNodes[i];
                var wp = worldPoses[i];
                if (n.ParentIndex >= 0 && !n.IsSuppressedBySkinnedMesh && !rig.SortedNodes[n.ParentIndex].IsSuppressedBySkinnedMesh)
                {
                    var pw = worldPoses[n.ParentIndex];
                    var jpt = wp.JointWorldXZ;
                    if (!IsOpaqueNear3D(pw, jpt.X, jpt.Z) || !IsOpaqueNear3D(wp, jpt.X, jpt.Z))
                    {
                        frameGaps++;
                        totalJointGaps++;
                        if (jointGapDetails.Count < 16)
                        {
                            jointGapDetails.Add(string.Create(CultureInfo.InvariantCulture,
                                $"t={tick:0.#}: {rig.SortedNodes[n.ParentIndex].Id}->{n.Id} gap at ({jpt.X:F2},{jpt.Z:F2})"));
                        }
                    }
                }

                // Bilateral wing midline check
                var idLow = n.Id.ToLowerInvariant();
                if (idLow.Contains("wing") && midlineCrossings.Count < 12)
                {
                    var jLat = wp.JointWorldXZ.X * rightAxis.X + wp.JointWorldXZ.Z * rightAxis.Z - rootLat;
                    bool isLeftWing = idLow.Contains("_left") || idLow.StartsWith("left_") || idLow.Contains("_l_") || idLow.EndsWith("_l") || idLow.Contains("wing_l");
                    bool isRightWing = idLow.Contains("_right") || idLow.StartsWith("right_") || idLow.Contains("_r_") || idLow.EndsWith("_r") || idLow.Contains("wing_r");
                    if (isLeftWing && jLat > 0.15)
                    {
                        midlineCrossings.Add(string.Create(CultureInfo.InvariantCulture, $"t={tick:0.#}: left wing '{n.Id}' crossed midline (+{jLat:F2} cells right)"));
                    }
                    else if (isRightWing && jLat < -0.15)
                    {
                        midlineCrossings.Add(string.Create(CultureInfo.InvariantCulture, $"t={tick:0.#}: right wing '{n.Id}' crossed midline ({jLat:F2} cells left)"));
                    }
                }
            }

            frameDiagnostics.Add(new AnimStripFrameDiagnostic(
                Tick: tick,
                WingspanCells: Math.Round(spanW, 2),
                LengthCells: Math.Round(spanL, 2),
                JointGapCount: frameGaps));
        }

        if (globalMinX > globalMaxX)
        {
            globalMinX = -4.0;
            globalMaxX = 4.0;
            globalMinZ = -4.0;
            globalMaxZ = 4.0;
        }

        double centerWorldX = 0.5 * (globalMinX + globalMaxX);
        double centerWorldZ = 0.5 * (globalMinZ + globalMaxZ);
        double worldSpan = Math.Max(2.0, Math.Max(globalMaxX - globalMinX, globalMaxZ - globalMinZ) * 1.14);
        double scalePx = (safeTile - 16.0) / worldSpan;

        var flatBindPoses = rig.SkinnedMeshes.Count > 0 ? SolveWorldPoses3D(rig, rootHeadingDeg: 0.0, null, suppressCoronal: true) : null;
        var panels = new List<RgbaImage>(safeFrames);
        var labels = new List<string>(safeFrames);

        foreach (var (tick, poses) in solvedFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            panels.Add(RenderSingleFrame(rig, poses, flatBindPoses, headingDeg, safeTile, centerWorldX, centerWorldZ, scalePx, showJoints));
            labels.Add(string.Create(CultureInfo.InvariantCulture, $"t={tick:0.#}/{anim.DurationTicks:0.#}"));
        }

        int cols = safeFrames <= 4 ? safeFrames : safeFrames <= 6 ? 3 : 4;
        var strip = PreviewRenderer.RenderCheckerboardPreview(panels, labels, tileSize: safeTile, cols: cols);
        var encoded = PngCodec.Encode(strip);

        var relOut = string.IsNullOrWhiteSpace(outputPath)
            ? $"Work/strip_{cleanAnim}.png"
            : outputPath.Trim();
        var outFull = workspace.ResolvePngPath(cleanCreature, relOut, requireUnderTextures: false, createParentDirs: true);
        File.WriteAllBytes(outFull, encoded);
        var canonicalRel = workspace.ToRelativeWorkspacePath(cleanCreature, outFull);

        return new AnimStripReport(
            CreatureName: cleanCreature,
            RigDefName: rig.DefName,
            AnimDefName: anim.DefName,
            Loop: anim.Loop,
            DurationTicks: anim.DurationTicks,
            FrameCount: safeFrames,
            HeadingDeg: headingDeg,
            OutputPath: canonicalRel,
            ImageWidth: strip.Width,
            ImageHeight: strip.Height,
            MinWingspanCells: Math.Round(minSpanW == double.MaxValue ? 0.0 : minSpanW, 2),
            MaxWingspanCells: Math.Round(maxSpanW, 2),
            MinLengthCells: Math.Round(minSpanL == double.MaxValue ? 0.0 : minSpanL, 2),
            MaxLengthCells: Math.Round(maxSpanL, 2),
            TotalJointGaps: totalJointGaps,
            JointGapDetails: jointGapDetails,
            MidlineCrossings: midlineCrossings,
            Frames: frameDiagnostics,
            StripImage: strip);
    }

    private static readonly (double U, double V)[] CornerUvs =
    [
        (0.0, 0.0),
        (1.0, 0.0),
        (0.0, 1.0),
        (1.0, 1.0)
    ];

    private static RgbaImage RenderSingleFrame(
        ParsedRig3D rig,
        WorldPose3D[] poses,
        WorldPose3D[]? flatBindPoses,
        double headingDeg,
        int tilePx,
        double centerWorldX,
        double centerWorldZ,
        double scalePx,
        bool showJoints)
    {
        var canvas = new RgbaImage(tilePx, tilePx);
        var cp = canvas.Pixels;
        for (int i = 0; i < cp.Length; i += 4)
        {
            cp[i] = 74;
            cp[i + 1] = 68;
            cp[i + 2] = 58;
            cp[i + 3] = 255;
        }

        // Subtle crosshair for spatial reference (root origin)
        int ox = (int)Math.Round(tilePx * 0.5 + (0.0 - centerWorldX) * scalePx);
        int oy = (int)Math.Round(tilePx * 0.5 - (0.0 - centerWorldZ) * scalePx);
        for (int x = 0; x < tilePx; x++)
        {
            if (oy >= 0 && oy < tilePx) BlendPixel(cp, tilePx, x, oy, 92, 86, 74, 140);
        }
        for (int y = 0; y < tilePx; y++)
        {
            if (ox >= 0 && ox < tilePx) BlendPixel(cp, tilePx, ox, y, 92, 86, 74, 140);
        }

        var drawActions = new List<(double Layer, Action Draw)>();
        for (int i = 0; i < poses.Length; i++)
        {
            var n = rig.SortedNodes[i];
            if (n.IsSuppressedBySkinnedMesh) continue;
            var wp = poses[i];
            if (wp.ActiveVar.SilImg != null)
            {
                var silImg = wp.ActiveVar.SilImg;
                drawActions.Add((wp.ActiveVar.SilLayer, () => RasterizeBonePose(cp, tilePx, silImg, wp, centerWorldX, centerWorldZ, scalePx)));
            }
            drawActions.Add((wp.LayerOffset, () => RasterizeBonePose(cp, tilePx, wp.ActiveVar.Img, wp, centerWorldX, centerWorldZ, scalePx)));
        }

        if (flatBindPoses != null)
        {
            foreach (var sm in rig.SkinnedMeshes)
            {
                if (sm.SilImg != null)
                {
                    drawActions.Add((sm.SilLayer, () => RasterizeSkinnedMesh(cp, tilePx, rig, sm, sm.SilImg, poses, flatBindPoses, headingDeg, centerWorldX, centerWorldZ, scalePx)));
                }
                drawActions.Add((sm.LayerOffset, () => RasterizeSkinnedMesh(cp, tilePx, rig, sm, sm.Img, poses, flatBindPoses, headingDeg, centerWorldX, centerWorldZ, scalePx)));
            }
        }

        drawActions.Sort((a, b) => a.Layer.CompareTo(b.Layer));
        foreach (var item in drawActions)
        {
            item.Draw();
        }

        if (showJoints)
        {
            for (int i = 0; i < poses.Length; i++)
            {
                var j = poses[i].JointWorldXZ;
                int jx = (int)Math.Round(tilePx * 0.5 + (j.X - centerWorldX) * scalePx);
                int jy = (int)Math.Round(tilePx * 0.5 - (j.Z - centerWorldZ) * scalePx);
                byte r = i == 0 ? (byte)255 : (byte)56;
                byte g = i == 0 ? (byte)196 : (byte)224;
                byte b = i == 0 ? (byte)64 : (byte)255;
                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (Math.Abs(dx) + Math.Abs(dy) <= 2 && jx + dx >= 0 && jx + dx < tilePx && jy + dy >= 0 && jy + dy < tilePx)
                        {
                            BlendPixel(cp, tilePx, jx + dx, jy + dy, r, g, b, 235);
                        }
                    }
                }
            }
        }

        return canvas;
    }

    private static void RasterizeBonePose(
        byte[] cp,
        int tilePx,
        RgbaImage src,
        WorldPose3D wp,
        double centerWorldX,
        double centerWorldZ,
        double scalePx)
    {
        double minWx = double.MaxValue, maxWx = double.MinValue;
        double minWz = double.MaxValue, maxWz = double.MinValue;
        foreach (var (u, v) in CornerUvs)
        {
            var pt = wp.UVToWorldXZ(u, v);
            if (pt.X < minWx) minWx = pt.X;
            if (pt.X > maxWx) maxWx = pt.X;
            if (pt.Z < minWz) minWz = pt.Z;
            if (pt.Z > maxWz) maxWz = pt.Z;
        }

        int x0 = Math.Max(0, (int)Math.Floor(tilePx * 0.5 + (minWx - centerWorldX) * scalePx) - 1);
        int x1 = Math.Min(tilePx - 1, (int)Math.Ceiling(tilePx * 0.5 + (maxWx - centerWorldX) * scalePx) + 1);
        int y0 = Math.Max(0, (int)Math.Floor(tilePx * 0.5 - (maxWz - centerWorldZ) * scalePx) - 1);
        int y1 = Math.Min(tilePx - 1, (int)Math.Ceiling(tilePx * 0.5 - (minWz - centerWorldZ) * scalePx) + 1);

        int sw = src.Width;
        int sh = src.Height;
        for (int py = y0; py <= y1; py++)
        {
            double wz = centerWorldZ + (tilePx * 0.5 - py) / scalePx;
            for (int px = x0; px <= x1; px++)
            {
                double wx = centerWorldX + (px - tilePx * 0.5) / scalePx;
                if (!wp.TryWorldToUV(wx, wz, out var u, out var v)) continue;
                int sx = Math.Clamp((int)Math.Round(u * (sw - 1)), 0, sw - 1);
                int sy = Math.Clamp((int)Math.Round((1.0 - v) * (sh - 1)), 0, sh - 1);
                byte sa = src.GetA(sx, sy);
                if (sa == 0) continue;
                BlendPixel(cp, tilePx, px, py, src.GetR(sx, sy), src.GetG(sx, sy), src.GetB(sx, sy), sa);
            }
        }
    }

    private static void RasterizeSkinnedMesh(
        byte[] cp,
        int tilePx,
        ParsedRig3D rig,
        SkinnedMeshSimple sm,
        RgbaImage src,
        WorldPose3D[] livePoses,
        WorldPose3D[] flatBindPoses,
        double headingDeg,
        double centerWorldX,
        double centerWorldZ,
        double scalePx)
    {
        var infIndices = sm.BoneIds.Count > 0
            ? sm.BoneIds.Where(rig.NodeIndex.ContainsKey).Select(id => rig.NodeIndex[id]).Distinct().ToArray()
            : sm.Capsules.Select(c => c.BoneId).Where(rig.NodeIndex.ContainsKey).Select(id => rig.NodeIndex[id]).Distinct().ToArray();
        if (infIndices.Length == 0 && sm.AnchorBoneId != null && rig.NodeIndex.TryGetValue(sm.AnchorBoneId, out var ancOnly))
        {
            infIndices = [ancOnly];
        }
        if (infIndices.Length == 0) return;

        int gridN = 18;
        var verts = new (double X, double Z)[(gridN + 1) * (gridN + 1)];
        int anchorIdx = sm.AnchorBoneId != null && rig.NodeIndex.TryGetValue(sm.AnchorBoneId, out var ai) ? ai : infIndices[0];
        var anchorBind = flatBindPoses[anchorIdx];

        for (int gy = 0; gy <= gridN; gy++)
        {
            double v = gy / (double)gridN;
            for (int gx = 0; gx <= gridN; gx++)
            {
                double u = gx / (double)gridN;
                (double X, double Z) bindXZ;
                if (sm.HasWorldBoundsXZ)
                {
                    bindXZ = (
                        sm.WorldMinXZ.X + u * (sm.WorldMaxXZ.X - sm.WorldMinXZ.X),
                        sm.WorldMinXZ.Z + v * (sm.WorldMaxXZ.Z - sm.WorldMinXZ.Z));
                }
                else
                {
                    double rad = (anchorBind.WorldAngleDeg + sm.BaseAngle) * Math.PI / 180.0;
                    double c = Math.Cos(rad), s = Math.Sin(rad);
                    double lx = (u - sm.PivotUv.U) * sm.DrawSize.X;
                    double lz = (v - sm.PivotUv.V) * sm.DrawSize.Y;
                    bindXZ = (anchorBind.FlatJointWorldXZ.X + lx * c + lz * s, anchorBind.FlatJointWorldXZ.Z - lx * s + lz * c);
                }

                // Distance-weighted LBS across influence bones
                double sumW = 0.0, outX = 0.0, outZ = 0.0;
                foreach (var bi in infIndices)
                {
                    var bBind = flatBindPoses[bi];
                    bBind.TryWorldToUV(bindXZ.X, bindXZ.Z, out var bu, out var bv);
                    var centerBind = bBind.UVToWorldXZFlat(0.5, 0.5);
                    double dx = bindXZ.X - centerBind.X;
                    double dz = bindXZ.Z - centerBind.Z;
                    double d2 = dx * dx + dz * dz + 0.15;
                    double w = 1.0 / (d2 * d2);
                    var mapped = livePoses[bi].UVToWorldXZ(bu, bv);
                    outX += mapped.X * w;
                    outZ += mapped.Z * w;
                    sumW += w;
                }
                verts[gy * (gridN + 1) + gx] = sumW > 1e-8 ? (outX / sumW, outZ / sumW) : bindXZ;
            }
        }

        for (int gy = 0; gy < gridN; gy++)
        {
            for (int gx = 0; gx < gridN; gx++)
            {
                int i00 = gy * (gridN + 1) + gx;
                int i10 = i00 + 1;
                int i01 = i00 + (gridN + 1);
                int i11 = i01 + 1;
                double u0 = gx / (double)gridN, u1 = (gx + 1) / (double)gridN;
                double v0 = gy / (double)gridN, v1 = (gy + 1) / (double)gridN;

                RasterizeTexturedTriangle(cp, tilePx, src, verts[i00], verts[i10], verts[i11], (u0, v0), (u1, v0), (u1, v1), centerWorldX, centerWorldZ, scalePx);
                RasterizeTexturedTriangle(cp, tilePx, src, verts[i00], verts[i11], verts[i01], (u0, v0), (u1, v1), (u0, v1), centerWorldX, centerWorldZ, scalePx);
            }
        }
    }

    private static void RasterizeTexturedTriangle(
        byte[] cp,
        int tilePx,
        RgbaImage src,
        (double X, double Z) w0,
        (double X, double Z) w1,
        (double X, double Z) w2,
        (double U, double V) uv0,
        (double U, double V) uv1,
        (double U, double V) uv2,
        double centerWorldX,
        double centerWorldZ,
        double scalePx)
    {
        double sx0 = tilePx * 0.5 + (w0.X - centerWorldX) * scalePx;
        double sy0 = tilePx * 0.5 - (w0.Z - centerWorldZ) * scalePx;
        double sx1 = tilePx * 0.5 + (w1.X - centerWorldX) * scalePx;
        double sy1 = tilePx * 0.5 - (w1.Z - centerWorldZ) * scalePx;
        double sx2 = tilePx * 0.5 + (w2.X - centerWorldX) * scalePx;
        double sy2 = tilePx * 0.5 - (w2.Z - centerWorldZ) * scalePx;

        double det = (sy1 - sy2) * (sx0 - sx2) + (sx2 - sx1) * (sy0 - sy2);
        if (Math.Abs(det) < 1e-4) return;
        double invDet = 1.0 / det;

        int minX = Math.Max(0, (int)Math.Floor(Math.Min(sx0, Math.Min(sx1, sx2))));
        int maxX = Math.Min(tilePx - 1, (int)Math.Ceiling(Math.Max(sx0, Math.Max(sx1, sx2))));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(sy0, Math.Min(sy1, sy2))));
        int maxY = Math.Min(tilePx - 1, (int)Math.Ceiling(Math.Max(sy0, Math.Max(sy1, sy2))));

        int sw = src.Width;
        int sh = src.Height;
        for (int py = minY; py <= maxY; py++)
        {
            double cy = py + 0.5;
            for (int px = minX; px <= maxX; px++)
            {
                double cx = px + 0.5;
                double l0 = ((sy1 - sy2) * (cx - sx2) + (sx2 - sx1) * (cy - sy2)) * invDet;
                double l1 = ((sy2 - sy0) * (cx - sx2) + (sx0 - sx2) * (cy - sy2)) * invDet;
                double l2 = 1.0 - l0 - l1;
                if (l0 < -1e-4 || l1 < -1e-4 || l2 < -1e-4) continue;

                double u = Math.Clamp(l0 * uv0.U + l1 * uv1.U + l2 * uv2.U, 0.0, 1.0);
                double v = Math.Clamp(l0 * uv0.V + l1 * uv1.V + l2 * uv2.V, 0.0, 1.0);
                int tx = Math.Clamp((int)Math.Round(u * (sw - 1)), 0, sw - 1);
                int ty = Math.Clamp((int)Math.Round((1.0 - v) * (sh - 1)), 0, sh - 1);
                byte sa = src.GetA(tx, ty);
                if (sa == 0) continue;
                BlendPixel(cp, tilePx, px, py, src.GetR(tx, ty), src.GetG(tx, ty), src.GetB(tx, ty), sa);
            }
        }
    }

    private static void BlendPixel(byte[] cp, int width, int x, int y, byte sr, byte sg, byte sb, byte sa)
    {
        int idx = (y * width + x) * 4;
        cp[idx] = (byte)((sr * sa + cp[idx] * (255 - sa) + 127) / 255);
        cp[idx + 1] = (byte)((sg * sa + cp[idx + 1] * (255 - sa) + 127) / 255);
        cp[idx + 2] = (byte)((sb * sa + cp[idx + 2] * (255 - sa) + 127) / 255);
        cp[idx + 3] = 255;
    }

    private static bool IsOpaqueNear3D(WorldPose3D wp, double wx, double wz)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!wp.TryWorldToUV(wx + dx * 0.04, wz + dz * 0.04, out var u, out var v)) continue;
                int sx = Math.Clamp((int)Math.Round(u * (wp.ActiveVar.Img.Width - 1)), 0, wp.ActiveVar.Img.Width - 1);
                int sy = Math.Clamp((int)Math.Round((1.0 - v) * (wp.ActiveVar.Img.Height - 1)), 0, wp.ActiveVar.Img.Height - 1);
                if (wp.ActiveVar.Img.GetA(sx, sy) >= wp.Node.AlphaThreshold)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // --- 3D Forward Kinematics & Spline Evaluator ---

    private static WorldPose3D[] SolveWorldPoses3D(
        ParsedRig3D rig,
        double rootHeadingDeg,
        LocalPose3D[]? localPoses,
        bool suppressCoronal = false)
    {
        int count = rig.SortedNodes.Count;
        var outPoses = new WorldPose3D[count];
        double rootRad = rootHeadingDeg * Math.PI / 180.0;
        double rootCos = Math.Cos(rootRad);
        double rootSin = Math.Sin(rootRad);

        for (int i = 0; i < count; i++)
        {
            var def = rig.SortedNodes[i];
            var anim = localPoses != null && i < localPoses.Length ? localPoses[i] : LocalPose3D.Identity;
            int pIdx = def.ParentIndex;

            double ownSagForward = 0.0;
            if (def.SagittalDropCells > 1e-5 && Math.Abs(anim.SagittalPitch) > 1e-4)
            {
                ownSagForward = def.SagittalDropCells * Math.Sin(anim.SagittalPitch * Math.PI / 180.0);
            }

            (double X, double Z) jointWorld;
            double worldAngle;
            if (pIdx < 0)
            {
                var rx = def.LocalOffset.X + anim.Offset.X;
                var rz = def.LocalOffset.Z + anim.Offset.Z;
                jointWorld = (rx * rootCos + rz * rootSin, -rx * rootSin + rz * rootCos);
                worldAngle = PositiveMod(rootHeadingDeg + def.BaseAngle + anim.Angle, 360.0);
            }
            else
            {
                var parentPose = outPoses[pIdx];
                var sockVar = GetSectorVar(def, parentPose.ActiveSector);
                var sockWorld = parentPose.UVToWorldXZFlat(sockVar.Sock.U, sockVar.Sock.V);
                if (Math.Abs(def.LocalOffset.X) > 1e-6 || Math.Abs(def.LocalOffset.Z) > 1e-6)
                {
                    sockWorld = (
                        sockWorld.X + def.LocalOffset.X * parentPose.CosTheta + def.LocalOffset.Z * parentPose.SinTheta,
                        sockWorld.Z - def.LocalOffset.X * parentPose.SinTheta + def.LocalOffset.Z * parentPose.CosTheta);
                }
                if (rig.UseCoupledSagittal)
                {
                    if (Math.Abs(parentPose.OwnSagittalForwardCells) > 1e-5)
                    {
                        sockWorld = (
                            sockWorld.X + parentPose.OwnSagittalForwardCells * rootSin,
                            sockWorld.Z + parentPose.OwnSagittalForwardCells * rootCos);
                    }
                }
                else if (Math.Abs(ownSagForward) > 1e-5)
                {
                    sockWorld = (sockWorld.X + ownSagForward * rootSin, sockWorld.Z + ownSagForward * rootCos);
                }
                jointWorld = sockWorld;
                double relAng = def.IsRigid ? 0.0 : anim.Angle;
                worldAngle = PositiveMod(parentPose.WorldAngleDeg + def.BaseAngle + relAng, 360.0);
            }

            string selfSector = "Dorsal";
            if (def.HasSelfVariant)
            {
                double srcAng = def.SectorSourceIndex >= 0 && def.SectorSourceIndex < i
                    ? outPoses[def.SectorSourceIndex].WorldAngleDeg
                    : worldAngle;
                selfSector = EvaluateSector(srcAng, def.LateralHalfDeg);
            }

            var selfVar = GetSectorVar(def, selfSector);
            var worldSize = (
                X: Math.Max(0.001, selfVar.Ds.X * def.BaseScale.X),
                Y: Math.Max(0.001, selfVar.Ds.Y * def.BaseScale.Y));
            double rad = worldAngle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            bool hasSagShear = false;
            var sagGrad = (X: 0.0, Z: 0.0);
            var sagFwdAxis = (X: rootSin, Z: rootCos);
            double sagDetVal = 1.0;
            if (rig.UseCoupledSagittal && !def.IsSuppressedBySkinnedMesh && Math.Abs(ownSagForward) > 1e-5)
            {
                int childIdx = def.FirstArticulatedChildIndex;
                var targetUv = childIdx >= 0 && childIdx < count
                    ? GetSectorVar(rig.SortedNodes[childIdx], selfSector).Sock
                    : (U: 0.5, V: 0.88);
                double dLx = (targetUv.U - selfVar.Piv.U) * worldSize.X;
                double dLz = (targetUv.V - selfVar.Piv.V) * worldSize.Y;
                double dSx = dLx * cos + dLz * sin;
                double dSz = -dLx * sin + dLz * cos;
                double segLenSq = dSx * dSx + dSz * dSz;
                if (segLenSq > 1e-5)
                {
                    double ch = (dSx * rootSin + dSz * rootCos) / segLenSq;
                    if (Math.Abs(ch) > 1e-5)
                    {
                        double rawDet = 1.0 + ownSagForward * ch;
                        if (rawDet < 0.30) ownSagForward = (0.30 - 1.0) / ch;
                        else if (rawDet > 2.40) ownSagForward = (2.40 - 1.0) / ch;
                    }
                    if (Math.Abs(ownSagForward) > 1e-5)
                    {
                        double invLenSq = ownSagForward / segLenSq;
                        sagGrad = (dSx * invLenSq, dSz * invLenSq);
                        sagDetVal = Math.Max(0.05, 1.0 + sagGrad.X * rootSin + sagGrad.Z * rootCos);
                        hasSagShear = true;
                    }
                }
            }

            bool hasFlap = false;
            var flapHinge = (X: 0.0, Z: 0.0);
            var flapNorm = (X: 0.0, Z: 0.0);
            double flapCos = 1.0;
            if (!suppressCoronal)
            {
                if (pIdx >= 0 && outPoses[pIdx].HasCoronalFlap)
                {
                    hasFlap = true;
                    flapHinge = outPoses[pIdx].FlapHingeXZ;
                    flapNorm = outPoses[pIdx].FlapNormalXZ;
                    flapCos = outPoses[pIdx].FlapCosPsi;
                }
                double effFlap = def.BaseCoronalFlap + (def.IsRigid && pIdx >= 0 ? 0.0 : anim.CoronalFlap);
                if (def.IsCoronalFlapHinge && Math.Abs(effFlap) > 1e-4)
                {
                    double cosPsi = Math.Clamp(Math.Cos(effFlap * Math.PI / 180.0), 0.25, 1.0);
                    if (!hasFlap)
                    {
                        hasFlap = true;
                        flapHinge = jointWorld;
                        flapNorm = (rootCos, -rootSin);
                        flapCos = cosPsi;
                    }
                    else
                    {
                        flapCos = Math.Clamp(flapCos * cosPsi, 0.25, 1.0);
                    }
                }
            }

            var projJoint = jointWorld;
            if (hasFlap && Math.Abs(flapCos - 1.0) > 1e-5)
            {
                double dLatJ = (jointWorld.X - flapHinge.X) * flapNorm.X + (jointWorld.Z - flapHinge.Z) * flapNorm.Z;
                double shiftJ = (flapCos - 1.0) * dLatJ;
                projJoint = (jointWorld.X + shiftJ * flapNorm.X, jointWorld.Z + shiftJ * flapNorm.Z);
            }

            outPoses[i] = new WorldPose3D
            {
                Node = def,
                ActiveVar = selfVar,
                ActiveSector = selfSector,
                JointWorldXZ = projJoint,
                FlatJointWorldXZ = jointWorld,
                WorldAngleDeg = worldAngle,
                CosTheta = cos,
                SinTheta = sin,
                WorldSize = worldSize,
                PivotUv = selfVar.Piv,
                LayerOffset = selfVar.LayerOffset,
                HasCoronalFlap = hasFlap,
                FlapHingeXZ = flapHinge,
                FlapNormalXZ = flapNorm,
                FlapCosPsi = flapCos,
                HasSagittalShear = hasSagShear,
                SagGradXZ = sagGrad,
                SagForwardAxisXZ = sagFwdAxis,
                SagDet = sagDetVal,
                OwnSagittalForwardCells = ownSagForward
            };
        }

        return outPoses;
    }

    private static LocalPose3D[] SampleAnimPosesAt(
        ParsedRig3D rig,
        ParsedAnim3D anim,
        double tick,
        HashSet<int>? isolatedIndices)
    {
        var poses = new LocalPose3D[rig.SortedNodes.Count];
        for (int i = 0; i < poses.Length; i++)
        {
            if (isolatedIndices != null && !isolatedIndices.Contains(i))
            {
                poses[i] = LocalPose3D.Identity;
                continue;
            }
            var id = rig.SortedNodes[i].Id;
            poses[i] = anim.Tracks.TryGetValue(id, out var kfs)
                ? SampleTrackAt(kfs, tick, anim.DurationTicks, anim.Loop)
                : LocalPose3D.Identity;
        }
        return poses;
    }

    private static LocalPose3D SampleTrackAt(List<AnimKeyframe3D> keyframes, double tick, double durationTicks, bool loop)
    {
        if (keyframes.Count == 0) return LocalPose3D.Identity;
        if (keyframes.Count == 1)
        {
            var s = keyframes[0];
            return new LocalPose3D(s.Offset, s.Angle, s.SagittalPitch, s.CoronalFlap);
        }

        double t = tick;
        if (durationTicks > 0.0)
        {
            t = loop ? PositiveMod(tick, durationTicks) : Math.Clamp(tick, 0.0, durationTicks);
        }

        int lastIdx = keyframes.Count - 1;
        if (t <= keyframes[0].Tick)
        {
            if (loop && durationTicks > keyframes[lastIdx].Tick && t < keyframes[0].Tick)
            {
                var a = keyframes[lastIdx];
                var b = keyframes[0];
                double span = (durationTicks - a.Tick) + b.Tick;
                double frac = span > 1e-4 ? ((durationTicks - a.Tick) + t) / span : 1.0;
                return b.Easing == "CatmullRom"
                    ? InterpolateCatmullRom(keyframes, lastIdx, 0, span, frac, durationTicks, true)
                    : InterpolateLinearOrEased(a, b, frac);
            }
            var f = keyframes[0];
            return new LocalPose3D(f.Offset, f.Angle, f.SagittalPitch, f.CoronalFlap);
        }

        if (t >= keyframes[lastIdx].Tick)
        {
            if (loop && durationTicks > keyframes[lastIdx].Tick)
            {
                var a = keyframes[lastIdx];
                var b = keyframes[0];
                double span = (durationTicks - a.Tick) + b.Tick;
                double frac = span > 1e-4 ? (t - a.Tick) / span : 1.0;
                return b.Easing == "CatmullRom"
                    ? InterpolateCatmullRom(keyframes, lastIdx, 0, span, frac, durationTicks, true)
                    : InterpolateLinearOrEased(a, b, frac);
            }
            var l = keyframes[lastIdx];
            return new LocalPose3D(l.Offset, l.Angle, l.SagittalPitch, l.CoronalFlap);
        }

        for (int i = 0; i < lastIdx; i++)
        {
            var k0 = keyframes[i];
            var k1 = keyframes[i + 1];
            if (t >= k0.Tick && t <= k1.Tick)
            {
                double span = k1.Tick - k0.Tick;
                double frac = span > 1e-4 ? (t - k0.Tick) / span : 1.0;
                return k1.Easing == "CatmullRom"
                    ? InterpolateCatmullRom(keyframes, i, i + 1, span, frac, durationTicks, loop)
                    : InterpolateLinearOrEased(k0, k1, frac);
            }
        }

        var fb = keyframes[lastIdx];
        return new LocalPose3D(fb.Offset, fb.Angle, fb.SagittalPitch, fb.CoronalFlap);
    }

    private static LocalPose3D InterpolateCatmullRom(
        List<AnimKeyframe3D> keyframes,
        int idxA,
        int idxB,
        double spanAB,
        double rawT,
        double durationTicks,
        bool loop)
    {
        double u = Math.Clamp(rawT, 0.0, 1.0);
        int count = keyframes.Count;
        int lastIdx = count - 1;
        double dt = Math.Max(1e-3, spanAB);
        var kA = keyframes[idxA];
        var kB = keyframes[idxB];
        if (count < 3) return InterpolateLinearOrEased(kA, kB, u);

        bool dupSeam = loop && durationTicks > 0.0 && Math.Abs((durationTicks - keyframes[lastIdx].Tick) + keyframes[0].Tick) < 1e-3;
        AnimKeyframe3D? kPrev = null;
        double dtPrev = dt;
        if (idxA > 0)
        {
            kPrev = keyframes[idxA - 1];
            dtPrev = Math.Max(1e-3, kA.Tick - kPrev.Tick);
        }
        else if (loop)
        {
            if (dupSeam && lastIdx >= 1)
            {
                kPrev = keyframes[lastIdx - 1];
                dtPrev = Math.Max(1e-3, keyframes[lastIdx].Tick - kPrev.Tick);
            }
            else
            {
                kPrev = keyframes[lastIdx];
                dtPrev = Math.Max(1e-3, (durationTicks - kPrev.Tick) + kA.Tick);
            }
        }

        AnimKeyframe3D? kNext = null;
        double dtNext = dt;
        if (idxB < lastIdx)
        {
            kNext = keyframes[idxB + 1];
            dtNext = Math.Max(1e-3, kNext.Tick - kB.Tick);
        }
        else if (loop)
        {
            if (dupSeam && count >= 2)
            {
                kNext = keyframes[1];
                dtNext = Math.Max(1e-3, kNext.Tick - keyframes[0].Tick);
            }
            else
            {
                kNext = keyframes[0];
                dtNext = Math.Max(1e-3, (durationTicks - kB.Tick) + kNext.Tick);
            }
        }

        double a1 = kA.Angle;
        double a2 = a1 + DeltaAngleDeg(a1, kB.Angle);
        double a0 = kPrev != null ? (a1 - DeltaAngleDeg(kPrev.Angle, a1)) : (a1 - (a2 - a1));
        double a3 = kNext != null ? (a2 + DeltaAngleDeg(a2, kNext.Angle)) : (a2 + (a2 - a1));
        double m1A = ((a2 - a0) / (dtPrev + dt)) * dt;
        double m2A = ((a3 - a1) / (dt + dtNext)) * dt;

        double ox1 = kA.Offset.X, ox2 = kB.Offset.X;
        double ox0 = kPrev != null ? kPrev.Offset.X : (ox1 - (ox2 - ox1));
        double ox3 = kNext != null ? kNext.Offset.X : (ox2 + (ox2 - ox1));
        double m1Ox = ((ox2 - ox0) / (dtPrev + dt)) * dt;
        double m2Ox = ((ox3 - ox1) / (dt + dtNext)) * dt;

        double oz1 = kA.Offset.Z, oz2 = kB.Offset.Z;
        double oz0 = kPrev != null ? kPrev.Offset.Z : (oz1 - (oz2 - oz1));
        double oz3 = kNext != null ? kNext.Offset.Z : (oz2 + (oz2 - oz1));
        double m1Oz = ((oz2 - oz0) / (dtPrev + dt)) * dt;
        double m2Oz = ((oz3 - oz1) / (dt + dtNext)) * dt;

        double p1 = kA.SagittalPitch, p2 = kB.SagittalPitch;
        double p0 = kPrev != null ? kPrev.SagittalPitch : (p1 - (p2 - p1));
        double p3 = kNext != null ? kNext.SagittalPitch : (p2 + (p2 - p1));
        double m1P = ((p2 - p0) / (dtPrev + dt)) * dt;
        double m2P = ((p3 - p1) / (dt + dtNext)) * dt;

        double f1 = kA.CoronalFlap, f2 = kB.CoronalFlap;
        double f0 = kPrev != null ? kPrev.CoronalFlap : (f1 - (f2 - f1));
        double f3 = kNext != null ? kNext.CoronalFlap : (f2 + (f2 - f1));
        double m1F = ((f2 - f0) / (dtPrev + dt)) * dt;
        double m2F = ((f3 - f1) / (dt + dtNext)) * dt;

        double u2 = u * u, u3 = u2 * u;
        double h00 = 2.0 * u3 - 3.0 * u2 + 1.0;
        double h10 = u3 - 2.0 * u2 + u;
        double h01 = -2.0 * u3 + 3.0 * u2;
        double h11 = u3 - u2;

        return new LocalPose3D(
            Offset: (h00 * ox1 + h10 * m1Ox + h01 * ox2 + h11 * m2Ox, h00 * oz1 + h10 * m1Oz + h01 * oz2 + h11 * m2Oz),
            Angle: h00 * a1 + h10 * m1A + h01 * a2 + h11 * m2A,
            SagittalPitch: h00 * p1 + h10 * m1P + h01 * p2 + h11 * m2P,
            CoronalFlap: h00 * f1 + h10 * m1F + h01 * f2 + h11 * m2F);
    }

    private static LocalPose3D InterpolateLinearOrEased(AnimKeyframe3D a, AnimKeyframe3D b, double rawT)
    {
        double t = ApplyEasing(Math.Clamp(rawT, 0.0, 1.0), b.Easing);
        return new LocalPose3D(
            Offset: (a.Offset.X + (b.Offset.X - a.Offset.X) * t, a.Offset.Z + (b.Offset.Z - a.Offset.Z) * t),
            Angle: a.Angle + DeltaAngleDeg(a.Angle, b.Angle) * t,
            SagittalPitch: a.SagittalPitch + (b.SagittalPitch - a.SagittalPitch) * t,
            CoronalFlap: a.CoronalFlap + (b.CoronalFlap - a.CoronalFlap) * t);
    }

    private static double ApplyEasing(double t, string easing) => easing switch
    {
        "Linear" => t,
        "SmoothStep" => t * t * (3.0 - 2.0 * t),
        "EaseInQuad" => t * t,
        "EaseOutQuad" => 1.0 - (1.0 - t) * (1.0 - t),
        "EaseInCubic" => t * t * t,
        "EaseOutCubic" => 1.0 - Math.Pow(1.0 - t, 3.0),
        "EaseOutBack" => 1.0 + 2.70158 * Math.Pow(t - 1.0, 3.0) + 1.70158 * Math.Pow(t - 1.0, 2.0),
        "Sine" => 0.5 - 0.5 * Math.Cos(t * Math.PI),
        _ => t * t * (3.0 - 2.0 * t)
    };

    // --- XML Parsing ---

    private static (ParsedRig3D Rig, ParsedAnim3D Anim) LoadRigAndAnim(
        string defsDir,
        string texturesDir,
        string? rigDefName,
        string animDefName)
    {
        if (!Directory.Exists(defsDir))
        {
            throw new DirectoryNotFoundException($"Defs directory not found: {defsDir}");
        }

        XElement? rigEl = null;
        XElement? animEl = null;
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var el in doc.Root.Elements())
            {
                var tag = el.Name.LocalName;
                if (tag.Contains("GiantRigDef", StringComparison.Ordinal))
                {
                    var dn = el.Element("defName")?.Value?.Trim();
                    if (rigEl == null && (string.IsNullOrWhiteSpace(rigDefName) || string.Equals(dn, rigDefName.Trim(), StringComparison.Ordinal)))
                    {
                        rigEl = el;
                    }
                }
                else if (tag.Contains("GiantAnimDef", StringComparison.Ordinal))
                {
                    var dn = el.Element("defName")?.Value?.Trim();
                    if (string.Equals(dn, animDefName, StringComparison.Ordinal))
                    {
                        animEl = el;
                    }
                }
            }
        }

        if (rigEl == null)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(rigDefName)
                    ? "No GiantRigDef found in workspace Defs/."
                    : $"GiantRigDef '{rigDefName}' not found in workspace Defs/.");
        }
        if (animEl == null)
        {
            throw new InvalidOperationException($"GiantAnimDef '{animDefName}' not found in workspace Defs/.");
        }

        return (ParseRig3D(rigEl, texturesDir), ParseAnim3D(animEl));
    }

    private static ParsedRig3D ParseRig3D(XElement rigEl, string texturesDir)
    {
        var defName = rigEl.Element("defName")?.Value?.Trim() ?? "unknown_rig";
        var useCoupled = ParseBool(rigEl.Element("useCoupledSagittalKinematics")?.Value, false);
        var halfAng = ParseDouble(rigEl.Element("lateralViewHalfAngleDeg")?.Value, 45.0);
        var rawMap = new Dictionary<string, Node3D>(StringComparer.Ordinal);
        var rawOrder = new List<string>();

        var nodesEl = rigEl.Element("nodes") ?? throw new InvalidDataException($"Rig '{defName}' is missing <nodes>.");
        foreach (var li in nodesEl.Elements("li"))
        {
            var nid = li.Element("nodeId")?.Value?.Trim() ?? throw new InvalidDataException("Node missing <nodeId>.");
            var pid = EmptyToNull(li.Element("parentId")?.Value);
            var texRel = li.Element("texPath")?.Value?.Trim() ?? throw new InvalidDataException($"Node '{nid}' missing <texPath>.");
            var img = PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, texRel)));
            var silRel = EmptyToNull(li.Element("silhouetteTexPath")?.Value);
            var silImg = silRel != null ? PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, silRel))) : null;

            int res = ParseInt(li.Element("textureResolution")?.Value, img.Width);
            var ds = ParseVec2(li.Element("drawSize")?.Value, (1.0, 1.0));
            var pivPx = ParseVec2(li.Element("pivotPixel")?.Value, (res * 0.5, res * 0.5));
            var pivUv = ParseVec2(li.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));

            var baseVar = new Variant3D
            {
                Img = img,
                SilImg = silImg,
                Res = res,
                Ds = ds,
                Piv = pivUv,
                RawSockPx = li.Element("parentSocketPixel")?.Value,
                RawSockUv = li.Element("parentSocketUV")?.Value,
                LayerOffset = ParseDouble(li.Element("layerOffset")?.Value, 0.0),
                SilLayer = ParseDouble(li.Element("silhouetteLayerOffset")?.Value, -9.2)
            };

            var node = new Node3D
            {
                Id = nid,
                ParentId = pid,
                BaseVar = baseVar,
                LateralHalfDeg = halfAng,
                BaseAngle = ParseDouble(li.Element("baseAngle")?.Value, 0.0),
                LocalOffset = ParseVec2(li.Element("localOffset")?.Value, (0.0, 0.0)),
                BaseScale = ParseVec2(li.Element("baseScale")?.Value, (1.0, 1.0)),
                SagittalDropCells = ParseDouble(li.Element("sagittalDropCells")?.Value, 0.0),
                IsCoronalFlapHinge = ParseBool(li.Element("isCoronalFlapHinge")?.Value, false),
                BaseCoronalFlap = ParseDouble(li.Element("baseCoronalFlap")?.Value, 0.0),
                IsRigid = ParseBool(li.Element("isRigid")?.Value, false),
                AlphaThreshold = ParseInt(li.Element("alphaThreshold")?.Value, 32),
                SectorSourceBoneId = EmptyToNull(li.Element("sectorSourceBoneId")?.Value)
            };
            node.EastVar = ParseOptVar(li.Element("eastVariant"), node, texturesDir);
            node.WestVar = ParseOptVar(li.Element("westVariant"), node, texturesDir);
            node.HasSelfVariant = node.EastVar?.HasSelfOverride == true || node.WestVar?.HasSelfOverride == true;

            rawMap[nid] = node;
            rawOrder.Add(nid);
        }

        var sorted = new List<Node3D>(rawOrder.Count);
        var idxMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            var p = rawMap[id].ParentId;
            if (p != null && rawMap.ContainsKey(p)) Visit(p);
            idxMap[id] = sorted.Count;
            sorted.Add(rawMap[id]);
        }
        foreach (var id in rawOrder) Visit(id);

        for (int i = 0; i < sorted.Count; i++)
        {
            var n = sorted[i];
            n.ParentIndex = n.ParentId != null && idxMap.TryGetValue(n.ParentId, out var pi) ? pi : -1;
            n.SectorSourceIndex = n.SectorSourceBoneId != null && idxMap.TryGetValue(n.SectorSourceBoneId, out var si) ? si : -1;
            int pRes = n.ParentIndex >= 0 ? sorted[n.ParentIndex].BaseVar.Res : n.BaseVar.Res;
            var sockPx = ParseVec2(n.BaseVar.RawSockPx, (pRes * 0.5, pRes * 0.5));
            n.BaseVar.Sock = ParseVec2(n.BaseVar.RawSockUv, (sockPx.X / pRes, 1.0 - sockPx.Y / pRes));
            if (n.EastVar != null)
            {
                int peRes = n.ParentIndex >= 0 ? (sorted[n.ParentIndex].EastVar?.Res ?? pRes) : pRes;
                var esPx = ParseVec2(n.EastVar.RawSockPx, sockPx);
                n.EastVar.Sock = ParseVec2(n.EastVar.RawSockUv, (esPx.X / peRes, 1.0 - esPx.Y / peRes));
            }
            if (n.WestVar != null)
            {
                int pwRes = n.ParentIndex >= 0 ? (sorted[n.ParentIndex].WestVar?.Res ?? pRes) : pRes;
                var wsPx = ParseVec2(n.WestVar.RawSockPx, sockPx);
                n.WestVar.Sock = ParseVec2(n.WestVar.RawSockUv, (wsPx.X / pwRes, 1.0 - wsPx.Y / pwRes));
            }
        }

        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].ParentIndex >= 0 && !sorted[i].IsRigid && sorted[sorted[i].ParentIndex].FirstArticulatedChildIndex < 0)
            {
                sorted[sorted[i].ParentIndex].FirstArticulatedChildIndex = i;
            }
        }

        var skinned = new List<SkinnedMeshSimple>();
        var smRoot = rigEl.Element("skinnedMeshes");
        if (smRoot != null)
        {
            foreach (var li in smRoot.Elements("li"))
            {
                var texRel = EmptyToNull(li.Element("texPath")?.Value);
                if (texRel == null) continue;
                var smImg = PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, texRel)));
                var silRel = EmptyToNull(li.Element("silhouetteTexPath")?.Value);
                var smSil = silRel != null ? PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, silRel))) : null;
                int res = ParseInt(li.Element("textureResolution")?.Value, smImg.Width);
                var pivPx = ParseVec2(li.Element("pivotPixel")?.Value, (res * 0.5, res * 0.5));
                var pivUv = ParseVec2(li.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));
                var wMin = ParseVec2(li.Element("worldMinXZ")?.Value, (-9999.0, -9999.0));
                var wMax = ParseVec2(li.Element("worldMaxXZ")?.Value, (-9999.0, -9999.0));
                bool suppress = ParseBool(li.Element("suppressMemberBoneSprites")?.Value, true);
                if (suppress && li.Element("memberBoneIds") is XElement memEl)
                {
                    foreach (var mli in memEl.Elements("li"))
                    {
                        var mid = mli.Value.Trim();
                        if (idxMap.TryGetValue(mid, out var mi)) sorted[mi].IsSuppressedBySkinnedMesh = true;
                    }
                }

                var boneIds = li.Element("boneIds")?.Elements("li").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList() ?? [];
                var caps = new List<(string, (double, double), (double, double), double, double)>();
                if (li.Element("capsules") is XElement capsEl)
                {
                    foreach (var cli in capsEl.Elements("li"))
                    {
                        var cbid = EmptyToNull(cli.Element("boneId")?.Value);
                        if (cbid == null) continue;
                        caps.Add((
                            cbid,
                            ParseVec2(cli.Element("startXZ")?.Value, (0.0, 0.0)),
                            ParseVec2(cli.Element("endXZ")?.Value, (0.0, 0.0)),
                            ParseDouble(cli.Element("radius")?.Value, 0.4),
                            ParseDouble(cli.Element("weightMultiplier")?.Value, 1.0)));
                    }
                }

                skinned.Add(new SkinnedMeshSimple
                {
                    MeshId = li.Element("meshId")?.Value?.Trim() ?? "mesh",
                    Img = smImg,
                    SilImg = smSil,
                    AnchorBoneId = EmptyToNull(li.Element("anchorBoneId")?.Value),
                    DrawSize = ParseVec2(li.Element("drawSize")?.Value, (1.0, 1.0)),
                    PivotUv = pivUv,
                    BaseAngle = ParseDouble(li.Element("baseAngle")?.Value, 0.0),
                    LayerOffset = ParseDouble(li.Element("layerOffset")?.Value, 0.0),
                    SilLayer = ParseDouble(li.Element("silhouetteLayerOffset")?.Value, -9.2),
                    HasWorldBoundsXZ = wMin.X > -9000.0 && wMax.X > wMin.X + 1e-4 && wMax.Y > wMin.Y + 1e-4,
                    WorldMinXZ = wMin,
                    WorldMaxXZ = wMax,
                    BoneIds = boneIds,
                    Capsules = caps
                });
            }
        }

        return new ParsedRig3D
        {
            DefName = defName,
            UseCoupledSagittal = useCoupled,
            SortedNodes = sorted,
            NodeIndex = idxMap,
            SkinnedMeshes = skinned
        };
    }

    private static Variant3D? ParseOptVar(XElement? el, Node3D parent, string texturesDir)
    {
        if (el == null) return null;
        var texRel = EmptyToNull(el.Element("texPath")?.Value);
        bool hasSelf = texRel != null || el.Element("drawSize") != null || el.Element("pivotPixel") != null || el.Element("pivotUV") != null || el.Element("layerOffset") != null;
        var img = texRel != null ? PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, texRel))) : parent.BaseVar.Img;
        var silRel = EmptyToNull(el.Element("silhouetteTexPath")?.Value);
        var silImg = silRel != null ? PngCodec.Decode(File.ReadAllBytes(ResolveTexPath(texturesDir, silRel))) : parent.BaseVar.SilImg;
        int res = texRel != null ? img.Width : parent.BaseVar.Res;
        var ds = ParseVec2(el.Element("drawSize")?.Value, parent.BaseVar.Ds);
        var pivPx = ParseVec2(el.Element("pivotPixel")?.Value, (parent.BaseVar.Piv.U * res, (1.0 - parent.BaseVar.Piv.V) * res));
        var pivUv = ParseVec2(el.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));

        return new Variant3D
        {
            Img = img,
            SilImg = silImg,
            Res = res,
            Ds = ds,
            Piv = pivUv,
            RawSockPx = el.Element("parentSocketPixel")?.Value,
            RawSockUv = el.Element("parentSocketUV")?.Value,
            LayerOffset = ParseDouble(el.Element("layerOffset")?.Value, parent.BaseVar.LayerOffset),
            SilLayer = ParseDouble(el.Element("silhouetteLayerOffset")?.Value, parent.BaseVar.SilLayer),
            HasSelfOverride = hasSelf
        };
    }

    private static ParsedAnim3D ParseAnim3D(XElement animEl)
    {
        var dn = animEl.Element("defName")?.Value?.Trim() ?? "unknown_anim";
        var loop = ParseBool(animEl.Element("loop")?.Value, true);
        var dur = Math.Max(1.0, ParseDouble(animEl.Element("durationTicks")?.Value, 60.0));
        var iso = EmptyToNull(animEl.Element("isolatedSubtreeRootBoneId")?.Value);
        var tracks = new Dictionary<string, List<AnimKeyframe3D>>(StringComparer.Ordinal);

        if (animEl.Element("tracks") is XElement trRoot)
        {
            foreach (var trLi in trRoot.Elements("li"))
            {
                var nid = EmptyToNull(trLi.Element("nodeId")?.Value);
                var kfRoot = trLi.Element("keyframes");
                if (nid == null || kfRoot == null) continue;
                var list = new List<AnimKeyframe3D>();
                foreach (var kli in kfRoot.Elements("li"))
                {
                    list.Add(new AnimKeyframe3D
                    {
                        Tick = ParseDouble(kli.Element("tick")?.Value, 0.0),
                        Angle = ParseDouble(kli.Element("angle")?.Value, 0.0),
                        SagittalPitch = ParseDouble(kli.Element("sagittalPitch")?.Value, 0.0),
                        CoronalFlap = ParseDouble(kli.Element("coronalFlap")?.Value, 0.0),
                        Offset = ParseVec2(kli.Element("offset")?.Value, (0.0, 0.0)),
                        Easing = EmptyToNull(kli.Element("easing")?.Value) ?? "SmoothStep"
                    });
                }
                list.Sort((a, b) => a.Tick.CompareTo(b.Tick));
                tracks[nid] = list;
            }
        }

        return new ParsedAnim3D
        {
            DefName = dn,
            Loop = loop,
            DurationTicks = dur,
            IsolatedSubtreeRootBoneId = iso,
            Tracks = tracks
        };
    }

    private static HashSet<int> CollectSubtreeIndices(ParsedRig3D rig, int rootIdx)
    {
        var set = new HashSet<int> { rootIdx };
        for (int i = 0; i < rig.SortedNodes.Count; i++)
        {
            int p = rig.SortedNodes[i].ParentIndex;
            if (p >= 0 && set.Contains(p)) set.Add(i);
        }
        return set;
    }

    private static Variant3D GetSectorVar(Node3D node, string sector) => sector switch
    {
        "East" when node.EastVar != null => node.EastVar,
        "West" when node.WestVar != null => node.WestVar,
        _ => node.BaseVar
    };

    private static string EvaluateSector(double angleDeg, double halfAngleDeg)
    {
        double a = PositiveMod(angleDeg, 360.0);
        if (Math.Abs(a - 90.0) <= halfAngleDeg) return "East";
        if (Math.Abs(a - 270.0) <= halfAngleDeg) return "West";
        return "Dorsal";
    }

    private static string ResolveTexPath(string texturesDir, string texRel) =>
        CreatureWorkspace.ResolveSandboxedTexturePng(
            texturesDir,
            texRel,
            allowBaseNameFallback: true,
            requireExists: true);

    private static double PositiveMod(double x, double m) => ((x % m) + m) % m;

    private static double DeltaAngleDeg(double fromDeg, double toDeg)
    {
        var r = ((toDeg - fromDeg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return Math.Abs(r + 180.0) < 1e-9 ? 180.0 : r;
    }

    private static (double X, double Y) ParseVec2(string? text, (double X, double Y) fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var p = text.Trim().Trim('(', ')').Split(',');
        return p.Length == 2
               && double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && double.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            ? (x, y)
            : fallback;
    }

    private static double ParseDouble(string? text, double fallback) =>
        !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseInt(string? text, int fallback) =>
        !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ParseBool(string? text, bool fallback) =>
        !string.IsNullOrWhiteSpace(text) && bool.TryParse(text.Trim(), out var v) ? v : fallback;

    private static string? EmptyToNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
