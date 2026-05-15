# AI Provider

Paperbase delegates all chat-completion and embedding calls to `Microsoft.Extensions.AI`. AI configuration is split into two disjoint sections:

| Section | Owns | Consumed by |
| --- | --- | --- |
| `PaperbaseAI` | Provider wiring (endpoint, credentials, model ids, prompt-cache middleware switch) | Host only — `PaperbaseHostModule.ConfigureAI` reads it once at startup to register `IChatClient` and `IEmbeddingGenerator<string, Embedding<float>>` |
| `PaperbaseAIBehavior` | Workflow / Chat behavior knobs (prompt language, truncation, chunking, rerank, tool-call cap, …) | Application layer via `IOptions<PaperbaseAIBehaviorOptions>` — `PaperbaseApplicationModule.ConfigureServices` binds the section to the type |

The split keeps credentials (`ApiKey`) out of any `IOptions<>` flowing into business code and lets operators tune behavior independently of provider switches. Every downstream feature — [classification](classification.md), [embedding](embedding.md), [document chat](chat.md), business-module field extraction — shares the same `IChatClient` registration regardless of behavior tuning.

## Provider wiring (`PaperbaseAI`)

```json
"PaperbaseAI": {
  "Endpoint": "https://api.openai.com/v1",
  "ApiKey": "YOUR_API_KEY",
  "ChatModelId": "gpt-4o-mini",
  "EmbeddingModelId": "text-embedding-3-small",
  "PromptCachingEnabled": true,
  "MaxToolIterations": 10
}
```

| Key | Description |
| --- | --- |
| `Endpoint` | API base URL. Any OpenAI-compatible `/v1` endpoint works (Azure OpenAI, Ollama, OpenRouter, vLLM, etc.) |
| `ApiKey` | API key for the provider |
| `ChatModelId` | Model used for classification, document chat answers, optional rerank, and any business-module field extractor |
| `EmbeddingModelId` | Model used to vectorize document chunks |
| `PromptCachingEnabled` | Wraps the chat client with `UseDistributedCache()` so repeated calls with identical inputs reuse the cached response. Uses the host's registered `IDistributedCache` (in-memory by default). Disable in development if you need every call to hit the model. |
| `MaxToolIterations` | Hard cap on tool-call rounds within a single chat turn, applied at host wiring time as `FunctionInvokingChatClient.MaximumIterationsPerRequest`. Once reached, MEAI stops sending tools to the model so it must produce a final answer rather than looping. Default `10`; raise for chains that legitimately need more tool round-trips. |

