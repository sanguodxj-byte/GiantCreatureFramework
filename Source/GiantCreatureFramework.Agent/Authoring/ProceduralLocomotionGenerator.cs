using System.Globalization;
using System.Text;
using System.Xml.Linq;
using GiantCreatureFramework.Agent.Imaging;

namespace GiantCreatureFramework.Agent.Authoring;

public sealed record GeneratedAnimSummary(
    string CreatureName,
    string RelativeXmlPath,
    string AnimDefName,
    string Preset,
    int DurationTicks,
    bool Loop,
    string? IsolatedSubtreeRootBoneId,
    int TrackCount,
    int TotalKeyframes,
    IReadOnlyList<string> AnimatedBoneIds,
    IReadOnlyList<string> Warnings);

public sealed record LocomotionAnimRequest
{
    public required string CreatureName { get; init; }
    public required string RelativeXmlPath { get; init; }
    public required string AnimDefName { get; init; }
    public required string Preset { get; init; }
    public string? RigDefName { get; init; }
    public int? DurationTicks { get; init; }
    public double AmplitudeScale { get; init; } = 1.0;
    public int? ImpactTick { get; init; }
    public string? TriggerToolTag { get; init; }
    public bool IsolateSubtree { get; init; }
    public bool WingGroundedFold { get; init; } = true;

    // Optional explicit bone chain overrides (comma, semicolon, or '>' separated)
    public string? RootBoneId { get; init; }
    public string? SpineChain { get; init; }
    public string? NeckChain { get; init; }
    public string? TailChain { get; init; }
    public string? LeftWingChain { get; init; }
    public string? RightWingChain { get; init; }
    public string? LeftForeChain { get; init; }
    public string? RightForeChain { get; init; }
    public string? LeftHindChain { get; init; }
    public string? RightHindChain { get; init; }
    public string? MultiLegLeftChains { get; init; }
    public string? MultiLegRightChains { get; init; }
}

/// <summary>
/// Rig-aware procedural skeletal animation synthesizer for the in-game GiantCreatureFramework Agent.
/// Inspects the creature workspace's GiantRigDef topology, rest angles, and sprite shaft bearings so
/// generated keyframes (angle, sagittalPitch, coronalFlap, and root-only offset) automatically adapt
/// to arbitrary rig bone counts, <baseAngle> conventions, and <isRigid> constraints without hand-coding
/// thousands of XML lines.
/// </summary>
public static class ProceduralLocomotionGenerator
{
    public static readonly string[] SupportedPresets =
    [
        "axial_idle",
        "quadruped_gait",
        "wyvern_wing_walk",
        "biped_gait",
        "multileg_gait",
        "wing_flap_loop",
        "wing_takeoff",
        "wing_land",
        "tail_sweep",
        "bite_strike",
        "death_collapse"
    ];

    private sealed class RigNodeMeta
    {
        public required string Id { get; init; }
        public string? ParentId { get; init; }
        public double BaseAngle { get; init; }
        public (double X, double Y) DrawSize { get; init; } = (1.0, 1.0);
        public (double U, double V) PivotUv { get; init; } = (0.5, 0.5);
        public (double U, double V) ParentSocketUv { get; set; } = (0.5, 0.5);
        public string? RawParentSocketPx { get; init; }
        public string? RawParentSocketUv { get; init; }
        public int Resolution { get; init; } = 512;
        public bool IsRigid { get; init; }
        public bool IsCoronalFlapHinge { get; init; }
        public double SagittalDropCells { get; init; }
        public string? TexRel { get; init; }
        public List<string> Children { get; } = new();

        // Computed rest kinematics (heading = 0 deg North)
        public double RestWorldAngleDeg { get; set; }
        public (double X, double Z) RestJointXZ { get; set; }
        public double RestShaftBearingDeg { get; set; }
    }

    private sealed class KeyframeSample
    {
        public double Tick { get; init; }
        public double Angle { get; set; }
        public double SagittalPitch { get; set; }
        public double CoronalFlap { get; set; }
        public (double X, double Z)? RootOffset { get; set; }
        public string Easing { get; init; } = "CatmullRom";
    }

    public static GeneratedAnimSummary GenerateAndWrite(CreatureWorkspace workspace, LocomotionAnimRequest request)
    {
        if (workspace == null) throw new ArgumentNullException(nameof(workspace));
        if (request == null) throw new ArgumentNullException(nameof(request));

        var creatureName = request.CreatureName.Trim();
        CreatureWorkspace.ValidateCreatureName(creatureName);

        var animDefName = request.AnimDefName.Trim();
        if (string.IsNullOrWhiteSpace(animDefName))
        {
            throw new ArgumentException("animDefName must not be empty.");
        }

        var preset = request.Preset.Trim().ToLowerInvariant();
        if (!SupportedPresets.Contains(preset, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported preset '{request.Preset}'. Supported presets: {string.Join(", ", SupportedPresets)}.");
        }

        var rawAmp = double.IsFinite(request.AmplitudeScale) ? request.AmplitudeScale : 1.0;
        var amp = Math.Clamp(rawAmp, 0.15, 2.5);
        var warnings = new List<string>();
        var creatureDir = workspace.GetCreatureDir(creatureName, createIfMissing: false);
        if (!Directory.Exists(creatureDir))
        {
            throw new DirectoryNotFoundException($"Creature workspace does not exist: {creatureName}");
        }

        var nodes = LoadWorkspaceRigNodes(creatureDir, request.RigDefName, warnings);
        if (nodes.Count == 0)
        {
            throw new InvalidOperationException(
                $"No GiantRigDef <nodes> found under {creatureName}/Defs. Author the GiantRigDef XML before calling generate_locomotion_anim.");
        }

        var rootId = ResolveRootBone(nodes, request.RootBoneId);
        var spineChain = ResolveChain(nodes, request.SpineChain) ?? AutoDetectSpineChain(nodes, rootId);
        var neckChain = ResolveChain(nodes, request.NeckChain) ?? AutoDetectAxialChain(nodes, rootId, ["neck", "throat", "cervical", "head", "skull", "jaw", "cranium"]);
        var tailChain = ResolveChain(nodes, request.TailChain) ?? AutoDetectAxialChain(nodes, rootId, ["tail", "caudal", "stinger", "club", "fluke"]);
        var (leftWing, rightWing) = ResolveBilateralChains(nodes, request.LeftWingChain, request.RightWingChain, ["wing", "pinion", "patagium"]);
        var (leftFore, rightFore) = ResolveBilateralChains(nodes, request.LeftForeChain, request.RightForeChain, ["fore", "front", "arm", "claw_l", "claw_r"]);
        var (leftHind, rightHind) = ResolveBilateralChains(nodes, request.LeftHindChain, request.RightHindChain, ["thigh", "femur", "hind", "rear_leg", "leg_rear", "shin", "tibia", "ankle", "paw", "foot"]);
        var (multiLeft, multiRight) = ResolveMultiLegChains(nodes, request.MultiLegLeftChains, request.MultiLegRightChains);

        var (defaultDur, isLoop) = GetDefaultDurationAndLoop(preset);
        var durationTicks = Math.Clamp(request.DurationTicks ?? defaultDur, 12, 360);
        var tracks = new Dictionary<string, List<KeyframeSample>>(StringComparer.Ordinal);
        string? isolatedRoot = null;

        switch (preset)
        {
            case "axial_idle":
                SynthesizeAxialIdle(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, durationTicks, amp, request.WingGroundedFold);
                break;

            case "quadruped_gait":
                SynthesizeQuadrupedGait(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftFore, rightFore, leftHind, rightHind, leftWing, rightWing, durationTicks, amp, request.WingGroundedFold);
                break;

            case "wyvern_wing_walk":
                SynthesizeWyvernWingWalk(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, leftHind, rightHind, durationTicks, amp);
                break;

            case "biped_gait":
                SynthesizeBipedGait(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftFore, rightFore, leftHind, rightHind, leftWing, rightWing, durationTicks, amp, request.WingGroundedFold);
                break;

            case "multileg_gait":
                SynthesizeMultiLegGait(nodes, tracks, rootId, spineChain, neckChain, tailChain, multiLeft, multiRight, durationTicks, amp);
                break;

            case "wing_flap_loop":
                SynthesizeWingFlapLoop(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, leftHind, rightHind, durationTicks, amp, warnings);
                break;

            case "wing_takeoff":
                SynthesizeWingTakeoff(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, leftHind, rightHind, durationTicks, amp, warnings);
                break;

            case "wing_land":
                SynthesizeWingLand(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, leftHind, rightHind, durationTicks, amp, warnings);
                break;

            case "tail_sweep":
                if (tailChain.Count == 0)
                {
                    throw new InvalidOperationException("tail_sweep preset requires a tail bone chain (pass tailChain or name tail bones with 'tail').");
                }
                if (request.IsolateSubtree)
                {
                    isolatedRoot = tailChain[0];
                }
                SynthesizeTailSweep(nodes, tracks, rootId, spineChain, tailChain, durationTicks, amp, isolatedRoot != null);
                break;

            case "bite_strike":
                SynthesizeBiteStrike(nodes, tracks, rootId, spineChain, neckChain, tailChain, durationTicks, amp);
                break;

            case "death_collapse":
                durationTicks = Math.Max(40, durationTicks);
                SynthesizeDeathCollapse(nodes, tracks, rootId, spineChain, neckChain, tailChain, leftWing, rightWing, leftFore, rightFore, leftHind, rightHind, multiLeft, multiRight, durationTicks, amp);
                break;
        }

        // Enforce engine invariants: strip rigid child rotations and strip non-root offsets
        SanitizeTracks(nodes, rootId, tracks, warnings);

        if (tracks.Count == 0)
        {
            throw new InvalidOperationException(
                $"Preset '{preset}' produced 0 animation tracks for {creatureName}. Check bone chain parameters against GiantRigDef <nodes>.");
        }

        var resolvedImpactTick = !isLoop
            ? (request.ImpactTick ?? ResolveDefaultImpactTick(preset, durationTicks))
            : (int?)null;

        var animEl = BuildAnimElement(
            animDefName,
            preset,
            durationTicks,
            isLoop,
            resolvedImpactTick,
            request.TriggerToolTag,
            isolatedRoot,
            tracks);

        var savedRelPath = UpsertAnimIntoWorkspaceXml(workspace, creatureName, request.RelativeXmlPath, animDefName, animEl);
        var totalKfs = tracks.Values.Sum(l => l.Count);

        return new GeneratedAnimSummary(
            CreatureName: creatureName,
            RelativeXmlPath: savedRelPath,
            AnimDefName: animDefName,
            Preset: preset,
            DurationTicks: durationTicks,
            Loop: isLoop,
            IsolatedSubtreeRootBoneId: isolatedRoot,
            TrackCount: tracks.Count,
            TotalKeyframes: totalKfs,
            AnimatedBoneIds: tracks.Keys.ToList(),
            Warnings: warnings);
    }

    private static (int DurationTicks, bool Loop) GetDefaultDurationAndLoop(string preset) => preset switch
    {
        "axial_idle" => (96, true),
        "quadruped_gait" => (48, true),
        "wyvern_wing_walk" => (48, true),
        "biped_gait" => (44, true),
        "multileg_gait" => (36, true),
        "wing_flap_loop" => (60, true),
        "wing_takeoff" => (44, false),
        "wing_land" => (44, false),
        "tail_sweep" => (34, false),
        "bite_strike" => (30, false),
        "death_collapse" => (48, false),
        _ => (48, true)
    };

    private static int? ResolveDefaultImpactTick(string preset, int durationTicks) => preset switch
    {
        "tail_sweep" => Math.Max(4, (int)Math.Round(durationTicks * 0.48)),
        "bite_strike" => Math.Max(4, (int)Math.Round(durationTicks * 0.44)),
        _ => null
    };

    private static void SynthesizeAxialIdle(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        int dur,
        double amp,
        bool holdFoldedWings)
    {
        var sampleTicks = BuildLoopTicks(dur, 8);
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var breathZ = 0.045 * amp * Math.Sin(phase);
            var swayAngle = 0.9 * amp * Math.Sin(phase);
            return (swayAngle, 0.0, 0.0, (0.0, breathZ));
        });

