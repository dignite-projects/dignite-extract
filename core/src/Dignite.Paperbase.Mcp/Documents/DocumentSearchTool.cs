using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;
using Volo.Abp.Authorization;

namespace Dignite.Paperbase.Mcp.Documents;

/// <summary>
/// MCP 检索 tool：按 keyword + 结构化元数据 + ExtractedFields 字段值发现文档。
/// 仅 keyword + metadata 过滤，**不做语义/向量检索**（向量检索是下游 RAG 的事，见 CLAUDE.md OUT of scope）。
/// 入参限定强类型 schema（无 free-form JSON path），fail-closed 安全门在仓储 + 本方法体内强制。
/// </summary>
[McpServerToolType]
public sealed class DocumentSearchTool
{
    [McpServerTool(Name = "search_paperbase_documents")]
    [Description("Search Paperbase documents by keyword and/or structured metadata. "
        + "Returns up to 50 thin rows (id, uri, title, type, lifecycle, created-at); "
        + "read a match's full Markdown via its paperbase://documents/{id} resource uri. "
        + "Keyword + metadata search only — no semantic/vector retrieval.")]
    public static async Task<IReadOnlyList<DocumentSearchResultItem>> SearchAsync(
        IDocumentRepository documentRepository,
        IAuthorizationService authorizationService,
        [Description("Substring matched against title, original file name, and Markdown body. Optional.")]
        string? keyword = null,
        [Description("Exact document type code to filter by (e.g. a classification result). Optional.")]
        string? documentTypeCode = null,
        [Description("Filter by lifecycle status. One of: Uploaded, Processing, Ready, Failed, Archived. Optional.")]
        string? lifecycleStatus = null,
        [Description("Name of an extracted field to filter by; pair with fieldValue. Optional.")]
        string? fieldName = null,
        [Description("Exact value the extracted field must equal; pair with fieldName. Optional.")]
        string? fieldValue = null,
        [Description("Max rows to return (1-50). Defaults to 50.")]
        int? maxResultCount = null,
        CancellationToken cancellationToken = default)
    {
        // fail-closed 安全门 #1：显式权限断言。MCP dispatch 不经 HTTP 边界 [Authorize]，方法体内断言是唯一防线。
        await authorizationService.CheckAsync(PaperbasePermissions.Documents.Default);

        // 容错解析 lifecycle 过滤值——LLM 客户端通常传字符串名（"Ready"）。无法解析则当作"不过滤"
        // （filter 缺失只会多返回结果，受 Take(N) 上限约束；不是安全门，权限 / 租户 / 上限仍生效）。
        DocumentLifecycleStatus? lifecycle = null;
        if (!string.IsNullOrWhiteSpace(lifecycleStatus)
            && Enum.TryParse<DocumentLifecycleStatus>(lifecycleStatus, ignoreCase: true, out var parsedLifecycle))
        {
            lifecycle = parsedLifecycle;
        }

        // 仓储入口强制：显式 TenantId 谓词 + 结果集硬上限（fail-closed 安全门 #2 / #3）。
        var documents = await documentRepository.SearchAsync(
            keyword: keyword,
            documentTypeCode: documentTypeCode,
            lifecycleStatus: lifecycle,
            fieldName: fieldName,
            fieldValue: fieldValue,
            maxResultCount: maxResultCount ?? DocumentConsts.MaxSearchResultCount,
            cancellationToken: cancellationToken);

        return documents
            .Select(d => new DocumentSearchResultItem
            {
                Uri = DocumentResourceUri.Format(d.Id),
                Id = d.Id,
                // fail-closed 安全门 #4：用户派生自由文本（title）经 PromptBoundary 包裹，防 indirect prompt injection。
                Title = PromptBoundary.WrapField(d.Title),
                DocumentTypeCode = d.DocumentTypeCode,
                LifecycleStatus = d.LifecycleStatus.ToString(),
                CreationTime = d.CreationTime
            })
            .ToList();
    }
}
