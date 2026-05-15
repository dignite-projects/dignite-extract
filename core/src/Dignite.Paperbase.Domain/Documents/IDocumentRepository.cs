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
}
