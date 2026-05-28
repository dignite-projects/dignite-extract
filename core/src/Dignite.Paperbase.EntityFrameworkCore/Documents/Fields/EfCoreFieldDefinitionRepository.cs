using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents.Fields;

public class EfCoreFieldDefinitionRepository
    : EfCoreRepository<PaperbaseDbContext, FieldDefinition, Guid>, IFieldDefinitionRepository
{
    public EfCoreFieldDefinitionRepository(IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider) { }

    public async Task<List<FieldDefinition>> GetForExtractionAsync(
        string documentTypeCode,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(f => f.DocumentTypeCode == documentTypeCode)
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<List<FieldDefinition>> GetByDocumentTypeAsync(
        string documentTypeCode,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Where(f => f.DocumentTypeCode == documentTypeCode)
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    public async Task<FieldDefinition?> FindByNameAsync(
        string documentTypeCode,
        string name,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet.FirstOrDefaultAsync(
            f => f.DocumentTypeCode == documentTypeCode && f.Name == name,
            GetCancellationToken(cancellationToken));
    }
}
