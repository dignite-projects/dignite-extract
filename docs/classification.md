# Document Classification

When a document finishes [text extraction](text-extraction.md), Paperbase classifies it against a registered set of `DocumentTypeDefinition`s — for example "Contract", "Invoice", "Receipt". The resulting `DocumentTypeCode` is the routing signal that triggers business modules: a `Contract` event handler picks up its own `DocumentClassifiedEto` and runs domain field extraction, an invoice module does the same for invoices, and so on.

This page covers the classification pipeline as a *feature*: how it works, how to tune it, and what happens when the LLM is unhappy. For low-level orchestration code see `core/src/Dignite.Paperbase.Application/Documents/Pipelines/Classification/`.

## How it works

```
Document.Markdown ──► DocumentClassificationBackgroundJob ──► DocumentClassificationWorkflow
                                                              (ChatClientAgent + structured output)
                                                                         │
                                                                         ▼
                                            ConfidenceScore ≥ Type.ConfidenceThreshold ?
                                                ├─ yes ─► DocumentClassifiedEto + enqueue embedding
                                                └─ no  ─► PendingReview (human triage)

                              transient LLM error          ──► KeywordDocumentClassifier (fallback)
                              schema deserialization error ──► PendingReview (no fallback)
```

Two design properties matter:

- **The LLM consumes Markdown directly.** Headings, tables and lists in `Document.Markdown` are kept as semantic signals. The system prompt explicitly tells the model "input is Markdown".
- **Keyword fallback only fires on transient provider failures** (HTTP errors, timeouts). Schema deserialization failures route straight to `PendingReview` — keyword-matching cannot patch a broken semantic decision.

## Registering document types

Each business module that wants its documents classified registers types via `DocumentTypeOptions`:

```csharp
Configure<DocumentTypeOptions>(options =>
{
    options.AddType(new DocumentTypeDefinition(
        typeCode: "Contract",
        displayName: "Contract",
        description: "Commercial contracts with one or more counterparties.",
        priority: 100,
        confidenceThreshold: 0.7,
        keywords: new[] { "contract", "agreement", "甲方", "乙方" }));
});
```

| Field | Used by |
|---|---|
| `typeCode` | Business module's event handler matches on this |
| `displayName` / `description` | Sent to the LLM as the candidate list |
| `priority` | Higher = appears earlier in the LLM prompt; tie-break when truncated |
| `confidenceThreshold` | LLM result must clear this to auto-classify; below it the document goes to `PendingReview` |
| `keywords` | Used by `KeywordDocumentClassifier` only when the LLM is unreachable |

## Configuration

```json
"PaperbaseAIBehavior": {
  "MaxDocumentTypesInClassificationPrompt": 50,
  "MaxTextLengthPerExtraction": 8000
}
```

| Key | Default | Description |
| --- | --- | --- |
| `MaxDocumentTypesInClassificationPrompt` | `50` | When more than this many types are registered, the prompt keeps the top N by `Priority`. Tune this against your LLM's context window — more types means a longer prompt and slower / more expensive calls. |
| `MaxTextLengthPerExtraction` | `8000` | Markdown longer than this is truncated before being sent. The first N characters usually contain the most discriminative content (title, table-of-contents, opening clauses). Increase if your documents bury the type signal deep, but watch token cost. |

The prompt language follows `PaperbaseAIBehavior:DefaultLanguage` (see [ai-provider.md](ai-provider.md#cross-cutting-llm-behavior-paperbaseaibehavior)).

## Outcomes

| Outcome | Pipeline state | What happens next |
|---|---|---|
| LLM result, confidence ≥ threshold | `DocumentPipelineRun` completes | `DocumentClassifiedEto` published; embedding job enqueued; business modules wake up |
| LLM result, confidence < threshold | `PendingReview` | `PipelineRunExtraPropertyNames.ClassificationCandidates` is populated for the UI ([pipeline-runs.md](pipeline-runs.md)) |
| LLM unreachable (transient) | Keyword fallback runs against stripped plain text. Hit = same path as LLM hit; miss = `PendingReview` | — |
| LLM returned malformed JSON | `PendingReview` (no fallback) | A human resolves the type code in the UI |

## See also

- [Text extraction](text-extraction.md) — produces the `Document.Markdown` consumed here
- [Embedding pipeline](embedding.md) — the next stage triggered on successful classification
- [Relation discovery](relation-discovery.md) — also subscribes to `DocumentClassifiedEto`; queues L2/L3 to find relations to other documents
- [Pipeline runs](pipeline-runs.md) — the `Candidates` payload schema for the review UI
