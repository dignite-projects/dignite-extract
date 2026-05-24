# Text Extraction

Every document uploaded to Paperbase passes through a text-extraction stage that converts the raw bytes into **Markdown**. The Markdown then drives the channel's internal pipeline — classification, Host field extraction, tenant field extraction (B 机制), and title generation — and is the only text payload Paperbase exposes to downstream consumers (RAG platforms, business systems, MCP clients) via REST / EventBus / MCP.

## Markdown-first contract

Paperbase is an AI-native platform. Markdown is the **single text payload** of the pipeline. But what Markdown contributes depends on whether the source document has structure — be honest about both cases:

**With structure — real signal.** For contracts, reports, CSV, DOCX with headings, layout-aware OCR output (PP-StructureV3, Azure DI `prebuilt-layout`): headings, tables and lists are not formatting decoration — they are semantic signals that downstream RAG chunkers (header-path injection) and Paperbase's own LLM prompts (system prompt: "input is Markdown") rely on. Use them in full.

**Without structure — container, not signal.** For OCR loose paragraphs, plain `.txt`, PP-OCRv4 line dumps, single-line notes: the Markdown wrapper is a **container name**, not a signal upgrade — `string.Join("\n\n", paragraphs)` and the plain text it wraps are byte-for-byte indistinguishable. We still route this through the Markdown contract so internal pipelines (classification / Host & tenant field extraction / title generation) and downstream consumers (RAG / business systems) stay on one shape. The wrapper buys uniformity, not LLM comprehension.

Contract obligations regardless of structure:

- Every text-extraction provider — built-in or third-party — **must** populate `TextExtractionResult.Markdown`. Plain-text fallbacks are a design violation.
- Even when the source has no structure, the provider must still emit flat Markdown paragraphs rather than expose a parallel raw-text channel — wrapping happens **inside the provider**, never bubbled up to the orchestrator.
- `Document.Markdown` is the **only** text field on the `Document` aggregate. Consumers that need plain text strip on demand via `Dignite.Paperbase.Documents.MarkdownStripper.Strip(...)`; nothing is persisted in stripped form.

**Markdown-first is an engineering default, not a creed.** Out-of-band signals (coordinates, confidence, page metadata, form key-value structure, stamp positions) are **orthogonal** to Markdown. When future needs arise — citation highlighting on the source PDF, stamp localization, form key-value extraction, page-aware QA — they belong as **named, optional, strongly-typed** fields on `TextExtractionResult` (e.g. `IReadOnlyList<PageBlock>? PageBlocks`) or as a separate extractor interface orthogonal to `ITextExtractor`. **Forbidden**: stuffing such signals back into the Markdown string, or adding a `Dictionary<string, object>` extension slot. Each new out-of-band signal needs its own Issue — it's an architecture decision, not a quiet field addition.

Source contract: [`ITextExtractor`](../core/src/Dignite.Paperbase.Abstractions/TextExtraction/ITextExtractor.cs), [`IMarkdownTextProvider`](../core/src/Dignite.Paperbase.TextExtraction/IMarkdownTextProvider.cs).

## Two extraction paths

```
Upload → DocumentTextExtractionBackgroundJob
              │
              ├─→ digital text layer? (PDF / DOCX / HTML / TXT / CSV / RTF / EPUB …)
              │     └─→ IMarkdownTextProvider (e.g. ElBruno MarkItDown)
              │
              └─→ image / scan?
                    └─→ IOcrProvider (PaddleOCR / Azure Document Intelligence)

Both paths write the same shape: TextExtractionResult { Markdown, Confidence, ... }
                                  → Document.Markdown
```

The two paths are dispatched by file kind. Hosts wire one digital provider plus one OCR provider via `[DependsOn(...)]`; switching providers is a host-level swap with no Application or Domain changes.

## Digital extraction — ElBruno MarkItDown

`PaperbaseElBrunoMarkItDownModule` is the default `IMarkdownTextProvider` and handles digital files (PDF with text layer, DOCX, HTML, TXT, CSV, RTF, EPUB). It is enabled automatically by the host module and needs no configuration.

If a digital PDF has no text layer (scanned PDF), the digital path returns empty Markdown and the pipeline falls through to the OCR provider.

## OCR — choosing a provider

Paperbase ships two OCR providers. Pick one in `host/src/PaperbaseHostModule.cs` based on the deployment scenario.

| | PaddleOCR (default) | Azure Document Intelligence |
|---|---|---|
| Where data goes | Local sidecar — never leaves the network | Cloud (Azure region) |
| Setup cost | `docker compose up paddleocr` | Azure subscription + AI resource |
| Best language coverage | Chinese + Japanese (PP-StructureV3 OmniDocBench) | Japanese / Chinese / English |
| Markdown output | Native (PP-StructureV3 / VL); flat (PP-OCRv4) | Native |
| Cold start | ~30–60 s first run (model download ~600 MB) | Instant |
| Per-page cost | Free | F0 free tier (500 pages/month, **first 2 pages only** per request) → S0 ~$1.50 / 1000 pages |
| Throughput | ~3.7 s/page on CPU | Subject to Azure tier (F0 ≈ 1–2 TPS) |

