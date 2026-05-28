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
    /// 结构化检索的字段值匹配子查询（字段架构 v2 / Issue #206）：返回当前层（ABP <c>IMultiTenant</c> + 软删除
    /// 全局过滤器按 ambient 状态自动隔离）内、<see cref="Document.DocumentTypeCode"/> == <paramref name="documentTypeCode"/>
    /// 且 <see cref="Document.ExtractedFieldValues"/> 满足 <paramref name="fieldQueries"/>（多个之间 <c>AND</c>，
    /// 结构化检索惯例：不同字段互相收窄）的文档 Id 集合。调用层（<c>DocumentAppService.GetListAsync</c>）据此与
    /// 元数据过滤求交（<c>query.Where(ids.Contains(d.Id))</c>）。
    /// <para>
    /// 实现从 <c>Documents</c> 聚合根起手，每个字段过滤编译成一个对 child 集合
    /// <see cref="Document.ExtractedFieldValues"/> 的 <c>Any</c>（EXISTS）+ 类型化列普通比较——纯 EF Core LINQ，
    /// 可翻译到 SQL Server / PostgreSQL / MySQL / SQLite，不再依赖 SQL Server <c>JSON_VALUE</c> / <c>TRY_CONVERT</c>
    /// / raw SQL（注入面归零）。
    /// </para>
    /// 安全：按 <see cref="DocumentFieldQuery.FieldDataType"/> 分派等值 / 区间；只 = + range，永不 LIKE；
    /// String/Boolean 传区间抛 <see cref="PaperbaseErrorCodes.FieldTypeDoesNotSupportRange"/>；值无法解析为声明类型抛
    /// <see cref="PaperbaseErrorCodes.InvalidExtractedFieldValue"/>（皆 loud，不静默空）。
    /// 权限断言、输入校验（必填 / 长度 / 数量 / 至少一个值）、字段类型解析（<c>FieldDefinition</c> → <see cref="FieldDataType"/>）
    /// 都属调用层（DTO + AppService）职责——本仓储只做 <see cref="Document"/> 聚合根的数据访问，不在此重复，也不依赖其它聚合的仓储。
    /// </summary>
    /// <param name="documentTypeCode">检索锚定的单一文档类型（调用层已校验非空），作为 SQL 参数施加。</param>
    /// <param name="fieldQueries">已解析的字段值过滤器（每个带 <c>FieldName</c> + <c>FieldDataType</c> + 至少一个值）；空 → 返回空集合。</param>
    Task<List<Guid>> GetFieldMatchedIdsAsync(
        string documentTypeCode,
        IReadOnlyList<DocumentFieldQuery> fieldQueries,
        CancellationToken cancellationToken = default);
}
