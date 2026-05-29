using System;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.Exports;

public interface IExportTemplateRepository : IRepository<ExportTemplate, Guid>
{
    /// <summary>按当前层 + Name 查模板（用于创建判重）。</summary>
    Task<ExportTemplate?> FindByNameAsync(string name, CancellationToken cancellationToken = default);
}
