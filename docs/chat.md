# Chat

Paperbase exposes a conversational endpoint that lets users ask questions over their document corpus. The chat runs as a MAF `ChatClientAgent` with retrieval-augmented generation: each turn pulls relevant chunks from the [vector store](vectors.md), feeds them into the prompt, and returns a grounded answer with citations.

This page covers the chat as a *feature* — what it does, how to tune it, and what knobs are safe to flip. For end-to-end HTTP request/response shapes (idempotency, retry, error handling), see [chat-client.md](chat-client.md).

## What it can do

- **Anchor-driven, not scope-locked.** A conversation may carry a `documentId` — the document the user opened when starting the chat. This is treated as a **soft anchor** in the system prompt (`id` + `documentTypeCode` only — never the title), not a retrieval constraint. The model is free to (and encouraged to) search across other documents and types, follow `DocumentRelation` edges, and reconcile across the corpus. A conversation without `documentId` behaves the same way, just without the anchor hint.
- **Citations.** Every answer carries the chunk(s) that grounded it. The agent prompt enforces `[chunk N]` citations and the result is post-processed into a structured `citations` array (document id, chunk index, snippet, source name). Citations from multiple `search_paperbase_documents` invocations within one turn are **appended and de-duplicated** — never overwritten — and capped at `MaxCapturedCitations`.
- **Tool calling.** The agent always sees the same flat toolset (built-in tools + every business module's contributor tools). See the [Tools](#tools) section below for the catalog and the fail-closed contract every tool follows.
- **Streaming.** The HTTP API exposes both a buffered `POST .../messages` and a Server-Sent-Events `POST .../messages/stream` endpoint. The streaming endpoint emits `PartialText`, `ToolCallStarted`, `ToolCallCompleted`, and a terminal `Done` (or `Error`) delta — see [client guide → Streaming](chat-client.md#3-stream-a-message-server-sent-events) for the protocol.
- **Idempotent turns.** The client generates a `clientTurnId` per turn; replays with the same id never re-invoke the model. Idempotency applies to both the buffered and streaming endpoints.
- **Optional LLM rerank.** Off by default. When enabled, retrieval recall is expanded `RecallExpandFactor`× and the chat model rescues the most relevant `TopK` before the answer prompt.

## Permissions

| Permission | Grants |
|---|---|
| `Paperbase.Chat` | Read own conversations and messages (default) |
| `Paperbase.Chat.Create` | Create a new conversation |
| `Paperbase.Chat.SendMessage` | Send a message in an existing conversation |
| `Paperbase.Chat.Delete` | Delete an owned conversation |

A user holding only `Paperbase.Chat` can read but not act. Tool contributors enforce their own per-feature permissions on top of this — for example `search_contracts` requires `Contracts.Default` even though the chat permission is already held.

## Configuration

Chat-related knobs live in `PaperbaseAIBehavior` alongside the other Application-layer behavior settings (see [ai-provider.md](ai-provider.md) for the full split between `PaperbaseAI` provider wiring and `PaperbaseAIBehavior` behavior knobs).

```json
"PaperbaseAIBehavior": {
  "EnableLlmRerank": false,
  "RecallExpandFactor": 4,
  "ChatTopK": 5,
  "ChatMinScore": 0.45,
  "MaxCapturedCitations": 50,
  "MaxToolsPerTurn": 0
}
```

| Key | Default | Description |
| --- | --- | --- |
| `EnableLlmRerank` | `false` | When enabled, document chat retrieves an expanded candidate set, asks the chat model to rerank chunks by question relevance, and injects only the final `TopK` into the answer prompt. Off by default to conserve tokens; enable when retrieval quality is the bottleneck (often in mixed-language corpora). |
| `RecallExpandFactor` | `4` | Multiplier applied to `ChatTopK` (or `PaperbaseVectorStore:DefaultTopK`) before LLM rerank. With the defaults `topK=5` × `4` = 20 candidates rescored. |
| `ChatTopK` | `5` | Default top-K passed to `search_paperbase_documents` when the model does not specify it. The model can override per call (e.g. raise to 10–15 for cross-document reconciliation). |
| `ChatMinScore` | `0.45` | Default normalized cosine threshold for document chat RAG searches when the model does not specify `minScore`. Intentionally lower than `PaperbaseVectorStore:MinScore` to improve recall for cross-language questions and proper-noun lookups. |
| `MaxCapturedCitations` | `50` | Hard upper bound on the number of distinct citations a single turn may accumulate across all `search_paperbase_documents` calls. When the cap is hit, additional results are dropped and `CitationsTrimmed = true` is recorded on the audit row. Defends against prompt-injection-driven citation bombs. |
| `MaxToolsPerTurn` | `0` (unlimited) | Soft cap on the number of contributor tools exposed to the agent per turn. `0` means no cap. When the cap is exceeded, the dispatcher prefers tools whose contributor `DocumentTypeCode` matches the conversation anchor and trims the rest, recording `ToolsTrimmed = true`. Leave at `0` until business modules genuinely outgrow the model's tool-list comprehension. |

The agent uses `ChatToolMode.Auto` — the model picks when (and with what `documentIds` / `documentTypeCode` / `topK` / `minScore`) to invoke each tool. There is no operator switch for "always retrieve before answering" — see *When the answer is degraded* below for the honest-signal contract that replaced it.

Every business-module skill (e.g. `search-contracts`) is advertised on every turn regardless of conversation anchor — there is no per-conversation filter. The chat agent picks which skill (if any) to load based on the user's question. Do not rely on the conversation anchor for authorization — each skill script body re-asserts the relevant feature permission (see *Adding a skill* below).

The hard cap on tool-call rounds within a single turn is configured at host wiring time via `PaperbaseAI:MaxToolIterations` (default `10`); see [ai-provider.md → Provider wiring](ai-provider.md#provider-wiring-paperbaseai). For prompt language behavior, see [ai-provider.md → Cross-cutting LLM behavior](ai-provider.md#cross-cutting-llm-behavior-paperbaseaibehavior). For retrieval `topK` / `minScore` defaults, see [vectors.md](vectors.md).

## Tools and skills

The chat agent exposes capabilities in two shapes:

- **Always-available tools** — high-frequency primitives registered directly on the agent's `ChatOptions.Tools`. There is exactly one: `search_paperbase_documents`. Every content-class question routes through it, so paying a `load_skill` round-trip is wasted.
- **Agent skills** — domain capabilities advertised by [MAF `AgentSkillsProvider`](https://learn.microsoft.com/agent-framework/agents/skills) ([open spec](https://agentskills.io)) using progressive disclosure. Each skill is advertised in ~100 tokens; the LLM calls `load_skill("name")` only when relevant, then `run_skill_script("name", "invoke", args)` to execute the script body.

The model picks when, in what order, and with what arguments to invoke each — there is no scripted workflow.

### Always-available tool

| Tool | What it does | Notes |
|---|---|---|
| `search_paperbase_documents` | Semantic vector search over the user's documents. The model supplies a natural-language `query`; optional `documentIds[]`, `documentTypeCode`, `topK`, `minScore` parameters let it drill in after a skill round. | Tenant-scoped at the binding layer (`_tenantId` is captured in the closure, not derived from `DataFilter`). Defaults from `PaperbaseAIBehavior:ChatTopK` / `:ChatMinScore`. Citations from all calls in one turn are appended + deduplicated up to `MaxCapturedCitations`. |

### Core skills (inline, owned by the platform)

| Skill | Scripts | What it does |
|---|---|---|
| `get-document-relations` | `invoke(documentId)` | Bidirectional lookup over the `DocumentRelation` aggregate — returns documents linked to a given `documentId` (manual + AI-discovered edges). Ordered by source (`Manual` first) then by `Confidence` desc, capped at 20 per call. Powers cross-document reasoning chains like contract → matching invoices. See [relation-discovery.md](relation-discovery.md) for how the underlying graph is populated. |
| `document-inspection` | `outline(documentId)` / `excerpt(documentId, query)` | Precise-navigation complement to vector search: `outline` returns the Markdown heading tree (level + title + line number) of a single document — body text omitted, capped at 50 headers per call. `excerpt` is exact-substring grep with 2 lines of surrounding context, capped at 5 matches per call. Vector embeddings underweight precise tokens (contract numbers, invoice IDs, dates, proper nouns); this skill is the literal-match path. |

Both core skills require `Paperbase.Documents.Default` (re-asserted inside each script). Cross-tenant hits collapse to `not_found` so existence is not leaked.

### Business-module skills

Every business module that wants to expose structured queries to the agent ships one or more `AgentClassSkill<TSelf>` subclasses (registered as `ITransientDependency` with `[ExposeServices(typeof(AgentSkill))]`). Reference: the contracts module contributes a single `contracts` skill with three scripts:

| Skill | Scripts | What it does |
|---|---|---|
| `contracts` | `search` / `get-detail` / `aggregate` | `search`: list contracts by counterparty / number / date / amount range. `get-detail`: fetch one contract's full extracted field set by ID. `aggregate`: sum amounts and counts grouped by currency. See [`ContractsSkill.cs`](../modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractsSkill.cs). |

> **Granularity guideline**: bundle scripts under one skill when they share a domain (same aggregate root, same auth, same chaining patterns). Split into separate skills when intents diverge enough that one SKILL.md cannot cover all of them. Future contract operations (export / compare / lifecycle) become new `[AgentSkillScript]` methods on `ContractsSkill`, not new skill classes.

See [Adding a skill](#adding-a-skill-business-modules) for the contract every new skill must follow.

### The fail-closed contract

Every script body — core skill, business-module skill, or the always-available `search_paperbase_documents` tool — must satisfy the same three rules, because the LLM (not the HTTP authorization filter) decides when to call it. HTTP-level `[Authorize]` on the AppService does **not** cover these calls.

1. **Explicit permission check** via `IAuthorizationService.CheckAsync(...)`. The Chat permission alone is insufficient — each script re-asserts the feature permission relevant to its data.
2. **Explicit tenant predicate** in the query (`Where(x => x.TenantId == tenantId)`). Do not rely on ABP's ambient `DataFilter` — any code path that disables it would silently leak across tenants.
3. **Hard `Take(N)` row cap** to defend against prompt-injection-driven recall bombs.

Skill scripts resolve `IAuthorizationService` / `ICurrentTenant` / repositories per call via the `IServiceProvider` parameter that MAF auto-injects (see `core/src/Dignite.Paperbase.Application/Chat/Tools/DocumentRelationsTool.InvokeAsync` for the pattern). User-derived free-text fields in the JSON return value are wrapped via `PromptBoundary.WrapField(...)`.

Reverse examples and the rationale: [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md), reverse examples C and D.

### Tool-call progress description

The streaming endpoint emits a `ToolCallStarted` delta when the model fires a tool. The label is currently **structural only**: for the always-available `search_paperbase_documents` tool, the binding's `progressDescriber` (e.g. "正在跨全库向量检索…") is used; for skill calls (which surface as MAF's `run_skill_script` / `load_skill` meta-tools) the fallback `"正在执行 {toolName}…"` is rendered. The describer must **never echo raw model arguments** — that would leak data before any per-skill permission check has fired (see [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) reverse example C #4).

## Citation-to-source navigation

`ChatCitationDto` is the UI-facing citation contract. Citation navigation is Markdown-only: every source document type is handled through the extracted `Document.Markdown`, not through a PDF/image/original-file viewer.

| Field | Navigation meaning |
| --- | --- |
| `documentId` | The source document to open. A citation click must navigate to this document even when the active conversation is scoped by `documentTypeCode` and the cited document is not currently displayed. |
| `snippet` | Primary Markdown positioning key. Search the current document Markdown for this text and highlight the first matching range when possible. |
| `chunkIndex` | Optional vector-store chunk ordinal for display/debug context only. It is not a stable Markdown anchor after re-embedding and must not drive positioning by itself. |
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

## Grounding source and degraded answers

Every turn carries a `groundingSource` enum on `ChatTurnResultDto` describing what kind of evidence the answer rests on:

| `groundingSource` | Meaning | `isDegraded` |
|---|---|---|
| `None` (0) | The model produced an answer **without invoking any tool** — no search, no contributor tool. Falls back to conversation history and the model's parametric knowledge. | `true` |
| `Vector` (1) | The model invoked `search_paperbase_documents` (and/or other vector-backed retrieval) at least once. Retrieved chunks are reflected in `citations`. | `false` |
| `Structured` (2) | The model invoked only structured/contributor tools (`search_contracts`, `get_contract_aggregate`, `get_document_relations`, …). The answer is grounded in business data, often without text citations. | `false` |
| `Mixed` (3) | The model invoked both vector search and structured tools in the same turn. | `false` |

`isDegraded` is therefore equivalent to `groundingSource == None`. The classification is performed by inspecting the turn's tool-call audit trail in `ChatTelemetryRecorder` — there is no separate flag the model can lie about.

| Cause | What happened | What to do |
|---|---|---|
| Knowledge index unavailable | `IDocumentKnowledgeIndex.SearchAsync` threw — Qdrant down, network fault, etc. The model may still call other tools or fall back to history. | Treat as a transient infrastructure incident. If the model still called structured tools, the turn is `Structured`, not `None`. |
| Model declined to invoke any tool | The model judged the question answerable without retrieval (greetings, follow-up clarifications). | Accept it: an empty `citations` array with `groundingSource = None` and `isDegraded = true` is the honest signal. If a class of questions is consistently answered without tool calls where you want them grounded, tighten the QA system prompt in `DefaultPromptProvider` rather than forcing pre-injection. |

`isDegraded` and `groundingSource` are both surfaced on the API response so the UI can show a "no sources used" banner or a "answered from contract data" badge.

## Auto-generated conversation titles

When the **first** message of a conversation arrives — defined as `ChatConversation.Messages.Count == 0` and the title still equal to the `Chat:UntitledConversation` localization placeholder — `ChatAppService.TryGenerateAndApplyTitleAsync` fires a small extra LLM call right after the main turn finishes. Its job is to produce a 2–3 word conversation title from `(user message, assistant answer)` so the UI sidebar shows something more informative than "Untitled conversation". The result is written via `conversation.Rename(title)` in the same unit of work.

| Property | Value |
|---|---|
| Chat client | **Separate keyed registration** under `PaperbaseAIConsts.TitleGeneratorChatClientKey` — no `UseFunctionInvocation`, no `UseDistributedCache`. Lets the title path be observed independently and lets hosts pick a different (smaller / cheaper) `TitleGeneratorModelId` if desired. Falls back to `ChatModelId` when unset. |
| Prompt | The user's message and the assistant's reply, wrapped in `PromptBoundary` envelopes |
| Instructions | `IPromptProvider.GetConversationTitlePrompt(DefaultLanguage)` — asks for a short title only |
| Token cost | ~200 input / ~5 output per turn (verified against SiliconFlow billing) |
| Failure mode | Try/catch around the whole call — if the LLM errors out, the conversation just keeps its "Untitled" title; no user-visible failure |

On the OTel trace, the title call shows up as a simple `chat <model>` span as a sibling of the main `orchestrate_tools` span — **not nested inside it**, **not wrapped in another `orchestrate_tools`**. That separation makes per-call cost attribution straightforward.

### Disabling or replacing

Override `ShouldGenerateTitle` to return `false` (in a host-level subclass) to skip auto-titling entirely. Override `TryGenerateAndApplyTitleAsync` to plug in a different naming strategy (e.g. take the first 30 chars of the user message verbatim, no LLM call). Both methods are `protected virtual` for that reason.

## Observability

Beyond the OpenTelemetry signals from `Microsoft.Extensions.AI` (see [ai-provider.md → OpenTelemetry signals](ai-provider.md#opentelemetry-signals)), each turn enriches the ABP audit row through `ChatTelemetryRecorder`. Two structured payloads are attached to `AbpAuditLogs.ExtraProperties`:

| Property key | Shape | Meaning |
| --- | --- | --- |
| `Chat.Turn` | `ChatTurnAuditEntry` (single object) | One per turn. Carries `ConversationId`, `Streaming`, `CitationCount`, `IsDegraded`, `ElapsedMs`, `Outcome`, plus the dimensions in the table below. |
| `Chat.ToolCalls` | `IReadOnlyList<ChatToolAuditEntry>` | One entry per tool invocation, in call order. Carries `ToolName`, `ArgumentsSummary` (sanitised — never raw user input), `ResultSizeBytes`, `ElapsedMs`, `Outcome`, `ExceptionType`. |

Notable fields on `Chat.Turn`:

| Field | Type | Meaning |
| --- | --- | --- |
| `GroundingSource` | enum | `None` / `Vector` / `Structured` / `Mixed` — derived by `ClassifyGrounding` from the per-tool entries, not a flag the model can set |
| `ToolCallDepth` | `int` | Total tool invocations in this turn (sum of `ToolCallSummary` values; includes failed invocations because they reflect actual model behaviour) |
| `ToolCallSummary` | `Dictionary<string,int>` | Per-tool invocation count. Direct tools appear under their registered name; skill scripts appear under `skill:<skill-name>/<script-name>`. Example: `{ "search_paperbase_documents": 2, "skill:contracts/get-detail": 1 }`. MAF skill meta-tools (`load_skill` / `read_skill_resource`) also appear when invoked, but are filtered out of `GroundingSource` classification (loading instructions ≠ grounding evidence). |
| `CitationsTrimmed` | `bool` | `true` if `MaxCapturedCitations` was hit and additional vector-search hits were dropped |
| `AnchorResolutionFailed` | `bool` | `true` if the conversation has a `documentId` but the per-turn anchor lookup degraded (document deleted, tenant mismatch, or caller lost `Documents.Default`). The turn proceeds **without** the anchor hint — never throws 404. |

These dimensions are what drives the upgrade decision in the future: if production telemetry shows `ToolCallDepth > 8` for ≥ 20% of turns, that is the trigger to evaluate planner sub-agents (Magentic Orchestration). Until then, the single `ChatClientAgent` + flat tool list is intentionally kept simple — see [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md) reverse example D for the rationale against premature `AsAIFunction()` sub-agents.

## Adding a skill (business modules)

To let the chat answer business-domain questions ("show contracts with Acme Corp expiring this quarter"), a business module ships one or more [MAF Agent Skills](https://learn.microsoft.com/agent-framework/agents/skills) ([open spec](https://agentskills.io)) — Paperbase consumes the framework primitive directly, no custom contributor abstraction.

The pattern is one **domain capability package** per skill class — typically one skill per business module aggregate, exposing one or more named scripts:

```csharp
[ExposeServices(typeof(AgentSkill))]
public sealed class InvoicesSkill : AgentClassSkill<InvoicesSkill>, ITransientDependency
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        "invoices",
        "Search, inspect, and aggregate invoices. Scripts: search, get-detail, aggregate. Use whenever the user asks anything about invoices.");

    protected override string Instructions => """
        Use this skill for any invoice-domain question — listing, looking up one
        specific invoice's details, or counting / summing across the set.

        Scripts:
        - `search` — find invoices by structured filters. Optional parameters; pass
          only those implied by the user's question. Returns documentIds + metadata.
        - `get-detail` — fetch full extracted fields by document ID.
        - `aggregate` — counts + totals grouped by currency, for arithmetic questions.

        ...chaining patterns, empty-result fallback advice, prompt-boundary notes...
        """;

    [AgentSkillScript("search")]
    [Description("Search invoices by structured criteria.")]
    private static async Task<string> SearchAsync(
        IServiceProvider serviceProvider,
        [Description("Vendor name (partial match).")] string? vendorName = null,
        /* other [Description] params */
        CancellationToken cancellationToken = default)
    {
        // Fail-closed: explicit auth + explicit tenant predicate + bounded result set.
        var authSvc = serviceProvider.GetRequiredService<IAuthorizationService>();
        await authSvc.CheckAsync(InvoicePermissions.Default);

        var currentTenant = serviceProvider.GetRequiredService<ICurrentTenant>();
        var repo = serviceProvider.GetRequiredService<IInvoiceRepository>();
        var q = (await repo.GetQueryableAsync()).Where(i => i.TenantId == currentTenant.Id);

        var rows = await /* IAsyncQueryableExecuter.ToListAsync */ (q.Where(...).Take(MaxResultRows));

        // User-derived free-text fields wrapped before serialization.
        return JsonSerializer.Serialize(new
        {
            rows = rows.Select(r => new
            {
                r.Id, r.Amount,
                vendorName = PromptBoundary.WrapField(r.VendorName),
                description = PromptBoundary.WrapField(r.Description)
            })
        });
    }

    // Additional [AgentSkillScript("get-detail")] / [AgentSkillScript("aggregate")]
    // methods on the same class — they share the Frontmatter + Instructions + helpers.
}
```

Three rules, each enforced at PR review:

1. **`Frontmatter` and `Instructions` are static** — never interpolate user-controlled text. Both feed directly into the LLM context (Frontmatter advertised every turn, Instructions loaded via `load_skill`).
2. **Each script method is fail-closed**: explicit `IAuthorizationService.CheckAsync(...)` for the feature permission + explicit `Where(x => x.TenantId == currentTenant.Id)` (do not rely on ABP's ambient `DataFilter`) + a hard `Take(N)` row cap. **Optional filter parameters must have `= null` defaults** so `AIFunctionFactory`'s JSON schema marks them optional, not required — put `IServiceProvider` first in the parameter list to satisfy C#'s default-value ordering rule.
3. **No raw SQL.** Compose queries via `IRepository<T>.GetQueryableAsync()` so all framework filters (soft-delete, tenant, audit) stay in effect. Wrap user-derived free-text fields via `PromptBoundary.WrapField(...)` before serialization.

ABP auto-registers the skill via `[ExposeServices(typeof(AgentSkill))]` + `ITransientDependency`; `ChatAppService` collects every registered `AgentSkill` into a single MAF `AgentSkillsProvider` per turn — no module-side wiring needed beyond the class itself.

**Resolution rule**: skills are consumed via `IEnumerable<AgentSkill>`, never by concrete type. `[ExposeServices(typeof(AgentSkill))]` defaults to `IncludeSelf = false`, so `GetRequiredService<InvoicesSkill>()` will throw — by design. Tests that want to inspect the skill in isolation should `new` it directly (the constructor is parameterless; services are resolved per-script-call via the `IServiceProvider` parameter).

**Granularity guideline**: bundle scripts under one skill when they share a domain (same aggregate root, same auth, same chaining patterns). Split into separate skills only when intents diverge enough that one SKILL.md cannot cover them all. The contracts module is the canonical example: one `ContractsSkill` with `search` / `get-detail` / `aggregate` scripts — they all operate over the `Contract` aggregate with the same `ContractsPermissions.Contracts.Default` permission. Future contract operations (export / compare / lifecycle) become new `[AgentSkillScript]` methods on the same class.

Reference implementations: [`ContractsSkill.cs`](../modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractsSkill.cs) + [`ContractSkillHelpers.cs`](../modules/contracts/src/Dignite.Paperbase.Contracts.Application/Chat/ContractSkillHelpers.cs). Counter-examples and the rationale: [`.claude/rules/doc-chat-anti-patterns.md`](../.claude/rules/doc-chat-anti-patterns.md).

## See also

- [HTTP client guide](chat-client.md) — request/response shapes, idempotency, 409 retry pattern
- [Vector store](vectors.md) — what backs retrieval
- [Embedding pipeline](embedding.md) — where chunks come from
- [Relation discovery](relation-discovery.md) — populates the `DocumentRelation` graph the chat agent reaches via `get_document_relations`
