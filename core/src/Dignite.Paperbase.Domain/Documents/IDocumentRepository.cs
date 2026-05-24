using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents;

public interface IDocumentRepository : IRepository<Document, Guid>
{
    Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default);

    Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default);

    Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 程序化 / LLM 触发的结构化检索入口（MCP 检索 tool 等）。fail-closed 安全门在本方法体内强制：
    /// <list type="number">
    ///   <item>显式 <c>TenantId</c> 谓词（读 <see cref="Volo.Abp.MultiTenancy.ICurrentTenant"/>，不依赖 ambient DataFilter）；</item>
    ///   <item>结果集硬上限 <see cref="DocumentConsts.MaxSearchResultCount"/>（即便 caller 传更大值也 clamp）；</item>
    ///   <item><paramref name="fieldName"/> 严格校验为标识符后才进 JSON path，<paramref name="fieldValue"/> 走 SQL 参数——无 raw SQL 注入面。</item>
    /// </list>
    /// 权限断言（<c>CheckAsync</c>）由调用方（出口适配器）负责，不在仓储做。
    /// </summary>
    /// <param name="keyword">子串匹配 Title / 原始文件名 / Markdown（任一命中）。</param>
    /// <param name="documentTypeCode">精确匹配分类结果。</param>
    /// <param name="lifecycleStatus">精确匹配生命周期状态。</param>
    /// <param name="fieldName">ExtractedFields 字段名（动态键）；与 <paramref name="fieldValue"/> 配对做等值过滤。</param>
    /// <param name="fieldValue">ExtractedFields 字段值（字符串等值）。</param>
    /// <param name="maxResultCount">期望条数；硬 clamp 到 <see cref="DocumentConsts.MaxSearchResultCount"/>。</param>
    Task<List<Document>> SearchAsync(
        string? keyword = null,
        string? documentTypeCode = null,
        DocumentLifecycleStatus? lifecycleStatus = null,
        string? fieldName = null,
        string? fieldValue = null,
        int maxResultCount = DocumentConsts.MaxSearchResultCount,
        CancellationToken cancellationToken = default);
}