> Cloud LLM OCR (Gemini / Mistral) and Google Document AI were evaluated and rejected — see issue #79 for the rationale (Japanese-language quality, region access, dependency footprint, free-tier shape).

### PaddleOCR — local sidecar

Default for development. `PP-StructureV3` runs on CPU and emits native Markdown out of the box.

```json
"PaddleOcr": {
  "Endpoint": "http://localhost:8866",
  "ModelName": "PP-StructureV3",
  "Languages": [ "ja", "en" ]
}
```

| Key | Default | Description |
| --- | --- | --- |
| `Endpoint` | `http://localhost:8866` | PaddleOCR sidecar REST endpoint |
| `ModelName` | `PP-StructureV3` | One of: `PP-StructureV3` (CPU + native Markdown, default), `PP-OCRv4` (lightest, no Markdown structure), `PaddleOCR-VL-1.5` (highest quality; requires GPU + ~2 GB model download; native Markdown) |
| `Languages` | `["ja", "en"]` | Default recognition languages (BCP 47); overridden per call by `OcrOptions.LanguageHints` |

Paperbase does not auto-switch OCR profiles per document. The OCR provider runs once with the host-configured model; there is no second OCR pass with a guessed specialized mode, and OCR average confidence is no longer a quality gate (#196).

OCR completion always advances the document to classification. `OcrConfidence` is surfaced on `OCRCompletedEto` / `DocumentReadyEto` as an informational quality metric (for downstream secondary gating), but it gates no Paperbase stage. A document reaches the review queue only via low classification confidence / no matching type, where the operator reclassifies, rejects (Paperbase keeps the original file, Markdown, OCR confidence, and rejection reason for audit, then marks the document failed — no "rerun OCR" or source-replacement path), or re-uploads a better source.

`ReviewStatus` is the current routing state, not a durable audit ledger: `None` (no human action needed), `PendingReview` (classification needs an operator), or `Reviewed` (operator confirmed a type). Automatic re-classification may reset it; OCR confidence and pipeline history remain available if a dedicated audit/event model is later needed.

**Bring up the sidecar:**

```bash
docker compose up paddleocr
```

The first run downloads ~600 MB of model weights and takes 30–60 seconds. Subsequent starts are instant.

**Resource footprint** (PP-StructureV3, CPU): ~3.7 s/page on a modern Intel CPU, ~2 GB RAM working set.

### Azure Document Intelligence — cloud

Recommended for production workloads where data is allowed to leave the network and the team prefers not to operate a sidecar.

1. Create an Azure AI Document Intelligence resource (F0 for trial, S0 for production).
2. Copy the **Endpoint** and **API Key**.
3. In `host/src/PaperbaseHostModule.cs`, swap `PaperbasePaddleOcrModule` for `PaperbaseAzureDocumentIntelligenceModule`. Re-enable the matching `ProjectReference` in `host/src/Dignite.Paperbase.Host.csproj`.
4. Add to `host/src/appsettings.Development.json` (or `appsettings.Production.json`):

```json
"AzureDocumentIntelligence": {
  "Endpoint": "https://<your-resource>.cognitiveservices.azure.com/",
  "ApiKey": "YOUR_KEY"
}
```

`PaperbaseAzureDocumentIntelligenceModule` binds this section automatically.

Paperbase fixes the Azure model to `prebuilt-layout` and does not expose it as a config option — it emits the structured Markdown that Markdown-first requires. `prebuilt-read` (plain text only) and business prebuilts (invoice / contract) are intentionally not channel-layer OCR options.

> ⚠️ **F0 limitations** — each request only processes the **first 2 pages**, only one F0 resource per subscription per region, ~1–2 TPS throughput. Suitable only for demos and short documents (≤ 2 pages). Switch to S0 for sustained development or any larger document.

## Adding a custom OCR / digital provider

Implement `IOcrProvider` (for image/scan input) or `IMarkdownTextProvider` (for files with a digital text layer). Both contracts are documented in their source files; both demand Markdown output.

The provider lives in its own module project (`Dignite.Paperbase.Ocr.<Vendor>` or `Dignite.Paperbase.TextExtraction.<Vendor>`) and is enabled by the host through `[DependsOn(...)]`.

**Markdown-first responsibility is on the provider, not the orchestrator.** The `OcrResult` and `TextExtractionResult` types expose only a `Markdown` field — there is no parallel `RawText` channel. If the underlying OCR engine returns plain text only (e.g. PaddleOCR PP-OCRv4), the provider itself must wrap paragraphs into flat Markdown (typically `string.Join("\n\n", paragraphs)`). Returning empty Markdown when the engine produced text is a contract violation. Custom OCR providers should expose their model choice through provider/host configuration, not through Paperbase core profile codes.

Custom OCR provider projects only need to reference `Dignite.Paperbase.Ocr` — they do not need (and should not pull in) `Dignite.Paperbase.TextExtraction` or `Dignite.Paperbase.Abstractions`.

## See also

- [Classification pipeline](classification.md) — how the LLM consumes the Markdown
- [AI provider](ai-provider.md) — provider wiring for the keyed chat clients used by classification / field extraction / title generation
- [Deployment checklist](deployment-checklist.md) — verifying OCR after a sidecar upgrade
