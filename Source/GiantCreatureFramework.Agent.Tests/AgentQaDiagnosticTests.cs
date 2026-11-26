using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GiantCreatureFramework.Agent;
using GiantCreatureFramework.Agent.Authoring;
using GiantCreatureFramework.Agent.Imaging;
using GiantCreatureFramework.Agent.Probes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GiantCreatureFramework.Agent.Tests;

/// <summary>
/// Post-fix QA regression tests covering all confirmed Module 6 fixes in GiantCreatureFramework.Agent:
/// tool schema number validation, AffineWarp cubic/bilinear small-source safety, UserImg2ImgGuard multi-turn
/// handling and full image stripping, AgentTurnRunner.Compact system-prompt &amp; synthetic-preview preservation,
/// synthesis attempt cap, ProceduralLocomotionGenerator NaN / bilateral side / wing tail seam fixes,
/// RigJointAuditor cycle detection &amp; duplicate whipId handling, path traversal sandboxing,
/// OpenUrlInBrowser HTTP/HTTPS allowlist, PngCodec IHDR/32-bit overflow hardening, Valstrax blocklists,
/// and SpriteSheetExtractor perf.
/// Run via: dotnet test .\Source\GiantCreatureFramework.Agent.Tests\GiantCreatureFramework.Agent.Tests.csproj -c Release --filter TestCategory=QaAudit
/// </summary>
[TestClass]
[TestCategory("QaAudit")]
public sealed class AgentQaDiagnosticTests
{
    private const string ValidStyle1Prompt =
        "Strict 90-degree overhead orthographic top-down modular part sheet on a SOLID PURE FLAT #00FF00 background with wide green spacing between parts and zero cast shadows. 1 separated components all strictly facing UP toward 12 o'clock: " +
        "(Part 1) Heavy rounded dorsal carapace shell with stepped collar plates. " +
        "Color palette: primary surfaces in #D6DCE5 cool titanium stepping through #96A2B4 slate midtones to #5D6778 deep shadow plates, secondary structural blocks in #48505E dark gunmetal, and accent details in #38BDF8 cyan emitter cores; darkest value anywhere clamped at #3E4248, no bright specular rim highlights, no glossy lip-gloss highlights. " +
        "Art medium and brushwork: clean lineless vector-and-airbrush industrial sci-fi illustration with zero lineart layer, zero wireframe, and zero brush or sponge grain—combining smooth curved cylindrical shells, circular rings, and rounded-arc armor cowlings shaded with continuous soft cylindrical airbrush gradients alongside rich multi-layered overlapping chamfered armor plates and stepped mechanical collars with crisp edge-to-edge tonal steps (never simplistic empty pills, and strictly NO triangular or diamond low-poly crystal tessellation), meeting neighboring shapes and #00FF00 directly edge-to-edge. " +
        "No text.";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "1.7", "AgentSkills", "tools.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root containing 1.7\\AgentSkills\\tools.json");
    }

    private static byte[] BuildSolidOpaquePng(int size = 64)
    {
        var img = new RgbaImage(size, size);
        for (int i = 0; i < size * size; i++)
        {
            img.Pixels[i * 4] = 140;
            img.Pixels[i * 4 + 1] = 150;
            img.Pixels[i * 4 + 2] = 165;
            img.Pixels[i * 4 + 3] = 255;
        }
        return PngCodec.Encode(img);
    }

    [TestMethod]
    public void QA_AGENT_01_ToolArgumentValidationAcceptsSchemaTypeNumberAndEnforcesRange()
    {
        var repoRoot = FindRepoRoot();
        var toolsRoot = JsonNode.Parse(File.ReadAllText(Path.Combine(repoRoot, "1.7", "AgentSkills", "tools.json")))!;
        var toolsJson = toolsRoot["schemas"]!.AsArray();
        var genTool = toolsJson.First(t => t?["name"]?.ToString() == "generate_locomotion_anim")!;
        var genParams = genTool["parameters"]!;
        var stripTool = toolsJson.First(t => t?["name"]?.ToString() == "preview_anim_strip")!;
        var stripParams = stripTool["parameters"]!;

        using var validGenDoc = JsonDocument.Parse(
            """{"creatureName":"TestWyvern","relativeXmlPath":"Defs/Anims.xml","animDefName":"GCF_Anim_Test","preset":"quadruped_gait","amplitudeScale":1.25}""");
        ToolArgumentValidation.Validate(genParams, validGenDoc.RootElement);

        using var validStripDoc = JsonDocument.Parse(
            """{"creatureName":"TestWyvern","animDefName":"GCF_Anim_Test","headingDeg":180}""");
        ToolArgumentValidation.Validate(stripParams, validStripDoc.RootElement);

        using var outOfRangeDoc = JsonDocument.Parse(
            """{"creatureName":"TestWyvern","relativeXmlPath":"Defs/Anims.xml","animDefName":"GCF_Anim_Test","preset":"quadruped_gait","amplitudeScale":9.5}""");
        Assert.ThrowsException<ArgumentException>(() => ToolArgumentValidation.Validate(genParams, outOfRangeDoc.RootElement));

        using var wrongTypeDoc = JsonDocument.Parse(
            """{"creatureName":"TestWyvern","animDefName":"GCF_Anim_Test","headingDeg":"south"}""");
        Assert.ThrowsException<ArgumentException>(() => ToolArgumentValidation.Validate(stripParams, wrongTypeDoc.RootElement));
    }

    [TestMethod]
    public void QA_AGENT_02_AffineWarpHandlesSmallSourcesWithoutUnderflow()
    {
        foreach (int dim in new[] { 1, 2, 3 })
        {
            var srcF = new float[dim * dim * 3];
            var srcB = new byte[dim * dim * 3];
            Array.Fill(srcF, 0.5f);
            Array.Fill(srcB, (byte)128);

            var fwd = AffineWarp.BuildForwardMatrix(0, 0, 0, 0, 0, 1.0, 1.0);
            var inv = AffineWarp.InvertForwardMatrix(fwd);
            var (sx, sy, frac) = AffineWarp.BuildMap(inv, 4, 4);

            var outF = AffineWarp.WarpFloat3(srcF, dim, dim, sx, sy, frac, 4, 4, cubic: true, 0f, 0f, 0f);
            var outB = AffineWarp.WarpByte3(srcB, dim, dim, sx, sy, frac, 4, 4, cubic: true, 0, 0, 0);
            Assert.AreEqual(4 * 4 * 3, outF.Length);
            Assert.AreEqual(4 * 4 * 3, outB.Length);
        }
    }

    [TestMethod]
    public void QA_AGENT_03_UserImg2ImgGuardAllowsFollowUpPureTextAndStripsAllImagesOnViolation()
    {
        // Scenario A: Turn 1 has a harmless diagnostic screenshot; Turn 2 is a pure text authoring request.
        var messagesA = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Check this HUD error screenshot please" },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,iVBORw0KGgo=" } }
                }
            },
            new JsonObject { ["role"] = "assistant", ["content"] = "Sure, I see the HUD layout issue." },
            new JsonObject { ["role"] = "user", ["content"] = "Now generate a brand-new original giant creature from text only." }
        };

        var inspA = UserImg2ImgGuard.InspectAndSanitizeTurnMessages(messagesA);
        Assert.IsFalse(inspA.IsImg2ImgViolation, "Benign historical screenshot must not lock out a subsequent pure-text authoring turn.");
        Assert.IsFalse(inspA.HasUserImageAttachment, "Pure-text Turn 2 should not inherit HasUserImageAttachment from Turn 1.");

        // Scenario B: Turn 1 has an image and Turn 2 attempts img2img -> MUST flag violation AND strip ALL prior user image_url entries!
        var messagesB = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Here is a screenshot" },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,iVBORw0KGgo=" } }
                }
            },
            new JsonObject { ["role"] = "assistant", ["content"] = "Noted." },
            new JsonObject { ["role"] = "user", ["content"] = "请用我发的这张图片做图生图生成一只巨兽" }
        };

        var inspB = UserImg2ImgGuard.InspectAndSanitizeTurnMessages(messagesB);
        Assert.IsTrue(inspB.IsImg2ImgViolation, "Explicit img2img attempt must be blocked.");
        Assert.IsFalse(messagesB.ToJsonString().Contains("image_url", StringComparison.Ordinal),
            "All prior user image_url entries must be stripped when an img2img violation is detected.");
    }

    [TestMethod]
    public void QA_AGENT_04_AgentTurnRunnerCompactPreservesAllSystemPromptsAndActiveUserMessage()
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "IMMUTABLE_SECURITY_GUARD" },
            new JsonObject { ["role"] = "system", ["content"] = "BASE_SYSTEM_PROMPT" },
            new JsonObject { ["role"] = "system", ["content"] = "SUBMISSION_CONTRACT_PROMPT" },
            new JsonObject { ["role"] = "system", ["content"] = "EVIDENCE_CONTRACT_PROMPT" },
            new JsonObject { ["role"] = "user", ["content"] = new string('A', 200) },
            new JsonObject { ["role"] = "assistant", ["content"] = new string('B', 200) },
            new JsonObject { ["role"] = "user", ["content"] = "ACTIVE_USER_REQUEST" }
        };

        AgentTurnRunner.Compact(messages, maxContextChars: 300, compactThresholdChars: 200);
        var remainingContents = messages.Select(m => m?["content"]?.ToString() ?? "").ToList();

        Assert.IsTrue(remainingContents.Contains("IMMUTABLE_SECURITY_GUARD"), "System[0] must be preserved.");
        Assert.IsTrue(remainingContents.Contains("BASE_SYSTEM_PROMPT"), "System[1] must be preserved.");
        Assert.IsTrue(remainingContents.Contains("SUBMISSION_CONTRACT_PROMPT"), "System[2] must be preserved.");
        Assert.IsTrue(remainingContents.Contains("EVIDENCE_CONTRACT_PROMPT"), "System[3] must be preserved.");
        Assert.IsTrue(remainingContents.Contains("ACTIVE_USER_REQUEST"), "Active user request must be preserved.");

        // Synthetic tool-preview user message must NOT be treated as a real user turn boundary that deletes the active prompt.
        var previewIntro = AgentProtocol.Format("TurnRunner_AttachedImageIntro", "Work/sheet.png");
        var syntheticOnly = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "SYS" },
            new JsonObject { ["role"] = "user", ["content"] = "ACTIVE_USER_PROMPT_" + new string('X', 200) },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = previewIntro },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" } }
                }
            }
        };

        Assert.ThrowsException<InvalidDataException>(
            () => AgentTurnRunner.Compact(syntheticOnly, maxContextChars: 120, compactThresholdChars: 100),
            "Compact must refuse to drop the sole real user turn by using a synthetic tool-preview message as a turn boundary.");
    }

    [TestMethod]
    public void QA_AGENT_05_ProceduralLocomotionGuardsNaNFixesSideClassificationAndWingTailSeam()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "gcf_qa_agent_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ws = new CreatureWorkspace(tmpRoot);
            var cDir = ws.GetCreatureDir("TestQaWyvern", createIfMissing: true);
            var defsDir = Path.Combine(cDir, "Defs");
            Directory.CreateDirectory(defsDir);

            var rigXml = """
<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <GiantCreatureFramework.GiantRigDef>
    <defName>GCF_Rig_TestQaWyvern</defName>
    <nodes>
      <li><nodeId>thorax</nodeId><drawSize>(4,4)</drawSize></li>
      <li><nodeId>tail_1</nodeId><parentId>thorax</parentId><drawSize>(2,2)</drawSize><parentSocketUV>(0.5,0.2)</parentSocketUV><baseAngle>180</baseAngle></li>
      <li><nodeId>leg_rear_left</nodeId><parentId>thorax</parentId><drawSize>(2,2)</drawSize><parentSocketUV>(0.25,0.3)</parentSocketUV></li>
      <li><nodeId>leg_rear_right</nodeId><parentId>thorax</parentId><drawSize>(2,2)</drawSize><parentSocketUV>(0.75,0.3)</parentSocketUV></li>
      <li><nodeId>wing_upper_left</nodeId><parentId>thorax</parentId><drawSize>(3,2)</drawSize><parentSocketUV>(0.2,0.6)</parentSocketUV><isCoronalFlapHinge>true</isCoronalFlapHinge></li>
      <li><nodeId>wing_upper_right</nodeId><parentId>thorax</parentId><drawSize>(3,2)</drawSize><parentSocketUV>(0.8,0.6)</parentSocketUV><isCoronalFlapHinge>true</isCoronalFlapHinge></li>
    </nodes>
  </GiantCreatureFramework.GiantRigDef>
</Defs>
""";
            File.WriteAllText(Path.Combine(defsDir, "Rig.xml"), rigXml);

            // 1) Non-finite AmplitudeScale defaults safely to 1.0 and never serializes NaN.
            ProceduralLocomotionGenerator.GenerateAndWrite(ws, new LocomotionAnimRequest
            {
                CreatureName = "TestQaWyvern",
                RelativeXmlPath = "Defs/AnimNonFinite.xml",
                AnimDefName = "GCF_Anim_TestNonFinite",
                Preset = "quadruped_gait",
                AmplitudeScale = double.NaN
            });
            var nanXml = File.ReadAllText(Path.Combine(cDir, "Defs", "AnimNonFinite.xml"));
            Assert.IsFalse(nanXml.Contains("NaN", StringComparison.Ordinal), "AmplitudeScale=NaN must never serialize literal NaN into XML.");
            Assert.IsFalse(nanXml.Contains("Infinity", StringComparison.Ordinal), "AmplitudeScale=NaN must never serialize Infinity into XML.");

            // 2) leg_rear_left must be classified as LEFT (opposite phase/sign from leg_rear_right) in both gait and death_collapse.
            ProceduralLocomotionGenerator.GenerateAndWrite(ws, new LocomotionAnimRequest
            {
                CreatureName = "TestQaWyvern",
                RelativeXmlPath = "Defs/AnimGait.xml",
                AnimDefName = "GCF_Anim_TestGait",
                Preset = "quadruped_gait",
                AmplitudeScale = 1.0
            });
            var gaitXml = XDocument.Load(Path.Combine(cDir, "Defs", "AnimGait.xml"));
            var tracks = gaitXml.Descendants("tracks").Elements("li").ToList();
            var rearLeft = tracks.FirstOrDefault(t => t.Element("nodeId")?.Value == "leg_rear_left");
            var rearRight = tracks.FirstOrDefault(t => t.Element("nodeId")?.Value == "leg_rear_right");
            Assert.IsNotNull(rearLeft, "leg_rear_left track should be synthesized.");
            Assert.IsNotNull(rearRight, "leg_rear_right track should be synthesized.");
            var rlPitch = rearLeft!.Descendants("sagittalPitch").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();
            var rrPitch = rearRight!.Descendants("sagittalPitch").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();
            Assert.IsTrue(rlPitch.Zip(rrPitch).Any(p => Math.Abs(p.First - p.Second) > 5.0),
                "leg_rear_left and leg_rear_right must have alternating gait phase.");

            ProceduralLocomotionGenerator.GenerateAndWrite(ws, new LocomotionAnimRequest
            {
                CreatureName = "TestQaWyvern",
                RelativeXmlPath = "Defs/AnimDeath.xml",
                AnimDefName = "GCF_Anim_TestDeath",
                Preset = "death_collapse",
                AmplitudeScale = 1.0
            });
            var deathXml = XDocument.Load(Path.Combine(cDir, "Defs", "AnimDeath.xml"));
            var deathTracks = deathXml.Descendants("tracks").Elements("li").ToList();
            var deathRl = deathTracks.First(t => t.Element("nodeId")?.Value == "leg_rear_left").Descendants("angle").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();
            var deathRr = deathTracks.First(t => t.Element("nodeId")?.Value == "leg_rear_right").Descendants("angle").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();
            Assert.AreEqual(-deathRl.Last(), deathRr.Last(), 0.05, "death_collapse must mirror leg_rear_left and leg_rear_right collapse angles.");

            // 3) wing_flap_loop tail yaw track must be smoothly 2*pi periodic without half-frequency seam jump.
            ProceduralLocomotionGenerator.GenerateAndWrite(ws, new LocomotionAnimRequest
            {
                CreatureName = "TestQaWyvern",
                RelativeXmlPath = "Defs/AnimFlap.xml",
                AnimDefName = "GCF_Anim_TestFlap",
                Preset = "wing_flap_loop",
                DurationTicks = 48
            });
            var flapXml = XDocument.Load(Path.Combine(cDir, "Defs", "AnimFlap.xml"));
            var tailTrack = flapXml.Descendants("tracks").Elements("li").First(t => t.Element("nodeId")?.Value == "tail_1");
            var tailAngles = tailTrack.Descendants("angle").Select(e => double.Parse(e.Value, CultureInfo.InvariantCulture)).ToList();
            Assert.AreEqual(tailAngles.First(), tailAngles.Last(), 0.05, "Loop start and end tail angles must match.");
            double prevStep = Math.Abs(tailAngles[^2] - tailAngles[^3]);
            double closingStep = Math.Abs(tailAngles[^1] - tailAngles[^2]);
            Assert.IsTrue(closingStep <= Math.Max(2.0, prevStep * 2.5),
                $"Closing tail step ({closingStep:F2}) should be smooth relative to interior step ({prevStep:F2}).");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [TestMethod]
    public void QA_AGENT_06_RigJointAuditorDetectsCyclesAndDuplicateWhipIdsSafely()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "gcf_qa_audit_" + Guid.NewGuid().ToString("N"));
        try
        {
            var ws = new CreatureWorkspace(tmpRoot);
            var cDir = ws.GetCreatureDir("BadRig", createIfMissing: true);
            var defsDir = Path.Combine(cDir, "Defs");
            var texDir = Path.Combine(cDir, "Textures");
            Directory.CreateDirectory(defsDir);
            Directory.CreateDirectory(Path.Combine(texDir, "Dummy"));
            File.WriteAllBytes(Path.Combine(texDir, "Dummy", "part.png"), BuildSolidOpaquePng(64));

            // 1) Cyclic parentId -> must throw InvalidDataException (never StackOverflowException) and be reported cleanly by AuditCreatureWorkspace
            var cyclicRig = """
<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <GiantCreatureFramework.GiantRigDef>
    <defName>GCF_Rig_Cyclic</defName>
    <nodes>
      <li><nodeId>a</nodeId><parentId>b</parentId><texPath>Dummy/part</texPath><drawSize>(4,4)</drawSize></li>
      <li><nodeId>b</nodeId><parentId>a</parentId><texPath>Dummy/part</texPath><drawSize>(4,4)</drawSize></li>
    </nodes>
  </GiantCreatureFramework.GiantRigDef>
</Defs>
""";
            var rigFile = Path.Combine(defsDir, "GiantRigDefs_Bad.xml");
            File.WriteAllText(rigFile, cyclicRig);

            var cycEx = Assert.ThrowsException<InvalidDataException>(() => RigJointAuditor.AuditRigFiles(rigFile, texDir));
            StringAssert.Contains(cycEx.Message, "Cyclic parentId");

            var wsReports = RigJointAuditor.AuditCreatureWorkspace(cDir);
            Assert.AreEqual(1, wsReports.Count);
            Assert.IsFalse(wsReports[0].Passed);
            Assert.IsTrue(wsReports[0].Failures.Any(f => f.Contains("Cyclic parentId", StringComparison.Ordinal)));

            // 2) Duplicate whipId in softWhips -> must record audit failure without throwing ArgumentException
            var dupWhipRig = """
<?xml version="1.0" encoding="utf-8"?>
<Defs>
  <GiantCreatureFramework.GiantRigDef>
    <defName>GCF_Rig_BadWhip</defName>
    <nodes>
      <li><nodeId>root</nodeId><texPath>Dummy/part</texPath><drawSize>(4,4)</drawSize></li>
    </nodes>
    <softWhips>
      <li><whipId>whip_a</whipId><rootBoneId>root</rootBoneId><texPath>Dummy/part</texPath></li>
      <li><whipId>whip_a</whipId><rootBoneId>root</rootBoneId><texPath>Dummy/part</texPath></li>
    </softWhips>
  </GiantCreatureFramework.GiantRigDef>
</Defs>
""";
            File.WriteAllText(rigFile, dupWhipRig);
            var dupReports = RigJointAuditor.AuditRigFiles(rigFile, texDir);
            Assert.AreEqual(1, dupReports.Count);
            Assert.IsFalse(dupReports[0].Passed);
            Assert.IsTrue(dupReports[0].Failures.Any(f => f.Contains("Duplicate SoftWhip whipId", StringComparison.Ordinal)),
                "Duplicate whipId must be recorded in audit failures instead of throwing ArgumentException.");
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
        }
        return ~crc;
    }

    private static void WritePngChunk(MemoryStream ms, string type, byte[] data)
    {
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        ms.Write(len);
        var typeAndData = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typeAndData, 4, data.Length);
        ms.Write(typeAndData);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, ComputeCrc32(typeAndData));
        ms.Write(crc);
    }

    [TestMethod]
    public void QA_AGENT_07_PngCodecRejectsTruncatedIhdrAnd32BitScanlineOverflow()
    {
        // 1) Truncated 8-byte IHDR must throw InvalidDataException, not IndexOutOfRangeException.
        var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WritePngChunk(ms, "IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1 });
        WritePngChunk(ms, "IEND", Array.Empty<byte>());
        Assert.ThrowsException<InvalidDataException>(() => PngCodec.Decode(ms.ToArray()));

        // 2) Extreme 1 x 536870911 RGBA PNG where width*height <= int.MaxValue/4 but (stride+1)*height > int.MaxValue.
        var overflowMs = new MemoryStream();
        overflowMs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 536_870_911);
        ihdr[8] = 8; // 8-bit
        ihdr[9] = 6; // RGBA
        WritePngChunk(overflowMs, "IHDR", ihdr);
        WritePngChunk(overflowMs, "IEND", Array.Empty<byte>());
        Assert.ThrowsException<InvalidDataException>(() => PngCodec.Decode(overflowMs.ToArray()));
    }

    [TestMethod]
    public void QA_AGENT_08_SpriteSheetExtractorQuadraticSpecklePerfBenchmark()
    {
        const int w = 512, h = 512;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                bool isMain = x >= 100 && x < 260 && y >= 100 && y < 260;
                bool isSpeckle = !isMain && (x % 16 == 2) && (y % 16 == 2) && y < 320;
                if (isMain || isSpeckle)
                {
                    px[o] = 180; px[o + 1] = 40; px[o + 2] = 40; px[o + 3] = 255;
                }
                else
                {
                    px[o] = 0; px[o + 1] = 255; px[o + 2] = 0; px[o + 3] = 255;
                }
            }
        }
        var img = new RgbaImage(w, h, px);
        var sw = Stopwatch.StartNew();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var extracted = SpriteSheetExtractor.ExtractSheet(img, columns: 1, minIslandArea: 100);
        long allocBytes = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        sw.Stop();

        Console.WriteLine($"[PERF] QA-AGENT-08: SpriteSheetExtractor on 512x512 with ~550 1px speckles took {sw.Elapsed.TotalMilliseconds:F2} ms, alloc={allocBytes:N0} B, extracted={extracted.Count} parts.");
        Assert.IsTrue(extracted.Count >= 1);
    }

    [TestMethod]
    public async Task QA_AGENT_10_SynthesisAttemptsAreCappedWhenModelRefusesSubmitAnswer()
    {
        var transport = new RefusingSubmitTransport();
        var runner = new AgentTurnRunner(transport);
        var connection = new ModelConnection("http://localhost", "", "refusing-model", 0.2f, MaxRounds: 1);
        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hello" } };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await runner.RunAsync(
                connection,
                messages,
                new JsonArray(),
                (_, _) => Task.FromResult("{}"),
                () => { },
                _ => { },
                _ => { },
                _ => { },
                CancellationToken.None));

        // 1 exploration round + at most 4 synthesis attempts = 5 total API calls.
        Assert.AreEqual(5, transport.CallCount, "Synthesis loop must stop after 4 attempts instead of looping infinitely.");
    }

    [TestMethod]
    public void QA_AGENT_11_TexturePathSandboxRejectsTraversalAndRootedPaths()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "gcf_qa_sandbox_" + Guid.NewGuid().ToString("N"));
        try
        {
            var texDir = Path.Combine(tmpRoot, "Textures");
            Directory.CreateDirectory(texDir);
            var outsidePng = Path.Combine(tmpRoot, "secret.png");
            File.WriteAllBytes(outsidePng, BuildSolidOpaquePng(16));

            Assert.ThrowsException<ArgumentException>(() => CreatureWorkspace.ResolveSandboxedTexturePng(texDir, "../secret"));
            Assert.ThrowsException<ArgumentException>(() => CreatureWorkspace.ResolveSandboxedTexturePng(texDir, "../secret.png", allowBaseNameFallback: true));
            Assert.ThrowsException<ArgumentException>(() => CreatureWorkspace.ResolveSandboxedTexturePng(texDir, outsidePng));
            Assert.ThrowsException<ArgumentException>(() => CreatureWorkspace.ResolveSandboxedTexturePng(texDir, "/etc/passwd"));
            Assert.ThrowsException<ArgumentException>(() => CreatureWorkspace.ResolveSandboxedTexturePng(texDir, @"\\server\share\x.png"));

            var insidePng = Path.Combine(texDir, "Things", "Part.png");
            Directory.CreateDirectory(Path.GetDirectoryName(insidePng)!);
            File.WriteAllBytes(insidePng, BuildSolidOpaquePng(16));
            var resolved = CreatureWorkspace.ResolveSandboxedTexturePng(texDir, "Things/Part");
            Assert.AreEqual(Path.GetFullPath(insidePng), resolved);
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [TestMethod]
    public void QA_AGENT_12_OpenUrlInBrowserAllowsOnlyHttpAndHttpsSchemes()
    {
        Assert.IsTrue(Player2Support.IsSafeBrowserUrl("https://player2.game/login", out _));
        Assert.IsTrue(Player2Support.IsSafeBrowserUrl("http://127.0.0.1:4315/auth", out _));
        Assert.IsFalse(Player2Support.IsSafeBrowserUrl("file:///C:/Windows/System32/cmd.exe", out _));
        Assert.IsFalse(Player2Support.IsSafeBrowserUrl("cmd.exe", out _));
        Assert.IsFalse(Player2Support.IsSafeBrowserUrl(@"\\evil\share\payload.bat", out _));
        Assert.IsFalse(Player2Support.IsSafeBrowserUrl("javascript:alert(1)", out _));
        Assert.IsFalse(Player2Support.IsSafeBrowserUrl("", out _));
    }

    [TestMethod]
    public void QA_AGENT_13_ValstraxBlockedByPromptAndAntiExampleGuards()
    {
        foreach (var term in new[] { "Valstrax", "天彗龙" })
        {
            var poisoned = ValidStyle1Prompt.Replace("stepped collar plates.", $"stepped collar plates inspired by {term}.", StringComparison.Ordinal);
            Assert.ThrowsException<InvalidOperationException>(() => BuiltInPromptGuard.ValidateOrThrow(poisoned),
                $"BuiltInPromptGuard must reject forbidden term '{term}'.");
            Assert.IsTrue(BuiltInGiantAntiExampleGuard.IsBuiltInAntiExampleQuery(term, out _),
                $"BuiltInGiantAntiExampleGuard must block '{term}'.");
        }
    }

    private sealed class RefusingSubmitTransport : IChatTransport
    {
        public int CallCount { get; private set; }

        public Task<ModelReply> SendAsync(
            ModelConnection connection,
            JsonObject payload,
            Action<string> onText,
            Action<string> onReasoning,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ModelReply("Plain text without submit_answer", "stop", Array.Empty<ModelToolCall>()));
        }
    }
}
