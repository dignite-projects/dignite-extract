using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Vectors;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Dignite.Paperbase.Tests.Vectors;

// Test double for DocumentChunkCollectionProvider that hands out a shared
// FakeDocumentChunkCollection instance. Wired into ChatAppServiceTestModule so
// every consumer (DocumentEmbeddingBackgroundJob, DocumentDeletingEventHandler,
// DocumentTextSearchAdapter) reads / writes the same in-memory store.
public sealed class FakeDocumentChunkCollectionProvider : DocumentChunkCollectionProvider
{
    private readonly FakeDocumentChunkCollection _collection;

    public FakeDocumentChunkCollectionProvider(FakeDocumentChunkCollection collection)
        : base(vectorStore: null!, options: Options.Create(new PaperbaseVectorStoreOptions()))
    {
        _collection = collection;
    }

    public FakeDocumentChunkCollection Collection => _collection;

    public override Task<VectorStoreCollection<System.Guid, DocumentChunkRecord>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<VectorStoreCollection<System.Guid, DocumentChunkRecord>>(_collection);
    }
}