The two model ids are independent — pair a small embedding model with a strong chat model freely. When changing the embedding model dimension, follow the steps in [Embedding pipeline → Switching the embedding model](embedding.md#switching-the-embedding-model).

## Picking a chat model

Three capabilities the chat model **must** have for Paperbase to work end-to-end:

| Capability | Where it's used | Failure mode if weak |
|---|---|---|
| Function calling (OpenAI-compatible `tool_calls`) | Chat (`search_paperbase_documents` direct tool + MAF `AgentSkillsProvider` meta-tools `load_skill` / `run_skill_script` / `read_skill_resource` dispatching to core + business-module skills), classification, business-module field extractors | Tools never invoked → Chat degrades to answering from training memory; classification fails silently |
| Structured JSON output (`response_format: json_schema`) | Contract / invoice / license field extraction via `agent.RunAsync<T>` | Extraction returns nulls or invalid JSON → `StructuredExtractionRetryMiddleware` burns its retries and the record is routed to manual review |
| **Tool-call _willingness_** — the model's tendency to actually CALL a tool when the question requires retrieval, instead of guessing from training data | Document chat above all (RAG is the whole point) | Worse failure than "function calling broken" — the model produces a confident-sounding answer that is **not grounded in the user's documents** at all |

The third capability is the one that surprises teams. Models that pass the OpenAI tool-calling test suite can still **decline to call tools** for plausible-sounding questions, answering from their training distribution instead. Smaller models (≤ 8B) are markedly worse at this than larger ones.

### Practical guidance

For **production chat**, prefer a model that scores well on tool-use benchmarks AND is large enough that it doesn't shortcut around retrieval:

| Tier | Examples (OpenAI-compatible providers) | Trade-off |
|---|---|---|
| **Recommended** | `gpt-4o-mini` / `gpt-4o`, `claude-3-5-sonnet`, `Qwen/Qwen3-32B`, `Pro/moonshotai/Kimi-K2.6`, `deepseek-ai/DeepSeek-V3` | Reliable tool selection; 3–10× slower / costlier than the budget tier |
| **Budget** (development only) | `Qwen/Qwen3-8B`, `Qwen/Qwen2.5-7B-Instruct`, small Llama variants | Tool calling protocol works for **explicit, narrow** tool calls (e.g. `get_contract_detail` when the question literally says "this contract") but the model often skips `search_paperbase_documents` for open-ended questions. Fast and free, but expect ungrounded answers. |
| **Avoid** | Models that don't advertise OpenAI-compatible function calling | Tools won't be invoked at all |

Field extraction is more forgiving than chat because the prompt explicitly demands a schema-conforming JSON object — even small models comply when forced. Classification is similarly forgiving. **It's chat-tier RAG behavior that needs the bigger model.**

### Choosing per-pipeline (advanced)

A single `PaperbaseAI` block registers one main chat client plus three keyed side-clients out of the box, all falling back to `ChatModelId` when their own model id is not overridden:

| Config key | Keyed registration | Consumed by |
|---|---|---|
| `PaperbaseAI:ChatModelId` (required) | default `IChatClient` (with `UseFunctionInvocation` + optional `UseDistributedCache`) | The agentic document chat path (`ChatAppService` main turn) — the **only** path that needs tool calling |
| `PaperbaseAI:SummarizerModelId` (optional) | `PaperbaseAIConsts.SummarizerChatClientKey` (no FunctionInvocation, no DistributedCache) | `SummarizationCompactionStrategy` for chat history compaction |
| `PaperbaseAI:TitleGeneratorModelId` (optional) | `PaperbaseAIConsts.TitleGeneratorChatClientKey` (no FunctionInvocation, no DistributedCache) | Auto-generated short titles: `ChatAppService.TryGenerateAndApplyTitleAsync` (conversation titles) + `DocumentTextExtractionBackgroundJob.TryGenerateTitleAsync` (document titles from Markdown) |
| `PaperbaseAI:StructuredModelId` (optional) | `PaperbaseAIConsts.StructuredChatClientKey` (no FunctionInvocation, no DistributedCache) | All single-shot schema-bound `RunAsync<T>` calls: `DocumentClassificationWorkflow`, `DocumentRerankWorkflow`, and business-module field extractors (e.g. `ContractDocumentHandler.ExtractFieldsAsync`) |

> **Provider-switch gotcha**: When switching `Endpoint` to a non-OpenAI provider (SiliconFlow, Anthropic-via-shim, Ollama, etc.), override **all four** `*ModelId` keys together in your environment-specific config — `ChatModelId` alone is not enough. The three side-client model ids in base `appsettings.json` are OpenAI-default placeholders (`gpt-4o-mini`), so leaving them unset on your environment means the title generator / structured client / summarizer will keep calling `gpt-4o-mini` against your non-OpenAI endpoint and fail with `400 Unknown model` (or whatever the provider returns). The simplest fix: copy all four overrides into your `appsettings.Development.json` / `appsettings.Production.json` / `appsettings.{Env}.json` / env vars whenever you change `Endpoint`.

The three side-clients exist to keep single-shot text-completion / structured-output calls off the main client's tool-calling pipeline. Splitting buys three things on every non-agentic call:

- **Trace cleanliness** — no phantom `orchestrate_tools` span wrapping a call that has no tools to invoke
- **No cache waste** — every non-agentic prompt in Paperbase is document-content-derived (unique per call), so `UseDistributedCache` lookups are guaranteed misses
- **Per-task model tuning** — production teams running tight token budgets can point classification / rerank / extraction at a smaller / cheaper model that can still satisfy schema-bound output, while keeping the main chat on a stronger model

The "agentic vs single-shot" distinction matches what each path actually needs from the model. Hosts that don't care about cost can leave the three optional model ids unset and ship — everything falls back to `ChatModelId` automatically.

To split even further (e.g. a different model per workflow), replace `ConfigureAI` in your host with additional per-purpose `AddKeyedChatClient` registrations following the same pattern as the existing three. The four sites that share `StructuredChatClientKey` today are deliberately consolidated because their call shape is identical; split them only when production telemetry shows a real per-task cost or quality reason.

## Where it is used

| Caller | Uses |
|---|---|
| `Documents/Pipelines/Classification/DocumentClassificationWorkflow` | `IChatClient` |
| `Documents/Pipelines/Embedding/DocumentEmbeddingWorkflow` | `IEmbeddingGenerator` |
| `Chat/ChatAppService` + `Chat/Search/DocumentRerankWorkflow` | `IChatClient` |
| Business-module field extractors (e.g. `ContractDocumentHandler`) | `IChatClient` (caller constructs its own `ChatClientAgent`) |

A single `PaperbaseAI` block serves all of them. There is no per-pipeline endpoint switch — picking different models per pipeline is a host-level customization that replaces the registrations in `PaperbaseHostModule.ConfigureServices`.

## Trying alternative providers

- **Azure OpenAI**: set `Endpoint` to `https://<resource>.openai.azure.com/openai/deployments/<deployment>/` and use the deployment name as `ChatModelId`. (API-key auth only — for Microsoft Entra ID see [Going off-protocol](#going-off-protocol-non-openai-providers).)
- **Ollama (local)**: run `ollama serve`, set `Endpoint` to `http://localhost:11434/v1`, leave `ApiKey` empty, and pick a locally pulled model id.
- **Any OpenAI-compatible gateway** (OpenRouter, vLLM, LM Studio, etc.) works the same way — only the keys in `PaperbaseAI` need to change.

## Going off-protocol (non-OpenAI providers)

The four `PaperbaseAI` keys above only describe **OpenAI-protocol** providers because `PaperbaseHostModule.ConfigureAI` builds them against `OpenAIClient`. Targeting a provider that speaks a different wire protocol — native Anthropic Claude, native Google Gemini, AWS Bedrock, Microsoft Entra ID-authenticated Azure OpenAI, native Ollama without the `/v1` shim — means **replacing `ConfigureAI` in your host project**. Application code (workflows, Chat, business-module field extractors) consumes the registered `IChatClient` / `IEmbeddingGenerator` and is unchanged.

The `PaperbaseAI` section becomes irrelevant in that case — drop it from `appsettings.json` and read your provider's keys from a configuration shape that matches its credential model (token, region, deployment id, etc.). `PaperbaseAIBehavior` still applies and is provider-agnostic.

### Invariants the new wiring must preserve

| Required | Why |
| --- | --- |
| `IChatClient` registered via `services.AddChatClient(...)` with `.UseFunctionInvocation()` | Chat tool calling — `search_paperbase_documents`, MAF `AgentSkillsProvider` meta-tools (`load_skill` / `run_skill_script` / `read_skill_resource`), and every business-module skill script — relies on this middleware. Without it the LLM's `tool_call` requests are returned to the caller instead of being executed. |
| `IEmbeddingGenerator<string, Embedding<float>>` registered via `services.AddEmbeddingGenerator(...)` | The embedding pipeline and document chat retrieval require it. If your chat provider has no embedding model (e.g. native Anthropic), pair it with a separate embedding provider — the chat and embedding registrations are independent. |
| `.UseDistributedCache()` on the chat builder when prompt caching is desired | Provider-agnostic; relies only on the host's `IDistributedCache`. |

Structured output (Classification, Rerank) is delegated to MAF's `RunAsync<T>` schema-aware path; non-OpenAI providers are handled inside the `IChatClient` adapter and need no extra configuration here.

### Sketch: Azure OpenAI with Microsoft Entra ID (no API key)

Replaces API-key auth with `DefaultAzureCredential`, which is the recommended path for Azure deployments with managed identity / workload identity. Requires `Azure.AI.OpenAI` and `Azure.Identity` package references in the host project:

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;

private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
{
    var azureClient = new AzureOpenAIClient(
        new Uri(configuration["AzureOpenAI:Endpoint"]!),
        new DefaultAzureCredential());

    var chatBuilder = context.Services
        .AddChatClient(_ => azureClient
            .GetChatClient(configuration["AzureOpenAI:ChatDeployment"]!)
            .AsIChatClient())
        .UseFunctionInvocation();

    if (configuration.GetValue("AzureOpenAI:PromptCachingEnabled", defaultValue: true))
        chatBuilder = chatBuilder.UseDistributedCache();

    chatBuilder.UseLogging();

    context.Services
        .AddEmbeddingGenerator(_ => azureClient
            .GetEmbeddingClient(configuration["AzureOpenAI:EmbeddingDeployment"]!)
            .AsIEmbeddingGenerator())
        .UseLogging();
}
```

### Sketch: native Ollama (without the OpenAI `/v1` shim)

Uses `OllamaSharp` directly, which exposes Ollama-only features (native tool format, model warm-up control) the OpenAI `/v1` shim doesn't expose. Requires the `OllamaSharp` package in the host project:

```csharp
using OllamaSharp;

context.Services
    .AddChatClient(_ => new OllamaApiClient(
        new Uri(configuration["Ollama:Endpoint"]!),
        configuration["Ollama:ChatModelId"]!))
    .UseFunctionInvocation()
    .UseDistributedCache()
    .UseLogging();

context.Services
    .AddEmbeddingGenerator(_ => new OllamaApiClient(
        new Uri(configuration["Ollama:Endpoint"]!),
        configuration["Ollama:EmbeddingModelId"]!))
    .UseLogging();
```

### Sketch: chat and embedding from different providers

When the chat provider does not ship an embedding model (typical for native Anthropic), keep two independent registrations — Paperbase resolves them separately and never assumes they share a transport:

```csharp
// Chat: from your provider's Microsoft.Extensions.AI integration package
// (or a custom IChatClient implementation)
context.Services
    .AddChatClient(sp => /* IChatClient from the chat provider */)
    .UseFunctionInvocation()
    .UseDistributedCache()
    .UseLogging();

// Embedding: keep on OpenAI / Azure OpenAI / Voyage / Ollama / ...
var openAIClient = new OpenAIClient(
    new System.ClientModel.ApiKeyCredential(configuration["EmbeddingProvider:ApiKey"]!));
context.Services
    .AddEmbeddingGenerator(_ => openAIClient
        .GetEmbeddingClient(configuration["EmbeddingProvider:ModelId"]!)
        .AsIEmbeddingGenerator())
    .UseLogging();
```

For the full set of `IChatClient`-compatible providers (Anthropic, Microsoft Foundry, GitHub Copilot, A2A, custom, …) and the exact factory calls each one ships, see the [Microsoft.Extensions.AI provider list](https://learn.microsoft.com/agent-framework/agents/providers/).

## Cross-cutting LLM behavior (`PaperbaseAIBehavior`)

These knobs describe *how Paperbase calls the model* (language hint, retrieval strategy, etc.). They are bound to `PaperbaseAIBehaviorOptions` and reach every pipeline through `IOptions<>`.

```json
"PaperbaseAIBehavior": {
  "DefaultLanguage": "ja"
}
```

| Key | Default | Description |
| --- | --- | --- |
| `DefaultLanguage` | `"ja"` | Language hint appended to every system prompt (Classification, Q&A, Rerank). Match this to your primary user base — Paperbase prompts are written language-agnostic and switch via this hint. |

Per-pipeline tuning also lives in `PaperbaseAIBehavior` — see the feature docs for the keys each pipeline reads:
- Classification truncation and prompt size → [classification.md](classification.md)
- Chunking → [embedding.md](embedding.md)
- Chat retrieval, rerank, tool-calling → [chat.md](chat.md)

## OpenTelemetry signals

`PaperbaseHostModule.ConfigureAI` wires `Microsoft.Extensions.AI`'s
`UseOpenTelemetry()` decorator into both the chat-client pipeline
(`UseFunctionInvocation` → `UseOpenTelemetry` → `UseLogging`) and the
embedding-generator pipeline. To collect these signals, register an OTel
exporter in your host and add the M.E.AI source name plus the
project-specific meter name:

```csharp
context.Services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource("Experimental.Microsoft.Extensions.AI"))
    .WithMetrics(b => b
        .AddMeter("Experimental.Microsoft.Extensions.AI")     // gen_ai.* signals
        .AddMeter("Dignite.Paperbase.Chat"));          // project-specific
```

| Source / Meter | Emitted by | What it covers |
| --- | --- | --- |
| `Experimental.Microsoft.Extensions.AI` (Activity + Meter) | `OpenTelemetryChatClient` | OTel GenAI semantic conventions: `chat {model}` / `execute_tool {name}` spans, `gen_ai.client.operation.duration` (s), `gen_ai.client.token.usage`, streaming `time_to_first_chunk` / `time_per_output_chunk` |
| `Dignite.Paperbase.Chat` (Meter) | `ChatTelemetryRecorder` | Project-specific deltas only: `paperbase.chat.turn.degraded` counter (the "honest signal" — model invoked **no tool at all**, equivalent to `GroundingSource == None`) and `paperbase.chat.tool.result.size` histogram. The full per-turn / per-tool dimensions (`GroundingSource`, `ToolCallSummary`, `ToolCallDepth`, `CitationsTrimmed`, `AnchorResolutionFailed`) are attached to `AbpAuditLogs.ExtraProperties` rather than emitted as metric tags — see [chat.md → Observability](chat.md#observability). **Does not duplicate** the gen_ai.* signals above. |

Business-domain audit (tenant / user / conversation / document) goes to
`AbpAuditLogs.Comments` + `ExtraProperties` (`ChatTelemetryRecorder`
calls `IAuditingManager.Current`); the audit row links to the OTel trace
through `Activity.Current.TraceId`.
