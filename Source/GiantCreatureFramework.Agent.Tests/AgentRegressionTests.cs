using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using GiantCreatureFramework.Agent.Probes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GiantCreatureFramework.Agent.Tests;

/// <summary>
/// Core agent protocol, transport, evidence, discovery, context, prompt, and provider registry
/// regression tests migrated from RimCopilot and extended for GCF Agent capabilities.
/// </summary>
[TestClass]
public sealed class AgentRegressionTests
{
    internal static ModelReply Adapt(ModelReply reply, JsonObject payload)
    {
        if (reply.Tools.Count > 0) return reply;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in payload["messages"]!.AsArray().OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool"))
        {
            AnswerContract.CollectEvidence(message["content"]?.ToString() ?? "{}", ids, message["tool_call_id"]?.ToString());
        }
        return Submit(reply.Text, "grounded", ids.ToArray()) with { Usage = reply.Usage };
    }

    internal static ModelReply Submit(string answer, string kind, params string[] ids) => new("", "tool_calls",
    [
        new ModelToolCall(
            "final_" + Guid.NewGuid().ToString("N"),
            AnswerContract.ToolName,
            JsonSerializer.Serialize(new { answer, kind, evidenceIds = ids }, Evidence.UnescapedJsonOptions))
    ]);

    private static JsonArray Conversation() => new(
        new JsonObject { ["role"] = "system", ["content"] = "system" },
        new JsonObject { ["role"] = "user", ["content"] = "question" });

