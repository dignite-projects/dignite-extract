using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Chat.Telemetry;

/// <summary>
/// Issue #149: was <c>IChatToolFactory</c> in Abstractions, exposed to business modules
/// so contributors could build audited <see cref="AIFunction"/>s. Business modules now use
/// MAF Agent Skills directly, so the factory collapses to a single concrete core-internal
/// type that only the core <c>search_paperbase_documents</c> wrapper consumes.
/// </summary>
public class ChatToolFactory : ITransientDependency
{
    // Short-prefix hash is plenty for dedup/correlation in audit/metrics; longer
    // prefixes give attackers more rainbow-table grip on free-form natural-language
    // arguments such as the LLM-supplied `query` parameter to search_paperbase_documents.
    private const int HashHexPrefixLength = 12;
    private const int MaxCollectionItems = 5;

    private static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "secret",
        "token",
        "apikey",
        "api_key",
        "authorization"
    ];

    private readonly ChatTelemetryRecorder _recorder;

    public ChatToolFactory(ChatTelemetryRecorder recorder)
    {
        _recorder = recorder;
    }

    // Arch review A3: these methods are `internal virtual` because ChatToolContext is
    // internal. Visibility is correct — only core's DocumentTextSearchAdapter and
    // AuditingSkillsContextProvider call them; business modules speak MAF skills, not
    // this factory. `virtual` is kept so test subclasses (InternalsVisibleTo grants the
    // test assembly access) can override for hermetic unit tests.
    internal virtual AIFunction Create(
        ChatToolContext ctx,
        Delegate method,
        string name,
        string description)
        => Create(ctx, method, name, description, progressDescriber: null);

    internal virtual AIFunction Create(
        ChatToolContext ctx,
        Delegate method,
        string name,
        string description,
        Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber)
    {
        var inner = AIFunctionFactory.Create(method, name, description);
        return new AuditedChatFunction(inner, ctx, _recorder, progressDescriber);
    }

    /// <summary>
    /// Issue #149: wraps an existing <see cref="AIFunction"/> (e.g. one of the three
    /// meta-tools emitted by <see cref="Microsoft.Agents.AI.AgentSkillsProvider"/>:
    /// <c>load_skill</c> / <c>read_skill_resource</c> / <c>run_skill_script</c>) with
    /// <see cref="AuditedChatFunction"/> so skill script invocations enter the same
    /// audit + grounding pipeline as <c>search_paperbase_documents</c>.
    ///
    /// <para>
    /// Idempotent: if <paramref name="inner"/> is already an <see cref="AuditedChatFunction"/>
    /// the same instance is returned unchanged. That matters because MAF's base
    /// <c>AIContextProvider.InvokingCoreAsync</c> merges the upstream context's tools into
    /// the provider's returned <c>AIContext</c>, so the
    /// <see cref="AuditingSkillsContextProvider"/> decorator can see (and would otherwise
    /// double-wrap) the already-audited <c>search_paperbase_documents</c> tool.
    /// </para>
    /// </summary>
    internal virtual AIFunction WrapAudited(AIFunction inner, ChatToolContext ctx)
        => inner is AuditedChatFunction ? inner : new AuditedChatFunction(inner, ctx, _recorder);

    private static IReadOnlyDictionary<string, object?> SummarizeArguments(AIFunctionArguments? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments == null)
        {
            return result;
        }

        foreach (var (key, value) in arguments)
        {
            result[key] = IsSensitiveKey(key)
                ? "***"
                : SummarizeValue(value);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, object?> SummarizeResult(object? result)
    {
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = result?.GetType().Name ?? "null"
        };

        switch (result)
        {
            case null:
                summary["sizeBytes"] = 0;
                break;
            case JsonElement json:
                SummarizeJsonElement(json, summary);
                break;
            case string text:
                SummarizeString(text, summary);
                break;
            default:
                var textValue = Convert.ToString(result);
                if (!string.IsNullOrEmpty(textValue))
                {
                    SummarizeString(textValue, summary);
                }
                break;
        }

        return summary;
    }

    private static long? GetResultSizeBytes(IReadOnlyDictionary<string, object?> resultSummary)
        => resultSummary.TryGetValue("sizeBytes", out var value) && value is long size
            ? size
            : null;

    private static void SummarizeJsonElement(JsonElement json, Dictionary<string, object?> summary)
    {
        summary["kind"] = "json";
        summary["sizeBytes"] = Encoding.UTF8.GetByteCount(json.GetRawText());

        if (json.ValueKind == JsonValueKind.String)
        {
            var value = json.GetString() ?? string.Empty;
            summary["stringLength"] = value.Length;
            summary["documentTagCount"] = CountDocumentTags(value);
            return;
        }

        if (json.ValueKind == JsonValueKind.Array)
        {
            summary["itemCount"] = json.GetArrayLength();
            return;
        }

        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "documentIds", "contracts", "buckets" })
            {
                if (json.TryGetProperty(propertyName, out var property)
                    && property.ValueKind == JsonValueKind.Array)
                {
                    summary[$"{propertyName}Count"] = property.GetArrayLength();
                }
            }

            if (json.TryGetProperty("found", out var found)
                && found.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                summary["found"] = found.GetBoolean();
            }
        }
    }

    private static void SummarizeString(string text, Dictionary<string, object?> summary)
    {
        summary["kind"] = "string";
        summary["sizeBytes"] = Encoding.UTF8.GetByteCount(text);
        summary["stringLength"] = text.Length;
        summary["documentTagCount"] = CountDocumentTags(text);

        try
        {
            using var doc = JsonDocument.Parse(text);
            SummarizeJsonElement(doc.RootElement, summary);
        }
        catch (JsonException)
        {
            // Plain text tool results are expected for the RAG context block.
        }
    }

    private static int CountDocumentTags(string text)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf("<document ", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "<document ".Length;
        }

        return count;
    }

    private static object? SummarizeValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonElement json:
                return SummarizeJsonValue(json);
            case string text:
                return HashStringForAudit(text);
            case Guid or DateTime or DateOnly or TimeOnly or bool:
                return value;
            case int or long or short or byte or decimal or double or float:
                return value;
            case IEnumerable enumerable when value is not string:
                return SummarizeEnumerable(enumerable);
            default:
                // Unknown type → ToString may surface PII; only structural metadata is recorded.
                return HashStringForAudit(Convert.ToString(value) ?? string.Empty);
        }
    }

    private static object? SummarizeJsonValue(JsonElement json)
    {
        return json.ValueKind switch
        {
            JsonValueKind.String => HashStringForAudit(json.GetString() ?? string.Empty),
            JsonValueKind.Number => json.TryGetInt64(out var l) ? l : json.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => new
            {
                count = json.GetArrayLength(),
                sample = json.EnumerateArray().Take(MaxCollectionItems).Select(SummarizeJsonValue).ToList()
            },
            JsonValueKind.Object => new
            {
                properties = json.EnumerateObject().Select(p => p.Name).Take(MaxCollectionItems).ToList()
            },
            _ => json.ValueKind.ToString()
        };
    }

    private static object SummarizeEnumerable(IEnumerable enumerable)
    {
        var sample = new List<object?>();
        var count = 0;

        foreach (var item in enumerable)
        {
            if (count < MaxCollectionItems)
            {
                sample.Add(SummarizeValue(item));
            }

            count++;
        }

        return new { count, sample };
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>
    /// Reduces a free-form string argument or result fragment to structural metadata
    /// only — never the raw text. The LLM-supplied <c>query</c> argument to
    /// <c>search_paperbase_documents</c> and similar contributor-tool inputs (party
    /// names, contract numbers, free-form questions) frequently contain PII that
    /// would otherwise be persisted indefinitely in <c>AbpAuditLogs.Comments</c>.
    /// </summary>
    /// <remarks>
    /// Hash prefix is short (12 hex chars = 48 bits) — enough for dedup/correlation
    /// across audit/metrics, not enough to surface plaintext.
    /// </remarks>
    private static object HashStringForAudit(string value)
    {
        return new
        {
            kind = "string",
            length = value.Length,
            hash = ComputeHashPrefix(value)
        };
    }

    private static string ComputeHashPrefix(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, HashHexPrefixLength / 2).ToLowerInvariant();
    }

    /// <summary>
    /// MAF <see cref="Microsoft.Agents.AI.AgentSkillsProvider"/> emits three meta-tools
    /// with these names (see <c>AgentSkillsProvider.BuildTools</c> in
    /// <c>Microsoft.Agents.AI</c> 1.5). Extracting them as named constants here keeps the
    /// dependency explicit: a future MAF rename surfaces as a single find/replace site
    /// instead of three scattered string literals. The end-to-end coverage in
    /// <c>ChatSkillInvocation_Tests</c> + <c>ChatTelemetryRecorder_Tests</c> still
    /// catches the rename loudly (FunctionInvokingChatClient can't dispatch a tool
    /// MAF doesn't expose), but those failures are diagnostic — these constants make
    /// the coupling browsable.
    /// </summary>
    internal const string MafLoadSkillToolName = "load_skill";
    internal const string MafRunSkillScriptToolName = "run_skill_script";
    internal const string MafReadSkillResourceToolName = "read_skill_resource";

    /// <summary>
    /// Internal so <c>ChatAppService</c> (same assembly) can downcast on the
    /// streaming path to fetch <see cref="ProgressDescriber"/> for
    /// <c>ToolCallStarted</c> events. External consumers should treat the returned
    /// <see cref="AIFunction"/> as opaque.
    /// </summary>
    internal sealed class AuditedChatFunction : AIFunction
    {
        private readonly AIFunction _inner;
        private readonly ChatToolContext _ctx;
        private readonly ChatTelemetryRecorder _recorder;

        /// <summary>
        /// Issue #116: optional sanitized-progress describer supplied at registration
        /// time. <c>null</c> when the tool didn't opt in; the streaming AppService
        /// falls back to a generic "正在执行 {ToolName}" label.
        /// </summary>
        public Func<IReadOnlyDictionary<string, object?>, string?>? ProgressDescriber { get; }

        public AuditedChatFunction(
            AIFunction inner,
            ChatToolContext ctx,
            ChatTelemetryRecorder recorder,
            Func<IReadOnlyDictionary<string, object?>, string?>? progressDescriber = null)
        {
            _inner = inner;
            _ctx = ctx;
            _recorder = recorder;
            ProgressDescriber = progressDescriber;
        }

        public override string Name => _inner.Name;
        public override string Description => _inner.Description;
        public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;
        public override JsonElement JsonSchema => _inner.JsonSchema;
        public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
        public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
        public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            // Issue #149: when this wrapper sits on top of MAF's `run_skill_script` meta-tool
            // the inner Name is always "run_skill_script", losing per-skill granularity in
            // audit + grounding classification. Derive the effective ToolName from the
            // run-time arguments — `skill:<skill-name>/<script-name>` — so each skill call
            // is auditable on its own and ClassifyGrounding can distinguish meta calls
            // (load_skill / read_skill_resource) from real data fetches.
            var auditToolName = DeriveSkillAwareToolName(Name, arguments);
            try
            {
                var result = await _inner.InvokeAsync(arguments, cancellationToken);
                sw.Stop();

                var resultSummary = SummarizeResult(result);
                _recorder.RecordToolCall(new ChatToolAuditEntry
                {
                    ConversationId = _ctx.ConversationId,
                    UserId = _ctx.UserId,
                    TenantId = _ctx.TenantId,
                    DocumentId = _ctx.DocumentId,
                    DocumentTypeCode = _ctx.DocumentTypeCode,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    ToolName = auditToolName,
                    ArgumentsSummary = SummarizeArguments(arguments),
                    ResultSummary = resultSummary,
                    ResultSizeBytes = GetResultSizeBytes(resultSummary),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Outcome = ChatTelemetryOutcome.Success
                });

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _recorder.RecordToolCall(new ChatToolAuditEntry
                {
                    ConversationId = _ctx.ConversationId,
                    UserId = _ctx.UserId,
                    TenantId = _ctx.TenantId,
                    DocumentId = _ctx.DocumentId,
                    DocumentTypeCode = _ctx.DocumentTypeCode,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    ToolName = auditToolName,
                    ArgumentsSummary = SummarizeArguments(arguments),
                    ElapsedMs = sw.Elapsed.TotalMilliseconds,
                    Outcome = ChatTelemetryOutcome.Failure,
                    ExceptionType = ex.GetType().FullName
                });
                throw;
            }
        }

        /// <summary>
        /// Derives the audit ToolName from a MAF skills meta-tool call so each underlying
        /// skill script appears as its own audit entry. Without this, every contract /
        /// navigation skill call would collapse to "run_skill_script" — losing per-skill
        /// granularity for grounding classification and for spotting which skills are
        /// being invoked under what tenant/user.
        /// </summary>
        /// <remarks>
        /// Returns one of:
        /// <list type="bullet">
        ///   <item><c>skill:&lt;skill-name&gt;/&lt;script-name&gt;</c> when the wrapped function is
        ///         <c>run_skill_script</c> and both arg names parse cleanly</item>
        ///   <item>the raw inner Name otherwise (covers <c>search_paperbase_documents</c>,
        ///         <c>load_skill</c>, <c>read_skill_resource</c>, and any future direct tool)</item>
        /// </list>
        /// </remarks>
        internal static string DeriveSkillAwareToolName(string innerName, AIFunctionArguments? arguments)
        {
            if (innerName != MafRunSkillScriptToolName || arguments is null)
            {
                return innerName;
            }

            var skill = TryGetStringArg(arguments, "skillName");
            var script = TryGetStringArg(arguments, "scriptName");
            if (string.IsNullOrWhiteSpace(skill) || string.IsNullOrWhiteSpace(script))
            {
                return innerName;
            }

            return $"skill:{skill}/{script}";
        }

        private static string? TryGetStringArg(AIFunctionArguments arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var value) || value is null)
            {
                return null;
            }
            if (value is string s)
            {
                return s;
            }
            if (value is JsonElement json && json.ValueKind == JsonValueKind.String)
            {
                return json.GetString();
            }
            return value.ToString();
        }
    }
}
