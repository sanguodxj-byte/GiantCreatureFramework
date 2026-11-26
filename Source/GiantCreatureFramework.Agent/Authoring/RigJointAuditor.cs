using System.Globalization;
using System.Xml.Linq;
using GiantCreatureFramework.Agent.Imaging;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record RigAuditReport(
    string RigDefName,
    int NodeCount,
    int BridgeCount,
    int MinOutlinePx,
    double MaxInteriorBlackPct,
    double MaxInteriorSeamPct,
    string? MatchedBodyDefName,
    IReadOnlyList<string> Failures,
    RgbaImage? PreviewImage)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>
/// Pure C# 1:1 counterpart of .agents/skills/giant-creature-dev/scripts/audit_rig_joints.py.
/// Audits GiantRigDef rotary socket/pivot alpha disks (>=20/25), parent-child layerOffset deltas (>=0.5),
/// outer silhouette stroke thickness (>=6px), deep-interior black/seam ratios, South Rest &amp; Articulated
/// world continuity (zero joint/bridge gaps), 1:1 BodyDef subtree nesting, <isRigid> rules, and mandatory
/// non-looping <deathAnim>. Also renders the South Rest vs Articulated composite preview PNG.
/// </summary>
public static class RigJointAuditor
{
    private static readonly int[] RadiusSteps = [-6, -3, 0, 3, 6];

    private sealed class VariantData
    {
        public required string TexRel { get; init; }
        public required string TexName { get; init; }
        public required int Res { get; init; }
        public required bool HasSelfOverride { get; init; }
        public required (double X, double Y) Ds { get; init; }
        public required (double X, double Y) PivPx { get; init; }
        public (double X, double Y) SockPx { get; set; }
        public string? RawSockPxText { get; init; }
        public string? RawSockUvText { get; init; }
        public required (double U, double V) Piv { get; init; }
        public (double U, double V) Sock { get; set; }
        public required double LayerOffset { get; init; }
        public required RgbaImage Img { get; init; }
        public string? SilRel { get; init; }
        public RgbaImage? SilImg { get; init; }
        public double SilLayer { get; init; } = -9.2;
    }

    private sealed class NodeRecord
    {
        public required string Id { get; init; }
        public string? Parent { get; init; }
        public required VariantData BaseVariant { get; init; }
        public double BaseAngle { get; init; }
        public int AlphaThreshold { get; init; } = 32;
        public string? LinkedBodyPart { get; init; }
        public string? LinkedBodyPartCustomLabel { get; init; }
        public int LinkedBodyPartIndex { get; init; } = -1;
        public bool HideWhenMissing { get; init; } = true;
        public bool IsRigid { get; init; }
        public bool IsTurret { get; init; }
        public bool ContributesToHitbox { get; init; } = true;
        public bool IsLocomotionDrivePart { get; init; }
        public bool HasWeaponMuzzle { get; init; }
        public double MaxTurretAimAngleDeg { get; init; } = 180.0;
        public double AimAtTargetWeight { get; init; }
        public double ContinuousSpinDegPerTick { get; init; }
        public bool IsReactorCoreBone { get; init; }
        public string? SectorSourceBoneId { get; init; }
        public double HalfAngle { get; init; } = 45.0;
        public VariantData? EastVariant { get; set; }
        public VariantData? WestVariant { get; set; }
        public bool HasSelfVariant { get; set; }
        public List<string> SteeringRefs { get; } = new();
    }

    private sealed class SoftWhipRecord
    {
        public required string Id { get; init; }
        public required string TexRel { get; init; }
        public required string TexName { get; init; }
        public required RgbaImage Img { get; init; }
        public string? RootBoneId { get; init; }
        public double LayerOffset { get; init; } = 0.76;
        public string? TendrilBodyPart { get; init; }
        public string? TendrilBodyPartCustomLabel { get; init; }
    }

    private sealed class ParsedRig
    {
        public required string DefName { get; init; }
        public required Dictionary<string, NodeRecord> Nodes { get; init; }
        public required List<string> SortedOrder { get; init; }
        public required List<List<string>> Chains { get; init; }
        public required List<SoftWhipRecord> SoftWhips { get; init; }
        public required string DeathAnim { get; init; }
        public required HashSet<string> ReferencedAnims { get; init; }
        public bool IsVehicleRig { get; init; }
        public bool HasGroundVehicleLocomotion { get; init; }
        public int MeleeAttackAnimCount { get; init; }
        public List<(string BoneId, string AttackId)> MeleePartAttacks { get; init; } = [];
    }

    private sealed class BodyPartNodeRecord
    {
        public required int Idx { get; init; }
        public required string Def { get; init; }
        public string? CustomLabel { get; init; }
        public required int Parent { get; init; }
        public required double Coverage { get; init; }
    }

    private sealed class ParsedBodyDef
    {
        public required string DefName { get; init; }
        public required List<BodyPartNodeRecord> Records { get; init; }
        public required List<string> CoverageIssues { get; init; }
    }

    private sealed class ParsedAnimDef
    {
        public required string DefName { get; init; }
        public required bool Loop { get; init; }
        public required double DurationTicks { get; init; }
        public required int TrackCount { get; init; }
        public required Dictionary<string, double> NonZeroAngleBones { get; init; }
        public required HashSet<string> NonZeroOffsetBones { get; init; }
        public required List<string> StructuralIssues { get; init; }
    }

    private sealed class WorldPose
    {
        public required double Px { get; init; }
        public required double Pz { get; init; }
        public required double Ang { get; init; }
        public required (double X, double Y) Ds { get; init; }
        public required (double U, double V) Piv { get; init; }
        public required double Layer { get; init; }
        public required string Sector { get; init; }
        public required RgbaImage Img { get; init; }
        public RgbaImage? SilImg { get; init; }
        public required double SilLayer { get; init; }
        public required string TexName { get; init; }
    }

    public static IReadOnlyList<RigAuditReport> AuditCreatureWorkspace(
        string creatureDirectory,
        int minOutlinePx = 6,
        double? maxInteriorBlackPct = null,
        double? maxInteriorSeamPct = null,
        double minLayerDelta = 0.5,
        int minDiskOpaque = 20)
    {
        var defsDir = Path.Combine(creatureDirectory, "Defs");
        var texturesDir = Path.Combine(creatureDirectory, "Textures");
        if (!Directory.Exists(defsDir))
        {
            throw new DirectoryNotFoundException($"Defs directory not found in workspace: {defsDir}");
        }

        var xmlFiles = Directory.EnumerateFiles(defsDir, "*.xml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var rigs = new List<ParsedRig>();
        var bodies = new List<ParsedBodyDef>();
        var anims = new Dictionary<string, ParsedAnimDef>(StringComparer.Ordinal);
        var partTagsByDef = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var abstractPartTagsByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var partParentByDef = new Dictionary<string, string>(StringComparer.Ordinal);
        var parseErrors = new List<string>();

        foreach (var file in xmlFiles)
        {
            var doc = XDocument.Load(file);
            var root = doc.Root;
            if (root == null) continue;

            foreach (var el in root.Elements())
            {
                var tag = el.Name.LocalName;
                if (tag == "BodyPartDef")
                {
                    var tags = el.Element("tags")?.Elements("li")
                        .Select(x => x.Value.Trim())
                        .Where(x => x.Length > 0)
                        .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
                    var absName = el.Attribute("Name")?.Value?.Trim();
                    if (!string.IsNullOrEmpty(absName))
                    {
                        abstractPartTagsByName[absName] = tags;
                    }
                    var bpDefName = el.Element("defName")?.Value?.Trim();
                    if (!string.IsNullOrEmpty(bpDefName))
                    {
                        partTagsByDef[bpDefName] = tags;
                        var parentAttr = el.Attribute("ParentName")?.Value?.Trim();
                        if (!string.IsNullOrEmpty(parentAttr))
                        {
                            partParentByDef[bpDefName] = parentAttr;
                        }
                    }
                }
                else if (tag.Contains("GiantRigDef", StringComparison.Ordinal))
                {
                    AuditXmlFieldsAgainstRuntimeType(el, "GiantCreatureFramework.GiantRigDef", Path.GetFileName(file), parseErrors);
                    try
                    {
                        rigs.Add(ParseSingleRig(el, texturesDir));
                    }
                    catch (Exception ex)
                    {
                        parseErrors.Add($"{el.Element("defName")?.Value ?? Path.GetFileName(file)}: {ex.Message}");
                    }
                }
                else if (tag == "BodyDef")
                {
                    var body = ParseSingleBodyDef(el);
                    if (body != null) bodies.Add(body);
                }
                else if (tag.Contains("GiantAnimDef", StringComparison.Ordinal))
                {
                    AuditXmlFieldsAgainstRuntimeType(el, "GiantCreatureFramework.GiantAnimDef", Path.GetFileName(file), parseErrors);
                    var anim = ParseSingleAnimDef(el);
                    if (anim != null) anims[anim.DefName] = anim;
                }
            }
        }

        foreach (var (bpDef, parentName) in partParentByDef)
        {
            if (abstractPartTagsByName.TryGetValue(parentName, out var pTags) && partTagsByDef.TryGetValue(bpDef, out var cTags))
            {
                cTags.UnionWith(pTags);
            }
        }

        static bool HasCapacityTag(
            string partDefName,
            string requiredTag,
            Dictionary<string, HashSet<string>> customTags)
        {
            if (customTags.TryGetValue(partDefName, out var set) && set.Contains(requiredTag))
            {
                return true;
            }
            return requiredTag switch
            {
                "ConsciousnessSource" => partDefName is "Brain" or "ArtificialBrain",
                "MovingLimbCore" => partDefName is "Leg" or "InsectLeg" or "Paw" or "Hoof" or "MechanicalLeg" or "MechanicalCentipedeBodyFirstRing" or "MechanicalWheel",
                _ => false
            };
        }

        foreach (var body in bodies)
        {
            var hasConsciousness = body.Records.Any(r => HasCapacityTag(r.Def, "ConsciousnessSource", partTagsByDef));
            if (!hasConsciousness)
            {
                parseErrors.Add($"BodyDef '{body.DefName}' lacks ConsciousnessSource (add vanilla ArtificialBrain/Brain inside corePart or <tags><li>ConsciousnessSource</li></tags> on a BodyPartDef; otherwise Consciousness=0% disables pawn)");
            }
            var hasMoving = body.Records.Any(r => HasCapacityTag(r.Def, "MovingLimbCore", partTagsByDef));
            if (!hasMoving)
            {
                parseErrors.Add($"BodyDef '{body.DefName}' lacks MovingLimbCore (add <tags><li>MovingLimbCore</li></tags> on locomotion/lift/hull BodyPartDef; otherwise Moving=0% freezes translation)");
            }
        }

        if (rigs.Count == 0 && parseErrors.Count > 0)
        {
            return
            [
                new RigAuditReport("unparsed_rig", 0, 0, 0, 0, 0, null, parseErrors, null)
            ];
        }

        var reports = new List<RigAuditReport>(Math.Max(1, rigs.Count));
        foreach (var rig in rigs)
        {
            var report = AuditParsedRig(
                rig,
                bodies,
                anims,
                minLayerDelta,
                minDiskOpaque,
                maxRestFoldback: null,
                minOutlinePx,
                maxInteriorBlackPct,
                maxInteriorSeamPct);
            if (parseErrors.Count > 0)
            {
                var combined = parseErrors.Concat(report.Failures).ToList();
                report = report with { Failures = combined };
            }
            reports.Add(report);
        }

        return reports;
    }

    public static IReadOnlyList<RigAuditReport> AuditRigFiles(
        string rigXmlPath,
        string texturesDir,
        string? bodyXmlPath = null,
        string? animXmlPath = null,
        string? targetDefName = null,
        double minLayerDelta = 0.5,
        int minDiskOpaque = 20,
        double? maxRestFoldback = null,
        int minOutlinePx = 6,
        double? maxInteriorBlackPct = null,
        double? maxInteriorSeamPct = null)
    {
        var doc = XDocument.Load(rigXmlPath);
        var root = doc.Root ?? throw new InvalidDataException($"Empty XML root in {rigXmlPath}");

        var bodies = new List<ParsedBodyDef>();
        var effectiveBodyPath = bodyXmlPath ?? InferDefaultBodyXml(rigXmlPath);
        if (!string.IsNullOrWhiteSpace(effectiveBodyPath) && File.Exists(effectiveBodyPath))
        {
            var bDoc = XDocument.Load(effectiveBodyPath);
            if (bDoc.Root != null)
            {
                foreach (var el in bDoc.Root.Elements("BodyDef"))
                {
                    var b = ParseSingleBodyDef(el);
                    if (b != null) bodies.Add(b);
                }
            }
        }

        var anims = new Dictionary<string, ParsedAnimDef>(StringComparer.Ordinal);
        var effectiveAnimPath = animXmlPath ?? InferDefaultAnimXml(rigXmlPath);
        if (!string.IsNullOrWhiteSpace(effectiveAnimPath) && File.Exists(effectiveAnimPath))
        {
            var aDoc = XDocument.Load(effectiveAnimPath);
            if (aDoc.Root != null)
            {
                foreach (var el in aDoc.Root.Elements().Where(e => e.Name.LocalName.Contains("GiantAnimDef", StringComparison.Ordinal)))
                {
                    var a = ParseSingleAnimDef(el);
                    if (a != null) anims[a.DefName] = a;
                }
            }
        }

        var reports = new List<RigAuditReport>();
        foreach (var child in root.Elements().Where(e => e.Name.LocalName.Contains("GiantRigDef", StringComparison.Ordinal)))
        {
            var dn = child.Element("defName")?.Value?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(targetDefName) && !string.Equals(dn, targetDefName, StringComparison.Ordinal))
            {
                continue;
            }
            var parsedRig = ParseSingleRig(child, texturesDir);
            reports.Add(AuditParsedRig(
                parsedRig,
                bodies,
                anims.Count > 0 ? anims : null,
                minLayerDelta,
                minDiskOpaque,
                maxRestFoldback,
                minOutlinePx,
                maxInteriorBlackPct,
                maxInteriorSeamPct));
        }

        return reports;
    }

