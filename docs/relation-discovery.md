# Relation Discovery

When a user uploads a contract, that contract may be related to an earlier framework agreement, several supporting invoices, and a few signed amendments — none of which are obvious from the document itself. Paperbase's **Relation Discovery** pipeline turns those scattered files into a navigable graph automatically: AI proposes the links, the user confirms them, and downstream features (the in-app graph view, the chat agent's `get_document_relations` tool) treat the resulting `DocumentRelation` aggregate as a first-class navigation surface.

This page is the **operator's manual**: how the pipeline works, what knobs to turn, and the operational pitfalls. If you're a **module developer** trying to wire your business module into the relation graph, read [relation-discovery-module-integration.md](relation-discovery-module-integration.md) instead — it covers the provider contracts, naming conventions, normalization rules, and verification checklist.

For low-level orchestration code see `core/src/Dignite.Paperbase.Application/Documents/Pipelines/RelationDiscovery/`. The design rationale (why this shape, what was rejected) lives in [Issue #115](https://github.com/dignite-projects/dignite-paperbase/issues/115).

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
                                                                                      ▼
                                                  RelationDiscoveryService
                                                  fans out across all
                                                  IDocumentIdentifierProviders +
                                                  IDocumentEntitySignatureProviders;
                                                  structured matches → AiSuggested
```

One layer, structured matching only. No LLM in the discovery path itself — if a user wants a free-form "what's the relation between these two documents?" answer, the chat agent does that on demand via `search_paperbase_documents` + the two documents' Markdown. A previous "L3 semantic + LLM fallback" path was removed (first-principles review): it added significant complexity and LLM cost for a use case the chat agent already handled interactively, and the default-off opt-in shape meant no real users had validated it.

L2-tier identifier matching catches the high-confidence cases:

- Two documents that share the same contract number, invoice number, PO number, project code
- A contract and its supplement that share `(PartyA, PartyB, signing year)` (multi-field signature matching)

Whatever doesn't match here remains undiscovered until a user manually creates the relation in the UI — or asks the chat agent about it.

`DocumentRelation` rows are written with `Source = AiSuggested`. The user clicks **Confirm** to flip to `Manual` (`paperbase.relation.suggestion.confirmed` counter increments), or **Delete** to dismiss (`paperbase.relation.suggestion.rejected` counter increments + the row is soft-deleted as a tombstone so it doesn't re-suggest).

## What L2 exposes vs hides

L2 only sees identifiers and signatures that business modules explicitly emit (see [relation-discovery-module-integration.md](relation-discovery-module-integration.md)). Rule of thumb for what to expose:

- **High-cardinality, near-unique values** (invoice numbers, contract numbers, PO numbers, project codes) → single-field identifier provider, confidence is implicit (deterministic match).
- **Multiple weaker fields combined** (party A + party B + signing year) → multi-field signature provider. Emit only when ALL fields are populated.
- **Low-cardinality standalone values** (party names alone, status enums, common dates) → don't expose. They'd connect every document with the same vendor into a noise graph. The contracts module exposes `(PartyA, PartyB, Year)` as a *signature* but NOT `PartyName` as a single-field identifier — see the codex review fix in PR [#131](https://github.com/dignite-projects/dignite-paperbase/pull/131).

## Trigger semantics

`RelationDiscoveryEventHandler` subscribes to `DocumentClassifiedEto` — the same event business modules use to extract their typed records. To avoid racing those extraction handlers, the background job is **enqueued with a delay** (`PaperbaseAIBehaviorOptions.RelationDiscoveryDelaySeconds`, default `30s`). By the time the worker picks the job up, the contract / invoice / etc. record has already been saved and the provider can read it.

If a business module's extraction takes longer than the delay (e.g. very slow LLM provider), RelationDiscovery will run with no identifiers and silently complete with zero relations. Tune the delay up rather than wait for retry coverage to land.

Orphan documents — failed classification, or classified into a type no business module owns — never publish `DocumentClassifiedEto`, so the pipeline never fires for them. They reach the relation graph only when a user manually creates a `DocumentRelation`.

## Configuration

```json
"PaperbaseAIBehavior": {
  "RelationDiscoveryDelaySeconds": 30
}
```

| Knob | Default | Notes |
|---|---|---|
| `RelationDiscoveryDelaySeconds` | `30` | Delay before the job is dequeued. Buys time for sibling `DocumentClassifiedEto` handlers to commit their typed records. Set to `0` only in test setups where extraction is synchronous. |

## Tenant flow

Both the event handler and the background job explicitly restore tenant context via `using (_currentTenant.Change(...))`:

- **Event handler** wraps with `Change(eventData.TenantId)` so that the scheduler stamps `RelationDiscoveryJobArgs.TenantId` correctly even when the distributed-event bus didn't restore ambient tenant.
- **Background job** wraps `ExecuteAsync` with `Change(args.TenantId)` as defense in depth — providers query through ABP's `IMultiTenant` ambient filter, which silently misses data if the ambient tenant is wrong.

This is the same pattern `ContractDocumentHandler` uses for the same event, addressing codex review finding [high] "Tenant context dropped" (PR [#131](https://github.com/dignite-projects/dignite-paperbase/pull/131)).

## Telemetry

Meter name: `Dignite.Paperbase.Documents.RelationDiscovery`.

| Instrument | Type | Tags | Source |
|---|---|---|---|
| `paperbase.relation_discovery.runs.total` | counter | `result` | every job completion (`Succeeded` / `Failed` / `DocumentMissing`) |
| `paperbase.relation_discovery.created` | histogram | — | AiSuggested relations created per run |
| `paperbase.relation_discovery.identifiers_by_provider` | histogram | `provider` | per (run, provider) — how many identifiers the provider emitted (incl. zero, which flags "provider installed but didn't fire") |
| `paperbase.relation_discovery.orphan_documents` | counter | — | source documents that produced zero identifiers across all providers |
| `paperbase.relation_discovery.high_ambiguity_identifiers` | counter | `type` | identifier values that matched ≥ `HighAmbiguityPeerThreshold` peers (default 10) — almost always noise |
| `paperbase.relation_discovery.duration` | histogram (ms) | `phase` (`discovery` / `total`) | per-run wall clock |
| `paperbase.relation.suggestion.confirmed` | counter | `source` | `IDocumentRelationAppService.ConfirmAsync` |
| `paperbase.relation.suggestion.rejected` | counter | `source` | `IDocumentRelationAppService.DeleteAsync` |

`tenant_id` is intentionally **not** a tag — high cardinality kills metric backends. Per-tenant drill-down lives in traces and audit logs.

The confirmed-vs-rejected counters are the only ground-truth signal for AI-suggestion quality. Build dashboards around `accept_rate = confirmed / (confirmed + rejected)` to validate the value users get from AI suggestions.

## UI surfacing

The Angular client renders three views:

- **Detail page → Relations tab** (`lib-document-relations`) — table of confirmed and AI-suggested relations on this document, with one-click Confirm / Delete buttons.
- **Detail page → Graph tab** (`lib-document-relation-graph`) — radial SVG of the relation graph rooted at this document. Configurable hop depth (1 / 2 / 3). Manual edges = solid, AI suggestions = dashed. Click a non-root node to navigate to it.
- **Detail page → Pipeline status** — the `relation-discovery` pipeline appears alongside text-extraction / classification / embedding so operators can see whether discovery ran for the current document and how it ended.

Inside the chat panel, the LLM agent has direct access to the relation graph through the [`get_document_relations`](chat.md#tools) tool. When asked "is this contract paid?", the model typically calls `get_document_relations(anchorId)` first to find linked invoices and payments, then narrows `search_paperbase_documents` to those `documentIds`.

## Operational notes

- **Backfill is manual.** Existing classified documents (uploaded before this pipeline shipped) won't trigger discovery retroactively because `DocumentClassifiedEto` only fires once on initial classification. A backfill batch job is on the roadmap; in the meantime, manual re-classification on a per-document basis re-fires the event.
- **RelationDiscovery is a non-key pipeline.** Failure does not affect `Document.LifecycleStatus`. If a run throws, the run is marked `Failed` in `DocumentPipelineRun` but the document remains `Ready`; the chat tool still works, just without that document's contributions.
- **Existing relations are protected.** The pipeline skips pairs that already have *any* `DocumentRelation` (`Manual` or `AiSuggested`, including dismissed soft-deleted rows). This keeps re-runs idempotent and prevents an AI suggestion from displacing a user-confirmed link.

## Related docs

- [classification.md](classification.md) — publishes the `DocumentClassifiedEto` that triggers RelationDiscovery.
- [pipeline-runs.md](pipeline-runs.md) — `DocumentPipelineRun` schema and `ExtraProperties` payload conventions.
- [chat.md](chat.md) — how the chat agent consumes the relation graph via the `get_document_relations` tool.
- [relation-discovery-module-integration.md](relation-discovery-module-integration.md) — module developer integration manual.
