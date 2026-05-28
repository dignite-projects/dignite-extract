using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents.DocumentTypes;

public class EfCoreDocumentTypeRepository
    : EfCoreRepository<PaperbaseDbContext, DocumentType, Guid>, IDocumentTypeRepository
{
    public EfCoreDocumentTypeRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<DocumentType>> GetByTenantAsync(
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.TypeCode)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<DocumentType?> FindByTypeCodeAsync(
        string typeCode,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            t => t.TypeCode == typeCode,
            GetCancellationToken(cancellationToken));
    }
}
