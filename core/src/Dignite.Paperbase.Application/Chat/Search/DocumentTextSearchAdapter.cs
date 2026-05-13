using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;
using Volo.Abp.DependencyInjection;

namespace Dignite.Paperbase.Chat.Search;

/// <summary>
/// Bridges Microsoft.Extensions.VectorData's <c>VectorStoreCollection&lt;Guid,
/// DocumentChunkRecord&gt;</c> to MAF tool calling, exposing pure dense vector
/// search as an LLM-callable <see cref="AIFunction"/> via <see cref="CreateSearchFunction"/>.
///
/// <para>
/// Keyword-precise queries (合同号 / 产品编号 / 人名) are handled by business-module
/// MAF Agent Skills (e.g. <c>ContractsSkill</c>'s <c>search</c> script) that query
/// SQL directly — the LLM routes structured-lookup intents to those skills before
/// reaching this adapter. Vector retrieval therefore covers semantic similarity only; we
/// intentionally do not enable MEVD's <c>IKeywordHybridSearchable</c> because its
/// scoring is non-normalized (cosine threshold would silently drop valid hits)
/// and its capability overlaps with business-module structured search.
/// </para>
///
/// The adapter owns the work the framework can't do on its own:
/// <list type="bullet">
///   <item>Embed the query because the vector store search uses a pre-computed vector.</item>
///   <item>Carry an explicit closure-captured TenantId so the search is safe under
///         Hangfire / CLI scenarios where ABP ambient context is absent.</item>
///   <item>Project MEVD's generic <c>VectorSearchResult&lt;DocumentChunkRecord&gt;</c>
///         into <see cref="DocumentChunkSearchHit"/> so downstream Capture +
///         ChatAppService stay free of vector-store-specific types.</item>
///   <item>Format result chunks into a prompt block with provenance metadata
///         (<c>&lt;document id chunk page&gt;</c> tags), the only payload the LLM sees.</item>
/// </list>
/// </summary>
public class DocumentTextSearchAdapter : ITransientDependency
{
    private readonly DocumentChunkCollectionProvider _collectionProvider;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly DocumentRerankWorkflow _rerankWorkflow;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;
    private readonly PaperbaseVectorStoreOptions _vectorStoreOptions;
    private readonly ILogger<DocumentTextSearchAdapter> _logger;

