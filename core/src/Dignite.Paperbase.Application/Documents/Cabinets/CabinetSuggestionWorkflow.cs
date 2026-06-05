using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 「留空 AI 兜底选柜」Workflow（#265，MAF ChatClientAgent + 结构化输出）。
/// <para>
/// 放在 <c>Documents/Cabinets/</c> 而非 <c>Pipelines/</c>——文件柜是<b>人工组织维度，正交于内容 pipeline</b>（#194）。
/// 本 workflow 是上传后<b>一次性独立</b>的兜底步骤，<b>不</b>是分类 / 字段抽取 pipeline 的阶段：它<b>只</b>在
/// 文档<b>未</b>归类（操作员上传时留空）时由 <c>DocumentCabinetSuggestionBackgroundJob</c> 调用一次，
/// 从当前层的文件柜里挑一个最贴合的，或在没有清晰匹配时弃选（保持「未归类」）。
/// </para>
/// <para>
/// 结构镜像 <see cref="Pipelines.Classification.DocumentClassificationWorkflow"/>（同 keyed structured client、
/// PromptBoundary 包裹、前段截断、结构化输出），但更轻——无 PipelineRun、无出口事件、无置信度归一化分支
/// （柜选择只需「编号 + 置信度」）。安全约定逐条对齐 <c>.claude/rules/llm-call-anti-patterns.md</c>：
/// 柜名是用户控制文本 → <see cref="PromptBoundary.WrapField"/>；markdown → <see cref="PromptBoundary.WrapDocument"/>；
/// system prompt 是编译期 <c>const</c>；不信任 LLM 输出（编号越界 / 非法 → 弃选）。
/// </para>
/// </summary>
public class CabinetSuggestionWorkflow : ITransientDependency
{
    /// <summary>
    /// 与分类同走 <see cref="PaperbaseAIConsts.StructuredChatClientKey"/> keyed client
    /// （structured-output、tool-free、prompt-unique）——host 可把它指向更小 / 更便宜的模型。
    /// </summary>
    private readonly IChatClient _chatClient;
    private readonly PaperbaseAIBehaviorOptions _options;

    public ILogger<CabinetSuggestionWorkflow> Logger { get; set; }
        = NullLogger<CabinetSuggestionWorkflow>.Instance;

    public CabinetSuggestionWorkflow(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        IOptions<PaperbaseAIBehaviorOptions> options)
    {
        _chatClient = chatClient;
        _options = options.Value;
    }

    /// <summary>
    /// 编译期常量 system instructions（防 prompt injection，<b>不</b>拼接任何运行时字符串）。
    /// 让 LLM 从编号候选里选一个，或在没有清晰匹配时返回 <c>null</c>（宁缺毋滥）。
    /// </summary>
    private const string SystemPrompt =
        "You help organize an uploaded document into the best-matching filing cabinet. " +
        "Cabinets are a human organizational dimension (e.g. by department, project, or batch) and are " +
        "independent of the document's content type. You are given a numbered list of the available " +
        "cabinets and the beginning of the document (as Markdown). " +
        "Pick the single cabinet whose name best fits the document, and report your confidence (0.0 to 1.0). " +
        "If no cabinet clearly fits, return null for cabinetIndex with a low confidence — it is better to " +
        "leave the document uncategorized than to file it into a poorly matching cabinet. " +
        "Return JSON only: {\"cabinetIndex\": <1-based number or null>, \"confidence\": <0.0-1.0>}.";

