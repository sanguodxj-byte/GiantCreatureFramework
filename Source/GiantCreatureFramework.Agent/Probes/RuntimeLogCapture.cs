using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GiantCreatureFramework.Agent.Probes;

public sealed record CapturedErrorEntry(
    string Kind,
    string Message,
    string StackTrace,
    int RepeatCount,
    DateTime TimestampUtc,
    IReadOnlyList<string> ExtractedMethods);

/// <summary>
/// 运行时错误与异常捕获器：同步聚合 Verse.Log.Messages 与 Unity 底层异常流，并从堆栈中提取涉事方法签名。
/// </summary>
[HarmonyPatch]
public static class RuntimeLogCapture
{
    private static readonly object SyncRoot = new();
    private static readonly List<CapturedErrorEntry> InternalBuffer = new();
    private static readonly Regex StackMethodRegex = new(
        @"at\s+(?<type>[A-Za-z0-9_`\.\+]+)\.(?<method>[A-Za-z0-9_<>`]+)\s*\(",
        RegexOptions.Compiled);

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            SyncFromVerseLogLocked(checkProbeBudget: false);
        }
    }

    public static void RecordInternal(string title, string detail)
    {
        lock (SyncRoot)
        {
            InternalBuffer.Add(new CapturedErrorEntry(
                "Internal",
                title + ": " + detail,
                detail,
                1,
                DateTime.UtcNow,
                ExtractMethodsFromStack(detail, checkProbeBudget: false)));
            TrimLocked();
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Notify_MessageReceivedThreadedInternal))]
    [HarmonyPostfix]
    public static void OnUnityLogReceived(string msg, string stackTrace, LogType type)
    {
        if (!GiantCreatureAgentMod.IsTerminalEnabled)
        {
            return;
        }
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
        {
            return;
        }

        lock (SyncRoot)
        {
            var normalizedMsg = (msg ?? string.Empty).Trim();
            var normalizedStack = (stackTrace ?? string.Empty).Trim();
            for (var i = InternalBuffer.Count - 1; i >= 0; i--)
            {
                var existing = InternalBuffer[i];
                if (string.Equals(existing.Message, normalizedMsg, StringComparison.Ordinal))
                {
                    InternalBuffer[i] = existing with
                    {
                        RepeatCount = existing.RepeatCount + 1,
                        TimestampUtc = DateTime.UtcNow
                    };
                    return;
                }
            }

            InternalBuffer.Add(new CapturedErrorEntry(
                type.ToString(),
                normalizedMsg,
                normalizedStack,
                1,
                DateTime.UtcNow,
                ExtractMethodsFromStack(normalizedMsg + "\n" + normalizedStack, checkProbeBudget: false)));
            TrimLocked();
        }
    }

    public static int GetErrorCount()
    {
        lock (SyncRoot)
        {
            SyncFromVerseLogLocked(checkProbeBudget: true);
            return InternalBuffer.Count;
        }
    }

    public static IReadOnlyList<CapturedErrorEntry> GetRecentErrors(int maxCount = 15, string? keyword = null)
    {
        lock (SyncRoot)
        {
            SyncFromVerseLogLocked(checkProbeBudget: true);
            IEnumerable<CapturedErrorEntry> query = InternalBuffer;
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var kw = keyword.Trim();
                query = query.Where(e =>
                    e.Message.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.StackTrace.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    e.ExtractedMethods.Any(m => m.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return query
                .OrderByDescending(e => e.TimestampUtc)
                .Take(Math.Clamp(maxCount, 1, 50))
                .ToList();
        }
    }

    public static string GetRecentErrorsJson(int maxCount = 12, string? keyword = null)
    {
        var entries = GetRecentErrors(maxCount, keyword);
        var payload = new
        {
            totalCapturedErrors = GetErrorCount(),
            returnedCount = entries.Count,
            filterKeyword = keyword ?? string.Empty,
            errors = entries.Select(e => new
            {
                kind = e.Kind,
                repeatCount = e.RepeatCount,
                timestampUtc = e.TimestampUtc.ToString("HH:mm:ss"),
                message = Truncate(e.Message, 800),
                stackTrace = Truncate(e.StackTrace, 1400),
                extractedMethods = e.ExtractedMethods
            }).ToArray()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void SyncFromVerseLogLocked(bool checkProbeBudget = false)
    {
        try
        {
            if (Log.Messages == null)
            {
                return;
            }

            foreach (var msg in Log.Messages)
            {
                if (checkProbeBudget)
                {
                    ProbeExecution.Check();
                }
                if (msg == null || msg.type != LogMessageType.Error)
                {
                    continue;
                }

                var text = (msg.text ?? string.Empty).Trim();
                var stack = (msg.StackTrace ?? string.Empty).Trim();
                var found = false;
                for (var i = 0; i < InternalBuffer.Count; i++)
                {
                    if (string.Equals(InternalBuffer[i].Message, text, StringComparison.Ordinal))
                    {
                        if (msg.repeats > InternalBuffer[i].RepeatCount)
                        {
                            InternalBuffer[i] = InternalBuffer[i] with { RepeatCount = msg.repeats };
                        }
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    InternalBuffer.Add(new CapturedErrorEntry(
                        "Error",
                        text,
                        stack,
                        Math.Max(1, msg.repeats),
                        DateTime.UtcNow,
                        ExtractMethodsFromStack(text + "\n" + stack, checkProbeBudget)));
                }
            }

            TrimLocked();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not TimeoutException)
        {
            // 忽略多线程枚举冲突
        }
    }

    private static void TrimLocked()
    {
        const int maxCapacity = 120;
        if (InternalBuffer.Count > maxCapacity)
        {
            InternalBuffer.RemoveRange(0, InternalBuffer.Count - maxCapacity);
        }
    }

    private static IReadOnlyList<string> ExtractMethodsFromStack(string combinedText, bool checkProbeBudget = false)
    {
        if (string.IsNullOrWhiteSpace(combinedText))
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        var matches = StackMethodRegex.Matches(combinedText);
        foreach (Match match in matches)
        {
            if (checkProbeBudget)
            {
                ProbeExecution.Check();
            }
            if (!match.Success)
            {
                continue;
            }

            var typeName = match.Groups["type"].Value;
            var methodName = match.Groups["method"].Value;
            var full = $"{typeName}.{methodName}";
            if (!list.Contains(full, StringComparer.Ordinal))
            {
                list.Add(full);
            }

            if (list.Count >= 10)
            {
                break;
            }
        }

        return list;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
        {
            return value;
        }

        return value.Substring(0, maxChars) + "\n" + AgentProtocol.Get("Probe_TruncatedTail");
    }
}
