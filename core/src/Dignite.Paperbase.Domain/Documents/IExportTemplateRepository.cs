using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents;

public interface IExportTemplateRepository : IRepository<ExportTemplate, Guid>
{
    /// <summary>
    /// 拿当前 ambient 租户层的导出模板（Name ASC）。ABP <c>IMultiTenant</c> filter 按
    /// <c>CurrentTenant.Id</c> 自动隔离单层——Host admin（ambient TenantId IS NULL）看 Host 模板，
    /// 租户 admin 看自己租户模板，不跨层 union。
    /// </summary>
    Task<List<ExportTemplate>> GetByTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>按当前层 + Name 查模板（用于创建判重）。</summary>
    Task<ExportTemplate?> FindByNameAsync(string name, CancellationToken cancellationToken = default);
}