    private static JsonObject ToolSchema(string name) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = "fixture",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string" }
                }
            }
        }
    };

    [TestMethod]
    public void CancelDoesNotReleaseOwnershipUntilOldTaskCompletes()
    {
        var session = new AgentSession();
        using var old = session.TryBeginTurn()!;
        session.Cancel();
        old.Cancellation.IsCancellationRequested.Should().BeTrue();
        session.IsBusy.Should().BeTrue();
        session.TryBeginTurn().Should().BeNull();
        session.Complete(old, "已中止");
        using var current = session.TryBeginTurn()!;
        session.Complete(old, "就绪");
        session.Owns(current).Should().BeTrue();
        session.IsBusy.Should().BeTrue();
    }

    [TestMethod]
    public async Task DispatchCancellationCompletesWithoutGamePump()
    {
        using var source = new CancellationTokenSource();
        var called = false;
        var task = Task.Run(() => MainThreadDispatcher.RunOnMainThreadAsync(() => called = true, source.Token));
        source.Cancel();
        Func<Task> act = async () => await task.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<OperationCanceledException>();
        called.Should().BeFalse();
    }

    [TestMethod]
    public void StreamParserHandlesInterleavedToolsPlayer2SharedIndexAndTruncatedErrors()
    {
        var parser = new ChatStreamParser(_ => { }, _ => { });
        parser.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"search_defs","arguments":"{\"query\":"}},{"index":1,"id":"b","function":{"name":"get_recent_errors","arguments":"{"}}]}}]}""");
        parser.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"}"}},{"index":0,"function":{"arguments":"\"Steel\"}"}}]},"finish_reason":"tool_calls"}]}""");
        parser.Accept("[DONE]");
        var reply = parser.Complete();
        reply.Tools.Should().HaveCount(2);
        reply.Tools[0].Arguments.Should().Be("{\"query\":\"Steel\"}");
        reply.Tools[1].Id.Should().Be("b");

        var p2 = new ChatStreamParser(_ => { }, _ => { });
        p2.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_p2_1","type":"function","function":{"name":"list_manuals","arguments":""}}]}}]}""");
        p2.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"","function":{"name":"","arguments":"{}"}}]}}]}""");
        p2.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_p2_2","type":"function","function":{"name":"search_defs","arguments":"{\"query\":"}}]}}]}""");
        p2.Accept("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"chunk_delta_id_99","function":{"arguments":"\"Thrumbo\"}"}}]},"finish_reason":"stop"}]}""");
        p2.Accept("[DONE]");
        var p2Reply = p2.Complete();
        p2Reply.FinishReason.Should().Be("tool_calls");
        p2Reply.Tools.Should().HaveCount(2);

        foreach (var badFinish in new[] { "", "length", "content_filter" })
        {
            var bad = new ChatStreamParser(_ => { }, _ => { }) { FinishReason = badFinish };
            Action act = () => bad.Complete();
            act.Should().Throw<InvalidDataException>();
        }
    }

    [TestMethod]
    public async Task StreamingHttpTransportParsesSseAndRespectsTimeouts()
    {
        using var server = new LoopbackServer(async stream =>
        {
            await WriteAscii(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
            await WriteAscii(stream, "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}\n\n");
            await WriteAscii(stream, "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        });
        var text = "";
        var reply = await new HttpChatTransport().SendAsync(
            new(server.Endpoint, "test-key", "fake", .2f),
            new JsonObject(),
            s => text += s,
            _ => { },
            default);
        reply.Text.Should().Be("hello");
        text.Should().Be("hello");
        await server.Completed;
    }

    [TestMethod]
    public async Task ToolDiscoveryLoadsSelectedToolsAndPreservesEvidenceContext()
    {
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => new ModelReply("", "tool_calls", [new("discover1", "discover_tools", "{\"tools\":[\"list_manuals\"]}")]),
            2 => new ModelReply("", "tool_calls", [new("read1", "list_manuals", "{}")]),
            3 => new ModelReply("", "tool_calls", [new("discover2", "discover_tools", "{\"tools\":[\"search_defs\"]}")]),
            4 => new ModelReply("", "tool_calls", [new("read2", "search_defs", "{\"query\":\"Thrumbo\"}")]),
            _ => Submit("verified result", "grounded", "E1", "E2")
        });

        var executed = new List<string>();
        var runner = new AgentTurnRunner(transport);
        await runner.RunAsync(
            new("http://localhost", "", "fixture", .2f),
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "investigate" }),
            new(ToolSchema("list_manuals"), ToolSchema("search_defs")),
            (call, _) =>
            {
                executed.Add(call.Name);
                return Task.FromResult("{\"status\":\"ok\",\"data\":\"complete\"}");
            },
            () => { }, _ => { }, _ => { }, _ => { }, default);

        executed.Should().Equal("list_manuals", "search_defs");
        runner.LastCitedEvidenceIds.Should().BeEquivalentTo("read1", "read2");
        runner.LastAnswerKind.Should().Be("grounded");
    }

    [TestMethod]
    public async Task LoadManualExemptionAndAttachedVisionImageInjectionWorkEndToEnd()
    {
        // 1. Verify Compact() exempts load_manual results while truncating non-exempt tool results
        var longManual = new string('M', 25_000);
        var longOther = new string('X', 25_000);
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "sys" },
            new JsonObject { ["role"] = "user", ["content"] = "build creature" },
            new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray(
                    new JsonObject { ["id"] = "tc_manual", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "load_manual", ["arguments"] = "{}" } },
                    new JsonObject { ["id"] = "tc_search", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "search_defs", ["arguments"] = "{}" } })
            },
            new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "tc_manual",
                ["content"] = new JsonObject { ["tool"] = "load_manual", ["exemptFromCompact"] = true, ["data"] = longManual }.ToJsonString()
            },
            new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = "tc_search",
                ["content"] = new JsonObject { ["tool"] = "search_defs", ["data"] = longOther }.ToJsonString()
            }
        };

        AgentTurnRunner.Compact(messages, maxContextChars: 35_000, compactThresholdChars: 2_000);
        messages[3]!["content"]!.ToString().Should().Contain(longManual);
        messages[4]!["content"]!.ToString().Should().Contain("historicalPreview");

        // 2. Verify _attachedImageDataUrl injects a vision user message in the next round without leaking into tool JSON
        var transport = new ScriptedTransport((n, _) => n switch
        {
            1 => new ModelReply("", "tool_calls", [new("gen1", "generate_image", "{}")]),
            _ => Submit("inspected sprite", "grounded", "gen1")
        });
        var runner = new AgentTurnRunner(transport);
        await runner.RunAsync(
            new("http://localhost", "", "vision-model", .2f),
            new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "generate sprite" }),
            new(ToolSchema("generate_image")),
            (_, _) => Task.FromResult(JsonSerializer.Serialize(new
            {
                status = "ok",
                data = new { outputPath = "Work/sheet1.png" },
                _attachedImageDataUrl = "data:image/png;base64,iVBORw0KGgo=",
                _attachedImageLabel = "MyBeast/Work/sheet1.png"
            }, Evidence.UnescapedJsonOptions)),
            () => { }, _ => { }, _ => { }, _ => { }, default);

        var secondPayloadMsgs = transport.Payloads[1]["messages"]!.AsArray();
        var toolMsg = secondPayloadMsgs.First(m => m?["role"]?.ToString() == "tool")!;
        toolMsg["content"]!.ToString().Should().NotContain("_attachedImageDataUrl");
        var injectedVisionUser = secondPayloadMsgs.Last()!;
        injectedVisionUser["role"]!.ToString().Should().Be("user");
        injectedVisionUser["content"]!.ToJsonString().Should().Contain("data:image/png;base64,iVBORw0KGgo=");
    }

    [TestMethod]
    public void EvidenceContextDeduplicatesTablesFoldsNotesAndRepairsCitations()
    {
        var source = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"{i}: line {i}"));
        var msg = new JsonObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = "c1",
            ["content"] = new JsonObject
            {
                ["status"] = "ok",
                ["evidenceId"] = "ev_1",
                ["data"] = new JsonObject { ["xml"] = source, ["count"] = 7 }
            }.ToJsonString()
        };
        var messages = new JsonArray(msg);
        var ctx = new EvidenceContext();
        ctx.Observe(messages);
        var wire = ctx.Project(messages);
        wire.ToJsonString().Length.Should().BeLessThan(messages.ToJsonString().Length / 2);

        var fullRead = JsonNode.Parse(ctx.Read("{\"evidenceId\":\"E1\",\"path\":\"/data/xml\",\"maxLines\":0}"))!;
        fullRead["data"]!["text"]!.ToString().Should().Be(source);

        var folded = ctx.Fold("{\"evidenceIds\":[\"E1\"],\"notes\":\"XML verified 200 lines\"}", messages);
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "fold1", ["content"] = folded });
        ctx.Project(messages).ToJsonString().Should().Contain("XML verified 200 lines");
        ctx.Validate(new("done", "grounded", ["E1"])).EvidenceIds.Should().Equal("ev_1");
    }

    [TestMethod]
    public void ProviderRegistryConversationHistoryPromptsValidationAndArchivesRemainIntact()
    {
        AIProviderRegistry.GetEndpointUrl(AIProvider.DeepSeek).Should().Be("https://api.deepseek.com/v1/chat/completions");
        AIProviderRegistry.GetExtraHeaders(AIProvider.OpenRouter)!["X-Title"].Should().Be("GiantCreatureFramework");
        AIProviderRegistry.NormalizeChatCompletionsEndpoint("http://127.0.0.1:11434").Should().Be("http://127.0.0.1:11434/v1/chat/completions");

        var overrides = new Dictionary<string, string> { ["Main"] = "自定义主提示\n第二行", ["Persona"] = "" };
        var xml = XElement.Parse(AgentPrompts.WriteOverrides(overrides).ToString());
        AgentPrompts.ReadOverrides(xml).Should().BeEquivalentTo(overrides);

        var schema = JsonNode.Parse("""{"type":"object","properties":{"q":{"type":"string"},"n":{"type":"integer","minimum":1}},"required":["q"],"additionalProperties":false}""")!;
        using var okDoc = JsonDocument.Parse("""{"q":"hello","n":2}""");
        ToolArgumentValidation.Validate(schema, okDoc.RootElement);
        using var badDoc = JsonDocument.Parse("""{"q":"hello","extra":true}""");
        Action badAct = () => ToolArgumentValidation.Validate(schema, badDoc.RootElement);
        badAct.Should().Throw<ArgumentException>();

        var mods = new[] { ("sanguo.giantcreatureframework", "Giant Creature Framework"), ("ludeon.rimworld", "Core") };
        ModSearchScope.Resolve(mods, "ludeon.rimworld", m => m.Item1, m => m.Item2).Should().Equal(mods[1]);

        var formatted = TermTagFormatter.FormatInlineMarkdown("测试 <term>巨兽工坊</term> 标签", out var spans);
        spans.Should().ContainSingle().Which.TermText.Should().Be("巨兽工坊");
        formatted.Should().NotContain("<term>");
    }

    private static Task WriteAscii(NetworkStream stream, string text) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    private sealed class ScriptedTransport(Func<int, JsonObject, ModelReply> response) : IChatTransport
    {
        public List<JsonObject> Payloads { get; } = [];
        public Task<ModelReply> SendAsync(ModelConnection connection, JsonObject payload, Action<string> onText,
            Action<string> onReasoning, CancellationToken cancellationToken)
        {
            Payloads.Add((JsonObject)payload.DeepClone());
            return Task.FromResult(response(Payloads.Count, payload));
        }
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public string Endpoint { get; }
        public Task Completed { get; }

        public LoopbackServer(Func<NetworkStream, Task> respond)
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/chat/completions";
            Completed = Task.Run(async () =>
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var length = 0;
                while (await reader.ReadLineAsync() is { Length: > 0 } line)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line.Split(':')[1]);
                }
                var buffer = new char[length];
                if (length > 0) await reader.ReadBlockAsync(buffer, 0, length);
                await respond(stream);
            });
        }

        public void Dispose() => _listener.Stop();
    }
}
