# Relation Discovery

When a user uploads a contract, that contract may be related to an earlier framework agreement, several supporting invoices, and a few signed amendments — none of which are obvious from the document itself. Paperbase's **Relation Discovery** pipeline turns those scattered files into a navigable graph automatically: AI proposes the links, the user confirms them, and downstream features (the in-app graph view, the chat agent's `get_document_relations` tool) treat the resulting `DocumentRelation` aggregate as a first-class navigation surface.

This page covers relation discovery as a *feature*: how the two-layer architecture works, how a business module plugs in, what knobs operators can turn, and the operational pitfalls. For low-level orchestration code see `core/src/Dignite.Paperbase.Application/Documents/Pipelines/RelationDiscovery/`. The design rationale (why this shape, what was rejected) lives in [Issue #115](https://github.com/dignite-projects/dignite-paperbase/issues/115); this doc is the operating manual.

## How it works

```
                                         DocumentClassifiedEto
                                                    │
                  ┌─────────────────────────────────┴─────────────────────────────────┐
                  │                                                                   │
        ContractDocumentHandler                                       RelationDiscoveryEventHandler
        (synchronous, extracts                                        (queues a delayed background
         Contract fields, autoSave)                                    job — see "Trigger" below)
                                                                                      │
                                                                                      ▼
                                                                  RelationDiscoveryBackgroundJob
                                                                  (CurrentTenant.Change(args.TenantId))
                                                                                      │
                                                                  ┌───────────────────┴───────────────────┐
                                                                  ▼                                       │
                                                  L2 RelationDiscoveryService                             │
                                                  (fan out across all                                     │
                                                   IDocumentIdentifierProviders;                          │
                                                   structured matches → AiSuggested)                      │
                                                                  │                                       │
                                            L2 found ≥ 1 match? ──┴── no ──► L3 SemanticRelationDiscoveryService
                                                  │                          (vector recall + LLM judge)
                                                  │ yes                      enabled by default? NO — opt-in
                                                  └─► return (skip L3)
```

Two design properties matter:

- **L2 is structured, deterministic, and cheap.** It only matches on identifiers business modules have already extracted into their own typed records (`Contract.ContractNumber`). No LLM call. Confidence is a fixed `0.95` because the match is exact.
- **L3 is semantic, opt-in, and capped.** It runs only when L2 found nothing — giving the system a fallback for documents that share content but no shared identifier. Each candidate pair goes through `RelationInferenceAgent` (a `ChatClientAgent.RunAsync<RelationInferenceResult>`) and must clear `SemanticRelationDiscoveryConfidenceThreshold` (default `0.7`) before becoming an `AiSuggested` row.

Both layers create `DocumentRelation` rows with `Source = AiSuggested`. A user clicks **Confirm** to flip them to `Manual` (clears `Confidence`); deletes them to mark a rejection (`paperbase.relation.suggestion.rejected` counter increments).

## Implementing `IDocumentIdentifierProvider` (business modules)

Each business module that owns a document type contributes one provider:

```csharp
public class ContractIdentifierProvider : IDocumentIdentifierProvider, ITransientDependency
{
    public IReadOnlyCollection<string> SupportedIdentifierTypes { get; } = new[]
    {
        DocumentIdentifierTypes.ContractNumber,   // see "What NOT to expose" below
    };

    public async Task<IReadOnlyList<DocumentIdentifierEntry>> GetIdentifiersAsync(
        Guid documentId, CancellationToken ct = default)
    {
        var contract = await _contractRepository.FindByDocumentIdAsync(documentId);
        if (contract == null) return Array.Empty<DocumentIdentifierEntry>();

        var entries = new List<DocumentIdentifierEntry>();
        if (!string.IsNullOrWhiteSpace(contract.ContractNumber))
            entries.Add(new(DocumentIdentifierTypes.ContractNumber, contract.ContractNumber.Trim()));
        return entries;
    }

    public async Task<IReadOnlyList<Guid>> FindDocumentsAsync(
        string identifierType, string identifierValue, CancellationToken ct = default)
    {
        return identifierType switch
        {
            DocumentIdentifierTypes.ContractNumber
                => (await _contractRepository.FindByContractNumberAsync(identifierValue, ct))
                    .Select(c => c.DocumentId).Distinct().ToList(),
            _ => Array.Empty<Guid>(),  // defensive
        };
    }
}
```

The contract is in `Dignite.Paperbase.Abstractions` so business modules don't need to depend on core Application. Two methods:

- **`GetIdentifiersAsync`** runs first to read the *source* document's identifiers. Return an empty list when the provider doesn't own the document.
- **`FindDocumentsAsync`** runs once per `(type, value)` pair to find peer documents that hold the same identifier. Defensive: return empty for any unsupported type even though L2 already filters by `SupportedIdentifierTypes`.

### What NOT to expose as an identifier

`PartyName` (counterparty, vendor) was originally exposed by `ContractIdentifierProvider` and intentionally removed (codex review fix [#131](https://github.com/dignite-projects/dignite-paperbase/pull/131), [high]). A common vendor that appears in 100 contracts would link them all into a fully-connected graph at confidence `0.95`, polluting both the in-app graph view and the chat agent's `get_document_relations` reasoning.

Rule of thumb for L2: only expose **high-cardinality, near-unique** identifiers — invoice numbers, contract numbers, PO numbers, project codes. Low-cardinality string fields (party names, common dates, status enums) belong to L3, where LLM judgment can distinguish "same vendor, unrelated transaction" from "same vendor, continuation of a deal".

## Trigger semantics

`RelationDiscoveryEventHandler` subscribes to `DocumentClassifiedEto` — the same event business modules use to extract their typed records. To avoid racing those extraction handlers, the L2 background job is **enqueued with a delay** (`PaperbaseAIBehaviorOptions.RelationDiscoveryDelaySeconds`, default `30s`). By the time the worker picks the job up, the contract / invoice / etc. record has already been saved and the provider can read it.

If a business module's extraction takes longer than the delay (e.g. very slow LLM provider), L2 will run with no identifiers and silently complete with zero relations. Tune the delay up rather than wait for retry coverage to land.

Orphan documents — failed classification, or classified into a type no business module owns — never publish `DocumentClassifiedEto`, so L2 never fires for them. They reach the relation graph only when a user manually creates a `DocumentRelation`, or when L3 is enabled and discovers them via vector similarity (see below).

## Configuration

```json
"PaperbaseAIBehavior": {
  "RelationDiscoveryDelaySeconds": 30,
  "EnableSemanticRelationDiscovery": false,
  "SemanticRelationDiscoveryTopK": 5,
  "SemanticRelationDiscoveryMinScore": 0.65,
  "SemanticRelationDiscoveryConfidenceThreshold": 0.7
}
```

| Knob | Default | Notes |
|---|---|---|
| `RelationDiscoveryDelaySeconds` | `30` | Delay before the L2 job is dequeued. Buys time for sibling `DocumentClassifiedEto` handlers to commit their typed records. Set to `0` only in test setups where extraction is synchronous. |
| `EnableSemanticRelationDiscovery` | `false` | Master switch for L3. **Default off** — LLM cost grows linearly with document count and there is no per-tenant budget cap. Operators opt in. |
| `SemanticRelationDiscoveryTopK` | `5` | Vector-recall fan-out before LLM evaluation. Each candidate is one LLM call; raise cautiously. |
| `SemanticRelationDiscoveryMinScore` | `0.65` | Minimum cosine score for a chunk to enter L3 evaluation. Higher than the chat default (`0.45`) on purpose — L3 wants strong matches only, weak matches are noise. |
| `SemanticRelationDiscoveryConfidenceThreshold` | `0.7` | LLM-reported confidence floor. Below this, the LLM's "yes, related" verdict is dropped. The L3 prompt is calibrated to this value (the prompt tells the model "below 0.7 means not sure → set isRelated=false"). |

L2's match confidence is the constant `RelationDiscoveryService.StructuralMatchConfidence = 0.95` and is not configurable — structured matches are by construction deterministic.

## Tenant flow

Both the event handler and the background job explicitly restore tenant context via `using (_currentTenant.Change(...))`:

- **Event handler** wraps with `Change(eventData.TenantId)` so that the scheduler stamps `RelationDiscoveryJobArgs.TenantId` correctly even when the distributed-event bus didn't restore ambient tenant.
- **Background job** wraps `ExecuteAsync` with `Change(args.TenantId)` as defense in depth — providers query through ABP's `IMultiTenant` ambient filter, which silently misses data if the ambient tenant is wrong.

This is the same pattern `ContractDocumentHandler` uses for the same event, and was added explicitly to address codex review finding [high] "Tenant context dropped" (PR [#131](https://github.com/dignite-projects/dignite-paperbase/pull/131)).

## Telemetry

Meter name: `Dignite.Paperbase.Documents.RelationDiscovery`.

| Instrument | Type | Tags | Source |
|---|---|---|---|
| `paperbase.relation_discovery.runs.total` | counter | `result` | every job completion |
| `paperbase.relation_discovery.l2.created` | histogram | — | per-run AiSuggested count from L2 |
| `paperbase.relation_discovery.l3.invoked` | counter | — | when L2 yielded zero and L3 ran |
| `paperbase.relation_discovery.l3.llm_calls` | counter | `result` (`Confirmed` / `Rejected` / `Error`) | per-candidate LLM evaluation |
| `paperbase.relation_discovery.l3.created` | histogram | — | per-run AiSuggested count from L3 |
| `paperbase.relation_discovery.duration` | histogram (ms) | `layer` (`l2` / `l3` / `total`) | per-run wall clock |
| `paperbase.relation.suggestion.confirmed` | counter | `source`, `confidence_bucket` | `IDocumentRelationAppService.ConfirmAsync` |
| `paperbase.relation.suggestion.rejected` | counter | `source`, `confidence_bucket` | `IDocumentRelationAppService.DeleteAsync` |

`tenant_id` is intentionally **not** a tag — high cardinality kills metric backends. Per-tenant drill-down lives in traces and audit logs.

The confirmed-vs-rejected counters are the only ground-truth signal for L2/L3 quality; `Confidence` reported by the LLM is a self-assessment, not a measurement. Build dashboards around `accept_rate = confirmed / (confirmed + rejected)` per `confidence_bucket` to validate that confidence calibration matches reality.

## UI surfacing

The Angular client renders three views:

- **Detail page → Relations tab** (`lib-document-relations`) — table of confirmed and AI-suggested relations on this document, with one-click Confirm / Delete buttons.
- **Detail page → Graph tab** (`lib-document-relation-graph`) — radial SVG of the relation graph rooted at this document. Configurable hop depth (1 / 2 / 3). Manual edges = solid green, AI suggestions = dashed amber, module-auto = solid cyan. Click a non-root node to navigate to it.
- **Detail page → Pipeline status** — the `relation-discovery` pipeline appears alongside text-extraction / classification / embedding so operators can see whether L2 ran for the current document and how it ended.

Inside the chat panel, the LLM agent has direct access to the relation graph through the [`get_document_relations`](document-chat.md#tools) tool. When asked "is this contract paid?", the model typically calls `get_document_relations(anchorId)` first to find linked invoices and payments, then narrows `search_paperbase_documents` to those `documentIds`.

## Operational notes

- **Backfill is manual.** Existing classified documents (uploaded before this pipeline shipped) won't trigger L2 retroactively because `DocumentClassifiedEto` only fires once on initial classification. A backfill batch job that walks `Document` and queues `RelationDiscovery` runs is on the roadmap; in the meantime, manual re-classification on a per-document basis re-fires the event.
- **L2 + L3 are non-key pipelines.** Failure does not affect `Document.LifecycleStatus`. If L2 throws, the run is marked `Failed` in `DocumentPipelineRun` but the document remains `Ready`; the chat tool still works, just without that document's contributions.
- **Existing relations are protected.** Both L2 and L3 skip pairs that already have *any* `DocumentRelation` (`Manual`, `AiSuggested`, or `ModuleAuto`). This keeps re-runs idempotent and prevents an AI suggestion from displacing a user-confirmed link.
- **L3 cost is unbounded by tenant.** There is no per-tenant LLM budget. If you enable L3 in a multi-tenant deployment, monitor `paperbase.relation_discovery.l3.llm_calls` and be ready to disable per-tenant via configuration overrides.

## Related docs

- [classification.md](classification.md) — publishes the `DocumentClassifiedEto` that triggers L2.
- [pipeline-runs.md](pipeline-runs.md) — `DocumentPipelineRun` schema and `ExtraProperties` payload conventions.
- [document-chat.md](document-chat.md) — how the chat agent consumes the relation graph via the `get_document_relations` tool.
- [knowledge-index.md](knowledge-index.md) — the vector store L3 queries during semantic recall.
