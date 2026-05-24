using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents;

public class EfCoreExportTemplateRepository
    : EfCoreRepository<PaperbaseDbContext, ExportTemplate, Guid>, IExportTemplateRepository
{
    public EfCoreExportTemplateRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<ExportTemplate>> GetByTenantAsync(CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .OrderBy(t => t.Name)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<ExportTemplate?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            t => t.Name == name,
            GetCancellationToken(cancellationToken));
    }
}
