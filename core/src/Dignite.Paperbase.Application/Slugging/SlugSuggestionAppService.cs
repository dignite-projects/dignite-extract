using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// 显示名 → 机器标识（slug）建议（issue #190）。FieldDefinition / DocumentType 创建表单共用。
///
/// <para>
/// 这是 Paperbase 中**首个同步 request/response 形态的 LLM 调用点**（其余 LLM 调用都在
/// BackgroundJob / EventHandler 中）。安全约定（CLAUDE.md "## 安全约定" /
/// .claude/rules/llm-call-anti-patterns.md）逐条对齐：
/// </para>
/// <list type="number">
///   <item>**Fail-closed 权限**：类级 <c>[Authorize(ConfirmClassification)]</c>——这是真实的 HTTP
///         AppService（经 SlugSuggestionController 暴露），属性在 HTTP 边界生效，与
///         FieldDefinitionAppService / DocumentTypeAppService 一致。</item>
///   <item>**无 DB 查询**：纯文本 → 文本，不落任何 <c>IRepository</c> / raw SQL，因而 Take(N) /
///         显式 TenantId 谓词不适用。</item>
///   <item>**PromptBoundary**：用户派生自由文本 DisplayName 进 prompt 前经
///         <see cref="PromptBoundary.WrapField"/> 包裹 + 追加 <see cref="PromptBoundary.BoundaryRule"/>。</item>
///   <item>**编译期常量 instructions**：<see cref="SlugSystemPrompt"/> 是 <c>const</c>，不拼接任何运行时字符串。</item>
///   <item>**不信任 LLM 输出**：结果经 <see cref="Sanitize"/> 限定为 <c>[a-z0-9_]</c>，且仅作为 admin 可改的
///         **建议**——最终 Create 仍走 FieldDefinition/DocumentType 的白名单校验。</item>
/// </list>
/// </summary>
[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class SlugSuggestionAppService : PaperbaseAppService, ISlugSuggestionAppService
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<SlugSuggestionAppService> _logger;

    public SlugSuggestionAppService(
        [FromKeyedServices(PaperbaseAIConsts.StructuredChatClientKey)] IChatClient chatClient,
        ILogger<SlugSuggestionAppService> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    /// <summary>
    /// 服务端硬超时。前端 8s 超时只保护浏览器侧——非 Angular 调用方、保持连接不放的客户端、
    /// 或不及时响应 request-abort 的 provider 仍可能拖住请求处理与 token 配额。作为首个交互式
    /// request/response LLM 路径，后端必须有自己的 deadline 兜底（取略大于前端 8s 的值）。
    /// </summary>
    private static readonly TimeSpan SuggestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 编译期常量 system instructions。**不允许**拼接任何运行时字符串（防 prompt injection）。
    /// </summary>
    private const string SlugSystemPrompt =
        "You convert a human-readable label into a short machine identifier (a \"slug\"). " +
        "Translate non-English labels into concise English first, then form the slug. " +
        "Output rules: lowercase ASCII snake_case using only letters a-z, digits 0-9 and single " +
        "underscores between words; 1 to 3 words; at most 64 characters; no leading or trailing " +
        "underscore; no spaces; no punctuation other than underscores; no quotes. " +
        "Examples: \"合同金额\" -> \"contract_amount\"; \"甲方名称\" -> \"party_name\"; \"発行日\" -> \"issue_date\". " +
        "Return JSON only in the form {\"slug\": \"...\"}.";

    public virtual async Task<SlugSuggestionDto> SuggestAsync(
        SuggestSlugInput input,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, SlugSystemPrompt + "\n\n" + PromptBoundary.BoundaryRule),
            // DisplayName 是用户派生自由文本 —— 经 PromptBoundary.WrapField 显式标记为数据。
            new(ChatRole.User, "Label:\n" + PromptBoundary.WrapField(input.DisplayName))
        };

        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        string slug;
        // 服务端 deadline：把调用方取消令牌（ABP 从 HttpContext.RequestAborted 注入）与 CancelAfter
        // 链接，给 LLM 调用一个不依赖客户端的硬上限。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SuggestTimeout);
        try
        {
            var response = await _chatClient.GetResponseAsync(messages, options, timeoutCts.Token);
            slug = ExtractSlug(response.Text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 客户端主动断开 / 取消 —— 正常情况，原样向上抛（按取消语义结束请求），不记为 LLM 失败、不产生日志噪音。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 服务端 deadline 触发（LLM 太慢 / provider 不响应取消）—— 回吐空 slug，前端回退本地占位。
            _logger.LogWarning(
                "Slug suggestion timed out after {TimeoutSeconds}s; returning empty slug for client-side fallback.",
                (int)SuggestTimeout.TotalSeconds);
            slug = string.Empty;
        }
        catch (Exception ex)
        {
            // LLM 不可用不应让 admin 卡死——回吐空 slug，前端回退到本地占位。
            _logger.LogWarning(ex, "Slug suggestion LLM call failed; returning empty slug for client-side fallback.");
            slug = string.Empty;
        }

        return new SlugSuggestionDto { Slug = slug };
    }

    /// <summary>
    /// 从 LLM 的 JSON 输出里取出 <c>slug</c> 字段并 sanitize。任何解析失败 → 空字符串（前端回退）。
    /// </summary>
    private string ExtractSlug(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawJson.Trim());
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("slug", out var slugProp) &&
                slugProp.ValueKind == JsonValueKind.String)
            {
                return Sanitize(slugProp.GetString());
            }

            // JSON 合法但 schema 漂移（缺 slug 键 / 非字符串）——回退仍生效，但记一条便于离线分析模型行为。
            _logger.LogWarning("Slug suggestion JSON missing a string 'slug' property: {Raw}", rawJson);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Slug suggestion returned non-JSON output: {Raw}", rawJson);
        }

        return string.Empty;
    }

    /// <summary>
    /// 服务端兜底 sanitize：不信任 LLM 输出。小写化、非 <c>[a-z0-9]</c> 折叠成单下划线、
    /// 去首尾下划线、截断到 <see cref="FieldDefinitionConsts.MaxNameLength"/>（64，两套白名单中较紧的上限）。
    /// </summary>
    private static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var lowered = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(ch);
            }
            else
            {
                // 空格 / 短横线 / 标点 / CJK 等一律折叠为下划线占位，下一步再合并。
                sb.Append('_');
            }
        }

        var collapsed = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (collapsed.Length > FieldDefinitionConsts.MaxNameLength)
        {
            collapsed = collapsed[..FieldDefinitionConsts.MaxNameLength].Trim('_');
        }

        return collapsed;
    }
}
