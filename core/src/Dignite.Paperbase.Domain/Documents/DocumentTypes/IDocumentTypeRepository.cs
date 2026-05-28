using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.DocumentTypes;

public interface IDocumentTypeRepository : IRepository<DocumentType, Guid>
{
    /// <summary>
    /// 拿当前 ambient 租户层的文档类型集合（Priority DESC + TypeCode ASC 排序）。
    /// ABP <c>IMultiTenant</c> filter 按 <c>CurrentTenant.Id</c> 自动隔离单层——
    /// 解读 X + 没有继承关系：Host 文档（ambient TenantId IS NULL）用 Host 类型；
    /// 租户文档用对应租户类型；不存在跨层 union。用于分类候选集组装。
    /// 后台 / 事件路径调用前必须用 <c>ICurrentTenant.Change(targetTenantId)</c> 切到目标层。
    /// </summary>
    Task<List<DocumentType>> GetByTenantAsync(
        CancellationToken cancellationToken = default);

    Task<DocumentType?> FindByTypeCodeAsync(
        string typeCode,
        CancellationToken cancellationToken = default);
}
