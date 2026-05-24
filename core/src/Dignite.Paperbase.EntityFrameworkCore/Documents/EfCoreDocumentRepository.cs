using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

public class EfCoreDocumentRepository
    : EfCoreRepository<PaperbaseDbContext, Document, Guid>, IDocumentRepository
{
    // ExtractedFields 字段名白名单——与 FieldDefinitionConsts.NamePattern 同源（^[A-Za-z0-9_\-]{1,64}$）。
    // 校验后字符集不含 " / \，可安全引号化为 JSON path key（$."name"）。
    private static readonly Regex ExtractedFieldNameRegex =
        new(FieldDefinitionConsts.NamePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ICurrentTenant _currentTenant;

    public EfCoreDocumentRepository(
        IDbContextProvider<PaperbaseDbContext> dbContextProvider,
        ICurrentTenant currentTenant)
        : base(dbContextProvider)
    {
        _currentTenant = currentTenant;
    }

    public virtual async Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .FirstOrDefaultAsync(
                d => d.OriginalFileBlobName == blobName,
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await dbSet
                .FirstOrDefaultAsync(
                    d => d.FileOrigin.ContentHash == contentHash,
                    GetCancellationToken(cancellationToken));
        }
    }

    public override async Task<IQueryable<Document>> WithDetailsAsync()
    {
        return (await GetQueryableAsync()).IncludeDetails();
    }

    public virtual async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextAsync();
        await dbContext.Set<Document>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == id)
            .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<Document>> SearchAsync(
        string? keyword = null,
        string? documentTypeCode = null,
        DocumentLifecycleStatus? lifecycleStatus = null,
        string? fieldName = null,
        string? fieldValue = null,
        int maxResultCount = DocumentConsts.MaxSearchResultCount,
        CancellationToken cancellationToken = default)
    {
        // fail-closed 安全门：超长输入直接空结果，不进 LIKE / JSON_VALUE 全表扫，防 DB/CPU 滥用。
        // （MCP tool 不经 REST DTO 的 [StringLength] 校验，长度守门必须落在这个统一入口。）
        // fieldName 的长度由下方 NamePattern（{1,64}）覆盖，此处不重复。
        if ((keyword != null && keyword.Length > DocumentConsts.MaxSearchKeywordLength)
            || (documentTypeCode != null && documentTypeCode.Length > DocumentConsts.MaxDocumentTypeCodeLength)
            || (fieldValue != null && fieldValue.Length > DocumentConsts.MaxSearchFieldValueLength))
        {
            return new List<Document>();
        }

        // fail-closed 安全门：结果集硬上限。即便 caller 传入更大或非法值也 clamp 到编译期常量。
        var take = maxResultCount <= 0
            ? DocumentConsts.MaxSearchResultCount
            : Math.Min(maxResultCount, DocumentConsts.MaxSearchResultCount);

        // keyword 归一化：去首尾空白，与 DocumentAppService.ApplyFilter 行为对齐（避免两条检索路径漂移）。
        keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();

        var dbSet = await GetDbSetAsync();

        IQueryable<Document> query;
        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            // ExtractedFields 是 Dictionary<string,JsonElement> 经 ValueConverter 映射为 native json 列，
            // EF Core 10 无法 LINQ 翻译动态键查询（d.ExtractedFields["x"] 不可译；EF.Functions.JsonContains
            // 要 EF 11）。用固定模板参数化 raw SQL 走 JSON_VALUE：
            //   - fieldName 按 FieldDefinitionConsts.NamePattern 白名单校验（与字段名实体约束同源，含连字符），
            //     校验后引号化为 JSON path key（$."name"，字符集不含 " / \，无注入面）；
            //   - fieldValue 作为 SQL 参数 {0}。
            // 这不是 llm-call-anti-patterns.md 反例 5 的"LLM 拼 SQL"——SQL 形状固定，仅受限 path + 参数化 value 可变。
            // 注：SQL Server 2025 CREATE JSON INDEX 仍 preview（见 issue 前置确认 / #198），此处暂为全表扫，GA 后另起 migration 建索引。
            if (!ExtractedFieldNameRegex.IsMatch(fieldName))
            {
                // 非法字段名：fail-closed，不把可疑输入透传到 SQL，直接空结果。
                return new List<Document>();
            }

            var table = string.IsNullOrEmpty(PaperbaseDbProperties.DbSchema)
                ? $"[{PaperbaseDbProperties.DbTablePrefix}Documents]"
                : $"[{PaperbaseDbProperties.DbSchema}].[{PaperbaseDbProperties.DbTablePrefix}Documents]";

            // 引号化 path key：$."{fieldName}"（fieldName 已白名单校验，无 " / \，引号内安全；支持连字符等合法字符）。
            var sql = $"SELECT * FROM {table} WHERE JSON_VALUE([ExtractedFields], '$.\"{fieldName}\"') = {{0}}";
            query = dbSet.FromSqlRaw(sql, fieldValue ?? (object)DBNull.Value);
        }
        else
        {
            query = await GetQueryableAsync();
        }

        // fail-closed 安全门 #2：显式 TenantId 谓词，读 ICurrentTenant 而非依赖 ambient DataFilter。
        // 多租户当前关闭时 CurrentTenant.Id 恒为 null（host 文档），谓词自然收敛到 host 宇宙；MT 打开后自动隔离。
        var tenantId = _currentTenant.Id;
        query = query.Where(d => d.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(d =>
                (d.Title != null && d.Title.Contains(keyword)) ||
                (d.FileOrigin.OriginalFileName != null && d.FileOrigin.OriginalFileName.Contains(keyword)) ||
                (d.Markdown != null && d.Markdown.Contains(keyword)));
        }

        if (!string.IsNullOrWhiteSpace(documentTypeCode))
        {
            query = query.Where(d => d.DocumentTypeCode == documentTypeCode);
        }

        if (lifecycleStatus.HasValue)
        {
            query = query.Where(d => d.LifecycleStatus == lifecycleStatus.Value);
        }

        // 只读检索：AsNoTracking 免去 change-tracker 对（至多 N 条）实体的快照开销。
        return await query
            .AsNoTracking()
            .OrderByDescending(d => d.CreationTime)
            .Take(take)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }
}