    public DocumentTextSearchAdapter(
        DocumentChunkCollectionProvider collectionProvider,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        DocumentRerankWorkflow rerankWorkflow,
        IOptions<PaperbaseAIBehaviorOptions> aiOptions,
        IOptions<PaperbaseVectorStoreOptions> vectorStoreOptions,
        ILogger<DocumentTextSearchAdapter> logger)
    {
        _collectionProvider = collectionProvider;
        _embeddingGenerator = embeddingGenerator;
        _rerankWorkflow = rerankWorkflow;
        _aiOptions = aiOptions.Value;
        _vectorStoreOptions = vectorStoreOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Formats the context block returned to the agent. Each chunk is wrapped in
    /// <c>&lt;document id="…" chunk="…"&gt;</c> tags; the chunk text is passed through
    /// <see cref="PromptBoundary.WrapDocument"/> so that any <c>&lt;</c> characters are
    /// escaped to <c>&amp;lt;</c> before injection, preventing tag-injection attacks.
    /// Override in a subclass to customize the prompt structure.
    /// </summary>
    protected virtual string FormatSearchContext(IReadOnlyList<DocumentChunkSearchHit> vectorResults)
    {
        var sb = new StringBuilder();
        foreach (var vr in vectorResults)
        {
            var pageAttr = vr.PageNumber.HasValue ? $" page=\"{vr.PageNumber}\"" : "";
            sb.AppendLine($"<document id=\"{vr.DocumentId:D}\" chunk=\"{vr.ChunkIndex}\"{pageAttr}>");
            sb.AppendLine(PromptBoundary.WrapDocument(vr.Text ?? string.Empty));
            sb.AppendLine("</document>");
        }
        return sb.ToString();
    }

    protected virtual async Task<IReadOnlyList<DocumentChunkSearchHit>> SearchVectorAsync(
        Guid? tenantId,
        DocumentSearchScope? scope,
        string query,
        CancellationToken cancellationToken = default)
    {
        var finalTopK = scope?.TopK ?? _vectorStoreOptions.DefaultTopK;
        var rerank = _aiOptions.EnableLlmRerank && finalTopK > 0;
        var recallTopK = rerank
            ? finalTopK * Math.Max(1, _aiOptions.RecallExpandFactor)
            : finalTopK;

        var embeddings = await _embeddingGenerator.GenerateAsync(
            [query], cancellationToken: cancellationToken);
        var queryVector = embeddings[0].Vector;

        var tenantKey = DocumentChunkPayloadEncoding.EncodeTenantId(tenantId);
        var hasMultiIds = scope?.DocumentIds is { Count: > 0 };
        var docKeys = hasMultiIds
            ? scope!.DocumentIds!.Select(DocumentChunkPayloadEncoding.EncodeDocumentId).ToArray()
            : null;
        var singleDocKey = (!hasMultiIds && scope?.DocumentId is { } single)
            ? DocumentChunkPayloadEncoding.EncodeDocumentId(single)
            : null;
        var docTypeCode = scope?.DocumentTypeCode;
        var minScore = scope?.MinScore ?? _vectorStoreOptions.MinScore;

        // Filter built once with closure-captured values. Tenant is always required
        // (the LLM cannot override it via tool arguments — that's the fail-closed
        // boundary, the same one the old IDocumentKnowledgeIndex enforced).
        System.Linq.Expressions.Expression<Func<DocumentChunkRecord, bool>> filter = r =>
            r.TenantId == tenantKey
            && (docKeys == null || docKeys.Contains(r.DocumentId))
            && (singleDocKey == null || r.DocumentId == singleDocKey)
            && (docTypeCode == null || r.DocumentTypeCode == docTypeCode);

        var collection = await _collectionProvider.GetAsync(cancellationToken);

        var results = collection.SearchAsync(
            queryVector,
            top: recallTopK,
            new VectorSearchOptions<DocumentChunkRecord>
            {
                Filter = filter,
                VectorProperty = r => r.Embedding,
            },
            cancellationToken);

        var hits = new List<DocumentChunkSearchHit>();
        await foreach (var result in results.WithCancellation(cancellationToken))
        {
            if (minScore.HasValue && result.Score is double s && s < minScore.Value)
            {
                continue;
            }

            var hit = MapToHit(result);
            if (hit != null)
            {
                hits.Add(hit);
            }
        }

        if (!rerank || hits.Count <= finalTopK)
        {
            return hits.Take(finalTopK).ToList();
        }

        var candidates = hits
            .Select(r => new RerankCandidate(r.Text, r.Score ?? 0.0, r))
            .ToList();

        var reranked = await _rerankWorkflow.RerankAsync(
            query,
            candidates,
            finalTopK,
            cancellationToken);

        return reranked
            .Select(r => (DocumentChunkSearchHit)r.Candidate.Tag!)
            .ToList();
    }

    // DocumentChunkRecord stores DocumentId as a string (D-format Guid) for backwards
    // compatibility with the previous Qdrant payload. We parse it once at the adapter
    // boundary so downstream code (citations, rerank) sees a typed Guid.
    private static DocumentChunkSearchHit? MapToHit(VectorSearchResult<DocumentChunkRecord> result)
    {
        var record = result.Record;
        if (record == null || !Guid.TryParse(record.DocumentId, out var docId))
        {
            return null;
        }

        return new DocumentChunkSearchHit
        {
            Id = record.Id,
            DocumentId = docId,
            DocumentTypeCode = record.DocumentTypeCode,
            ChunkIndex = record.ChunkIndex,
            Text = record.Text,
            PageNumber = record.PageNumber,
            Score = result.Score,
        };
    }

    /// <summary>
    /// Creates an <see cref="AIFunction"/> named <paramref name="functionName"/> that exposes
    /// vector search as an LLM-callable tool. Accepts an optional <c>documentIds</c> parameter
    /// so the LLM can restrict the search to documents returned by earlier tool calls
    /// (e.g. <c>search_contracts</c> → <c>search_paperbase_documents</c>).
    ///
    /// <para>
    /// The returned function logs its call arguments and latency at Information level and
    /// sets <paramref name="capture"/> so that citations remain available after the turn.
    /// </para>
    /// </summary>
    // Arch review A3: internal because ChatToolContext is internal — only
    // ChatAppService (same assembly) builds this function. `virtual` is kept so test
    // subclasses (InternalsVisibleTo grants the test assembly access) can override.
    internal virtual AIFunction CreateSearchFunction(
        Guid? tenantId,
        DocumentSearchScope? baseScope,
        DocumentSearchCapture capture,
        ChatToolContext toolContext,
        Telemetry.ChatToolFactory toolFactory,
        string functionName,
        string functionDescription)
    {
        var binding = new SearchFunctionBinding(this, tenantId, baseScope, capture);
        return toolFactory.Create(
            toolContext,
            binding.InvokeAsync,
            name: functionName,
            description: functionDescription,
            // Issue #116: never surface the LLM-rewritten `query` (PII risk per
            // .claude/rules/doc-chat-anti-patterns.md reverse example C #4). Show
            // structural intent only — type filter and how many docs are being
            // drilled into.
            progressDescriber: SummarizeProgress);
    }

    private static string SummarizeProgress(IReadOnlyDictionary<string, object?> arguments)
    {
        var documentTypeCode = TryGetString(arguments, "documentTypeCode");
        var documentIdsCount = TryGetCollectionCount(arguments, "documentIds");

        if (documentIdsCount > 0)
        {
            return $"正在 {documentIdsCount} 份候选文档中向量检索…";
        }

        if (!string.IsNullOrEmpty(documentTypeCode))
        {
            return $"正在按文档类型 \"{documentTypeCode}\" 向量检索…";
        }

        return "正在跨全库向量检索…";
    }

    private static string? TryGetString(IReadOnlyDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int TryGetCollectionCount(IReadOnlyDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        if (value is System.Collections.ICollection coll)
        {
            return coll.Count;
        }

        if (value is System.Collections.IEnumerable seq)
        {
            var count = 0;
            foreach (var _ in seq) count++;
            return count;
        }

        return 0;
    }

    // ── nested helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Holds the bound context for the <c>search_paperbase_documents</c> AIFunction.
    /// Factored into a class so parameter-level <see cref="DescriptionAttribute"/>s are
    /// accessible via reflection (lambda parameters cannot carry attributes in C#).
    /// </summary>
    private sealed class SearchFunctionBinding
    {
        private readonly DocumentTextSearchAdapter _adapter;
        private readonly Guid? _tenantId;
        private readonly DocumentSearchScope? _baseScope;
        private readonly DocumentSearchCapture _capture;

        public SearchFunctionBinding(
            DocumentTextSearchAdapter adapter,
            Guid? tenantId,
            DocumentSearchScope? baseScope,
            DocumentSearchCapture capture)
        {
            _adapter = adapter;
            _tenantId = tenantId;
            _baseScope = baseScope;
            _capture = capture;
        }

        public async Task<string> InvokeAsync(
            [Description("Search query text — describe what information you are looking for. Will be embedded for vector similarity search.")]
            string query,
            [Description("Optional document IDs to restrict the search. Pass IDs returned by other tools (e.g. search_contracts) to focus the RAG search on specific documents — do not invent IDs from raw user input.")]
            Guid[]? documentIds = null,
            [Description("Optional document type code to restrict the search to a single type (e.g. 'contract.general', 'receipt.general'). Useful for cross-document reconciliation when narrowing to receipts/invoices/etc.")]
            string? documentTypeCode = null,
            [Description("Number of top chunks to return. Default 5; raise to 10–15 for cross-document reconciliation when broader recall helps.")]
            int? topK = null,
            [Description("Minimum cosine similarity in [0,1] for hits to be returned. Default 0.45; raise for strict-match queries, lower for cross-language / proper-noun lookups.")]
            double? minScore = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            // Issue #100: the previous "single-document conversation" boundary that
            // ignored model-supplied documentIds is gone. ChatConversation no longer
            // pins a DocumentId / DocumentTypeCode at the scope level, so the model is
            // free (and encouraged — see ChatInstructionsBuilder.MultiStepReasoningGuidance)
            // to widen / focus the search per turn. The real safety boundary is _tenantId
            // (closure-captured from the conversation aggregate, threaded into the
            // filter expression by SearchVectorAsync) — that the LLM cannot
            // override via tool arguments.
            var scope = new DocumentSearchScope
            {
                DocumentIds = documentIds is { Length: > 0 } ? documentIds : null,
                DocumentTypeCode = string.IsNullOrWhiteSpace(documentTypeCode)
                    ? _baseScope?.DocumentTypeCode
                    : documentTypeCode,
                TopK = topK ?? _baseScope?.TopK,
                MinScore = minScore ?? _baseScope?.MinScore
            };

            var vectorResults = await _adapter.SearchVectorAsync(_tenantId, scope, query, cancellationToken);
            _capture.Append(vectorResults);

            sw.Stop();
            // Argument hashing + audit are recorded by AuditedChatFunction; do not
            // log the raw `query` here — it usually contains the user's natural-language
            // input or LLM rephrasing thereof, which can include PII.
            _adapter._logger.LogInformation(
                "doc-chat search_paperbase_documents queryLength={Length} documentIds={Ids} type={TypeCode} topK={TopK} minScore={MinScore} results={Count} latency={Latency}ms",
                query?.Length ?? 0,
                documentIds == null ? "(none)" : string.Join(",", documentIds),
                scope.DocumentTypeCode ?? "(none)",
                scope.TopK,
                scope.MinScore,
                vectorResults.Count,
                sw.ElapsedMilliseconds);

            return _adapter.FormatSearchContext(vectorResults);
        }
    }
}
