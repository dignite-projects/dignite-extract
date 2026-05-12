# Observability

Paperbase emits OpenTelemetry traces and metrics from three sources and ships them through a single host-configured export pipeline. This page covers what's emitted, how to wire it up locally, and how to point it at a production backend.

## What's emitted

| Source | Type | Highlights |
|---|---|---|
| **`Microsoft.Agents.AI`** | Traces, Metrics | MAF's built-in `CompactionTelemetry` (`compaction.compact`, `compaction.summarize` spans with `Strategy / Triggered / BeforeTokens / AfterTokens / DurationMs` tags), `compaction.provider.invoke` lifecycle, plus token-usage / tool-call metrics. |
| **`Microsoft.Extensions.AI`** | Traces, Metrics | `chat-client.GetResponseAsync` and `execute_tool {tool_name}` spans with GenAI semantic-convention tags (model id, prompt / completion tokens, finish reason). Emitted automatically by the `.UseOpenTelemetry()` decorators wired on every chat client + embedding generator in `PaperbaseHostModule.ConfigureAI`. |
| **`Dignite.Paperbase.*`** | Metrics | Project-specific counters and histograms. The major Meters today: |
| ↳ `Dignite.Paperbase.Chat` | | `paperbase.chat.turn.degraded` (counter), `paperbase.chat.tool.result.size` (histogram). |
| ↳ `Dignite.Paperbase.Documents.RelationDiscovery` | | `paperbase.relation_discovery.runs.total`, `.l2.created`, `.l3.invoked`, `.l3.llm_calls`, `.l3.created`, `.duration`, `.suggestion.confirmed` / `.rejected`. |
| ↳ `Dignite.Paperbase.Contracts` | | `paperbase.contracts.extraction.attempts` (counter, tags `document_type_code`, `success`), `.extraction.validation_errors` (counter, tags `rule`, `document_type_code`), `.extraction.confidence` (histogram). See [structured-extraction.md](structured-extraction.md) for what drives the values. |

A new business module that adds its own Meter automatically lands in the pipeline as long as the Meter name starts with `Dignite.Paperbase.` — the host registers a wildcard `AddMeter("Dignite.Paperbase.*")`.

## Host pipeline configuration

The pipeline is set up in `host/src/PaperbaseHostModule.cs → ConfigureOpenTelemetry`. It's **opt-in** so an unconfigured host doesn't spawn a background exporter or hit a non-existent OTLP endpoint.

Default in `host/src/appsettings.json`:

```json
"OpenTelemetry": {
  "Enabled": false,
  "ConsoleExporter": false,
  "Otlp": {
    "Endpoint": "http://localhost:4317",
    "Protocol": "Grpc"
  }
}
```

| Key | Default | Description |
|---|---|---|
| `OpenTelemetry:Enabled` | `false` | Master switch. When `false`, `AddOpenTelemetry()` is not called at all — zero runtime cost. |
| `OpenTelemetry:ConsoleExporter` | `false` | Adds an extra console exporter alongside OTLP. Useful for one-off "is anything being emitted at all" sanity checks in containers without a dashboard. |
| `OpenTelemetry:Otlp:Endpoint` | `http://localhost:4317` | The OTLP collector endpoint. Falsy / empty disables the OTLP exporter (Console-only mode still works if enabled). |
| `OpenTelemetry:Otlp:Protocol` | `Grpc` | `Grpc` or `HttpProtobuf`. Most OTLP collectors accept both; pick whichever your network policy allows. |

The same overrides work via environment variables — replace `:` with `__`:

```bash
OpenTelemetry__Enabled=true
OpenTelemetry__Otlp__Endpoint=http://otel-collector.internal:4317
```

## Local development with Aspire Dashboard

For local dev we ship a profile-gated `aspire-dashboard` service in `host/docker-compose.yml`. It receives OTLP over gRPC and renders traces + metrics + logs at `http://localhost:18888`.

**Why aspire-dashboard for dev**: single container, zero-config, runs on the developer's laptop. Use Jaeger / Datadog / Grafana Tempo / Azure Monitor in shared environments; OTLP is vendor-neutral so the same instrumentation hits any backend.

### Bring it up

```powershell
cd D:\dignite-projects\dignite-paperbase\host

# Profile-gated so plain `docker compose up` doesn't pull a 300MB image
docker compose --profile observability up -d aspire-dashboard
```

### Tell the host to send to it

Pick one of three places to set `OpenTelemetry:Enabled = true`:

| Where | Scope | Notes |
|---|---|---|
| `host/src/Properties/launchSettings.json` → `environmentVariables` | Per-launch-profile, **persisted in git** | Recommended for the project default. Already populated for both `IIS Express` and `Dignite.Paperbase.Host` profiles. |
| `host/src/appsettings.Development.json` → `OpenTelemetry.Enabled = true` | Development environment, **persisted in git** | Equivalent effect to launchSettings; choose one or the other (both is harmless but redundant). |
| Shell env vars (`$env:OpenTelemetry__Enabled = "true"` in PowerShell) | Current shell session only | For ad-hoc inspection without changing any tracked file. |

The repo defaults to **launchSettings.json**: contributors who clone, `docker compose --profile observability up -d`, then F5 / `dotnet run` immediately see signals on the dashboard with no further config.

### Verify

```powershell
# Start the host
dotnet run --project host/src/Dignite.Paperbase.Host.csproj

# Hit any endpoint or send a chat turn, then open the dashboard
start http://localhost:18888
```

Expected sightings:

- **Traces** tab — an ASP.NET Core request span containing nested `chat-client.GetResponseAsync` spans and any `execute_tool {tool_name}` children. If `ChatCompaction:Enabled = true`, you'll also see `compaction.compact` spans with token-delta tags.
- **Metrics** tab — `paperbase.chat.turn.degraded` and `paperbase.contracts.extraction.attempts` counters tick on each turn / contract upload.
- **Structured Logs** tab — Serilog logs with `TraceId` correlations to the spans on the left.

### First-start delay

aspire-dashboard takes 30–60 seconds to become reachable after `Up` status. If `http://localhost:18888` refuses, wait and retry — or check `docker compose logs aspire-dashboard | tail` for `Now listening on:`.

## Pointing at a different OTLP backend

OTLP is vendor-neutral. To switch from aspire-dashboard to anything else, change only the endpoint:

```bash
# Jaeger (OTLP-native since 1.35)
OpenTelemetry__Otlp__Endpoint=http://jaeger:4317

# Grafana Tempo
OpenTelemetry__Otlp__Endpoint=http://tempo:4317

# Datadog (via OTel collector with the datadogexporter)
OpenTelemetry__Otlp__Endpoint=http://otel-collector:4317

# Azure Monitor: use OpenTelemetry.Exporter.AzureMonitor instead of OTLP
# (requires a code change in PaperbaseHostModule.ConfigureOpenTelemetry)
```

Production deployments should set the endpoint via env var or Kubernetes ConfigMap — never commit a production OTLP URL to `appsettings.json`.

## Tagging policy and cardinality

All Paperbase-owned Meters follow the same rule: **tags are low-cardinality enums or bounded sets**.

| Allowed as tag | Not allowed as tag |
|---|---|
| `document_type_code` (bounded by `DocumentTypeDefinition`) | `tenant_id` (multi-tenant cardinality blowup) |
| `success` (`true` / `false`) | `user_id` |
| `rule` (one of the static `ContractExtractionValidator.RuleCodes.*`) | `document_id` |
| `stage` / `strategy` (compaction layer names) | Free-text from the model or user |

Per-tenant / per-user drill-down belongs in traces and structured logs — those are sampled, while metrics are aggregated by tag and would explode storage and dashboard latency.

When adding a new tag to an existing metric, audit the cardinality first. A tag that can grow unboundedly is a regression even if it "just works" for the first month.

## Tests

A test must not register the production OTel pipeline. The `PaperbaseHostModule.ConfigureOpenTelemetry` short-circuits when `Enabled = false` (the default), so test hosts that don't set `OpenTelemetry:Enabled = true` skip the export entirely. Tests that need to *capture* metric emissions instead use `MeterListener` directly — see `core/test/Dignite.Paperbase.Application.Tests/Documents/Pipelines/RelationDiscovery/RelationDiscoveryTelemetryRecorder_Tests.cs` for the pattern.

## Adding a Meter from a new module

```csharp
// In your module's Domain layer
public class MyModuleTelemetryRecorder : ISingletonDependency
{
    public const string MeterName = "Dignite.Paperbase.MyModule";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SomeCounter = Meter.CreateCounter<long>(
        "paperbase.my_module.something.total",
        description: "...");

    public virtual void RecordSomething(string tagValue)
    {
        SomeCounter.Add(1, new KeyValuePair<string, object?>("dimension", tagValue));
    }
}
```

No host-side change required. The `AddMeter("Dignite.Paperbase.*")` wildcard in `ConfigureOpenTelemetry` picks it up automatically when the host is rebuilt with the new module.

For ActivitySource-based traces, the wildcard `AddSource("Dignite.Paperbase.*")` registration covers the same naming convention.
