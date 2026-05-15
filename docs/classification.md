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

                              transient LLM error          ──► rethrow → ABP Job retry (MaxTryCount)
                              schema deserialization error ──► PendingReview (no retry)
```

Two design properties matter:

- **The LLM consumes Markdown directly.** For structured documents (contracts, reports, layout-aware OCR output), headings, tables and lists in `Document.Markdown` are kept as **real semantic signals** the LLM exploits. The system prompt explicitly tells the model "input is Markdown". For unstructured content (loose OCR paragraphs, plain text), the Markdown wrapper is a container name — it keeps the classifier on one prompt template, but no extra signal is being conveyed beyond what plain paragraphs would carry.
- **Transient LLM failures rely on ABP Job retry, not a keyword fallback.** Network errors, timeouts and cancellations bubble out of `DocumentClassificationBackgroundJob`; the `PipelineRun` is marked `Failed` for operator visibility, and ABP reschedules the job per `BackgroundJobOptions.JobTypes` retry policy. When the LLM recovers, the next attempt produces a real classification — far better than freezing a document on a low-confidence keyword guess. Schema deserialization errors short-circuit straight to `PendingReview` because retrying the same malformed output wastes quota.

## Registering document types

Each business module that wants its documents classified registers types via `DocumentTypeOptions`:

```csharp
Configure<DocumentTypeOptions>(options =>
{
    options.Register(new DocumentTypeDefinition(
        ContractsDocumentTypes.General,
        LocalizableString.Create<ContractsResource>("DocumentType:Contract"))
    {
        ConfidenceThreshold = 0.80,
        Priority = 10
    });
});
```

| Field | Used by |
|---|---|
| `TypeCode` | Business module's event handler matches on this. Must follow `<owner-module>.<sub-type>`. |
| `DisplayName` (`ILocalizableString`) | Sent to the LLM as the candidate name. Resolved through `IStringLocalizerFactory` under `PaperbaseAIBehavior:DefaultLanguage`. UI also reads it under the user's current culture. |
| `Priority` | Higher = appears earlier in the LLM prompt; tie-break when truncated to `MaxDocumentTypesInClassificationPrompt`. |
| `ConfidenceThreshold` | LLM result must clear this to auto-classify; below it the document goes to `PendingReview`. |

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
| LLM unreachable (transient) | `Failed`, exception rethrown | ABP retries the job per `BackgroundJobOptions.JobTypes` `MaxTryCount`. Next attempt does a fresh LLM classification once the provider recovers. |
| LLM returned malformed JSON | `PendingReview` | No retry — a human resolves the type code in the UI |

## See also

- [Text extraction](text-extraction.md) — produces the `Document.Markdown` consumed here
- [Embedding pipeline](embedding.md) — the next stage triggered on successful classification
- [Relation discovery](relation-discovery.md) — also subscribes to `DocumentClassifiedEto`; queues a discovery run to find relations to other documents
- [Pipeline runs](pipeline-runs.md) — the `Candidates` payload schema for the review UI