        for (int i = 0; i < spineChain.Count; i++)
        {
            var bid = spineChain[i];
            if (bid == rootId) continue;
            var lag = (i + 1) * 0.55;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-1.2 * amp * Math.Sin(phase - lag), 0.0, 0.0, null));
        }

        // Neck breathes gently while terminal skull counter-stabilizes for steady gaze
        if (neckChain.Count > 0)
        {
            for (int i = 0; i < neckChain.Count; i++)
            {
                var bid = neckChain[i];
                var isHead = i == neckChain.Count - 1 && neckChain.Count >= 2;
                if (isHead)
                {
                    int headIdx = i;
                    AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                    {
                        double parentSum = 0.9 * amp * Math.Sin(phase);
                        for (int k = 0; k < headIdx; k++)
                        {
                            parentSum += 1.6 * amp * Math.Sin(phase - (k + 1) * 0.45);
                        }
                        return (-parentSum * 0.92, 0.0, 0.0, null);
                    });
                }
                else
                {
                    var lag = (i + 1) * 0.45;
                    AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                        (1.6 * amp * Math.Sin(phase - lag), 0.0, 0.0, null));
                }
            }
        }

        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            var lag = 0.7 + i * 0.62;
            var deg = (2.2 + i * 0.85) * amp;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (deg * Math.Sin(phase - lag), 0.0, 0.0, null));
        }

        if (holdFoldedWings && leftWing.Count > 0 && rightWing.Count > 0)
        {
            ApplyWingBearingPoseLoop(nodes, tracks, leftWing, isRight: false, sampleTicks, dur,
                (segIdx, segCount, phase) => TargetGroundedZFoldBearingLeft(segIdx, segCount) - 1.2 * amp * Math.Sin(phase - segIdx * 0.35),
                (_, _, _) => 0.0);
            ApplyWingBearingPoseLoop(nodes, tracks, rightWing, isRight: true, sampleTicks, dur,
                (segIdx, segCount, phase) => -TargetGroundedZFoldBearingLeft(segIdx, segCount) + 1.2 * amp * Math.Sin(phase - segIdx * 0.35),
                (_, _, _) => 0.0);
        }
    }

    private static void SynthesizeQuadrupedGait(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftFore,
        List<string> rightFore,
        List<string> leftHind,
        List<string> rightHind,
        List<string> leftWing,
        List<string> rightWing,
        int dur,
        double amp,
        bool holdFoldedWings)
    {
        var sampleTicks = BuildLoopTicks(dur, 8);
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var bodyYaw = 3.2 * amp * Math.Sin(phase);
            var surgeZ = 0.11 * amp * Math.Sin(2.0 * phase);
            var swayX = 0.05 * amp * Math.Cos(phase);
            return (bodyYaw, 0.0, 0.0, (swayX, surgeZ));
        });

        for (int i = 0; i < spineChain.Count; i++)
        {
            var bid = spineChain[i];
            if (bid == rootId) continue;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-3.8 * amp * Math.Sin(phase - (i + 1) * 0.35), 0.0, 0.0, null));
        }

        AddStabilizedNeckStride(tracks, neckChain, sampleTicks, dur, rootYawAmp: 3.2 * amp, neckPitchAmp: 5.5 * amp);
        AddTailStrideCounterwave(tracks, tailChain, sampleTicks, dur, amp * 1.15);

        // Diagonal pairs: LeftFore (0) + RightHind (0.15pi) vs RightFore (pi) + LeftHind (pi + 0.15pi)
        AddLimbStrideChain(nodes, tracks, leftFore, sampleTicks, dur, phaseOffset: 0.0, sideSign: -1.0, amp: amp);
        AddLimbStrideChain(nodes, tracks, rightFore, sampleTicks, dur, phaseOffset: Math.PI, sideSign: 1.0, amp: amp);
        AddLimbStrideChain(nodes, tracks, rightHind, sampleTicks, dur, phaseOffset: 0.25, sideSign: 1.0, amp: amp);
        AddLimbStrideChain(nodes, tracks, leftHind, sampleTicks, dur, phaseOffset: Math.PI + 0.25, sideSign: -1.0, amp: amp);

        if (holdFoldedWings && leftWing.Count > 0 && rightWing.Count > 0)
        {
            ApplyWingBearingPoseLoop(nodes, tracks, leftWing, isRight: false, sampleTicks, dur,
                (segIdx, segCount, phase) => TargetGroundedZFoldBearingLeft(segIdx, segCount) + 2.4 * amp * Math.Sin(phase - segIdx * 0.3),
                (_, _, _) => 0.0);
            ApplyWingBearingPoseLoop(nodes, tracks, rightWing, isRight: true, sampleTicks, dur,
                (segIdx, segCount, phase) => -TargetGroundedZFoldBearingLeft(segIdx, segCount) + 2.4 * amp * Math.Sin(phase - segIdx * 0.3),
                (_, _, _) => 0.0);
        }
    }

    private static void SynthesizeWyvernWingWalk(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        List<string> leftHind,
        List<string> rightHind,
        int dur,
        double amp)
    {
        var sampleTicks = BuildLoopTicks(dur, 8);
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var bodyYaw = 3.8 * amp * Math.Sin(phase);
            var surgeZ = 0.12 * amp * Math.Sin(2.0 * phase - 0.3);
            var swayX = 0.07 * amp * Math.Cos(phase);
            return (bodyYaw, 0.0, 0.0, (swayX, surgeZ));
        });

        for (int i = 0; i < spineChain.Count; i++)
        {
            var bid = spineChain[i];
            if (bid == rootId) continue;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-4.2 * amp * Math.Sin(phase - 0.35 * (i + 1)), 0.0, 0.0, null));
        }

        AddStabilizedNeckStride(tracks, neckChain, sampleTicks, dur, rootYawAmp: 3.8 * amp, neckPitchAmp: 6.0 * amp);
        AddTailStrideCounterwave(tracks, tailChain, sampleTicks, dur, amp * 1.25);

        // Folded wings step forward-and-back on wing-wrists in diagonal synchrony with hindlegs
        if (leftWing.Count > 0 && rightWing.Count > 0)
        {
            ApplyWingBearingPoseLoop(nodes, tracks, leftWing, isRight: false, sampleTicks, dur,
                (segIdx, segCount, phase) =>
                {
                    var baseFold = TargetGroundedZFoldBearingLeft(segIdx, segCount);
                    var swing = segIdx == 0 ? 11.0 * amp * Math.Sin(phase)
                        : segIdx == 1 ? 14.0 * amp * Math.Sin(phase - 0.25)
                        : -8.0 * amp * Math.Sin(phase - 0.45);
                    return Math.Clamp(baseFold + swing, -156.0, -54.0);
                },
                (segIdx, _, phase) => segIdx == 0 ? (10.0 + 8.0 * Math.Max(0.0, Math.Cos(phase))) * amp : 0.0);

            ApplyWingBearingPoseLoop(nodes, tracks, rightWing, isRight: true, sampleTicks, dur,
                (segIdx, segCount, phase) =>
                {
                    var baseFold = -TargetGroundedZFoldBearingLeft(segIdx, segCount);
                    var pR = phase + Math.PI;
                    var swing = segIdx == 0 ? -11.0 * amp * Math.Sin(pR)
                        : segIdx == 1 ? -14.0 * amp * Math.Sin(pR - 0.25)
                        : 8.0 * amp * Math.Sin(pR - 0.45);
                    return Math.Clamp(baseFold + swing, 54.0, 156.0);
                },
                (segIdx, _, phase) => segIdx == 0 ? (10.0 + 8.0 * Math.Max(0.0, Math.Cos(phase + Math.PI))) * amp : 0.0);
        }

        AddLimbStrideChain(nodes, tracks, rightHind, sampleTicks, dur, phaseOffset: 0.20, sideSign: 1.0, amp: amp * 1.1);
        AddLimbStrideChain(nodes, tracks, leftHind, sampleTicks, dur, phaseOffset: Math.PI + 0.20, sideSign: -1.0, amp: amp * 1.1);
    }

    private static void SynthesizeBipedGait(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftFore,
        List<string> rightFore,
        List<string> leftHind,
        List<string> rightHind,
        List<string> leftWing,
        List<string> rightWing,
        int dur,
        double amp,
        bool holdFoldedWings)
    {
        var sampleTicks = BuildLoopTicks(dur, 8);
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var yaw = 3.6 * amp * Math.Sin(phase);
            var bobZ = 0.13 * amp * Math.Sin(2.0 * phase);
            var swayX = 0.08 * amp * Math.Cos(phase);
            return (yaw, 0.0, 0.0, (swayX, bobZ));
        });

        for (int i = 0; i < spineChain.Count; i++)
        {
            var bid = spineChain[i];
            if (bid == rootId) continue;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-3.2 * amp * Math.Sin(phase - 0.3 * (i + 1)), 0.0, 0.0, null));
        }

        AddStabilizedNeckStride(tracks, neckChain, sampleTicks, dur, rootYawAmp: 3.6 * amp, neckPitchAmp: 4.5 * amp);
        AddTailStrideCounterwave(tracks, tailChain, sampleTicks, dur, amp * 1.1);

        var useLegsL = leftHind.Count > 0 ? leftHind : leftFore;
        var useLegsR = rightHind.Count > 0 ? rightHind : rightFore;
        AddLimbStrideChain(nodes, tracks, useLegsL, sampleTicks, dur, phaseOffset: 0.0, sideSign: -1.0, amp: amp * 1.15);
        AddLimbStrideChain(nodes, tracks, useLegsR, sampleTicks, dur, phaseOffset: Math.PI, sideSign: 1.0, amp: amp * 1.15);

        if (leftHind.Count > 0 && leftFore.Count > 0 && rightFore.Count > 0)
        {
            AddLimbStrideChain(nodes, tracks, leftFore, sampleTicks, dur, phaseOffset: Math.PI, sideSign: -1.0, amp: amp * 0.65);
            AddLimbStrideChain(nodes, tracks, rightFore, sampleTicks, dur, phaseOffset: 0.0, sideSign: 1.0, amp: amp * 0.65);
        }

        if (holdFoldedWings && leftWing.Count > 0 && rightWing.Count > 0)
        {
            ApplyWingBearingPoseLoop(nodes, tracks, leftWing, isRight: false, sampleTicks, dur,
                (segIdx, segCount, phase) => TargetGroundedZFoldBearingLeft(segIdx, segCount) - 3.0 * amp * Math.Sin(phase),
                (_, _, _) => 0.0);
            ApplyWingBearingPoseLoop(nodes, tracks, rightWing, isRight: true, sampleTicks, dur,
                (segIdx, segCount, phase) => -TargetGroundedZFoldBearingLeft(segIdx, segCount) - 3.0 * amp * Math.Sin(phase),
                (_, _, _) => 0.0);
        }
    }

    private static void SynthesizeMultiLegGait(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<List<string>> multiLeft,
        List<List<string>> multiRight,
        int dur,
        double amp)
    {
        var sampleTicks = BuildLoopTicks(dur, 8);
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var yaw = 2.2 * amp * Math.Sin(phase);
            var surgeZ = 0.07 * amp * Math.Sin(2.0 * phase);
            return (yaw, 0.0, 0.0, (0.0, surgeZ));
        });

        for (int i = 0; i < spineChain.Count; i++)
        {
            var bid = spineChain[i];
            if (bid == rootId) continue;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-2.0 * amp * Math.Sin(phase - 0.35 * (i + 1)), 0.0, 0.0, null));
        }

        AddStabilizedNeckStride(tracks, neckChain, sampleTicks, dur, rootYawAmp: 2.2 * amp, neckPitchAmp: 2.5 * amp);
        AddTailStrideCounterwave(tracks, tailChain, sampleTicks, dur, amp * 0.85);

        int pairCount = Math.Max(multiLeft.Count, multiRight.Count);
        for (int p = 0; p < pairCount; p++)
        {
            // Alternating tripod / metachronal phase offset across pairs
            double basePhase = (p % 2 == 0 ? 0.0 : Math.PI) + p * 0.28;
            if (p < multiLeft.Count)
            {
                AddArthropodLegChain(tracks, multiLeft[p], sampleTicks, dur, basePhase, sideSign: -1.0, amp: amp);
            }
            if (p < multiRight.Count)
            {
                AddArthropodLegChain(tracks, multiRight[p], sampleTicks, dur, basePhase + Math.PI, sideSign: 1.0, amp: amp);
            }
        }
    }

    /// <summary>
    /// 3-segment delayed whip-cascade aerial wingbeat (FlyMove):
    /// Shoulder/humerus drives the downstroke while forearm (-0.15 cycle) and outer membrane fan (-0.30 cycle)
    /// follow with progressive aerodynamic phase lag and 3D coronalFlap foreshortening.
    /// </summary>
    private static void SynthesizeWingFlapLoop(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        List<string> leftHind,
        List<string> rightHind,
        int dur,
        double amp,
        List<string> warnings)
    {
        if (leftWing.Count == 0 || rightWing.Count == 0)
        {
            throw new InvalidOperationException("wing_flap_loop requires bilateral wing bone chains (pass leftWingChain/rightWingChain or name wing bones with 'wing').");
        }

        bool anyFlapHinge = leftWing.Concat(rightWing).Any(id => nodes.TryGetValue(id, out var nm) && nm.IsCoronalFlapHinge);
        if (!anyFlapHinge)
        {
            warnings.Add("Tip: set <isCoronalFlapHinge>true</isCoronalFlapHinge> on proximal wing bones in GiantRigDef so <coronalFlap> produces 3D foreshortening during wingbeats.");
        }

        var sampleTicks = BuildLoopTicks(dur, 8);

        // Quadrature body lift heave coupled with wing downstroke
        AddPeriodicTrack(tracks, rootId, sampleTicks, dur, "CatmullRom", phase =>
        {
            var liftZ = 0.18 * amp * Math.Sin(phase - 0.55);
            return (0.0, 0.0, 0.0, (0.0, liftZ));
        });

        for (int i = 0; i < neckChain.Count; i++)
        {
            var bid = neckChain[i];
            var isHead = i == neckChain.Count - 1 && neckChain.Count >= 2;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
            {
                var pitch = isHead ? -3.5 * amp * Math.Sin(phase - 0.55) : 3.5 * amp * Math.Sin(phase - 0.55);
                return (0.0, pitch, 0.0, null);
            });
        }

        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            var lag = 0.65 + i * 0.48;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (2.0 * amp * Math.Sin(phase - lag), -4.0 * amp * Math.Sin(phase - lag), 0.0, null));
        }

        // Aerodynamic hindleg tuck in flight
        TuckHindlimbsLoop(tracks, leftHind, sampleTicks, dur, sideSign: -1.0, amp: amp);
        TuckHindlimbsLoop(tracks, rightHind, sampleTicks, dur, sideSign: 1.0, amp: amp);

        ApplyWingBearingPoseLoop(nodes, tracks, leftWing, isRight: false, sampleTicks, dur,
            (segIdx, segCount, phase) => EvaluateAerialFlapBearingLeft(segIdx, segCount, phase, amp),
            (segIdx, segCount, phase) => EvaluateAerialCoronalFlapDeg(segIdx, segCount, phase, amp));

        ApplyWingBearingPoseLoop(nodes, tracks, rightWing, isRight: true, sampleTicks, dur,
            (segIdx, segCount, phase) => -EvaluateAerialFlapBearingLeft(segIdx, segCount, phase, amp),
            (segIdx, segCount, phase) => EvaluateAerialCoronalFlapDeg(segIdx, segCount, phase, amp));
    }

    private static void SynthesizeWingTakeoff(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        List<string> leftHind,
        List<string> rightHind,
        int dur,
        double amp,
        List<string> warnings)
    {
        if (leftWing.Count == 0 || rightWing.Count == 0)
        {
            throw new InvalidOperationException("wing_takeoff requires bilateral wing bone chains.");
        }

        // 5 key beats: 0.00 (Grounded Z-Fold) -> 0.22 (Crouch & Raise Wings) -> 0.52 (Power Downstroke Vault) -> 0.80 (Recovery Sweep) -> 1.00 (Open-Wing Cruise)
        double[] norms = [0.0, 0.22, 0.52, 0.80, 1.0];
        var ticks = norms.Select(u => Math.Round(u * dur, 2)).ToList();

        AddOneShotTrack(tracks, rootId, ticks, [
            (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep"),
            (0.0, 0.0, 0.0, (0.0, -0.22 * amp), "EaseInQuad"),
            (0.0, 0.0, 0.0, (0.0, 0.34 * amp), "EaseOutCubic"),
            (0.0, 0.0, 0.0, (0.0, 0.16 * amp), "CatmullRom"),
            (0.0, 0.0, 0.0, (0.0, 0.06 * amp), "SmoothStep")
        ]);

        ApplyWingOneShot(nodes, tracks, leftWing, isRight: false, ticks,
            (segIdx, segCount, beatIdx) => beatIdx switch
            {
                0 => (TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep"),
                1 => (Lerp(TargetGroundedZFoldBearingLeft(segIdx, segCount), -98.0 - segIdx * 4.0, 0.72), 44.0 * amp * (segIdx == 0 ? 1.0 : 0.35), "EaseOutQuad"),
                2 => (-72.0 + segIdx * 3.5, 18.0 * amp * (segIdx == 0 ? 1.0 : 0.25), "EaseOutCubic"),
                3 => (-96.0 - segIdx * 2.5, 32.0 * amp * (segIdx == 0 ? 1.0 : 0.30), "CatmullRom"),
                _ => (TargetOpenSpreadBearingLeft(segIdx, segCount), 16.0 * amp * (segIdx == 0 ? 1.0 : 0.20), "SmoothStep")
            });

        ApplyWingOneShot(nodes, tracks, rightWing, isRight: true, ticks,
            (segIdx, segCount, beatIdx) => beatIdx switch
            {
                0 => (-TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep"),
                1 => (-Lerp(TargetGroundedZFoldBearingLeft(segIdx, segCount), -98.0 - segIdx * 4.0, 0.72), 44.0 * amp * (segIdx == 0 ? 1.0 : 0.35), "EaseOutQuad"),
                2 => (72.0 - segIdx * 3.5, 18.0 * amp * (segIdx == 0 ? 1.0 : 0.25), "EaseOutCubic"),
                3 => (96.0 + segIdx * 2.5, 32.0 * amp * (segIdx == 0 ? 1.0 : 0.30), "CatmullRom"),
                _ => (-TargetOpenSpreadBearingLeft(segIdx, segCount), 16.0 * amp * (segIdx == 0 ? 1.0 : 0.20), "SmoothStep")
            });

        ApplyHindlimbTransitionOneShot(tracks, leftHind, ticks, isTakeoff: true, amp: amp);
        ApplyHindlimbTransitionOneShot(tracks, rightHind, ticks, isTakeoff: true, amp: amp);
    }

    private static void SynthesizeWingLand(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        List<string> leftHind,
        List<string> rightHind,
        int dur,
        double amp,
        List<string> warnings)
    {
        if (leftWing.Count == 0 || rightWing.Count == 0)
        {
            throw new InvalidOperationException("wing_land requires bilateral wing bone chains.");
        }

        // 0.00 (Open-Wing Approach) -> 0.32 (Forward Braking Flare) -> 0.62 (Touchdown Cushion) -> 0.84 (Z-Fold Tuck) -> 1.00 (Grounded Z-Fold Settle)
        double[] norms = [0.0, 0.32, 0.62, 0.84, 1.0];
        var ticks = norms.Select(u => Math.Round(u * dur, 2)).ToList();

        AddOneShotTrack(tracks, rootId, ticks, [
            (0.0, 0.0, 0.0, (0.0, 0.14 * amp), "SmoothStep"),
            (0.0, 0.0, 0.0, (0.0, 0.22 * amp), "EaseOutQuad"),
            (0.0, 0.0, 0.0, (0.0, -0.20 * amp), "EaseOutCubic"),
            (0.0, 0.0, 0.0, (0.0, -0.05 * amp), "CatmullRom"),
            (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep")
        ]);

        ApplyWingOneShot(nodes, tracks, leftWing, isRight: false, ticks,
            (segIdx, segCount, beatIdx) => beatIdx switch
            {
                0 => (TargetOpenSpreadBearingLeft(segIdx, segCount), 18.0 * amp * (segIdx == 0 ? 1.0 : 0.2), "SmoothStep"),
                1 => (-68.0 + segIdx * 3.0, 12.0 * amp * (segIdx == 0 ? 1.0 : 0.15), "EaseOutQuad"),
                2 => (Lerp(-82.0, TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.55), 26.0 * amp * (segIdx == 0 ? 1.0 : 0.25), "EaseOutCubic"),
                3 => (TargetGroundedZFoldBearingLeft(segIdx, segCount) - 3.0, 6.0 * amp * (segIdx == 0 ? 1.0 : 0.1), "CatmullRom"),
                _ => (TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep")
            });

        ApplyWingOneShot(nodes, tracks, rightWing, isRight: true, ticks,
            (segIdx, segCount, beatIdx) => beatIdx switch
            {
                0 => (-TargetOpenSpreadBearingLeft(segIdx, segCount), 18.0 * amp * (segIdx == 0 ? 1.0 : 0.2), "SmoothStep"),
                1 => (68.0 - segIdx * 3.0, 12.0 * amp * (segIdx == 0 ? 1.0 : 0.15), "EaseOutQuad"),
                2 => (-Lerp(-82.0, TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.55), 26.0 * amp * (segIdx == 0 ? 1.0 : 0.25), "EaseOutCubic"),
                3 => (-TargetGroundedZFoldBearingLeft(segIdx, segCount) + 3.0, 6.0 * amp * (segIdx == 0 ? 1.0 : 0.1), "CatmullRom"),
                _ => (-TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep")
            });

        ApplyHindlimbTransitionOneShot(tracks, leftHind, ticks, isTakeoff: false, amp: amp);
        ApplyHindlimbTransitionOneShot(tracks, rightHind, ticks, isTakeoff: false, amp: amp);
    }

    private static void SynthesizeTailSweep(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> tailChain,
        int dur,
        double amp,
        bool isolated)
    {
        double[] norms = [0.0, 0.26, 0.48, 0.72, 1.0];
        var ticks = norms.Select(u => Math.Round(u * dur, 2)).ToList();

        if (!isolated)
        {
            AddOneShotTrack(tracks, rootId, ticks, [
                (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep"),
                (-6.0 * amp, 0.0, 0.0, (0.0, -0.08 * amp), "EaseInQuad"),
                (10.5 * amp, 0.0, 0.0, (0.0, 0.10 * amp), "EaseOutBack"),
                (3.0 * amp, 0.0, 0.0, (0.0, 0.02 * amp), "CatmullRom"),
                (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep")
            ]);
        }

        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            double segGain = 1.0 + i * 0.28;
            AddOneShotTrack(tracks, bid, ticks, [
                (0.0, 0.0, 0.0, null, "SmoothStep"),
                (-16.0 * segGain * amp, 0.0, 0.0, null, "EaseInQuad"),
                (26.0 * segGain * amp, 0.0, 0.0, null, "EaseOutBack"),
                (7.5 * segGain * amp, 0.0, 0.0, null, "CatmullRom"),
                (0.0, 0.0, 0.0, null, "SmoothStep")
            ]);
        }
    }

    private static void SynthesizeBiteStrike(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        int dur,
        double amp)
    {
        double[] norms = [0.0, 0.28, 0.45, 0.70, 1.0];
        var ticks = norms.Select(u => Math.Round(u * dur, 2)).ToList();

        AddOneShotTrack(tracks, rootId, ticks, [
            (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep"),
            (-4.0 * amp, 0.0, 0.0, (0.0, -0.24 * amp), "EaseInQuad"),
            (5.5 * amp, 0.0, 0.0, (0.0, 0.42 * amp), "EaseOutBack"),
            (1.5 * amp, 0.0, 0.0, (0.0, 0.10 * amp), "CatmullRom"),
            (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep")
        ]);

        for (int i = 0; i < neckChain.Count; i++)
        {
            var bid = neckChain[i];
            var isHead = i == neckChain.Count - 1;
            double yawSign = i % 2 == 0 ? -1.0 : 1.0;
            AddOneShotTrack(tracks, bid, ticks, [
                (0.0, 0.0, 0.0, null, "SmoothStep"),
                (yawSign * 8.5 * amp, -18.0 * amp, 0.0, null, "EaseInQuad"),
                ((isHead ? 14.0 : -6.0) * amp, 26.0 * amp, 0.0, null, "EaseOutBack"),
                ((isHead ? -4.0 : 2.0) * amp, 6.0 * amp, 0.0, null, "CatmullRom"),
                (0.0, 0.0, 0.0, null, "SmoothStep")
            ]);
        }

        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            AddOneShotTrack(tracks, bid, ticks, [
                (0.0, 0.0, 0.0, null, "SmoothStep"),
                (6.0 * amp, 0.0, 0.0, null, "EaseInQuad"),
                (-9.0 * amp, 0.0, 0.0, null, "EaseOutQuad"),
                (-2.5 * amp, 0.0, 0.0, null, "CatmullRom"),
                (0.0, 0.0, 0.0, null, "SmoothStep")
            ]);
        }
    }

    private static void SynthesizeDeathCollapse(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        string rootId,
        List<string> spineChain,
        List<string> neckChain,
        List<string> tailChain,
        List<string> leftWing,
        List<string> rightWing,
        List<string> leftFore,
        List<string> rightFore,
        List<string> leftHind,
        List<string> rightHind,
        List<List<string>> multiLeft,
        List<List<string>> multiRight,
        int dur,
        double amp)
    {
        double[] norms = [0.0, 0.24, 0.58, 0.82, 1.0];
        var ticks = norms.Select(u => Math.Round(u * dur, 2)).ToList();

        AddOneShotTrack(tracks, rootId, ticks, [
            (0.0, 0.0, 0.0, (0.0, 0.0), "SmoothStep"),
            (-7.5 * amp, 0.0, 0.0, (-0.12 * amp, 0.14 * amp), "EaseOutQuad"),
            (14.5 * amp, 0.0, 0.0, (0.28 * amp, -0.36 * amp), "EaseInCubic"),
            (18.0 * amp, 0.0, 0.0, (0.34 * amp, -0.44 * amp), "EaseOutQuad"),
            (17.2 * amp, 0.0, 0.0, (0.34 * amp, -0.44 * amp), "SmoothStep")
        ]);

        for (int i = 0; i < neckChain.Count; i++)
        {
            var bid = neckChain[i];
            double g = 1.0 + i * 0.22;
            AddOneShotTrack(tracks, bid, ticks, [
                (0.0, 0.0, 0.0, null, "SmoothStep"),
                (-10.0 * g * amp, -12.0 * amp, 0.0, null, "EaseOutQuad"),
                (16.0 * g * amp, 14.0 * amp, 0.0, null, "EaseInCubic"),
                (21.0 * g * amp, 18.0 * amp, 0.0, null, "EaseOutQuad"),
                (20.5 * g * amp, 18.0 * amp, 0.0, null, "SmoothStep")
            ]);
        }

        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            double g = 1.0 + i * 0.20;
            AddOneShotTrack(tracks, bid, ticks, [
                (0.0, 0.0, 0.0, null, "SmoothStep"),
                (9.0 * g * amp, 0.0, 0.0, null, "EaseOutQuad"),
                (-14.0 * g * amp, 0.0, 0.0, null, "EaseInCubic"),
                (-18.0 * g * amp, 0.0, 0.0, null, "EaseOutQuad"),
                (-17.5 * g * amp, 0.0, 0.0, null, "SmoothStep")
            ]);
        }

        if (leftWing.Count > 0 && rightWing.Count > 0)
        {
            ApplyWingOneShot(nodes, tracks, leftWing, isRight: false, ticks,
                (segIdx, segCount, beatIdx) => beatIdx switch
                {
                    0 => (TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep"),
                    1 => (-86.0 - segIdx * 4.0, 24.0 * amp * (segIdx == 0 ? 1.0 : 0.2), "EaseOutQuad"),
                    2 => (-108.0 - segIdx * 6.0, 6.0, "EaseInCubic"),
                    _ => (-114.0 - segIdx * 6.0, 0.0, "SmoothStep")
                });
            ApplyWingOneShot(nodes, tracks, rightWing, isRight: true, ticks,
                (segIdx, segCount, beatIdx) => beatIdx switch
                {
                    0 => (-TargetGroundedZFoldBearingLeft(segIdx, segCount), 0.0, "SmoothStep"),
                    1 => (82.0 + segIdx * 4.0, 20.0 * amp * (segIdx == 0 ? 1.0 : 0.2), "EaseOutQuad"),
                    2 => (118.0 + segIdx * 7.0, 4.0, "EaseInCubic"),
                    _ => (124.0 + segIdx * 7.0, 0.0, "SmoothStep")
                });
        }

        foreach (var (limb, sign) in new[] { (leftFore, -1.0), (rightFore, 1.0), (leftHind, -1.0), (rightHind, 1.0) }
            .Concat(multiLeft.Select(l => (l, -1.0)))
            .Concat(multiRight.Select(r => (r, 1.0))))
        {
            if (limb.Count == 0) continue;
            for (int i = 0; i < limb.Count; i++)
            {
                AddOneShotTrack(tracks, limb[i], ticks, [
                    (0.0, 0.0, 0.0, null, "SmoothStep"),
                    (sign * 8.0 * amp, -12.0 * amp, 0.0, null, "EaseOutQuad"),
                    (-sign * 16.0 * amp, -26.0 * amp, 0.0, null, "EaseInCubic"),
                    (-sign * 20.0 * amp, -30.0 * amp, 0.0, null, "SmoothStep"),
                    (-sign * 20.0 * amp, -30.0 * amp, 0.0, null, "SmoothStep")
                ]);
            }
        }
    }

    // --- Wing & Limb Mathematical Helpers ---

    private static double TargetGroundedZFoldBearingLeft(int segIdx, int segCount)
    {
        if (segCount <= 1) return -126.0;
        if (segCount == 2) return segIdx == 0 ? -122.0 : -138.0;
        return segIdx switch
        {
            0 => -122.0, // Proximal arm tucked back-and-outward
            1 => -76.0,  // Forearm swept forward-and-outward (classic Z-fold wrist apex)
            2 => -128.0, // Outer membrane / pinion swept cleanly backward outside body flank
            _ => -136.0
        };
    }

    private static double TargetOpenSpreadBearingLeft(int segIdx, int segCount) =>
        -88.0 - segIdx * 3.0;

    private static double EvaluateAerialFlapBearingLeft(int segIdx, int segCount, double phase, double amp)
    {
        // Progressive whip-cascade lag from shoulder -> humerus -> forearm -> outer fan
        double phaseLag = segIdx * 0.72;
        double baseSpread = TargetOpenSpreadBearingLeft(segIdx, segCount);
        double sweepAmp = (11.5 + segIdx * 4.2) * amp;
        double raw = baseSpread + sweepAmp * Math.Sin(phase - phaseLag);
        return Math.Clamp(raw, -148.0, -56.0);
    }

    private static double EvaluateAerialCoronalFlapDeg(int segIdx, int segCount, double phase, double amp)
    {
        // Primary coronal foreshortening at upstroke/downstroke peaks with distal whip lag
        double phaseLag = segIdx * 0.65;
        double weight = segIdx == 0 ? 1.0 : segIdx == 1 ? 0.42 : 0.22;
        return (26.0 + 22.0 * Math.Sin(phase - phaseLag)) * weight * Math.Clamp(amp, 0.4, 1.35);
    }

    private static void ApplyWingBearingPoseLoop(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> wingChain,
        bool isRight,
        List<double> sampleTicks,
        int dur,
        Func<int, int, double, double> desiredWorldBearingFn,
        Func<int, int, double, double> coronalFlapFn)
    {
        var active = wingChain.Where(id => nodes.TryGetValue(id, out var nm) && !nm.IsRigid).ToList();
        if (active.Count == 0) return;

        foreach (var id in active)
        {
            tracks[id] = new List<KeyframeSample>(sampleTicks.Count);
        }

        for (int k = 0; k < sampleTicks.Count; k++)
        {
            double t = sampleTicks[k];
            double phase = (t / dur) * 2.0 * Math.PI;
            double cumParentDelta = 0.0;
            for (int s = 0; s < active.Count; s++)
            {
                var id = active[s];
                var meta = nodes[id];
                double targetBearing = desiredWorldBearingFn(s, active.Count, phase);
                targetBearing = isRight ? Math.Clamp(targetBearing, 48.0, 158.0) : Math.Clamp(targetBearing, -158.0, -48.0);
                double localAngle = DeltaAngleDeg(meta.RestShaftBearingDeg + cumParentDelta, targetBearing);
                double cFlap = coronalFlapFn(s, active.Count, phase);
                tracks[id].Add(new KeyframeSample
                {
                    Tick = t,
                    Angle = Math.Round(localAngle, 2),
                    CoronalFlap = Math.Round(cFlap, 2),
                    Easing = "CatmullRom"
                });
                cumParentDelta += localAngle;
            }
        }

        // Enforce exact loop closure on final keyframe
        foreach (var id in active)
        {
            var list = tracks[id];
            if (list.Count >= 2)
            {
                list[^1].Angle = list[0].Angle;
                list[^1].CoronalFlap = list[0].CoronalFlap;
                list[^1].SagittalPitch = list[0].SagittalPitch;
            }
        }
    }

    private static void ApplyWingOneShot(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> wingChain,
        bool isRight,
        List<double> ticks,
        Func<int, int, int, (double TargetBearing, double CoronalFlap, string Easing)> beatFn)
    {
        var active = wingChain.Where(id => nodes.TryGetValue(id, out var nm) && !nm.IsRigid).ToList();
        if (active.Count == 0) return;

        foreach (var id in active)
        {
            tracks[id] = new List<KeyframeSample>(ticks.Count);
        }

        for (int b = 0; b < ticks.Count; b++)
        {
            double cumParentDelta = 0.0;
            for (int s = 0; s < active.Count; s++)
            {
                var id = active[s];
                var meta = nodes[id];
                var (targetBearing, cFlap, easing) = beatFn(s, active.Count, b);
                targetBearing = isRight ? Math.Clamp(targetBearing, 48.0, 158.0) : Math.Clamp(targetBearing, -158.0, -48.0);
                double localAngle = DeltaAngleDeg(meta.RestShaftBearingDeg + cumParentDelta, targetBearing);
                tracks[id].Add(new KeyframeSample
                {
                    Tick = ticks[b],
                    Angle = Math.Round(localAngle, 2),
                    CoronalFlap = Math.Round(cFlap, 2),
                    Easing = easing
                });
                cumParentDelta += localAngle;
            }
        }
    }

    private static void AddStabilizedNeckStride(
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> neckChain,
        List<double> sampleTicks,
        int dur,
        double rootYawAmp,
        double neckPitchAmp)
    {
        if (neckChain.Count == 0) return;
        for (int i = 0; i < neckChain.Count; i++)
        {
            var bid = neckChain[i];
            bool isHead = i == neckChain.Count - 1 && neckChain.Count >= 2;
            int idx = i;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
            {
                if (isHead)
                {
                    double parentYaw = rootYawAmp * Math.Sin(phase);
                    for (int k = 0; k < idx; k++)
                    {
                        parentYaw += 1.8 * Math.Sin(phase - (k + 1) * 0.3);
                    }
                    return (-parentYaw * 0.90, -neckPitchAmp * 0.45 * Math.Sin(2.0 * phase), 0.0, null);
                }
                return (1.8 * Math.Sin(phase - (idx + 1) * 0.3), neckPitchAmp * 0.55 * Math.Sin(2.0 * phase), 0.0, null);
            });
        }
    }

    private static void AddTailStrideCounterwave(
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> tailChain,
        List<double> sampleTicks,
        int dur,
        double amp)
    {
        for (int i = 0; i < tailChain.Count; i++)
        {
            var bid = tailChain[i];
            double lag = 0.55 + i * 0.52;
            double deg = (4.2 + i * 1.4) * amp;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
                (-deg * Math.Sin(phase - lag), 0.0, 0.0, null));
        }
    }

    private static void AddLimbStrideChain(
        Dictionary<string, RigNodeMeta> nodes,
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> limbChain,
        List<double> sampleTicks,
        int dur,
        double phaseOffset,
        double sideSign,
        double amp)
    {
        for (int i = 0; i < limbChain.Count; i++)
        {
            var bid = limbChain[i];
            int segIdx = i;
            bool hasSagDrop = nodes.TryGetValue(bid, out var nm) && nm.SagittalDropCells > 0.05;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
            {
                double p = phase + phaseOffset - segIdx * 0.35;
                double swing = Math.Sin(p);
                double lift = Math.Max(0.0, Math.Cos(p));
                double sagPitch = (segIdx == 0 ? 28.0 * swing : segIdx == 1 ? (-18.0 * lift + 12.0 * swing) : 16.0 * swing) * amp;
                double yawDeg = hasSagDrop
                    ? sideSign * (3.5 - segIdx * 0.8) * swing * amp
                    : sideSign * (12.5 - segIdx * 2.2) * swing * amp;
                return (yawDeg, sagPitch, 0.0, null);
            });
        }
    }

    private static void AddArthropodLegChain(
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> legChain,
        List<double> sampleTicks,
        int dur,
        double phaseOffset,
        double sideSign,
        double amp)
    {
        for (int i = 0; i < legChain.Count; i++)
        {
            var bid = legChain[i];
            int segIdx = i;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
            {
                double p = phase + phaseOffset - segIdx * 0.32;
                double swing = sideSign * (14.0 - segIdx * 2.5) * Math.Sin(p) * amp;
                double sag = (segIdx == 0 ? 14.0 : -10.0) * Math.Cos(p) * amp;
                return (swing, sag, 0.0, null);
            });
        }
    }

    private static void TuckHindlimbsLoop(
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> hindChain,
        List<double> sampleTicks,
        int dur,
        double sideSign,
        double amp)
    {
        for (int i = 0; i < hindChain.Count; i++)
        {
            var bid = hindChain[i];
            int segIdx = i;
            AddPeriodicTrack(tracks, bid, sampleTicks, dur, "CatmullRom", phase =>
            {
                double sag = (segIdx == 0 ? -28.0 : segIdx == 1 ? -18.0 : -12.0) + 3.0 * Math.Sin(phase - 0.6) * amp;
                double cFlap = segIdx == 0 ? 34.0 : 0.0;
                double yaw = -sideSign * 3.0 * segIdx;
                return (yaw, sag, cFlap, null);
            });
        }
    }

    private static void ApplyHindlimbTransitionOneShot(
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> hindChain,
        List<double> ticks,
        bool isTakeoff,
        double amp)
    {
        for (int i = 0; i < hindChain.Count; i++)
        {
            var bid = hindChain[i];
            double tuckSag = i == 0 ? -28.0 : -16.0;
            double crouchSag = i == 0 ? 22.0 * amp : -18.0 * amp;
            double flap = i == 0 ? 34.0 : 0.0;
            if (isTakeoff)
            {
                AddOneShotTrack(tracks, bid, ticks, [
                    (0.0, 0.0, 0.0, null, "SmoothStep"),
                    (0.0, crouchSag, 0.0, null, "EaseInQuad"),
                    (0.0, tuckSag * 0.75, flap * 0.7, null, "EaseOutCubic"),
                    (0.0, tuckSag, flap, null, "CatmullRom"),
                    (0.0, tuckSag, flap, null, "SmoothStep")
                ]);
            }
            else
            {
                AddOneShotTrack(tracks, bid, ticks, [
                    (0.0, tuckSag, flap, null, "SmoothStep"),
                    (0.0, 18.0 * amp, flap * 0.3, null, "EaseOutQuad"),
                    (0.0, crouchSag, 0.0, null, "EaseOutCubic"),
                    (0.0, 4.0 * amp, 0.0, null, "CatmullRom"),
                    (0.0, 0.0, 0.0, null, "SmoothStep")
                ]);
            }
        }
    }

    private static List<double> BuildLoopTicks(int durationTicks, int segments)
    {
        var list = new List<double>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            list.Add(Math.Round(i * (double)durationTicks / segments, 2));
        }
        return list;
    }

    private static void AddPeriodicTrack(
        Dictionary<string, List<KeyframeSample>> tracks,
        string boneId,
        List<double> sampleTicks,
        int dur,
        string easing,
        Func<double, (double Angle, double SagPitch, double CorFlap, (double X, double Z)? Offset)> eval)
    {
        var list = new List<KeyframeSample>(sampleTicks.Count);
        for (int i = 0; i < sampleTicks.Count; i++)
        {
            double t = sampleTicks[i];
            double phase = i == sampleTicks.Count - 1 ? 0.0 : (t / dur) * 2.0 * Math.PI;
            var (a, sp, cf, off) = eval(phase);
            list.Add(new KeyframeSample
            {
                Tick = t,
                Angle = Math.Round(a, 2),
                SagittalPitch = Math.Round(sp, 2),
                CoronalFlap = Math.Round(cf, 2),
                RootOffset = off.HasValue ? (Math.Round(off.Value.X, 3), Math.Round(off.Value.Z, 3)) : null,
                Easing = easing
            });
        }
        tracks[boneId] = list;
    }

    private static void AddOneShotTrack(
        Dictionary<string, List<KeyframeSample>> tracks,
        string boneId,
        List<double> ticks,
        (double Angle, double SagPitch, double CorFlap, (double X, double Z)? Offset, string Easing)[] beats)
    {
        int n = Math.Min(ticks.Count, beats.Length);
        var list = new List<KeyframeSample>(n);
        for (int i = 0; i < n; i++)
        {
            var b = beats[i];
            list.Add(new KeyframeSample
            {
                Tick = ticks[i],
                Angle = Math.Round(b.Angle, 2),
                SagittalPitch = Math.Round(b.SagPitch, 2),
                CoronalFlap = Math.Round(b.CorFlap, 2),
                RootOffset = b.Offset.HasValue ? (Math.Round(b.Offset.Value.X, 3), Math.Round(b.Offset.Value.Z, 3)) : null,
                Easing = b.Easing
            });
        }
        tracks[boneId] = list;
    }

    // --- Rig Parsing & Auto-Chain Discovery ---

    private static Dictionary<string, RigNodeMeta> LoadWorkspaceRigNodes(string creatureDir, string? rigDefName, List<string> warnings)
    {
        var defsDir = Path.Combine(creatureDir, "Defs");
        var texturesDir = Path.Combine(creatureDir, "Textures");
        var result = new Dictionary<string, RigNodeMeta>(StringComparer.Ordinal);
        if (!Directory.Exists(defsDir)) return result;

        XElement? chosenRig = null;
        foreach (var file in Directory.EnumerateFiles(defsDir, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var el in doc.Root.Elements().Where(e => e.Name.LocalName.Contains("GiantRigDef", StringComparison.Ordinal)))
            {
                var dn = el.Element("defName")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(rigDefName) || string.Equals(dn, rigDefName.Trim(), StringComparison.Ordinal))
                {
                    chosenRig = el;
                    break;
                }
            }
            if (chosenRig != null) break;
        }

        var nodesEl = chosenRig?.Element("nodes");
        if (nodesEl == null) return result;

        var rawOrder = new List<string>();
        foreach (var li in nodesEl.Elements("li"))
        {
            var id = li.Element("nodeId")?.Value?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var pid = li.Element("parentId")?.Value?.Trim();
            if (string.IsNullOrEmpty(pid)) pid = null;
            var res = ParseInt(li.Element("textureResolution")?.Value, 512);
            var ds = ParseVec2(li.Element("drawSize")?.Value, (1.0, 1.0));
            var pivPx = ParseVec2(li.Element("pivotPixel")?.Value, (res * 0.5, res * 0.5));
            var pivUv = ParseVec2(li.Element("pivotUV")?.Value, (pivPx.X / res, 1.0 - pivPx.Y / res));

            result[id] = new RigNodeMeta
            {
                Id = id,
                ParentId = pid,
                BaseAngle = ParseDouble(li.Element("baseAngle")?.Value, 0.0),
                DrawSize = ds,
                PivotUv = pivUv,
                RawParentSocketPx = li.Element("parentSocketPixel")?.Value,
                RawParentSocketUv = li.Element("parentSocketUV")?.Value,
                Resolution = res,
                IsRigid = ParseBool(li.Element("isRigid")?.Value, false),
                IsCoronalFlapHinge = ParseBool(li.Element("isCoronalFlapHinge")?.Value, false),
                SagittalDropCells = ParseDouble(li.Element("sagittalDropCells")?.Value, 0.0),
                TexRel = li.Element("texPath")?.Value?.Trim()
            };
            rawOrder.Add(id);
        }

        foreach (var kv in result)
        {
            if (kv.Value.ParentId != null && result.TryGetValue(kv.Value.ParentId, out var p))
            {
                p.Children.Add(kv.Key);
            }
        }

        // Topologically solve rest poses & rest shaft bearings (heading = 0 deg North)
        var sorted = new List<string>(result.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string id)
        {
            if (!visited.Add(id)) return;
            var pid = result[id].ParentId;
            if (pid != null && result.ContainsKey(pid)) Visit(pid);
            sorted.Add(id);
        }
        foreach (var id in rawOrder) Visit(id);

        foreach (var id in sorted)
        {
            var n = result[id];
            if (n.ParentId != null && result.TryGetValue(n.ParentId, out var p))
            {
                var pRes = p.Resolution;
                var sockPx = ParseVec2(n.RawParentSocketPx, (pRes * 0.5, pRes * 0.5));
                n.ParentSocketUv = ParseVec2(n.RawParentSocketUv, (sockPx.X / pRes, 1.0 - sockPx.Y / pRes));
                var du = (n.ParentSocketUv.U - p.PivotUv.U) * p.DrawSize.X;
                var dv = (n.ParentSocketUv.V - p.PivotUv.V) * p.DrawSize.Y;
                var rad = p.RestWorldAngleDeg * Math.PI / 180.0;
                var c = Math.Cos(rad);
                var s = Math.Sin(rad);
                n.RestJointXZ = (p.RestJointXZ.X + du * c + dv * s, p.RestJointXZ.Z - du * s + dv * c);
                n.RestWorldAngleDeg = NormalizeSigned180(p.RestWorldAngleDeg + n.BaseAngle);
            }
            else
            {
                n.RestJointXZ = (0.0, 0.0);
                n.RestWorldAngleDeg = NormalizeSigned180(n.BaseAngle);
            }
        }

        foreach (var id in sorted)
        {
            var n = result[id];
            (double U, double V)? tipUv = null;
            var primaryChild = n.Children.FirstOrDefault(cid => !result[cid].IsRigid) ?? n.Children.FirstOrDefault();
            if (primaryChild != null)
            {
                tipUv = result[primaryChild].ParentSocketUv;
            }
            else if (!string.IsNullOrEmpty(n.TexRel))
            {
                var texFile = CreatureWorkspace.ResolveSandboxedTexturePng(
                    texturesDir,
                    n.TexRel,
                    allowBaseNameFallback: false,
                    requireExists: false);
                if (File.Exists(texFile))
                {
                    tipUv = EstimateFarthestOpaqueUv(texFile, n.PivotUv);
                }
            }

            if (tipUv.HasValue)
            {
                var dxLocal = (tipUv.Value.U - n.PivotUv.U) * n.DrawSize.X;
                var dzLocal = (tipUv.Value.V - n.PivotUv.V) * n.DrawSize.Y;
                if (dxLocal * dxLocal + dzLocal * dzLocal > 1e-4)
                {
                    var localDeg = Math.Atan2(dxLocal, dzLocal) * 180.0 / Math.PI;
                    n.RestShaftBearingDeg = NormalizeSigned180(n.RestWorldAngleDeg + localDeg);
                    continue;
                }
            }
            n.RestShaftBearingDeg = n.RestWorldAngleDeg;
        }

        return result;
    }

    private static (double U, double V)? EstimateFarthestOpaqueUv(string pngPath, (double U, double V) pivotUv)
    {
        try
        {
            var img = PngCodec.Decode(File.ReadAllBytes(pngPath));
            double sumU = 0.0, sumV = 0.0, weightSum = 0.0;
            for (int y = 0; y < img.Height; y += 4)
            {
                for (int x = 0; x < img.Width; x += 4)
                {
                    if (img.GetA(x, y) < 32) continue;
                    double u = x / (double)Math.Max(1, img.Width - 1);
                    double v = 1.0 - y / (double)Math.Max(1, img.Height - 1);
                    double du = u - pivotUv.U;
                    double dv = v - pivotUv.V;
                    double d2 = du * du + dv * dv;
                    if (d2 < 0.01) continue;
                    sumU += u * d2;
                    sumV += v * d2;
                    weightSum += d2;
                }
            }
            return weightSum > 1e-5 ? (sumU / weightSum, sumV / weightSum) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveRootBone(Dictionary<string, RigNodeMeta> nodes, string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot) && nodes.TryGetValue(explicitRoot.Trim(), out var r) && r.ParentId == null)
        {
            return r.Id;
        }
        return nodes.Values.First(n => n.ParentId == null).Id;
    }

    private static List<string>? ResolveChain(Dictionary<string, RigNodeMeta> nodes, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var ids = raw.Split([',', '>', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(nodes.ContainsKey)
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    private static List<string> AutoDetectSpineChain(Dictionary<string, RigNodeMeta> nodes, string rootId)
    {
        var chain = new List<string> { rootId };
        var cur = rootId;
        while (true)
        {
            var next = nodes[cur].Children.FirstOrDefault(cid =>
            {
                var low = cid.ToLowerInvariant();
                return (low.Contains("thorax") || low.Contains("spine") || low.Contains("abdomen") || low.Contains("pelvis") || low.Contains("hull"))
                       && !IsLeftSideId(low) && !IsRightSideId(low);
            });
            if (next == null || chain.Contains(next)) break;
            chain.Add(next);
            cur = next;
        }
        return chain;
    }

    private static List<string> AutoDetectAxialChain(Dictionary<string, RigNodeMeta> nodes, string rootId, string[] tokens)
    {
        var start = nodes.Values
            .Where(n => n.ParentId != null && !IsLeftSideId(n.Id) && !IsRightSideId(n.Id)
                        && tokens.Any(t => n.Id.Contains(t, StringComparison.OrdinalIgnoreCase))
                        && (n.ParentId == null || !tokens.Any(t => n.ParentId.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (start == null) return [];

        var chain = new List<string> { start.Id };
        var cur = start.Id;
        while (true)
        {
            var next = nodes[cur].Children.FirstOrDefault(cid =>
                !IsLeftSideId(cid) && !IsRightSideId(cid) && !nodes[cid].IsRigid &&
                tokens.Any(t => cid.Contains(t, StringComparison.OrdinalIgnoreCase)));
            if (next == null || chain.Contains(next)) break;
            chain.Add(next);
            cur = next;
        }
        return chain;
    }

    private static (List<string> Left, List<string> Right) ResolveBilateralChains(
        Dictionary<string, RigNodeMeta> nodes,
        string? rawLeft,
        string? rawRight,
        string[] tokens)
    {
        var expL = ResolveChain(nodes, rawLeft);
        var expR = ResolveChain(nodes, rawRight);
        if (expL != null && expR != null) return (expL, expR);

        var leftRoot = nodes.Values
            .Where(n => n.ParentId != null && IsLeftSideId(n.Id)
                        && tokens.Any(t => n.Id.Contains(t, StringComparison.OrdinalIgnoreCase))
                        && !IsLeftSideId(n.ParentId))
            .OrderByDescending(n => CountLongestSubchain(nodes, n.Id))
            .FirstOrDefault();
        var rightRoot = nodes.Values
            .Where(n => n.ParentId != null && IsRightSideId(n.Id)
                        && tokens.Any(t => n.Id.Contains(t, StringComparison.OrdinalIgnoreCase))
                        && !IsRightSideId(n.ParentId))
            .OrderByDescending(n => CountLongestSubchain(nodes, n.Id))
            .FirstOrDefault();

        return (
            expL ?? (leftRoot != null ? WalkPrimaryLimbChain(nodes, leftRoot.Id) : []),
            expR ?? (rightRoot != null ? WalkPrimaryLimbChain(nodes, rightRoot.Id) : []));
    }

    private static (List<List<string>> Left, List<List<string>> Right) ResolveMultiLegChains(
        Dictionary<string, RigNodeMeta> nodes,
        string? rawLeft,
        string? rawRight)
    {
        List<List<string>> ParseGroup(string? raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? []
                : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => ResolveChain(nodes, s))
                    .Where(c => c is { Count: > 0 })
                    .Select(c => c!)
                    .ToList();

        var expL = ParseGroup(rawLeft);
        var expR = ParseGroup(rawRight);
        if (expL.Count > 0 || expR.Count > 0) return (expL, expR);

        var leftRoots = nodes.Values
            .Where(n => n.ParentId != null && IsLeftSideId(n.Id)
                        && (n.Id.Contains("leg", StringComparison.OrdinalIgnoreCase) || n.Id.Contains("thigh", StringComparison.OrdinalIgnoreCase))
                        && !IsLeftSideId(n.ParentId))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => WalkPrimaryLimbChain(nodes, n.Id))
            .ToList();
        var rightRoots = nodes.Values
            .Where(n => n.ParentId != null && IsRightSideId(n.Id)
                        && (n.Id.Contains("leg", StringComparison.OrdinalIgnoreCase) || n.Id.Contains("thigh", StringComparison.OrdinalIgnoreCase))
                        && !IsRightSideId(n.ParentId))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => WalkPrimaryLimbChain(nodes, n.Id))
            .ToList();
        return (leftRoots, rightRoots);
    }

    private static List<string> WalkPrimaryLimbChain(Dictionary<string, RigNodeMeta> nodes, string startId)
    {
        var chain = new List<string> { startId };
        var cur = startId;
        while (true)
        {
            var candidates = nodes[cur].Children
                .Where(cid => !nodes[cid].IsRigid
                              && !cid.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                              && !cid.Contains("axilla", StringComparison.OrdinalIgnoreCase)
                              && !cid.Contains("inner_fan", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(cid => CountLongestSubchain(nodes, cid))
                .ToList();
            if (candidates.Count == 0 || chain.Contains(candidates[0])) break;
            chain.Add(candidates[0]);
            cur = candidates[0];
        }
        return chain;
    }

    private static int CountLongestSubchain(Dictionary<string, RigNodeMeta> nodes, string id, int depth = 0)
    {
        if (depth > 24 || !nodes.TryGetValue(id, out var n) || n.IsRigid) return 0;
        int best = 0;
        foreach (var c in n.Children)
        {
            best = Math.Max(best, CountLongestSubchain(nodes, c, depth + 1));
        }
        return 1 + best;
    }

    private static bool HasExplicitSideWord(string[] tokens, string word)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == word) return true;
        }
        return false;
    }

    private static bool IsShortSideToken(string token, char sideChar)
    {
        if (token.Length == 0) return false;
        if (token[0] == sideChar)
        {
            if (token.Length == 1) return true;
            for (int i = 1; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i])) return false;
            }
            return true;
        }
        if (token.Length >= 2 && (token[0] == 'f' || token[0] == 'r') && token[1] == sideChar)
        {
            if (token.Length == 2) return true;
            for (int i = 2; i < token.Length; i++)
            {
                if (!char.IsDigit(token[i])) return false;
            }
            return true;
        }
        return false;
    }

    private static bool IsLeftSideId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var tokens = id.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (HasExplicitSideWord(tokens, "left")) return true;
        if (HasExplicitSideWord(tokens, "right")) return false;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (IsShortSideToken(tokens[i], 'l')) return true;
        }
        return false;
    }

    private static bool IsRightSideId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var tokens = id.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (HasExplicitSideWord(tokens, "right")) return true;
        if (HasExplicitSideWord(tokens, "left")) return false;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (IsShortSideToken(tokens[i], 'r')) return true;
        }
        return false;
    }

    private static void SanitizeTracks(
        Dictionary<string, RigNodeMeta> nodes,
        string rootId,
        Dictionary<string, List<KeyframeSample>> tracks,
        List<string> warnings)
    {
        foreach (var id in tracks.Keys.ToList())
        {
            if (!nodes.TryGetValue(id, out var meta))
            {
                tracks.Remove(id);
                continue;
            }
            if (meta.IsRigid && meta.ParentId != null)
            {
                tracks.Remove(id);
                warnings.Add($"Skipped <isRigid> bone '{id}'.");
                continue;
            }
            if (id != rootId)
            {
                foreach (var kf in tracks[id])
                {
                    kf.RootOffset = null;
                }
            }
        }
    }

    private static XElement BuildAnimElement(
        string animDefName,
        string preset,
        int durationTicks,
        bool isLoop,
        int? impactTick,
        string? triggerToolTag,
        string? isolatedRoot,
        Dictionary<string, List<KeyframeSample>> tracks)
    {
        var animEl = new XElement("GiantCreatureFramework.GiantAnimDef",
            new XElement("defName", animDefName),
            new XElement("label", $"{animDefName} ({preset})"),
            new XElement("durationTicks", durationTicks.ToString(CultureInfo.InvariantCulture)),
            new XElement("loop", isLoop ? "true" : "false"),
            new XElement("blendInTicks", isLoop ? "8" : "6"));

        if (impactTick.HasValue)
        {
            animEl.Add(new XElement("impactTick", impactTick.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (!string.IsNullOrWhiteSpace(triggerToolTag))
        {
            animEl.Add(new XElement("triggerToolTag", triggerToolTag.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(isolatedRoot))
        {
            animEl.Add(new XElement("isolatedSubtreeRootBoneId", isolatedRoot));
        }

        var tracksEl = new XElement("tracks");
        foreach (var (nodeId, kfs) in tracks)
        {
            var kfContainer = new XElement("keyframes");
            foreach (var kf in kfs)
            {
                var li = new XElement("li",
                    new XElement("tick", FormatNum(kf.Tick)),
                    new XElement("angle", FormatNum(kf.Angle)));
                if (Math.Abs(kf.SagittalPitch) > 1e-3)
                {
                    li.Add(new XElement("sagittalPitch", FormatNum(kf.SagittalPitch)));
                }
                if (Math.Abs(kf.CoronalFlap) > 1e-3)
                {
                    li.Add(new XElement("coronalFlap", FormatNum(kf.CoronalFlap)));
                }
                if (kf.RootOffset.HasValue && (Math.Abs(kf.RootOffset.Value.X) > 1e-4 || Math.Abs(kf.RootOffset.Value.Z) > 1e-4))
                {
                    li.Add(new XElement("offset", string.Create(CultureInfo.InvariantCulture, $"({kf.RootOffset.Value.X:0.###}, {kf.RootOffset.Value.Z:0.###})")));
                }
                li.Add(new XElement("easing", kf.Easing));
                kfContainer.Add(li);
            }
            tracksEl.Add(new XElement("li",
                new XElement("nodeId", nodeId),
                kfContainer));
        }
        animEl.Add(tracksEl);
        return animEl;
    }

    private static string UpsertAnimIntoWorkspaceXml(
        CreatureWorkspace workspace,
        string creatureName,
        string relativeXmlPath,
        string animDefName,
        XElement animEl)
    {
        var fullPath = workspace.ResolveDefsXmlPath(creatureName, relativeXmlPath, createParentDirs: true);
        XDocument doc;
        if (File.Exists(fullPath))
        {
            doc = XDocument.Load(fullPath);
            if (doc.Root == null || doc.Root.Name.LocalName != "Defs")
            {
                throw new InvalidDataException($"Existing XML file '{relativeXmlPath}' does not have root <Defs>.");
            }
            var existing = doc.Root.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Contains("GiantAnimDef", StringComparison.Ordinal)
                                     && string.Equals(e.Element("defName")?.Value?.Trim(), animDefName, StringComparison.Ordinal));
            if (existing != null)
            {
                existing.ReplaceWith(animEl);
            }
            else
            {
                doc.Root.Add(animEl);
            }
        }
        else
        {
            doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Defs", animEl));
        }

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture))
        {
            doc.Save(writer);
        }
        var xmlText = sb.ToString().Replace("utf-16", "utf-8", StringComparison.OrdinalIgnoreCase);
        var (_, savedRel, _) = workspace.WriteCreatureXml(creatureName, relativeXmlPath, xmlText);
        return savedRel;
    }

    private static string FormatNum(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-4
            ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double NormalizeSigned180(double deg)
    {
        var r = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return Math.Abs(r + 180.0) < 1e-9 ? 180.0 : r;
    }

    private static double DeltaAngleDeg(double fromDeg, double toDeg) =>
        NormalizeSigned180(toDeg - fromDeg);

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
}
