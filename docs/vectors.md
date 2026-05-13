# Vector Data

Paperbase stores document chunk embeddings in a Qdrant collection accessed through [`Microsoft.Extensions.VectorData`](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) (MEVD) — the .NET 9 official abstraction for vector stores. The Application layer's contact point is `core/src/Dignite.Paperbase.Application/Vectors/` (POCO + helpers + provider); there is no project-specific vector-store abstraction.

This page covers what hosts need to configure to run vector search in production.

## Why no custom abstraction

Earlier iterations of Paperbase shipped a project-owned `Dignite.Paperbase.KnowledgeIndex` interface plus a Qdrant provider. That layer was deleted once MEVD's `VectorStore` / `VectorStoreCollection<TKey,TRecord>` reached GA — there's no reason to maintain a parallel abstraction over Microsoft's official building block.

Consumers (`DocumentEmbeddingBackgroundJob`, `DocumentTextSearchAdapter`, `SemanticRelationDiscoveryService`, `DocumentDeletingEventHandler`) talk directly to `VectorStoreCollection<Guid, DocumentChunkRecord>` via `DocumentChunkCollectionProvider`. Swapping Qdrant for another MEVD-supported backend (Azure AI Search, Postgres+pgvector, SQL Server, Cosmos DB, Redis, etc.) is a host-level package swap with no Application changes.

## Configuration

```json
"PaperbaseVectorStore": {
  "CollectionName": "paperbase_document_chunks",
  "EmbeddingDimension": 1536,
  "DefaultTopK": 5,
  "MinScore": 0.65,
  "CleanupPageSize": 1000,
  "CleanupMaxIterations": 100,
  "Qdrant": {
    "Endpoint": "http://localhost:6334",
    "ApiKey": ""
  }
}
```

| Key | Default | Description |
| --- | --- | --- |
| `CollectionName` | `paperbase_document_chunks` | Qdrant collection backing `DocumentChunkRecord`. Rotate the name + re-run embedding when changing the embedding model dimension. |
| `EmbeddingDimension` | `1536` | Must match the dimension of the embedding model in [`PaperbaseAI:EmbeddingModelId`](ai-provider.md). `DocumentChunkCollectionProvider` binds this into the runtime `VectorStoreCollectionDefinition` so the same model can be swapped without recompiling. |
| `DefaultTopK` | `5` | Default number of chunks returned per search call when the caller passes no per-request `topK`. Document chat conversations may override this via `DocumentSearchScope.TopK` or the `search_paperbase_documents` tool argument. |
| `MinScore` | `0.65` | Minimum cosine similarity in `[0, 1]`. Hits below this threshold are dropped. Set to `null` to disable. |
| `CleanupPageSize` | `1000` | Page size for the list-then-delete cleanup paths (cascade delete on document deletion, stale-chunk pruning on re-embed). Loops continue until the page returns empty, so this only caps the working set per round-trip — not the total number of chunks that can be cleaned up. |
| `CleanupMaxIterations` | `100` | Safety cap on the cleanup loop. 100 × `CleanupPageSize` = 100K chunks per document, well above any realistic single-document size; if hit, a warning is logged and the loop bails. |
| `Qdrant:Endpoint` | `http://localhost:6334` | Qdrant gRPC endpoint. Parsed for host / port / scheme; `https://...` URLs enable TLS. |
| `Qdrant:ApiKey` | empty | Optional Qdrant API key. |

## Data model

`DocumentChunkRecord` (in `Dignite.Paperbase.Vectors`) is the POCO MEVD persists:

| Field | Storage name | Index | Notes |
|---|---|---|---|
| `Id` | (Qdrant point id) | — | `Guid` derived deterministically from `(TenantId, DocumentId, ChunkIndex)` via `DocumentChunkPointId.Create` so re-runs of the same upsert produce stable point ids. |
| `TenantId` | `tenant_id` | keyword | `Guid.ToString("D")` for tenants, literal `__host__` for host-level data. Every search and delete filters on this. |
| `DocumentId` | `document_id` | keyword | `Guid.ToString("D")`. Filtered on stale-chunk cleanup and document-scoped search. |
| `DocumentTypeCode` | `document_type_code` | keyword | Filtered on type-scoped search (`DocumentSearchScope.DocumentTypeCode`). |
| `ChunkIndex` | `chunk_index` | none | Payload-only — no filter uses it after the move off `MatchExcept`. |
| `Text` | `text` | none | Payload-only — chat surfaces it in citations. Not indexed (we don't run keyword search against the vector store; see "No hybrid search" below). |
| `PageNumber` | `page_number` | none | Optional, payload-only. |
| `Embedding` | `vector` (single unnamed) | HNSW + cosine | Runtime dimension bound from `EmbeddingDimension`. |

Tenant scoping is explicit on every search and delete filter — it does **not** depend on ABP's ambient `DataFilter`. Calls from Hangfire jobs, CLI tools, or any non-HTTP path remain safe.

## No hybrid search

The vector store is dense-only. We do not enable MEVD's `IKeywordHybridSearchable` because:

1. **Keyword-precise queries (合同号 / 产品编号 / 人名) go through business-module MAF Agent Skills** (e.g. `ContractsSkill` with `search` / `get-detail` / `aggregate` scripts) that query SQL directly. The LLM routes structured-lookup intents to those skills before reaching `search_paperbase_documents`.
2. **MEVD's hybrid for Qdrant uses Qdrant's full-text payload index + dense vector in parallel union**, not sparse BM25 + RRF. Its score range is not normalized cosine similarity, so a single `MinScore` threshold cannot safely apply to both branches.
3. **Capability overlap.** Pure dense vector search is the right tool for "find documents semantically similar to this one"; business skills are the right tool for exact-match lookups. The hybrid in between was solving a problem we no longer have.

If a future workload demands cross-document keyword retrieval that business skills cannot serve (e.g. searching free-form note bodies that have no aggregate root), revisit by adding the appropriate full-text store; do not re-enable MEVD hybrid as a stopgap.

## Operational notes

- **No EF Core migration history.** Schema evolution happens at startup through MEVD's `EnsureCollectionExistsAsync`. The collection is created with the configured `EmbeddingDimension` on first use.
- **Embedding dimension is sticky.** Changing `EmbeddingDimension` requires either rotating `CollectionName` (recommended) or dropping the existing Qdrant collection and re-running the embedding job for every document. There is no in-place migration.
- **Upgrading from a pre-MEVD deployment requires dropping the old collection.** Collections written by the previous `KnowledgeIndex.Qdrant` module used named vectors plus a sparse `bm25` field; the new schema is a single unnamed dense vector. MEVD's `EnsureCollectionExistsAsync` does **not** detect schema mismatch — if a `paperbase_document_chunks` collection already exists, MEVD treats it as compatible and subsequent upserts / searches will fail at runtime with errors like "named vector 'bm25' not found". Drop the old collection (`DELETE /collections/paperbase_document_chunks` against the Qdrant HTTP port, default `6333`) before first start; the embedding job re-creates the collection with the new schema and re-embeds documents.
- **Document delete is after-commit.** `DocumentDeletingEventHandler` registers a `UoW.OnCompleted` callback that pages through `(tenant_id, document_id)` and deletes matching points only after the relational transaction commits. This avoids deleting Qdrant points for a relational transaction that later rolls back.
- **Re-embed is whole-document replace.** `DocumentEmbeddingBackgroundJob` upserts the new chunk set (deterministic point ids overwrite prior versions) and then deletes any stragglers under `(tenant_id, document_id)` whose key is no longer in the new set.
- **Tenant sentinel `__host__` is part of the on-disk format.** Host-level documents (`Document.TenantId == null`) persist with the literal string `"__host__"` in the `tenant_id` payload. The `DocumentChunkRecord.HostTenantId` constant carries this value — once data is written, never rename it; doing so orphans every host-level chunk.

## Swapping the backing store

`Microsoft.SemanticKernel.Connectors.Qdrant` is registered in `host/src/PaperbaseHostModule.cs::ConfigureVectorStore`. To swap to another MEVD-supported backend:

1. Reference the new connector package (`Microsoft.SemanticKernel.Connectors.AzureAISearch`, `Microsoft.Extensions.VectorData.Postgres`, etc.).
2. Replace `services.AddQdrantVectorStore()` with the connector's `Add…VectorStore()` extension.
3. Adjust the `Qdrant` sub-section under `PaperbaseVectorStore` to whatever connection params the new connector expects.

`DocumentChunkRecord` attributes (`StorageName`, `IsIndexed`, `IndexKind`, `DistanceFunction`) are MEVD-neutral and apply across connectors. The Application layer does not change.

> Note: the `Microsoft.SemanticKernel.Connectors.*` packages are still in preview as of MEVD 10.1.0. The MS docs explicitly state these providers have nothing to do with Semantic Kernel proper and are usable from anywhere in .NET — the naming is legacy and a rename to `Microsoft.Extensions.VectorData.*` is expected in a future release.

## Enhancement triggers

The current stack — `bge-m3` dense embeddings + MEVD/Qdrant + optional [LLM rerank](../core/src/Dignite.Paperbase.Application/Chat/Search/DocumentRerankWorkflow.cs) — is intentionally the honest minimum. Several well-known retrieval enhancements (cross-encoder rerank, MMR, HyDE, multi-query expansion, Matryoshka / quantized embeddings) are **not** enabled. Each adds engineering cost; none of them is justified until a concrete signal shows up. This section lists the signals so future maintainers can decide based on evidence, not vibes.

| Enhancement | What it does | Trigger to add it |
|---|---|---|
| `PaperbaseAIBehavior:EnableLlmRerank = true` | Already implemented. Expands recall by `RecallExpandFactor` × `topK`, asks the structured LLM to score each chunk 0-1, returns top N. Off by default to save tokens. | User reports "chat's cited chunks aren't quite the right ones" **and** the top-K candidates are visibly out of order by relevance |
| **Cross-encoder rerank** (bge-reranker-v2-m3, Cohere Rerank API, etc.) — replaces LLM rerank | Dedicated small reranker model: ~10× faster, ~50× cheaper than LLM rerank. Requires separate model deployment or external API. | `EnableLlmRerank = true` has been on long enough to measure: rerank adds > 300ms P99 **or** rerank tokens > 20% of chat total cost |
| **MMR** (Maximal Marginal Relevance) in `DocumentTextSearchAdapter` | Picks top-K balancing relevance and diversity, so K chunks aren't all from the same paragraph of one document. No extra LLM call. | User reports "citations all come from one section, I can't see other relevant places" |
| **HyDE** / **Multi-query expansion** | LLM generates a hypothetical answer (HyDE) or multiple query variants (multi-query) before embedding. | A systematic class of short / vague user queries shows degraded recall@k vs. the same query manually expanded |
| Matryoshka / int8 / binary quantization | Compresses embeddings 4-32× for memory or storage savings. | Qdrant RAM or disk becomes a real bottleneck at production data scale |

Note: keyword-precise queries (合同号 / 产品编号 / 人名) intentionally do **not** flow through vector retrieval — they're handled by business-module MAF Agent Skills hitting SQL directly. "Hybrid search" in the MEVD sense (text-match + dense union) is therefore not on the trigger list; it would overlap that path with non-normalized scores.

Before adding any enhancement, build a small query/expected-result benchmark corpus first — without ground truth there's no way to measure the change.

## See also

- [Embedding pipeline](embedding.md) — what writes into the vector store
- [Document chat](chat.md) — primary reader
- [Relation discovery](relation-discovery.md) — L3 semantic relation discovery also queries the vector store
- [Microsoft.Extensions.VectorData overview](https://learn.microsoft.com/dotnet/ai/vector-stores/overview) — upstream abstraction