    /// <summary>
    /// 从候选柜列表中为 <paramref name="markdown"/> 挑一个柜。返回 <see cref="CabinetSuggestionOutcome"/>
    /// （<see cref="CabinetSuggestionOutcome.CabinetId"/> 为 <c>null</c> 表示弃选 / 无法识别 / 编号越界）。
    /// 置信度阈值由调用方（<c>DocumentCabinetSuggestionBackgroundJob</c>）按
    /// <see cref="PaperbaseAIBehaviorOptions.MinCabinetSuggestionConfidence"/> 施加——本方法只解析 + 映射，不做阈值裁决。
    /// </summary>
    public virtual async Task<CabinetSuggestionOutcome> RunAsync(
        IReadOnlyList<Cabinet> candidates,
        string markdown,
        CancellationToken cancellationToken = default)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return CabinetSuggestionOutcome.None;
        }

        // 选柜只需文档前段语义即可判断归属，故按 MaxTextLengthPerExtraction 截断前部（与分类同策略）。
        var truncatedText = markdown;
        if (markdown.Length > _options.MaxTextLengthPerExtraction)
        {
            // 截断率是有价值的运营信号（与 DocumentClassificationWorkflow 同精神记一条 warning）。
            Logger.LogWarning(
                "Cabinet suggestion input truncated from {OriginalLength} to {TruncatedLength} characters.",
                markdown.Length, _options.MaxTextLengthPerExtraction);
            truncatedText = TruncateAtCharBoundary(markdown, _options.MaxTextLengthPerExtraction);
        }

        // 候选集以 1-based 编号喂入——用编号而非 Guid / Name 回显：避免 LLM 复制 GUID 出错，
        // 且天然抗注入（只能在预载候选集内选）。柜名是用户控制文本，必须 PromptBoundary.WrapField 包裹。
        var numbered = string.Join(
            "\n",
            candidates.Select((c, i) => $"{i + 1}. {PromptBoundary.WrapField(c.Name)}"));

        var userMessage = $$"""
                ## Available Cabinets
                {{numbered}}

                ## Document Markdown (first {{_options.MaxTextLengthPerExtraction}} characters)
                {{PromptBoundary.WrapDocument(truncatedText)}}
                """;

        AIAgent agent = new ChatClientAgent(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = "PaperbaseCabinetSuggester",
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemPrompt + " " + PromptBoundary.BoundaryRule
                },
                UseProvidedChatClientAsIs = true
            })
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();

        var response = await agent.RunAsync<CabinetSuggestionResponse>(
            userMessage,
            cancellationToken: cancellationToken);

        return ResolveOutcome(response.Result, candidates);
    }

    /// <summary>
    /// 把 LLM 的 <see cref="CabinetSuggestionResponse"/> 解析为 <see cref="CabinetSuggestionOutcome"/>：
    /// 1-based 编号映射回候选 <see cref="Cabinet.Id"/>，越界 / null / 非法编号 → 弃选；置信度 clamp 到 0..1。
    /// internal static 便于 Application.Tests 直接验证映射 / 弃选边界（与分类的 helper 同源）。
    /// </summary>
    internal CabinetSuggestionOutcome ResolveOutcome(
        CabinetSuggestionResponse? parsed,
        IReadOnlyList<Cabinet> candidates)
    {
        if (parsed?.CabinetIndex is not { } index)
        {
            return CabinetSuggestionOutcome.None;
        }

        // 1-based 编号；越界（含 LLM 返回 0 / 负数 / 超过候选数）→ 弃选，不写脏柜。
        if (index < 1 || index > candidates.Count)
        {
            Logger.LogWarning(
                "Cabinet suggestion returned out-of-range index {Index} for {CandidateCount} candidates; abstaining.",
                index, candidates.Count);
            return CabinetSuggestionOutcome.None;
        }

        return new CabinetSuggestionOutcome
        {
            CabinetId = candidates[index - 1].Id,
            Confidence = ClampConfidence(parsed.Confidence)
        };
    }

    internal static double ClampConfidence(double value)
    {
        if (double.IsNaN(value))
            return 0d;
        if (value < 0d) return 0d;
        if (value > 1d) return 1d;
        return value;
    }

    // 按 UTF-16 码元上限截断，但不切断代理对（与 DocumentClassificationWorkflow.TruncateAtCharBoundary 同源）。
    // internal 便于 Application.Tests 直接验证边界逻辑。
    internal static string TruncateAtCharBoundary(string text, int maxChars)
    {
        if (maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;

        var end = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..end];
    }

    internal sealed class CabinetSuggestionResponse
    {
        /// <summary>1-based 候选编号；<c>null</c> 表示 LLM 弃选（无清晰匹配）。</summary>
        public int? CabinetIndex { get; set; }

        public double Confidence { get; set; }
    }
}

/// <summary>
/// 选柜结果。<see cref="CabinetId"/> 为 <c>null</c> 表示弃选（无候选 / LLM 弃选 / 编号越界）——
/// 调用方据此保持文档「未归类」。<see cref="Confidence"/> 供调用方按阈值裁决。
/// </summary>
public sealed class CabinetSuggestionOutcome
{
    public Guid? CabinetId { get; init; }

    public double Confidence { get; init; }

    public static CabinetSuggestionOutcome None { get; } = new() { CabinetId = null, Confidence = 0d };
}
