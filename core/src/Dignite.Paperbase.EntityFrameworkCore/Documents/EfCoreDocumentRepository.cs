using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Dignite.Paperbase.Documents;

public class EfCoreDocumentRepository
    : EfCoreRepository<PaperbaseDbContext, Document, Guid>, IDocumentRepository
{
    public EfCoreDocumentRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
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
}