    private static RigAuditReport AuditParsedRig(
        ParsedRig rig,
        IReadOnlyList<ParsedBodyDef>? bodyDefs,
        IReadOnlyDictionary<string, ParsedAnimDef>? animDefs,
        double minLayerDelta,
        int minDiskOpaque,
        double? maxRestFoldback,
        int minOutlinePx,
        double? maxInteriorBlackPct,
        double? maxInteriorSeamPct)
    {
        var failures = new List<string>();
        int? minObservedSpx = null;
        var maxObservedBlackPct = 0.0;
        var maxObservedSeamPct = 0.0;
        string? matchedBodyName = null;

        if (bodyDefs is { Count: > 0 })
        {
            List<string>? bestIssues = null;
            foreach (var bInfo in bodyDefs)
            {
                var candIssues = AuditAnatomicalTreeAlignment(rig.DefName, rig.Nodes, rig.SortedOrder, rig.SoftWhips, bInfo);
                if (bestIssues == null || candIssues.Count < bestIssues.Count)
                {
                    bestIssues = candIssues;
                    matchedBodyName = bInfo.DefName;
                }
            }
            if (bestIssues is { Count: > 0 })
            {
                failures.AddRange(bestIssues);
            }
        }

        var hasVariants = rig.Nodes.Values.Any(n => n.HasSelfVariant);
        var sectorsToCheck = hasVariants ? new[] { "Dorsal", "East", "West" } : new[] { "Dorsal" };

        // 1. Layer deltas & alpha disks
        foreach (var nid in rig.SortedOrder)
        {
            var n = rig.Nodes[nid];
            var pid = n.Parent;
            foreach (var sec in sectorsToCheck)
            {
                var nv = GetNodeSectorData(n, sec);
                var pivPx = (nv.Piv.U * nv.Res, (1.0 - nv.Piv.V) * nv.Res);
                var (cOk, cCnt, cTot) = CheckAlphaDisk(nv.Img, pivPx.Item1, pivPx.Item2, n.AlphaThreshold);
                if (!cOk || cCnt < minDiskOpaque)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Sparse pivot alpha on {nid}[{sec}] px=({pivPx.Item1:F1},{pivPx.Item2:F1}): center={cOk} {cCnt}/{cTot}"));
                }
            }

            if (string.IsNullOrEmpty(pid) || !rig.Nodes.TryGetValue(pid, out var p))
            {
                continue;
            }

            var lockedToParent = string.Equals(n.SectorSourceBoneId, pid, StringComparison.Ordinal);
            foreach (var pSec in sectorsToCheck)
            {
                var pv = GetNodeSectorData(p, pSec);
                var sockV = GetNodeSectorData(n, pSec);
                var sockPx = (sockV.Sock.U * pv.Res, (1.0 - sockV.Sock.V) * pv.Res);
                var (sOk, sCnt, sTot) = CheckAlphaDisk(pv.Img, sockPx.Item1, sockPx.Item2, p.AlphaThreshold);
                if (!sOk || sCnt < minDiskOpaque)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Sparse socket alpha on {pid}[{pSec}] for {nid} px=({sockPx.Item1:F1},{sockPx.Item2:F1}): center={sOk} {sCnt}/{sTot}"));
                }

                var childSecs = lockedToParent ? new[] { pSec } : sectorsToCheck;
                foreach (var nSec in childSecs)
                {
                    var nv = GetNodeSectorData(n, nSec);
                    var delta = Math.Abs(pv.LayerOffset - nv.LayerOffset);
                    if (delta + 1e-4 < minLayerDelta)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Layer delta too small: {pid}[{pSec}]({pv.LayerOffset}) -> {nid}[{nSec}]({nv.LayerOffset}) delta={delta:F3}"));
                    }
                }
            }
        }

        AuditPhysicalLayerInvariants(rig, hasVariants, minLayerDelta, failures);

        // 1b. Outer silhouette outline thickness & interior quality
        var seenTex = new HashSet<(string TexRel, bool HasSil, bool IsOpen)>();
        var outlineTargets = new List<(string ItemId, string TexRel, string TexName, RgbaImage Img, RgbaImage? SilImg, bool IsOpen)>();
        foreach (var nid in rig.SortedOrder)
        {
            var n = rig.Nodes[nid];
            var bv = n.BaseVariant;
            outlineTargets.Add((nid, bv.TexRel, bv.TexName, bv.Img, bv.SilImg, false));
            if (n.EastVariant != null)
            {
                outlineTargets.Add(($"{nid}[East]", n.EastVariant.TexRel, n.EastVariant.TexName, n.EastVariant.Img, n.EastVariant.SilImg, false));
            }
            if (n.WestVariant != null)
            {
                outlineTargets.Add(($"{nid}[West]", n.WestVariant.TexRel, n.WestVariant.TexName, n.WestVariant.Img, n.WestVariant.SilImg, false));
            }
        }
        foreach (var sw in rig.SoftWhips)
        {
            outlineTargets.Add((sw.Id, sw.TexRel, sw.TexName, sw.Img, null, true));
        }

        foreach (var (itemId, texRel, texName, img, silImg, isOpen) in outlineTargets)
        {
            var key = (texRel, silImg != null, isOpen);
            if (!seenTex.Add(key))
            {
                continue;
            }

            int spx;
            if (silImg != null)
            {
                var plateSpx = MeasureOutlineStrokePx(img, openBottom: isOpen);
                if (plateSpx > 0)
                {
                    failures.Add($"Union hull plate {itemId} ({texName}) must have 0 internal outline, found stroke={plateSpx}px");
                }
                var silResized = (silImg.Width == img.Width && silImg.Height == img.Height)
                    ? silImg
                    : PreviewRenderer.LanczosResize(silImg, img.Width, img.Height);
                var compIm = AlphaCompositeSameSize(silResized, img);
                spx = MeasureOutlineStrokePx(compIm, openBottom: isOpen);
            }
            else
            {
                spx = MeasureOutlineStrokePx(img, openBottom: isOpen);
            }

            minObservedSpx = minObservedSpx.HasValue ? Math.Min(minObservedSpx.Value, spx) : spx;
            if (minOutlinePx > 0 && spx < minOutlinePx)
            {
                failures.Add($"Outline too thin on {itemId} ({texName}): stroke={spx}px < {minOutlinePx}px");
            }

            var (blkRatio, deepCnt) = BoneWarper.MeasureInteriorBlackRatio(img, openBottom: isOpen);
            var (seamRatio, _) = BoneWarper.MeasureInteriorSeamRatio(img, openBottom: isOpen);
            var blkPct = blkRatio * 100.0;
            var seamPct = seamRatio * 100.0;
            if (deepCnt >= 200)
            {
                maxObservedBlackPct = Math.Max(maxObservedBlackPct, blkPct);
                maxObservedSeamPct = Math.Max(maxObservedSeamPct, seamPct);
                if (maxInteriorBlackPct is >= 0.0 && blkPct > maxInteriorBlackPct.Value)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Interior black ink too high on {itemId} ({texName}): {blkPct:F2}% > {maxInteriorBlackPct.Value:F2}%"));
                }
                if (maxInteriorSeamPct is >= 0.0 && seamPct > maxInteriorSeamPct.Value)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Interior valley seam too high on {itemId} ({texName}): {seamPct:F2}% > {maxInteriorSeamPct.Value:F2}%"));
                }
            }
        }

        // 2. Rest & Articulated pose world continuity
        var restWorld = SolveWorldPoses(rig.Nodes, rig.SortedOrder, headingDeg: 180.0);
        var (bridges, bentOverrides) = BuildGenericBridgesAndArticulation(rig.Nodes, rig.SortedOrder, rig.Chains);
        var bentWorld = SolveWorldPoses(rig.Nodes, rig.SortedOrder, headingDeg: 180.0, rotOverrides: bentOverrides);

        var posesToCheck = new List<(string Label, Dictionary<string, WorldPose> World)>
        {
            ("Rest", restWorld),
            ("Articulated", bentWorld)
        };

        if (hasVariants)
        {
            var eastWorld = SolveWorldPoses(rig.Nodes, rig.SortedOrder, headingDeg: 90.0);
            var westWorld = SolveWorldPoses(rig.Nodes, rig.SortedOrder, headingDeg: 270.0);
            var chainRoots = rig.Chains
                .Where(c => c.Count > 0 && rig.Nodes.TryGetValue(c[0], out var cn) && !string.IsNullOrEmpty(cn.Parent))
                .Select(c => c[0])
                .ToHashSet(StringComparer.Ordinal);
            var crossRot = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var nid in rig.SortedOrder)
            {
                var n = rig.Nodes[nid];
                if (chainRoots.Contains(nid) ||
                    (!string.IsNullOrEmpty(n.Parent) &&
                     !string.Equals(n.SectorSourceBoneId, n.Parent, StringComparison.Ordinal) &&
                     n.HasSelfVariant))
                {
                    crossRot[nid] = -90.0;
                }
            }
            var crossWorld = SolveWorldPoses(rig.Nodes, rig.SortedOrder, headingDeg: 90.0, rotOverrides: crossRot);
            posesToCheck.Add(("East_90", eastWorld));
            posesToCheck.Add(("West_270", westWorld));
            posesToCheck.Add(("CrossSector", crossWorld));
        }

        if (maxRestFoldback.HasValue)
        {
            foreach (var nid in rig.SortedOrder)
            {
                var n = rig.Nodes[nid];
                var pid = n.Parent;
                if (string.IsNullOrEmpty(pid) || !rig.Nodes.TryGetValue(pid, out var p) || string.IsNullOrEmpty(p.Parent))
                {
                    continue;
                }
                if (Math.Abs(Math.Abs(n.BaseAngle) - 180.0) < 30.0 || Math.Abs(Math.Abs(p.BaseAngle) - 180.0) < 30.0)
                {
                    continue;
                }
                var diff = Math.Abs(PositiveMod(restWorld[nid].Ang - restWorld[pid].Ang + 180.0, 360.0) - 180.0);
                if (diff > maxRestFoldback.Value)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Rest foldback on {pid}->{nid}: |DeltaAngle|={diff:F1} deg (>{maxRestFoldback.Value:F1})"));
                }
            }
        }

        foreach (var (label, world) in posesToCheck)
        {
            foreach (var nid in rig.SortedOrder)
            {
                var pid = rig.Nodes[nid].Parent;
                if (string.IsNullOrEmpty(pid)) continue;
                var jx = world[nid].Px;
                var jz = world[nid].Pz;
                if (!(IsWorldOpaque(pid, jx, jz, rig.Nodes, world) && IsWorldOpaque(nid, jx, jz, rig.Nodes, world)))
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Joint world gap [{label}] {pid}->{nid} at ({jx:F2},{jz:F2})"));
                }
            }

            foreach (var (a, b) in bridges)
            {
                var ax = world[a].Px;
                var az = world[a].Pz;
                var bx = world[b].Px;
                var bz = world[b].Pz;
                for (var k = 0; k < 17; k++)
                {
                    var t = k / 16.0;
                    var sx = ax + (bx - ax) * t;
                    var sz = az + (bz - az) * t;
                    if (!rig.SortedOrder.Any(m => IsWorldOpaque(m, sx, sz, rig.Nodes, world)))
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Axial bridge gap [{label}] {a}->{b} k={k}/16 at ({sx:F2},{sz:F2})"));
                    }
                }
            }
        }

        // 3. Mandatory <deathAnim> & <isRigid> constraints
        if (string.IsNullOrWhiteSpace(rig.DeathAnim))
        {
            failures.Add($"{rig.DefName}: missing mandatory <deathAnim>");
        }
        else if (animDefs != null)
        {
            if (!animDefs.TryGetValue(rig.DeathAnim, out var da))
            {
                failures.Add($"{rig.DefName}: <deathAnim> '{rig.DeathAnim}' not found in GiantAnimDefs");
            }
            else
            {
                if (da.Loop)
                {
                    failures.Add($"{rig.DefName}: <deathAnim> '{rig.DeathAnim}' must have <loop>false</loop>");
                }
                if (da.DurationTicks < 20.0 || da.TrackCount <= 0)
                {
                    failures.Add($"{rig.DefName}: <deathAnim> '{rig.DeathAnim}' requires durationTicks>=20 and >=1 track");
                }
            }
        }

        foreach (var nid in rig.SortedOrder)
        {
            var n = rig.Nodes[nid];
            if (!n.IsRigid) continue;
            if (string.IsNullOrEmpty(n.Parent))
            {
                failures.Add($"{rig.DefName}: root bone '{nid}' cannot set <isRigid>true</isRigid>");
            }
            if (n.AimAtTargetWeight > 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{rig.DefName}: rigid bone '{nid}' cannot set aimAtTargetWeight={n.AimAtTargetWeight}"));
            }
            if (Math.Abs(n.ContinuousSpinDegPerTick) > 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{rig.DefName}: rigid bone '{nid}' cannot set continuousSpinDegPerTick={n.ContinuousSpinDegPerTick}"));
            }
            foreach (var kind in n.SteeringRefs)
            {
                failures.Add($"{rig.DefName}: rigid bone '{nid}' cannot be bound to {kind}");
            }
            if (animDefs != null)
            {
                foreach (var clipName in rig.ReferencedAnims.OrderBy(c => c, StringComparer.Ordinal))
                {
                    if (animDefs.TryGetValue(clipName, out var clipInfo) &&
                        clipInfo.NonZeroAngleBones.TryGetValue(nid, out var maxAng))
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{rig.DefName}: clip '{clipName}' sets non-zero angle ({maxAng:F2} deg) on <isRigid> bone '{nid}'"));
                    }
                }
            }
        }

        if (rig.IsVehicleRig)
        {
            if (rig.MeleeAttackAnimCount > 0)
            {
                failures.Add($"{rig.DefName}: vehicle rig cannot define <meleeAttackAnims> (found {rig.MeleeAttackAnimCount})");
            }
            foreach (var (boneId, attackId) in rig.MeleePartAttacks)
            {
                failures.Add($"{rig.DefName}: vehicle rig bone '{boneId}' partAttack '{attackId}' cannot use <kind>Melee</kind>");
            }
        }

        if (animDefs != null)
        {
            foreach (var clipName in rig.ReferencedAnims.OrderBy(c => c, StringComparer.Ordinal))
            {
                if (!animDefs.TryGetValue(clipName, out var clipInfo)) continue;
                foreach (var issue in clipInfo.StructuralIssues)
                {
                    failures.Add($"{rig.DefName}: {issue}");
                }
                foreach (var offBone in clipInfo.NonZeroOffsetBones)
                {
                    if (rig.Nodes.TryGetValue(offBone, out var offNode) && !string.IsNullOrEmpty(offNode.Parent))
                    {
                        failures.Add($"{rig.DefName}: clip '{clipName}' sets non-zero <offset> on child bone '{offBone}' (only root bone may translate)");
                    }
                    else if (rig.HasGroundVehicleLocomotion && !string.Equals(clipName, rig.DeathAnim, StringComparison.Ordinal))
                    {
                        failures.Add($"{rig.DefName}: ground vehicle clip '{clipName}' sets non-zero <offset> on root bone '{offBone}' (vehicle hulls must stay unshifted; use <turretRecoil> on barrel)");
                    }
                }
            }
        }

        // Render side-by-side preview of Rest & Articulated poses
        RgbaImage? preview = null;
        try
        {
            var restPanel = RenderPoseComposite(rig.Nodes, rig.SortedOrder, restWorld, canvasW: 460, canvasH: 510, scalePx: 20.0);
            var bentPanel = RenderPoseComposite(rig.Nodes, rig.SortedOrder, bentWorld, canvasW: 460, canvasH: 510, scalePx: 20.0);
            preview = PreviewRenderer.RenderCheckerboardPreview(
                [restPanel, bentPanel],
                [$"{rig.DefName} Rest", $"{rig.DefName} Articulated"],
                tileSize: 420,
                cols: 2);
        }
        catch
        {
            // Non-fatal if preview rendering fails
        }

        return new RigAuditReport(
            rig.DefName,
            rig.SortedOrder.Count,
            bridges.Count,
            minObservedSpx ?? 0,
            Math.Round(maxObservedBlackPct, 2),
            Math.Round(maxObservedSeamPct, 2),
            matchedBodyName,
            failures,
            preview);
    }

    public static int MeasureOutlineStrokePx(RgbaImage image, bool openBottom = false, double lumThreshold = 45.0)
    {
        var w = image.Width;
        var h = image.Height;
        var n = w * h;
        var px = image.Pixels;
        var fg = new byte[n];
        var anyFg = false;
        for (var i = 0; i < n; i++)
        {
            fg[i] = px[i * 4 + 3] >= 128 ? (byte)1 : (byte)0;
            anyFg |= fg[i] != 0;
        }
        if (!anyFg) return 0;

        var pw = w + 2;
        var ph = h + 2;
        var padded = new byte[pw * ph];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                padded[(y + 1) * pw + x + 1] = fg[y * w + x];
            }
        }
        if (openBottom)
        {
            for (var x = 0; x < w; x++)
            {
                padded[(h + 1) * pw + x + 1] = fg[(h - 1) * w + x];
            }
        }

        var paddedDist = DistanceField.Compute(padded, pw, ph);
        var distIn = new float[n];
        var lum = new float[n];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                distIn[i] = paddedDist[(y + 1) * pw + x + 1];
                lum[i] = 0.299f * px[i * 4] + 0.587f * px[i * 4 + 1] + 0.114f * px[i * 4 + 2];
            }
        }

        var strokePx = 0;
        var bandValues = new List<float>(2048);
        for (var d = 1; d <= 15; d++)
        {
            bandValues.Clear();
            var lo = d - 0.5f;
            var hi = d + 0.5f;
            for (var i = 0; i < n; i++)
            {
                var dist = distIn[i];
                if (dist >= lo && dist < hi)
                {
                    bandValues.Add(lum[i]);
                }
            }
            if (bandValues.Count == 0) break;
            bandValues.Sort();
            var mid = bandValues.Count / 2;
            var median = (bandValues.Count & 1) == 1
                ? bandValues[mid]
                : 0.5 * (bandValues[mid - 1] + bandValues[mid]);
            if (median < lumThreshold)
            {
                strokePx = d;
            }
            else
            {
                break;
            }
        }
        return strokePx;
    }

    private static (bool CenterOk, int OpaqueCount, int TotalPoints) CheckAlphaDisk(
        RgbaImage img, double px, double py, int threshold)
    {
        var w = img.Width;
        var h = img.Height;
        var cx = Math.Clamp((int)Math.Round(px, MidpointRounding.AwayFromZero), 0, w - 1);
        var cy = Math.Clamp((int)Math.Round(py, MidpointRounding.AwayFromZero), 0, h - 1);
        var centerOk = img.GetA(cx, cy) >= threshold;
        var cnt = 0;
        foreach (var dy in RadiusSteps)
        {
            foreach (var dx in RadiusSteps)
            {
                var sx = Math.Clamp((int)Math.Round(px + dx, MidpointRounding.AwayFromZero), 0, w - 1);
                var sy = Math.Clamp((int)Math.Round(py + dy, MidpointRounding.AwayFromZero), 0, h - 1);
                if (img.GetA(sx, sy) >= threshold)
                {
                    cnt++;
                }
            }
        }
        return (centerOk, cnt, RadiusSteps.Length * RadiusSteps.Length);
    }

    private static string EvaluateSector(double angleDeg, double halfAngleDeg = 45.0)
    {
        var a = PositiveMod(angleDeg, 360.0);
        if (Math.Abs(a - 90.0) <= halfAngleDeg + 1e-3) return "East";
        if (Math.Abs(a - 270.0) <= halfAngleDeg + 1e-3) return "West";
        return "Dorsal";
    }

    private static VariantData GetNodeSectorData(NodeRecord node, string sector)
    {
        if (sector == "East" && node.EastVariant != null) return node.EastVariant;
        if (sector == "West" && node.WestVariant != null) return node.WestVariant;
        return node.BaseVariant;
    }

    private static Dictionary<string, WorldPose> SolveWorldPoses(
        Dictionary<string, NodeRecord> nodes,
        List<string> order,
        double headingDeg = 180.0,
        Dictionary<string, double>? rotOverrides = null,
        Dictionary<string, string>? sectorOverrides = null)
    {
        var world = new Dictionary<string, WorldPose>(StringComparer.Ordinal);
        foreach (var nid in order)
        {
            var n = nodes[nid];
            var pid = n.Parent;
            var hasParent = !string.IsNullOrEmpty(pid) && world.ContainsKey(pid);
            var rotOv = (hasParent && n.IsRigid)
                ? 0.0
                : (rotOverrides != null && rotOverrides.TryGetValue(nid, out var ro) ? ro : 0.0);
            var ang = (hasParent ? world[pid!].Ang : headingDeg) + n.BaseAngle + rotOv;

            string selfSec;
            if (sectorOverrides != null && sectorOverrides.TryGetValue(nid, out var so))
            {
                selfSec = so;
            }
            else if (!n.HasSelfVariant)
            {
                selfSec = "Dorsal";
            }
            else if (!string.IsNullOrEmpty(n.SectorSourceBoneId) && world.TryGetValue(n.SectorSourceBoneId, out var srcPose))
            {
                selfSec = srcPose.Sector;
            }
            else
            {
                selfSec = EvaluateSector(ang, n.HalfAngle);
            }

            var selfV = GetNodeSectorData(n, selfSec);
            double px, pz;
            if (!hasParent)
            {
                px = 0.0;
                pz = 0.0;
            }
            else
            {
                var pw = world[pid!];
                var parentSec = pw.Sector;
                var pv = GetNodeSectorData(nodes[pid!], parentSec);
                var sockV = GetNodeSectorData(n, parentSec);
                var du = (sockV.Sock.U - pv.Piv.U) * pv.Ds.X;
                var dv = (sockV.Sock.V - pv.Piv.V) * pv.Ds.Y;
                var rad = pw.Ang * Math.PI / 180.0;
                var c = Math.Cos(rad);
                var s = Math.Sin(rad);
                px = pw.Px + du * c + dv * s;
                pz = pw.Pz - du * s + dv * c;
            }

            world[nid] = new WorldPose
            {
                Px = px,
                Pz = pz,
                Ang = ang,
                Ds = selfV.Ds,
                Piv = selfV.Piv,
                Layer = selfV.LayerOffset,
                Sector = selfSec,
                Img = selfV.Img,
                SilImg = selfV.SilImg,
                SilLayer = selfV.SilLayer,
                TexName = selfV.TexName
            };
        }
        return world;
    }

    private static bool IsWorldOpaque(
        string nid,
        double wx,
        double wz,
        Dictionary<string, NodeRecord> nodes,
        Dictionary<string, WorldPose> world)
    {
        var pw = world[nid];
        var n = nodes[nid];
        var dx = wx - pw.Px;
        var dz = wz - pw.Pz;
        var rad = pw.Ang * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        var lx = dx * c - dz * s;
        var lz = dx * s + dz * c;
        var u = pw.Piv.U + lx / pw.Ds.X;
        var v = pw.Piv.V + lz / pw.Ds.Y;
        if (u < 0.0 || u > 1.0 || v < 0.0 || v > 1.0)
        {
            return false;
        }
        var w = pw.Img.Width;
        var h = pw.Img.Height;
        var x = Math.Clamp((int)Math.Round(u * (w - 1), MidpointRounding.AwayFromZero), 0, w - 1);
        var y = Math.Clamp((int)Math.Round((1.0 - v) * (h - 1), MidpointRounding.AwayFromZero), 0, h - 1);
        return pw.Img.GetA(x, y) >= n.AlphaThreshold;
    }

    private static (List<(string A, string B)> Bridges, Dictionary<string, double> BentOverrides) BuildGenericBridgesAndArticulation(
        Dictionary<string, NodeRecord> nodes,
        List<string> order,
        List<List<string>> chains)
    {
        var bridges = new List<(string A, string B)>();
        var seenEdges = new HashSet<(string, string)>();
        var bentOverrides = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var rawChain in chains)
        {
            var validChain = rawChain.Where(nodes.ContainsKey).ToList();
            for (var i = 0; i + 1 < validChain.Count; i++)
            {
                var a = validChain[i];
                var b = validChain[i + 1];
                if (nodes[b].Parent == a || nodes[a].Parent == b)
                {
                    if (seenEdges.Add((a, b)))
                    {
                        bridges.Add((a, b));
                    }
                }
            }

            var count = validChain.Count;
            if (count == 1)
            {
                if (!nodes[validChain[0]].IsRigid && !bentOverrides.ContainsKey(validChain[0]))
                {
                    bentOverrides[validChain[0]] = 22.0;
                }
            }
            else if (count > 1)
            {
                var mid = count / 2;
                for (var idx = 0; idx < count; idx++)
                {
                    var nid = validChain[idx];
                    if (nodes[nid].IsRigid || bentOverrides.ContainsKey(nid)) continue;
                    bentOverrides[nid] = idx < mid ? 25.0 : -25.0;
                }
            }
        }

        if (bentOverrides.Count == 0)
        {
            var rootIds = order.Where(nid => string.IsNullOrEmpty(nodes[nid].Parent)).ToHashSet(StringComparer.Ordinal);
            for (var idx = 0; idx < order.Count; idx++)
            {
                var nid = order[idx];
                var p = nodes[nid].Parent;
                if (!string.IsNullOrEmpty(p) && rootIds.Contains(p) && !nodes[nid].IsRigid)
                {
                    bentOverrides[nid] = (idx % 2 == 0) ? 20.0 : -20.0;
                }
            }
        }

        return (bridges, bentOverrides);
    }

    private static List<string> AuditAnatomicalTreeAlignment(
        string defName,
        Dictionary<string, NodeRecord> nodes,
        List<string> order,
        List<SoftWhipRecord> softWhips,
        ParsedBodyDef bodyInfo)
    {
        var issues = new List<string>(bodyInfo.CoverageIssues);
        var records = bodyInfo.Records;
        var bodyName = bodyInfo.DefName;
        var resolvedByBone = new Dictionary<string, BodyPartNodeRecord>(StringComparer.Ordinal);
        var severableOwners = new Dictionary<int, string>();

        foreach (var nid in order)
        {
            var n = nodes[nid];
            if (string.IsNullOrEmpty(n.LinkedBodyPart)) continue;
            var rec = ResolveBodyPartRecord(records, n.LinkedBodyPart, n.LinkedBodyPartCustomLabel, n.LinkedBodyPartIndex);
            if (rec == null)
            {
                issues.Add($"[{defName} vs {bodyName}] Bone '{nid}' failed to resolve linkedBodyPart='{n.LinkedBodyPart}' (customLabel='{n.LinkedBodyPartCustomLabel}')");
                continue;
            }
            resolvedByBone[nid] = rec;
            if (n.HideWhenMissing)
            {
                if (severableOwners.TryGetValue(rec.Idx, out var existingOwner))
                {
                    issues.Add($"[{defName} vs {bodyName}] Severable bones '{existingOwner}' and '{nid}' share BodyPartRecord '{rec.CustomLabel ?? rec.Def}' (#{rec.Idx})");
                }
                else
                {
                    severableOwners[rec.Idx] = nid;
                }
            }
        }

        foreach (var nid in order)
        {
            var n = nodes[nid];
            if (!n.HideWhenMissing || !resolvedByBone.TryGetValue(nid, out var childRec)) continue;
            var pid = n.Parent;
            BodyPartNodeRecord? ancRec = null;
            string? ancBone = null;
            while (!string.IsNullOrEmpty(pid))
            {
                if (resolvedByBone.TryGetValue(pid, out var foundAnc))
                {
                    ancRec = foundAnc;
                    ancBone = pid;
                    break;
                }
                pid = nodes.TryGetValue(pid, out var pNode) ? pNode.Parent : null;
            }

            if (ancRec != null && !IsStrictBodyDescendant(records, childRec, ancRec))
            {
                issues.Add($"[{defName} vs {bodyName}] Child bone '{nid}' (part '{childRec.CustomLabel ?? childRec.Def}') is parented to '{ancBone}' (part '{ancRec.CustomLabel ?? ancRec.Def}'), which is NOT an ancestor in BodyDef <parts>");
            }
        }

        foreach (var sw in softWhips)
        {
            if (string.IsNullOrEmpty(sw.TendrilBodyPart)) continue;
            var tRec = ResolveBodyPartRecord(records, sw.TendrilBodyPart, sw.TendrilBodyPartCustomLabel);
            if (tRec == null)
            {
                issues.Add($"[{defName} vs {bodyName}] SoftWhip '{sw.Id}' failed to resolve tendrilBodyPart='{sw.TendrilBodyPart}' (customLabel='{sw.TendrilBodyPartCustomLabel}')");
                continue;
            }
            if (severableOwners.TryGetValue(tRec.Idx, out var owner))
            {
                issues.Add($"[{defName} vs {bodyName}] SoftWhip '{sw.Id}' collides with '{owner}' on BodyPartRecord '{tRec.CustomLabel ?? tRec.Def}'");
            }
            else
            {
                severableOwners[tRec.Idx] = $"softWhip:{sw.Id}";
            }

            if (!string.IsNullOrEmpty(sw.RootBoneId) && resolvedByBone.TryGetValue(sw.RootBoneId, out var rootPartRec))
            {
                if (!IsStrictBodyDescendant(records, tRec, rootPartRec))
                {
                    issues.Add($"[{defName} vs {bodyName}] SoftWhip '{sw.Id}' tendril '{tRec.CustomLabel ?? tRec.Def}' is NOT nested under rootBoneId '{sw.RootBoneId}' in BodyDef <parts>");
                }
            }
        }

        return issues;
    }

    private static BodyPartNodeRecord? ResolveBodyPartRecord(
        List<BodyPartNodeRecord> records,
        string partDef,
        string? customLabel = null,
        int partIndex = -1)
    {
        if (string.IsNullOrEmpty(partDef)) return null;
        if (!string.IsNullOrEmpty(customLabel))
        {
            foreach (var r in records)
            {
                if (string.Equals(r.Def, partDef, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(r.CustomLabel) &&
                    string.Equals(r.CustomLabel, customLabel, StringComparison.OrdinalIgnoreCase))
                {
                    return r;
                }
            }
        }

        var matches = records.Where(r => string.Equals(r.Def, partDef, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0) return null;
        if (partIndex >= 0) return matches[Math.Min(partIndex, matches.Count - 1)];
        return matches[0];
    }

    private static bool IsStrictBodyDescendant(
        List<BodyPartNodeRecord> records,
        BodyPartNodeRecord childRec,
        BodyPartNodeRecord ancestorRec)
    {
        if (childRec.Idx == ancestorRec.Idx) return false;
        var cur = childRec.Parent;
        while (cur >= 0)
        {
            if (cur == ancestorRec.Idx) return true;
            cur = records[cur].Parent;
        }
        return false;
    }

    private static ParsedRig ParseSingleRig(XElement rigEl, string texturesDir)
    {
        var defName = rigEl.Element("defName")?.Value?.Trim() ?? "unknown_rig";
        var lateralHalfDeg = ParseDouble(rigEl.Element("lateralViewHalfAngleDeg")?.Value, 45.0);
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var order = new List<string>();

        var nodesEl = rigEl.Element("nodes") ?? throw new InvalidDataException($"Rig '{defName}' is missing <nodes>.");
        foreach (var li in nodesEl.Elements("li"))
        {
            var nid = li.Element("nodeId")?.Value?.Trim() ?? throw new InvalidDataException($"Rig '{defName}' has a node without <nodeId>.");
            var pid = li.Element("parentId")?.Value?.Trim();
            if (string.IsNullOrEmpty(pid)) pid = null;

            var texRel = li.Element("texPath")?.Value?.Trim();
            if (string.IsNullOrEmpty(texRel))
            {
                throw new InvalidDataException($"Node {nid} missing texPath");
            }

            var texPath = ResolveTextureFile(texturesDir, texRel);
            var im = PngCodec.Decode(File.ReadAllBytes(texPath));

            var silRel = li.Element("silhouetteTexPath")?.Value?.Trim();
            RgbaImage? silIm = null;
            if (!string.IsNullOrEmpty(silRel))
            {
                var silPath = ResolveTextureFile(texturesDir, silRel);
                silIm = PngCodec.Decode(File.ReadAllBytes(silPath));
            }

            var res = ParseInt(li.Element("textureResolution")?.Value, im.Width);
            var ds = ParseVec2(li.Element("drawSize")?.Value, (1.0, 1.0));
            var pivPx = ParseVec2(li.Element("pivotPixel")?.Value, (res * 0.5, res * 0.5));
            var rawSockPxText = li.Element("parentSocketPixel")?.Value;
            var rawSockUvText = li.Element("parentSocketUV")?.Value;
            var sockPx = ParseVec2(rawSockPxText, (res * 0.5, res * 0.5));
            var pivUv = ParseVec2(li.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));
            var sockUv = ParseVec2(rawSockUvText, (sockPx.X / res, 1.0 - sockPx.Y / res));

            var baseVariant = new VariantData
            {
                TexRel = texRel,
                TexName = Path.GetFileName(texPath),
                Res = res,
                HasSelfOverride = false,
                Ds = ds,
                PivPx = pivPx,
                SockPx = sockPx,
                RawSockPxText = rawSockPxText,
                RawSockUvText = rawSockUvText,
                Piv = pivUv,
                Sock = sockUv,
                LayerOffset = ParseDouble(li.Element("layerOffset")?.Value, 0.0),
                Img = im,
                SilRel = silRel,
                SilImg = silIm,
                SilLayer = ParseDouble(li.Element("silhouetteLayerOffset")?.Value, -9.2)
            };

            var nodeRec = new NodeRecord
            {
                Id = nid,
                Parent = pid,
                BaseVariant = baseVariant,
                BaseAngle = ParseDouble(li.Element("baseAngle")?.Value, 0.0),
                AlphaThreshold = ParseInt(li.Element("alphaThreshold")?.Value, 32),
                LinkedBodyPart = EmptyToNull(li.Element("linkedBodyPart")?.Value),
                LinkedBodyPartCustomLabel = EmptyToNull(li.Element("linkedBodyPartCustomLabel")?.Value),
                LinkedBodyPartIndex = ParseInt(li.Element("linkedBodyPartIndex")?.Value, -1),
                HideWhenMissing = ParseBool(li.Element("hideWhenMissing")?.Value, true),
                IsRigid = ParseBool(li.Element("isRigid")?.Value, false),
                IsTurret = ParseBool(li.Element("isTurret")?.Value, false),
                ContributesToHitbox = ParseBool(li.Element("contributesToHitbox")?.Value, true),
                IsLocomotionDrivePart = ParseBool(li.Element("isLocomotionDrivePart")?.Value, false),
                HasWeaponMuzzle = li.Element("muzzlePixel") != null
                    || li.Element("muzzleUV") != null
                    || li.Element("emitterSockets")?.Elements("li").Any() == true
                    || li.Element("partAttacks")?.Elements("li").Any() == true,
                MaxTurretAimAngleDeg = ParseDouble(li.Element("maxTurretAimAngleDeg")?.Value, 180.0),
                AimAtTargetWeight = ParseDouble(li.Element("aimAtTargetWeight")?.Value, 0.0),
                ContinuousSpinDegPerTick = ParseDouble(li.Element("continuousSpinDegPerTick")?.Value, 0.0),
                IsReactorCoreBone = ParseBool(li.Element("isReactorCoreBone")?.Value, false),
                SectorSourceBoneId = EmptyToNull(li.Element("sectorSourceBoneId")?.Value),
                HalfAngle = lateralHalfDeg
            };

            nodeRec.EastVariant = ParseVariant(li.Element("eastVariant"), nodeRec, texturesDir);
            nodeRec.WestVariant = ParseVariant(li.Element("westVariant"), nodeRec, texturesDir);
            nodeRec.HasSelfVariant = (nodeRec.EastVariant?.HasSelfOverride == true) || (nodeRec.WestVariant?.HasSelfOverride == true);
            nodes[nid] = nodeRec;
            order.Add(nid);
        }

        // Topological sort parent-before-child
        var sortedOrder = new List<string>(order.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string nid)
        {
            if (visited.Contains(nid)) return;
            if (!visiting.Add(nid))
            {
                throw new InvalidDataException($"Cyclic parentId detected at node '{nid}'.");
            }
            var p = nodes[nid].Parent;
            if (!string.IsNullOrEmpty(p) && nodes.ContainsKey(p))
            {
                Visit(p);
            }
            visiting.Remove(nid);
            visited.Add(nid);
            sortedOrder.Add(nid);
        }
        foreach (var nid in order) Visit(nid);

        // Resolve parentSocketPixel/UV using parent's textureResolution
        foreach (var nid in sortedOrder)
        {
            var n = nodes[nid];
            var pid = n.Parent;
            var pNode = (!string.IsNullOrEmpty(pid) && nodes.TryGetValue(pid, out var pn)) ? pn : null;
            var pRes = pNode?.BaseVariant.Res ?? n.BaseVariant.Res;
            var sockPx = ParseVec2(n.BaseVariant.RawSockPxText, (pRes * 0.5, pRes * 0.5));
            n.BaseVariant.SockPx = sockPx;
            n.BaseVariant.Sock = ParseVec2(n.BaseVariant.RawSockUvText, (sockPx.X / pRes, 1.0 - sockPx.Y / pRes));

            if (n.EastVariant != null)
            {
                var pvRes = pNode?.EastVariant?.Res ?? pRes;
                var vsPx = ParseVec2(n.EastVariant.RawSockPxText, sockPx);
                n.EastVariant.SockPx = vsPx;
                n.EastVariant.Sock = ParseVec2(n.EastVariant.RawSockUvText, (vsPx.X / pvRes, 1.0 - vsPx.Y / pvRes));
            }
            if (n.WestVariant != null)
            {
                var pvRes = pNode?.WestVariant?.Res ?? pRes;
                var vsPx = ParseVec2(n.WestVariant.RawSockPxText, sockPx);
                n.WestVariant.SockPx = vsPx;
                n.WestVariant.Sock = ParseVec2(n.WestVariant.RawSockUvText, (vsPx.X / pvRes, 1.0 - vsPx.Y / pvRes));
            }
        }

        var chains = new List<List<string>>();
        var steeringRefs = new List<(string Kind, string BoneId)>();
        var steerEl = rigEl.Element("steering");
        if (steerEl != null)
        {
            var lookChainsEl = steerEl.Element("lookChains");
            if (lookChainsEl != null)
            {
                foreach (var lcEl in lookChainsEl.Elements("li"))
                {
                    var ids = lcEl.Element("boneIds")?.Elements("li")
                        .Select(e => e.Value.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();
                    if (ids is { Count: > 0 })
                    {
                        chains.Add(ids);
                        steeringRefs.AddRange(ids.Select(id => ("lookChain", id)));
                    }
                }
            }
            foreach (var subTag in new[] { "lagBones", "yawBankBones" })
            {
                var subEl = steerEl.Element(subTag);
                if (subEl == null) continue;
                foreach (var itemEl in subEl.Elements("li"))
                {
                    var bid = itemEl.Element("boneId")?.Value?.Trim();
                    if (!string.IsNullOrEmpty(bid))
                    {
                        steeringRefs.Add((subTag, bid));
                    }
                }
            }
        }

        var bezierEl = rigEl.Element("bezierChains");
        if (bezierEl != null)
        {
            foreach (var bcEl in bezierEl.Elements("li"))
            {
                var ids = bcEl.Element("boneIds")?.Elements("li")
                    .Select(e => e.Value.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                if (ids is { Count: > 0 })
                {
                    chains.Add(ids);
                    steeringRefs.AddRange(ids.Select(id => ("bezierChain", id)));
                }
            }
        }

        foreach (var (kind, bid) in steeringRefs)
        {
            if (nodes.TryGetValue(bid, out var nr))
            {
                nr.SteeringRefs.Add(kind);
            }
        }

        var referencedAnims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in new[] { "walkAnim", "runAnim", "rangedAttackAnim", "downedAnim", "deathAnim" })
        {
            var v = rigEl.Element(tag)?.Value?.Trim();
            if (!string.IsNullOrEmpty(v)) referencedAnims.Add(v);
        }
        var aqEl = rigEl.Element("aquaticLocomotion") ?? steerEl?.Element("aquaticLocomotion");
        if (aqEl != null)
        {
            foreach (var aqAnimTag in new[] { "swimIdleAnim", "swimAnim", "swimFastAnim" })
            {
                var v = aqEl.Element(aqAnimTag)?.Value?.Trim();
                if (!string.IsNullOrEmpty(v)) referencedAnims.Add(v);
            }
        }
        var meleeAnimCount = 0;
        foreach (var listTag in new[] { "meleeAttackAnims", "abilityAnims" })
        {
            var lEl = rigEl.Element(listTag);
            if (lEl == null) continue;
            foreach (var li in lEl.Elements("li"))
            {
                var v = li.Value.Trim();
                if (!string.IsNullOrEmpty(v))
                {
                    referencedAnims.Add(v);
                    if (listTag == "meleeAttackAnims") meleeAnimCount++;
                }
            }
        }
        var meleePartAttacks = new List<(string BoneId, string AttackId)>();
        foreach (var nLi in rigEl.Element("nodes")?.Elements("li") ?? [])
        {
            var bId = nLi.Element("nodeId")?.Value?.Trim() ?? "";
            foreach (var atkLi in nLi.Element("partAttacks")?.Elements("li") ?? [])
            {
                var aAnim = atkLi.Element("attackAnim")?.Value?.Trim();
                if (!string.IsNullOrEmpty(aAnim)) referencedAnims.Add(aAnim);
                var kindText = atkLi.Element("kind")?.Value?.Trim();
                if (string.IsNullOrEmpty(kindText) || string.Equals(kindText, "Melee", StringComparison.OrdinalIgnoreCase))
                {
                    meleePartAttacks.Add((bId, atkLi.Element("attackId")?.Value?.Trim() ?? "<unnamed>"));
                }
            }
        }
        static bool HasActiveLocomotionEl(XElement? el, string typeField, string noneName)
        {
            if (el == null) return false;
            var t = el.Element(typeField)?.Value?.Trim();
            return !string.IsNullOrEmpty(t) && !string.Equals(t, noneName, StringComparison.OrdinalIgnoreCase);
        }
        var hasGroundVeh = HasActiveLocomotionEl(rigEl.Element("vehicleLocomotion") ?? steerEl?.Element("vehicleLocomotion"), "chassisType", "None");
        var hasFlightVeh = HasActiveLocomotionEl(rigEl.Element("flightVehicleLocomotion") ?? steerEl?.Element("flightVehicleLocomotion"), "airframeType", "None");

        var softWhips = new List<SoftWhipRecord>();
        var swEl = rigEl.Element("softWhips");
        if (swEl != null)
        {
            foreach (var li in swEl.Elements("li"))
            {
                var swId = EmptyToNull(li.Element("whipId")?.Value) ?? "soft_whip";
                var texRel = li.Element("texPath")?.Value?.Trim();
                if (string.IsNullOrEmpty(texRel)) continue;
                var texPath = ResolveTextureFile(texturesDir, texRel);
                var swIm = PngCodec.Decode(File.ReadAllBytes(texPath));
                softWhips.Add(new SoftWhipRecord
                {
                    Id = swId,
                    TexRel = texRel,
                    TexName = Path.GetFileName(texPath),
                    Img = swIm,
                    RootBoneId = EmptyToNull(li.Element("rootBoneId")?.Value),
                    LayerOffset = ParseDouble(li.Element("layerOffset")?.Value, 0.76),
                    TendrilBodyPart = EmptyToNull(li.Element("tendrilBodyPart")?.Value),
                    TendrilBodyPartCustomLabel = EmptyToNull(li.Element("tendrilBodyPartCustomLabel")?.Value)
                });
            }
        }

        var deathAnim = rigEl.Element("deathAnim")?.Value?.Trim() ?? "";
        return new ParsedRig
        {
            DefName = defName,
            Nodes = nodes,
            SortedOrder = sortedOrder,
            Chains = chains,
            SoftWhips = softWhips,
            DeathAnim = deathAnim,
            ReferencedAnims = referencedAnims,
            IsVehicleRig = hasGroundVeh || hasFlightVeh,
            HasGroundVehicleLocomotion = hasGroundVeh,
            MeleeAttackAnimCount = meleeAnimCount,
            MeleePartAttacks = meleePartAttacks
        };
    }

    private static VariantData? ParseVariant(XElement? varEl, NodeRecord parentNode, string texturesDir)
    {
        if (varEl == null) return null;
        var baseVar = parentNode.BaseVariant;
        var texRel = EmptyToNull(varEl.Element("texPath")?.Value) ?? baseVar.TexRel;
        var texPath = ResolveTextureFile(texturesDir, texRel);
        var im = PngCodec.Decode(File.ReadAllBytes(texPath));

        var silRel = EmptyToNull(varEl.Element("silhouetteTexPath")?.Value) ?? baseVar.SilRel;
        RgbaImage? silIm = null;
        if (!string.IsNullOrEmpty(silRel))
        {
            var silPath = ResolveTextureFile(texturesDir, silRel);
            silIm = PngCodec.Decode(File.ReadAllBytes(silPath));
        }

        var res = ParseInt(varEl.Element("textureResolution")?.Value, im.Width);
        var hasSelfOverride = new[] { "texPath", "silhouetteTexPath", "drawSize", "pivotPixel", "pivotUV", "layerOffset" }
            .Any(k => !string.IsNullOrWhiteSpace(varEl.Element(k)?.Value));
        var ds = ParseVec2(varEl.Element("drawSize")?.Value, baseVar.Ds);
        var pivPx = ParseVec2(varEl.Element("pivotPixel")?.Value, baseVar.PivPx);
        var rawSockPxText = EmptyToNull(varEl.Element("parentSocketPixel")?.Value) ?? baseVar.RawSockPxText;
        var rawSockUvText = EmptyToNull(varEl.Element("parentSocketUV")?.Value) ?? baseVar.RawSockUvText;
        var sockPx = ParseVec2(rawSockPxText, baseVar.SockPx);
        var pivUv = ParseVec2(varEl.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));
        var sockUv = ParseVec2(rawSockUvText, (sockPx.X / res, 1.0 - sockPx.Y / res));

        return new VariantData
        {
            TexRel = texRel,
            TexName = Path.GetFileName(texPath),
            Res = res,
            HasSelfOverride = hasSelfOverride,
            Ds = ds,
            PivPx = pivPx,
            SockPx = sockPx,
            RawSockPxText = rawSockPxText,
            RawSockUvText = rawSockUvText,
            Piv = pivUv,
            Sock = sockUv,
            LayerOffset = ParseDouble(varEl.Element("layerOffset")?.Value, baseVar.LayerOffset),
            Img = im,
            SilRel = silRel,
            SilImg = silIm,
            SilLayer = baseVar.SilLayer
        };
    }

    private static ParsedBodyDef? ParseSingleBodyDef(XElement el)
    {
        var bodyDefName = el.Element("defName")?.Value?.Trim() ?? "unknown_body";
        var coreEl = el.Element("corePart");
        if (coreEl == null) return null;

        var records = new List<BodyPartNodeRecord>();
        var coverageIssues = new List<string>();

        int WalkPart(XElement pEl, int parentIdx)
        {
            var idx = records.Count;
            var pDef = pEl.Element("def")?.Value?.Trim() ?? "";
            var cLabel = EmptyToNull(pEl.Element("customLabel")?.Value);
            var cov = ParseDouble(pEl.Element("coverage")?.Value, parentIdx < 0 ? 1.0 : 0.0);
            records.Add(new BodyPartNodeRecord
            {
                Idx = idx,
                Def = pDef,
                CustomLabel = cLabel,
                Parent = parentIdx,
                Coverage = cov
            });

            var partsEl = pEl.Element("parts");
            if (partsEl != null)
            {
                var covSum = 0.0;
                foreach (var childLi in partsEl.Elements("li"))
                {
                    var cIdx = WalkPart(childLi, idx);
                    covSum += records[cIdx].Coverage;
                }
                if (covSum >= 0.999)
                {
                    coverageIssues.Add(string.Create(CultureInfo.InvariantCulture,
                        $"BodyDef '{bodyDefName}' part '{cLabel ?? pDef}' child <coverage> sum={covSum:F3} >= 1.0"));
                }
            }
            return idx;
        }

        WalkPart(coreEl, -1);
        return new ParsedBodyDef
        {
            DefName = bodyDefName,
            Records = records,
            CoverageIssues = coverageIssues
        };
    }

    private static ParsedAnimDef? ParseSingleAnimDef(XElement el)
    {
        var dn = el.Element("defName")?.Value?.Trim();
        if (string.IsNullOrEmpty(dn)) return null;
        var loopVal = ParseBool(el.Element("loop")?.Value, true);
        var durVal = ParseDouble(el.Element("durationTicks")?.Value, 60.0);
        var tracksEl = el.Element("tracks");
        var trackList = tracksEl?.Elements("li").ToList() ?? [];
        var nonZeroAngleBones = new Dictionary<string, double>(StringComparer.Ordinal);
        var nonZeroOffsetBones = new HashSet<string>(StringComparer.Ordinal);
        var structuralIssues = new List<string>();
        foreach (var trLi in trackList)
        {
            var nid = trLi.Element("nodeId")?.Value?.Trim();
            var kfEl = trLi.Element("keyframes");
            if (string.IsNullOrEmpty(nid) || kfEl == null) continue;
            var maxAbsAng = 0.0;
            foreach (var kfLi in kfEl.Elements("li"))
            {
                var a = Math.Abs(ParseDouble(kfLi.Element("angle")?.Value, 0.0));
                var cf = Math.Abs(ParseDouble(kfLi.Element("coronalFlap")?.Value, 0.0));
                var effRot = Math.Max(a, cf);
                if (effRot > maxAbsAng) maxAbsAng = effRot;

                if (kfLi.Element("offset") is XElement offEl)
                {
                    var off = ParseVec2(offEl.Value, (0.0, 0.0));
                    if (Math.Abs(off.X) > 1e-4 || Math.Abs(off.Y) > 1e-4)
                    {
                        nonZeroOffsetBones.Add(nid);
                    }
                }
                if (kfLi.Element("scale") is XElement scEl)
                {
                    var sc = ParseVec2(scEl.Value, (1.0, 1.0));
                    if (Math.Abs(sc.X - 1.0) > 1e-4 || Math.Abs(sc.Y - 1.0) > 1e-4)
                    {
                        structuralIssues.Add($"clip '{dn}' sets forbidden non-unit <scale> on bone '{nid}'");
                    }
                }
                if (kfLi.Element("easing") is XElement easeEl)
                {
                    var easeName = easeEl.Value.Trim();
                    if (easeName.Length > 0 && !AnimStripPreviewRenderer.ValidEasings.Contains(easeName))
                    {
                        structuralIssues.Add($"clip '{dn}' uses unsupported <easing>{easeName}</easing> on bone '{nid}'");
                    }
                }
            }
            if (maxAbsAng > 1e-4)
            {
                nonZeroAngleBones[nid] = maxAbsAng;
            }
        }

        return new ParsedAnimDef
        {
            DefName = dn,
            Loop = loopVal,
            DurationTicks = durVal,
            TrackCount = trackList.Count,
            NonZeroAngleBones = nonZeroAngleBones,
            NonZeroOffsetBones = nonZeroOffsetBones,
            StructuralIssues = structuralIssues
        };
    }

    private static RgbaImage RenderPoseComposite(
        Dictionary<string, NodeRecord> nodes,
        List<string> order,
        Dictionary<string, WorldPose> world,
        int canvasW = 460,
        int canvasH = 510,
        double scalePx = 20.0)
    {
        var canvas = new RgbaImage(canvasW, canvasH);
        var cp = canvas.Pixels;
        for (var i = 0; i < cp.Length; i += 4)
        {
            cp[i] = 82;
            cp[i + 1] = 74;
            cp[i + 2] = 62;
            cp[i + 3] = 255;
        }

        var drawItems = new List<(double Layer, RgbaImage Src, WorldPose Pose)>();
        foreach (var nid in order)
        {
            var pw = world[nid];
            if (pw.SilImg != null)
            {
                drawItems.Add((pw.SilLayer, pw.SilImg, pw));
            }
            drawItems.Add((pw.Layer, pw.Img, pw));
        }
        drawItems.Sort((a, b) => a.Layer.CompareTo(b.Layer));

        foreach (var (_, src, pw) in drawItems)
        {
            var tw = Math.Max(1, (int)Math.Round(pw.Ds.X * scalePx));
            var th = Math.Max(1, (int)Math.Round(pw.Ds.Y * scalePx));
            var pivTopY = 1.0 - pw.Piv.V;
            var screenX = canvasW * 0.5 + pw.Px * scalePx;
            var screenY = canvasH * 0.5 - (pw.Pz + 2.5) * scalePx;

            var rad = pw.Ang * Math.PI / 180.0;
            var cosA = Math.Cos(rad);
            var sinA = Math.Sin(rad);
            var maxDim = Math.Ceiling(Math.Sqrt(tw * tw + th * th)) + 4;
            var x0 = Math.Max(0, (int)Math.Floor(screenX - maxDim));
            var x1 = Math.Min(canvasW - 1, (int)Math.Ceiling(screenX + maxDim));
            var y0 = Math.Max(0, (int)Math.Floor(screenY - maxDim));
            var y1 = Math.Min(canvasH - 1, (int)Math.Ceiling(screenY + maxDim));

            for (var y = y0; y <= y1; y++)
            {
                var dy = y - screenY;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x - screenX;
                    var lx = dx * cosA + dy * sinA;
                    var ly = -dx * sinA + dy * cosA;
                    var u = pw.Piv.U + lx / tw;
                    var vTop = pivTopY + ly / th;
                    if (u < 0.0 || u > 1.0 || vTop < 0.0 || vTop > 1.0) continue;

                    var sx = Math.Clamp((int)Math.Round(u * (src.Width - 1)), 0, src.Width - 1);
                    var sy = Math.Clamp((int)Math.Round(vTop * (src.Height - 1)), 0, src.Height - 1);
                    var sa = src.GetA(sx, sy);
                    if (sa == 0) continue;

                    var dIdx = (y * canvasW + x) * 4;
                    var sr = src.GetR(sx, sy);
                    var sg = src.GetG(sx, sy);
                    var sb = src.GetB(sx, sy);
                    cp[dIdx] = (byte)((sr * sa + cp[dIdx] * (255 - sa) + 127) / 255);
                    cp[dIdx + 1] = (byte)((sg * sa + cp[dIdx + 1] * (255 - sa) + 127) / 255);
                    cp[dIdx + 2] = (byte)((sb * sa + cp[dIdx + 2] * (255 - sa) + 127) / 255);
                    cp[dIdx + 3] = 255;
                }
            }
        }

        return canvas;
    }

    private static RgbaImage AlphaCompositeSameSize(RgbaImage bottom, RgbaImage top)
    {
        var outImg = bottom.Clone();
        var dp = outImg.Pixels;
        var sp = top.Pixels;
        for (var i = 0; i < dp.Length; i += 4)
        {
            uint sa = sp[i + 3];
            if (sa == 0) continue;
            uint da = dp[i + 3];
            uint outA255 = sa * 255u + da * (255u - sa);
            if (outA255 == 0) continue;
            dp[i] = (byte)((sp[i] * sa * 255u + dp[i] * da * (255u - sa) + outA255 / 2) / outA255);
            dp[i + 1] = (byte)((sp[i + 1] * sa * 255u + dp[i + 1] * da * (255u - sa) + outA255 / 2) / outA255);
            dp[i + 2] = (byte)((sp[i + 2] * sa * 255u + dp[i + 2] * da * (255u - sa) + outA255 / 2) / outA255);
            dp[i + 3] = (byte)((outA255 + 127u) / 255u);
        }
        return outImg;
    }

    private static string ResolveTextureFile(string texturesDir, string texRel) =>
        CreatureWorkspace.ResolveSandboxedTexturePng(
            texturesDir,
            texRel,
            allowBaseNameFallback: false,
            requireExists: true);

    private static string? InferDefaultBodyXml(string rigPath)
    {
        var rigBase = Path.GetFileName(rigPath);
        if (!rigBase.StartsWith("GiantRigDefs_", StringComparison.Ordinal)) return null;
        var suffix = rigBase["GiantRigDefs_".Length..];
        var parentDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rigPath)));
        if (parentDir != null)
        {
            var cand = Path.Combine(parentDir, "Bodies", $"Bodies_{suffix}");
            if (File.Exists(cand)) return cand;
        }
        var sameDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? "", $"Bodies_{suffix}");
        return File.Exists(sameDir) ? sameDir : null;
    }

    private static string? InferDefaultAnimXml(string rigPath)
    {
        var rigBase = Path.GetFileName(rigPath);
        if (!rigBase.StartsWith("GiantRigDefs_", StringComparison.Ordinal)) return null;
        var suffix = rigBase["GiantRigDefs_".Length..];
        var parentDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(rigPath)));
        if (parentDir != null)
        {
            var cand = Path.Combine(parentDir, "GiantAnimDefs", $"GiantAnimDefs_{suffix}");
            if (File.Exists(cand)) return cand;
        }
        var sameDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rigPath)) ?? "", $"GiantAnimDefs_{suffix}");
        return File.Exists(sameDir) ? sameDir : null;
    }

    private static (double X, double Y) ParseVec2(string? text, (double X, double Y) fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = text.Trim().Trim('(', ')');
        var parts = clean.Split(',');
        if (parts.Length < 2) return fallback;
        return (
            double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : fallback.X,
            double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ? y : fallback.Y);
    }

    private static double ParseDouble(string? text, double fallback) =>
        !string.IsNullOrWhiteSpace(text) && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static int ParseInt(string? text, int fallback) =>
        !string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static bool ParseBool(string? text, bool fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim().ToLowerInvariant() is "true" or "1" or "yes";

    private static string? EmptyToNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static readonly (string LeftToken, string RightToken)[] BilateralAuditorTokens =
    [
        ("_left", "_right"),
        ("_Left", "_Right"),
        ("_LEFT", "_RIGHT"),
        ("_l_", "_r_"),
        ("_L_", "_R_"),
        ("_fl_", "_fr_"),
        ("_rl_", "_rr_"),
        ("_l1", "_r1"),
        ("_l2", "_r2"),
        ("_l3", "_r3"),
        ("_l4", "_r4"),
        ("_l5", "_r5"),
        ("_l6", "_r6"),
        ("_l7", "_r7"),
        ("_l8", "_r8"),
    ];

    private static bool TryResolveCounterpartId(string leftId, Func<string, bool> exists, out string rightId)
    {
        rightId = "";
        foreach (var (lTok, rTok) in BilateralAuditorTokens)
        {
            var idx = leftId.IndexOf(lTok, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var cand = string.Concat(leftId.AsSpan(0, idx), rTok, leftId.AsSpan(idx + lTok.Length));
                if (!string.Equals(cand, leftId, StringComparison.Ordinal) && exists(cand))
                {
                    rightId = cand;
                    return true;
                }
            }
        }
        if (!leftId.EndsWith("_right", StringComparison.OrdinalIgnoreCase) &&
            !leftId.EndsWith("_left", StringComparison.OrdinalIgnoreCase))
        {
            var suffixed = leftId + "_right";
            if (exists(suffixed))
            {
                rightId = suffixed;
                return true;
            }
        }
        return false;
    }

    private static bool IsGroundedWalkingLimbId(string nodeId) =>
        nodeId.StartsWith("leg_", StringComparison.OrdinalIgnoreCase)
        || nodeId.StartsWith("thigh_", StringComparison.OrdinalIgnoreCase)
        || nodeId.StartsWith("shin_", StringComparison.OrdinalIgnoreCase)
        || nodeId.StartsWith("ankle_", StringComparison.OrdinalIgnoreCase)
        || nodeId.StartsWith("hind_paw_", StringComparison.OrdinalIgnoreCase)
        || nodeId.StartsWith("paw_", StringComparison.OrdinalIgnoreCase);

    private static void AuditPhysicalLayerInvariants(
        ParsedRig rig,
        bool hasVariants,
        double minLayerDelta,
        List<string> failures)
    {
        // 1. Bilateral left/right symmetry across bones and SoftWhips
        foreach (var nid in rig.SortedOrder)
        {
            if (!TryResolveCounterpartId(nid, rig.Nodes.ContainsKey, out var rid))
            {
                continue;
            }
            var ln = rig.Nodes[nid];
            var rn = rig.Nodes[rid];
            var ld = GetNodeSectorData(ln, "Dorsal").LayerOffset;
            var rd = GetNodeSectorData(rn, "Dorsal").LayerOffset;
            if (Math.Abs(ld - rd) > 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Bilateral Dorsal layerOffset asymmetry: {nid}({ld:F2}) != {rid}({rd:F2})"));
            }
            if (hasVariants)
            {
                var le = GetNodeSectorData(ln, "East").LayerOffset;
                var rw = GetNodeSectorData(rn, "West").LayerOffset;
                var lw = GetNodeSectorData(ln, "West").LayerOffset;
                var re = GetNodeSectorData(rn, "East").LayerOffset;
                if (Math.Abs(le - rw) > 1e-4 || Math.Abs(lw - re) > 1e-4)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Lateral East/West layerOffset mirror mismatch: {nid}(E={le:F2},W={lw:F2}) vs {rid}(E={re:F2},W={rw:F2})"));
                }
            }
            if (ln.BaseVariant.SilImg != null && rn.BaseVariant.SilImg != null &&
                Math.Abs(ln.BaseVariant.SilLayer - rn.BaseVariant.SilLayer) > 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Bilateral silhouetteLayerOffset asymmetry: {nid}({ln.BaseVariant.SilLayer:F2}) != {rid}({rn.BaseVariant.SilLayer:F2})"));
            }
        }

        if (rig.SoftWhips.Count > 1)
        {
            var swById = new Dictionary<string, SoftWhipRecord>(StringComparer.Ordinal);
            var duplicateWhipIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sw in rig.SoftWhips)
            {
                if (!swById.TryAdd(sw.Id, sw) && duplicateWhipIds.Add(sw.Id))
                {
                    failures.Add($"Duplicate SoftWhip whipId '{sw.Id}' in rig '{rig.DefName}'.");
                }
            }
            foreach (var sw in rig.SoftWhips)
            {
                if (TryResolveCounterpartId(sw.Id, swById.ContainsKey, out var rSwId))
                {
                    var rSw = swById[rSwId];
                    if (Math.Abs(sw.LayerOffset - rSw.LayerOffset) > 1e-4)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Bilateral SoftWhip layerOffset asymmetry: {sw.Id}({sw.LayerOffset:F2}) != {rSw.Id}({rSw.LayerOffset:F2})"));
                    }
                }
            }
        }

        // 2. Active dorsal turret & barrel elevation above structural hull/deck plates
        bool IsInActiveTurretSubtree(string id)
        {
            var cur = id;
            var guard = 0;
            while (!string.IsNullOrEmpty(cur) && rig.Nodes.TryGetValue(cur, out var nr) && guard++ < 64)
            {
                if (nr.IsTurret && nr.ContributesToHitbox) return true;
                cur = nr.Parent;
            }
            return false;
        }

        var hasActiveTurret = rig.SortedOrder.Any(id => rig.Nodes[id].IsTurret && rig.Nodes[id].ContributesToHitbox);
        var hasOverheadRotor = rig.SortedOrder.Any(id =>
            rig.Nodes[id].ContributesToHitbox &&
            Math.Abs(rig.Nodes[id].ContinuousSpinDegPerTick) > 0.01 &&
            !rig.Nodes[id].IsReactorCoreBone);
        if (hasActiveTurret || hasOverheadRotor)
        {
            var isWeaponAssembly = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var i = rig.SortedOrder.Count - 1; i >= 0; i--)
            {
                var id = rig.SortedOrder[i];
                var n = rig.Nodes[id];
                if (!n.ContributesToHitbox)
                {
                    isWeaponAssembly[id] = false;
                    continue;
                }
                var inTurret = IsInActiveTurretSubtree(id);
                var isGimbal = false;
                if (!string.IsNullOrEmpty(n.Parent) && n.BaseVariant.SilImg == null)
                {
                    var deployedChildren = rig.SortedOrder
                        .Where(cid => string.Equals(rig.Nodes[cid].Parent, id, StringComparison.Ordinal) && rig.Nodes[cid].ContributesToHitbox)
                        .ToList();
                    isGimbal = deployedChildren.Count > 0 && deployedChildren.All(cid => isWeaponAssembly.GetValueOrDefault(cid));
                }
                isWeaponAssembly[id] = inTurret || n.HasWeaponMuzzle || isGimbal;
            }

            var maxStructuralLayer = double.MinValue;
            string? maxStructuralId = null;
            var maxLimitedArcLayer = double.MinValue;
            string? maxLimitedArcId = null;
            var maxWeaponLayer = double.MinValue;
            string? maxWeaponId = null;

            foreach (var id in rig.SortedOrder)
            {
                var n = rig.Nodes[id];
                if (!n.ContributesToHitbox) continue;
                var isOverheadRotor = Math.Abs(n.ContinuousSpinDegPerTick) > 0.01 && !n.IsReactorCoreBone;
                var dLayer = GetNodeSectorData(n, "Dorsal").LayerOffset;
                if (!isWeaponAssembly[id] && !isOverheadRotor && dLayer > maxStructuralLayer)
                {
                    maxStructuralLayer = dLayer;
                    maxStructuralId = id;
                }
                if (isWeaponAssembly[id] && !isOverheadRotor && dLayer > maxWeaponLayer)
                {
                    maxWeaponLayer = dLayer;
                    maxWeaponId = id;
                }
                if (n.IsTurret && n.MaxTurretAimAngleDeg < 175.0)
                {
                    var checkVentral = !string.IsNullOrEmpty(n.Parent) && rig.Nodes.TryGetValue(n.Parent, out var limParent) &&
                                       dLayer <= GetNodeSectorData(limParent, "Dorsal").LayerOffset - minLayerDelta + 1e-4;
                    if (!checkVentral && dLayer > maxLimitedArcLayer)
                    {
                        maxLimitedArcLayer = dLayer;
                        maxLimitedArcId = id;
                    }
                }
            }

            foreach (var id in rig.SortedOrder)
            {
                var n = rig.Nodes[id];
                if (!n.ContributesToHitbox) continue;
                var isOverheadRotor = Math.Abs(n.ContinuousSpinDegPerTick) > 0.01 && !n.IsReactorCoreBone;
                var dLayer = GetNodeSectorData(n, "Dorsal").LayerOffset;

                if (isOverheadRotor)
                {
                    if (maxStructuralId != null && dLayer < maxStructuralLayer + minLayerDelta - 1e-4)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Overhead spinning rotor {id}({dLayer:F2}) must sit >= {minLayerDelta:F2} above structural hull/wing {maxStructuralId}({maxStructuralLayer:F2})"));
                    }
                    if (maxWeaponId != null && dLayer < maxWeaponLayer + minLayerDelta - 1e-4)
                    {
                        failures.Add(string.Create(CultureInfo.InvariantCulture,
                            $"Overhead spinning rotor {id}({dLayer:F2}) must sit >= {minLayerDelta:F2} above turret/weapon {maxWeaponId}({maxWeaponLayer:F2})"));
                    }
                    continue;
                }

                var isVentralTurret = n.IsTurret &&
                                      !string.IsNullOrEmpty(n.Parent) && rig.Nodes.TryGetValue(n.Parent, out var vParent) &&
                                      dLayer <= GetNodeSectorData(vParent, "Dorsal").LayerOffset - minLayerDelta + 1e-4;

                if (n.IsTurret && !isVentralTurret && maxStructuralId != null && dLayer < maxStructuralLayer + minLayerDelta - 1e-4)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Active dorsal turret {id}({dLayer:F2}) must sit >= {minLayerDelta:F2} above structural hull/deck {maxStructuralId}({maxStructuralLayer:F2})"));
                }
                else if (!n.IsTurret && isWeaponAssembly[id] && !string.IsNullOrEmpty(n.Parent) && rig.Nodes.TryGetValue(n.Parent, out var pNode) &&
                         !string.IsNullOrEmpty(pNode.Parent) && !pNode.IsTurret &&
                         dLayer > GetNodeSectorData(pNode, "Dorsal").LayerOffset &&
                         maxStructuralId != null &&
                         dLayer < maxStructuralLayer + minLayerDelta - 1e-4)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Deployed deck weapon/gimbal {id}({dLayer:F2}) must sit >= {minLayerDelta:F2} above structural hull/deck {maxStructuralId}({maxStructuralLayer:F2})"));
                }

                if (!string.IsNullOrEmpty(n.Parent) && rig.Nodes.TryGetValue(n.Parent, out var tMount) && tMount.IsTurret)
                {
                    var mLayer = GetNodeSectorData(tMount, "Dorsal").LayerOffset;
                    var mountIsVentral = !string.IsNullOrEmpty(tMount.Parent) && rig.Nodes.TryGetValue(tMount.Parent, out var tmParent) &&
                                         mLayer <= GetNodeSectorData(tmParent, "Dorsal").LayerOffset - minLayerDelta + 1e-4;
                    if (mountIsVentral && rig.Nodes.TryGetValue(tMount.Parent!, out var hullParent))
                    {
                        var hullFloor = GetNodeSectorData(hullParent, "Dorsal").LayerOffset - minLayerDelta + 1e-4;
                        if (dLayer > hullFloor)
                        {
                            failures.Add(string.Create(CultureInfo.InvariantCulture,
                                $"Ventral turret child {id}({dLayer:F2}) must remain strictly below parent hull {hullParent.Id}({hullFloor:F2})"));
                        }
                    }
                    else
                    {
                        if (dLayer < mLayer + minLayerDelta - 1e-4)
                        {
                            failures.Add(string.Create(CultureInfo.InvariantCulture,
                                $"Turret child {id}({dLayer:F2}) must sit strictly above dorsal turret mount {tMount.Id}({mLayer:F2} + {minLayerDelta:F2})"));
                        }
                        if (tMount.MaxTurretAimAngleDeg >= 175.0 && n.BaseVariant.Ds.Y >= 3.5 &&
                            maxLimitedArcId != null && dLayer <= maxLimitedArcLayer + 1e-4)
                        {
                            failures.Add(string.Create(CultureInfo.InvariantCulture,
                                $"360-degree long barrel {id}({dLayer:F2}) must sit strictly above limited-arc superstructure {maxLimitedArcId}({maxLimitedArcLayer:F2})"));
                        }
                    }
                }
            }
        }

        // 3. Grounded locomotion & walking limb ordering
        foreach (var id in rig.SortedOrder)
        {
            var n = rig.Nodes[id];
            if (string.IsNullOrEmpty(n.Parent) || !rig.Nodes.TryGetValue(n.Parent, out var p)) continue;
            var nLayer = GetNodeSectorData(n, "Dorsal").LayerOffset;
            var pLayer = GetNodeSectorData(p, "Dorsal").LayerOffset;
            if (n.IsLocomotionDrivePart && !p.IsLocomotionDrivePart && nLayer > pLayer - minLayerDelta + 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Locomotion drive part {id}({nLayer:F2}) must sit strictly below chassis {p.Id}({pLayer:F2} - {minLayerDelta:F2})"));
            }
            if (IsGroundedWalkingLimbId(id) && nLayer > pLayer - minLayerDelta + 1e-4)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Walking limb {id}({nLayer:F2}) must descend strictly below parent {p.Id}({pLayer:F2} - {minLayerDelta:F2})"));
            }
        }

        // 4. Global silhouette underlay floor
        if (rig.SortedOrder.Count > 0)
        {
            var minBoneId = rig.SortedOrder[0];
            var minBoneLayer = GetNodeSectorData(rig.Nodes[minBoneId], "Dorsal").LayerOffset;
            foreach (var id in rig.SortedOrder)
            {
                var dLayer = GetNodeSectorData(rig.Nodes[id], "Dorsal").LayerOffset;
                if (dLayer < minBoneLayer)
                {
                    minBoneLayer = dLayer;
                    minBoneId = id;
                }
            }
            var maxSilAllowed = minBoneLayer - (minLayerDelta - 0.01);
            foreach (var id in rig.SortedOrder)
            {
                var bv = rig.Nodes[id].BaseVariant;
                if (bv.SilImg != null && bv.SilLayer > maxSilAllowed)
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture,
                        $"Silhouette underlay on {id}({bv.SilLayer:F2}) exceeds global floor {maxSilAllowed:F2} (lowest bone {minBoneId}={minBoneLayer:F2})"));
                }
            }
        }
    }

    private static void AuditXmlFieldsAgainstRuntimeType(XElement element, string fullTypeName, string contextLabel, List<string> errors)
    {
        var gcfAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "GiantCreatureFramework", StringComparison.Ordinal));
        var targetType = gcfAsm?.GetType(fullTypeName, throwOnError: false);
        if (targetType != null)
        {
            AuditXmlFieldsAgainstCSharpType(element, targetType, contextLabel, errors);
        }
    }

    private static void AuditXmlFieldsAgainstCSharpType(XElement element, Type targetType, string contextLabel, List<string> errors)
    {
        var validFields = new Dictionary<string, System.Reflection.FieldInfo>(StringComparer.Ordinal);
        for (var t = targetType; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (f.Name.StartsWith('<')) continue;
                if (f.GetCustomAttributes(inherit: true).Any(a => a.GetType().Name == "UnsavedAttribute")) continue;
                validFields.TryAdd(f.Name, f);
            }
        }

        foreach (var child in element.Elements())
        {
            var childName = child.Name.LocalName;
            if (!validFields.TryGetValue(childName, out var fieldInfo))
            {
                var sampleValid = string.Join(", ", validFields.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(20));
                errors.Add($"{contextLabel}: XML element '<{childName}>' does not exist on {targetType.Name} (valid fields include: {sampleValid})");
                continue;
            }

            var fType = fieldInfo.FieldType;
            if (fType.IsGenericType && fType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var itemType = fType.GetGenericArguments()[0];
                if (IsAuditableGcfComplexType(itemType))
                {
                    foreach (var li in child.Elements("li"))
                    {
                        AuditXmlFieldsAgainstCSharpType(li, itemType, $"{contextLabel}->{childName}[li]", errors);
                    }
                }
            }
            else if (fType.IsArray)
            {
                var itemType = fType.GetElementType()!;
                if (IsAuditableGcfComplexType(itemType))
                {
                    foreach (var li in child.Elements("li"))
                    {
                        AuditXmlFieldsAgainstCSharpType(li, itemType, $"{contextLabel}->{childName}[li]", errors);
                    }
                }
            }
            else if (IsAuditableGcfComplexType(fType))
            {
                AuditXmlFieldsAgainstCSharpType(child, fType, $"{contextLabel}->{childName}", errors);
            }
        }
    }

    private static bool IsAuditableGcfComplexType(Type t)
    {
        if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t.Namespace != "GiantCreatureFramework")
        {
            return false;
        }
        for (var bt = t; bt != null && bt != typeof(object); bt = bt.BaseType)
        {
            if (bt.FullName == "Verse.Def") return false;
        }
        return true;
    }

    private static double PositiveMod(double x, double m) => ((x % m) + m) % m;
}
