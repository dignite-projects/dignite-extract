using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.Fields;

public interface IFieldDefinitionRepository : IRepository<FieldDefinition, Guid>
{
    /// <summary>
    /// 当前 ambient 租户层用于抽取的字段定义。ABP <c>IMultiTenant</c> filter 按
    /// <c>CurrentTenant.Id</c> 自动隔离单层——
    /// 解读 X：Host 文档（ambient TenantId IS NULL）用 Host 字段；租户文档用对应租户字段。
    /// 两层 mutually exclusive 不混。后台 / 事件路径调用前必须 <c>ICurrentTenant.Change(targetTenantId)</c>。
    /// </summary>
    Task<List<FieldDefinition>> GetForExtractionAsync(
        string documentTypeCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按当前 ambient 租户层查某文档类型下的字段定义（仅当前层；管理 UI 列表用）。
    /// </summary>
    Task<List<FieldDefinition>> GetByDocumentTypeAsync(
        string documentTypeCode,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition?> FindByNameAsync(
        string documentTypeCode,
        string name,
        CancellationToken cancellationToken = default);
}
