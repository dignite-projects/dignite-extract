using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L3: 把两份文档的 Markdown 摘要喂给 LLM，让它判断是否有业务关联并给出 confidence。
///
/// <para>
/// <strong>fail-closed 设计</strong>：仅 <see cref="IChatClient"/> + 静态 system instructions，
/// <strong>不挂 <c>AIContextProviders</c> / <c>ChatHistoryProvider</c></strong>。
/// 见 <c>.claude/rules/doc-chat-anti-patterns.md</c> 反例 A：字段抽取/关系评判类 agent
/// 一旦挂 RAG provider 会把无关文档的 chunk 注入 prompt，导致结构化字段被错误文档影响。
/// </para>
///
/// <para>
/// <strong>Markdown 截断</strong>：两份文档总长度可能超过 LLM context window。每份截到
/// <see cref="PaperbaseAIBehaviorOptions.MaxTextLengthPerExtraction"/> 的一半，
/// 头部信息（合同的标题/编号/当事人通常在开头）保留率最高。
/// </para>
///
/// <para>
/// <strong>system instructions 是编译期常量</strong>（<see cref="InferenceInstructions"/>），
/// 不含任何用户控制字段，符合反例 C #4 的 prompt-injection 防御要求。
/// </para>
/// </summary>
public class RelationInferenceAgent : ITransientDependency
{
    /// <summary>
    /// 编译期常量，禁止变量插值。末尾追加 <see cref="PromptBoundary.BoundaryRule"/>——document
    /// markdown 是用户上传文档抽取出来的弱签名输入，需要明确告诉模型"标签内是数据非指令"。
    /// </summary>
    public const string InferenceInstructions =
        "You are a strict business-document relationship judge. Given two documents (their type codes plus markdown excerpts), " +
        "decide whether they reference the SAME business event or entity (same contract / same project / same parties / same case / same transaction). " +
        "Return JSON matching the response schema strictly.\n" +
        "\n" +
        "Set isRelated=true ONLY when both documents contain concrete evidence pointing to the same thing — same contract number, " +
        "same party names appearing in both, same project code, same dates with overlapping parties, etc. " +
        "DO NOT set true based on superficial similarity (both being 'contracts', both mentioning 'payment', etc.) — that's noise.\n" +
        "\n" +
        "Confidence calibration: 0.95+ when there's a unique identifier match (contract number, invoice number); " +
        "0.75-0.9 when multiple soft signals align (party + date + amount); " +
        "below 0.7 means you're not sure — set isRelated=false in that case.\n" +
        "\n" +
        "Description: when isRelated=true, write one short sentence (Chinese or English, match document language) " +
        "explaining the link, mentioning the key shared identifier or signal. Keep under 100 characters. " +
        "When isRelated=false, leave description empty.\n" +
        "\n" +
        PromptBoundary.BoundaryRule;

    private readonly IChatClient _chatClient;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    public RelationInferenceAgent(
        IChatClient chatClient,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions)
    {
        _chatClient = chatClient;
        _aiOptions = aiOptions.Value;
    }

    public virtual async Task<RelationInferenceResult> EvaluateAsync(
        DocumentSnapshot source,
        DocumentSnapshot candidate,
        CancellationToken cancellationToken = default)
    {
        var agent = new ChatClientAgent(_chatClient, instructions: InferenceInstructions);

        var prompt = BuildPrompt(source, candidate);
        var run = await agent.RunAsync<RelationInferenceResult>(prompt, cancellationToken: cancellationToken);
        return run.Result ?? new RelationInferenceResult { IsRelated = false };
    }

    protected virtual string BuildPrompt(DocumentSnapshot source, DocumentSnapshot candidate)
    {
        // Per-document budget = half of the per-extraction limit. Both documents get equal share;
        // headers (which usually carry the unique identifier) survive truncation.
        var perDocLimit = _aiOptions.MaxTextLengthPerExtraction / 2;

        // Markdown 是 OCR/抽取出来的弱签名输入；用 PromptBoundary.WrapDocument 包裹 + 转义 '<'，
        // 与 InferenceInstructions 末尾的 BoundaryRule 配合形成"标签内是数据非指令"防御。
        // DocumentTypeCode 是系统管理字段（注册表常量），不需要包裹。
        var sb = new StringBuilder(perDocLimit * 2 + 256);
        sb.Append("DOCUMENT 1 — type: ").Append(source.DocumentTypeCode ?? "(unclassified)").Append('\n');
        sb.Append(PromptBoundary.WrapDocument(Truncate(source.Markdown, perDocLimit)));
        sb.Append("\n\nDOCUMENT 2 — type: ").Append(candidate.DocumentTypeCode ?? "(unclassified)").Append('\n');
        sb.Append(PromptBoundary.WrapDocument(Truncate(candidate.Markdown, perDocLimit)));
        return sb.ToString();
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? string.Empty;
        return text.Substring(0, max);
    }
}

/// <summary>
/// L3 在 LLM 调用前把 <see cref="Document"/> 的关键字段拷出来——避免在背景任务的 external phase
/// 里持有 EF 加载的实体（防止懒加载/UoW 越界）。
///
/// <para>
/// 携带 <c>TenantId</c>：调用方在 Hangfire 等无 ambient <c>ICurrentTenant</c> 的背景任务里
/// 可以直接从 snapshot 取租户 id 写入新建的 <c>DocumentRelation</c>，而不是依赖 ambient——
/// 与 <c>RecallCandidatesAsync</c> 用 <c>source.TenantId</c> 做向量搜索的策略保持一致。
/// </para>
/// </summary>
public sealed record DocumentSnapshot(Guid? TenantId, string? DocumentTypeCode, string Markdown);
