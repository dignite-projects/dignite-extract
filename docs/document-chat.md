# Document Chat

Paperbase exposes a conversational endpoint that lets users ask questions over their document corpus. The chat runs as a MAF `ChatClientAgent` with retrieval-augmented generation: each turn pulls relevant chunks from the [knowledge index](knowledge-index.md), feeds them into the prompt, and returns a grounded answer with citations.

This page covers the chat as a *feature* — what it does, how to tune it, and what knobs are safe to flip. For end-to-end HTTP request/response shapes (idempotency, retry, error handling), see [document-chat-client.md](document-chat-client.md).

## What it can do

- **Conversation-scoped retrieval.** A conversation can be unscoped (search across all the user's documents), scoped to a `documentTypeCode` (e.g. only contracts), or scoped to a single `documentId`.
- **Citations.** Every answer carries the chunk(s) that grounded it. The agent prompt enforces `[chunk N]` citations and the result is post-processed into a structured `citations` array (document id, chunk index, snippet, source name).
- **Tool calling.** Business modules contribute structured query tools through `IDocumentChatToolContributor`. Example: a `ContractChatToolContributor` can let the model call `search_contracts` to filter by counterparty or amount, alongside the built-in `search_paperbase_documents` semantic-search tool. Each tool runs **fail-closed**: explicit permission check + explicit tenant predicate + result-row cap inside the tool body. See [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) for the contract.
- **Idempotent turns.** The client generates a `clientTurnId` per turn; replays with the same id never re-invoke the model.
- **Optional LLM rerank.** Off by default. When enabled, retrieval recall is expanded `RecallExpandFactor`× and the chat model rescues the most relevant `TopK` before the answer prompt.

## Permissions

| Permission | Grants |
|---|---|
| `Paperbase.Documents.Chat` | Read own conversations and messages (default) |
| `Paperbase.Documents.Chat.Create` | Create a new conversation |
| `Paperbase.Documents.Chat.SendMessage` | Send a message in an existing conversation |
| `Paperbase.Documents.Chat.Delete` | Delete an owned conversation |

A user holding only `Paperbase.Documents.Chat` can read but not act. Tool contributors enforce their own per-feature permissions on top of this — for example `search_contracts` requires `Contracts.Default` even though the chat permission is already held.

## Configuration

Chat-related knobs live in `PaperbaseAIBehavior` alongside the other Application-layer behavior settings (see [ai-provider.md](ai-provider.md) for the full split between `PaperbaseAI` provider wiring and `PaperbaseAIBehavior` behavior knobs).

```json
"PaperbaseAIBehavior": {
  "EnableLlmRerank": false,
  "RecallExpandFactor": 4,
  "DocumentChatMinScore": 0.45
}
```

| Key | Default | Description |
| --- | --- | --- |
| `EnableLlmRerank` | `false` | When enabled, document chat retrieves an expanded candidate set, asks the chat model to rerank chunks by question relevance, and injects only the final `TopK` into the answer prompt. Off by default to conserve tokens; enable when retrieval quality is the bottleneck (often in mixed-language corpora). |
| `RecallExpandFactor` | `4` | Multiplier applied to the conversation's `topK` (or `PaperbaseKnowledgeIndex:DefaultTopK`) before LLM rerank. With the defaults `topK=5` × `4` = 20 candidates rescored. |
| `DocumentChatMinScore` | `0.45` | Default normalized cosine threshold for document chat RAG searches when the conversation has no explicit `minScore`. This is intentionally lower than `PaperbaseKnowledgeIndex:MinScore` to improve recall for cross-language questions and proper-noun lookups. Set to `null` to fall back to the knowledge-index default. |

Document chat uses a single MAF tool-calling path: the agent exposes `search_paperbase_documents` (RAG) plus any business-module contributor tools, with `ChatToolMode.Auto` so the model picks when (and with what query / `documentIds`) to invoke them. There is no operator switch for "always retrieve before answering" — see *When the answer is degraded* below for the honest-signal contract that replaced it.

The hard cap on tool-call rounds within a single turn is configured at host wiring time via `PaperbaseAI:MaxToolIterations` (default `10`); see [ai-provider.md → Provider wiring](ai-provider.md#provider-wiring-paperbaseai). For prompt language behavior, see [ai-provider.md → Cross-cutting LLM behavior](ai-provider.md#cross-cutting-llm-behavior-paperbaseaibehavior). For retrieval `topK` / `minScore` defaults, see [knowledge-index.md](knowledge-index.md). For BM25-augmented hybrid retrieval, see [hybrid-search.md](hybrid-search.md).

## Citation-to-source navigation

`ChatCitationDto` is the UI-facing citation contract. Citation navigation is Markdown-only: every source document type is handled through the extracted `Document.Markdown`, not through a PDF/image/original-file viewer.

| Field | Navigation meaning |
| --- | --- |
| `documentId` | The source document to open. A citation click must navigate to this document even when the active conversation is scoped by `documentTypeCode` and the cited document is not currently displayed. |
| `snippet` | Primary Markdown positioning key. Search the current document Markdown for this text and highlight the first matching range when possible. |
| `chunkIndex` | Optional knowledge-index chunk ordinal for display/debug context only. It is not a stable Markdown anchor after re-embedding and must not drive positioning by itself. |
| `sourceName` | Display label only. Do not parse it for routing or positioning. |

Fallback order:

1. If `documentId` is missing or the document cannot be loaded, keep the citation as non-navigable display text.
2. Open `documentId` in the chat source pane and render the persisted Markdown.
3. Try to locate `snippet` in the current Markdown and highlight the first matching range.
4. If `snippet` cannot be found, show the Markdown without a highlight and keep `chunkIndex` visible as citation context.

This deliberately does not introduce a separate `DocumentSourceLocation` DTO, PDF page navigation, persisted chunk IDs, or stored character offsets. Add exact offsets only after snippet matching proves insufficient in real use.

**Snippet match is whole-document `indexOf`.** The first occurrence of the snippet in the persisted Markdown is highlighted. Re-extracting the document with a different OCR run can shift the persisted Markdown enough that the snippet no longer matches; the UI surfaces this as a visible warning without breaking the chat.

### Developer notes

The source pane is intentionally AI-first. It must render the same Markdown artifact that retrieval, embedding, reranking, and citation snippets are based on. Do not add a parallel PDF/image/original-file source pane for citation navigation.

The original file is still available through the document detail experience, but that is an auxiliary inspection action. A chat citation click must stay on the Markdown source path: load `documentId`, render `Document.Markdown`, then try to highlight `snippet`.

`pageNumber` is not part of the navigation contract. The DTO may still carry it for backward compatibility or future metadata, but new UI and server code must not branch to a PDF viewer, append `#page=N`, or introduce `preferredView: 'pdf' | 'markdown'` based on it.

`chunkIndex` is not a durable anchor. Re-extraction, re-chunking, embedding option changes, or model changes can shift chunk ordinals. Use it in labels, diagnostics, and logs only; never make it the sole positioning key.

Do not add `preferredView` to server DTOs. There is only one citation source view today: Markdown. If exact positioning becomes necessary later, prefer adding Markdown offsets or persisted source ranges after measuring snippet-match failures in production data.

## When the answer is degraded

`ChatTurnResultDto.IsDegraded = true` flags answers that ran without retrieval grounding. Two cases produce it:

| Cause | What happened | What to do |
|---|---|---|
| Knowledge index unavailable | `IDocumentKnowledgeIndex.SearchAsync` threw — Qdrant down, network fault, etc. | Treat as a transient infrastructure incident. The model still produced an answer using only conversation history. |
| Model declined to invoke `search_paperbase_documents` | The model judged the question answerable without retrieval (greetings, follow-up clarifications, contributor-tool answers that don't need RAG). | Accept it: citations reflect what the model *actually used*; an empty list with `IsDegraded = true` is the honest signal. If a class of questions is consistently answered without search where you want it grounded, tighten the QA system prompt in `DefaultPromptProvider` rather than forcing pre-injection. |

`isDegraded` is surfaced to the API client so UIs can show a "no sources used" banner.

## Adding a tool contributor (business modules)

To let the chat answer business-domain questions ("show contracts with Acme Corp expiring this quarter"), a business module implements `IDocumentChatToolContributor`. Three rules apply, each enforced at PR review:

1. **`ContributeTools` returns `AIFunction`s with static descriptions** — never interpolate user-controlled text into the description (prompt-injection vector).
2. **Each tool method is fail-closed**: explicit `IAuthorizationService.CheckAsync(...)` for the feature permission + explicit `Where(x => x.TenantId == _tenantId)` (do not rely on ABP's ambient `DataFilter`) + a hard `Take(N)` row cap.
3. **No raw SQL.** Compose queries via `IRepository<T>.GetQueryableAsync()` so all framework filters (soft-delete, tenant, audit) stay in effect.

Reference implementation: `modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractChatToolContributor.cs`. Counter-examples and the rationale: [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md).

## See also

- [HTTP client guide](document-chat-client.md) — request/response shapes, idempotency, 409 retry pattern
- [Knowledge index](knowledge-index.md) — what backs retrieval
- [Hybrid search](hybrid-search.md) — BM25 + dense recall fusion
- [Embedding pipeline](embedding.md) — where chunks come from
- [Relation discovery](relation-discovery.md) — populates the `DocumentRelation` graph the chat agent reaches via `get_document_relations`
