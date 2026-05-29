using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.Fields;

public interface IFieldDefinitionRepository : IRepository<FieldDefinition, Guid>
{
    /// <summary>
    /// 按当前 ambient 租户层查某文档类型下的字段定义（按 <paramref name="documentTypeId"/> 内部关联匹配，#207，
    /// 按 <c>DisplayOrder</c> 排序）。字段抽取路径与管理 / MCP 读取路径共用此查询。
    /// <para>
    /// 隔离由 ambient <c>IMultiTenant</c> filter 施加，不跨层。后台 / 事件路径（如字段抽取）调用前
    /// 必须 <c>ICurrentTenant.Change(targetTenantId)</c>，使 ambient 层对齐 <c>Document.TenantId</c>。
    /// </para>
    /// </summary>
    Task<List<FieldDefinition>> GetListAsync(
        Guid documentTypeId,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition?> FindByNameAsync(
        Guid documentTypeId,
        string name,
        CancellationToken cancellationToken = default);
}
