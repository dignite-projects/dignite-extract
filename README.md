# Dignite Paperbase

> **Paperbase = physical paper / scans / photos / PDF images / Office files → trustworthy digitized data.**
> A **channel layer**, not an end-product. It doesn't consume, doesn't own, doesn't dive into business — it hands Markdown + structured metadata to downstream RAG platforms, business systems, and AI clients via REST / EventBus / MCP server / Webhook.

For the full positioning, architecture rules, OUT-of-scope list, Markdown-first contract, six-stage ETO event contract, and security covenant, see [CLAUDE.md](./CLAUDE.md). It is the truth source — this README only stages the operational entry points.

## Data flow

```
physical paper / scans / photos / PDF images / Office files
    ↓
[Paperbase channel]: OCR + Markdown + system metadata + type-bound field extraction
    ↓ (REST / EventBus / MCP server / Webhook)
    ├─→ downstream RAG platform
    ├─→ business systems (finance / CLM / HR / ERP)
    ├─→ AI clients (Claude Desktop / Cursor / any MCP client)
    └─→ any consumer (build your own subscriber)
```

## Solution structure

```
dignite-paperbase/
├── core/      # Channel implementation — ABP layers (Abstractions / Domain.Shared / Domain / Application / EntityFrameworkCore / HttpApi)
├── host/      # Host application — provider wiring (OCR + AI) and middleware (ASP.NET Core API)
├── angular/   # Angular SPA (operator UI)
└── docs/      # Operator-facing documentation (design decisions go to GitHub Issues, not here)
```

Business modules (contract management / invoice management / HR records / etc.) are **not** in this repo — they belong on the downstream consumer side per the channel philosophy.

## Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 | |
| [Node.js](https://nodejs.org) | 20 | Required for the Angular frontend (Angular 21 needs Node 20.19+ / 22.12+) |
| SQL Server | 2019+ | LocalDB works for development; production runs full SQL Server |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | any recent | Optional but recommended — runs the PaddleOCR sidecar and the local OpenTelemetry dashboard |

## Getting started (local development)

### 1. Start the PaddleOCR sidecar

PaddleOCR is the default OCR provider. It runs as a Docker container:

```bash
cd host
docker compose up -d paddleocr
```

First run downloads ~600 MB of model weights and takes 30–60 seconds. Subsequent starts are instant.

### 2. Configure the database

Create `host/src/appsettings.Development.json` with your local SQL Server connection string:

```json
{
  "Serilog": { "MinimumLevel": { "Default": "Debug" } },
  "ConnectionStrings": {
    "Default": "Server=YOUR_DB_SERVER;Database=Paperbase-Dev;User ID=YOUR_USER;Password=YOUR_PASSWORD;TrustServerCertificate=true"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  }
}
```

> This file is git-ignored. In Development mode, the application automatically generates temporary OpenIddict certificates — no `.pfx` file is needed. For LocalDB, the committed `appsettings.json` default (`Server=(LocalDb)\MSSQLLocalDB;...`) already works without any override.

### 3. Install client-side libraries

```bash
cd host/src
abp install-libs
```

### 4. Run the backend

```bash
cd host/src
dotnet run
```

API: `https://localhost:44348`. Swagger: `https://localhost:44348/swagger`.

### 5. Install frontend dependencies and run Angular

```bash
cd host/angular
npm install
npm start
```

SPA: `http://localhost:4200`. Default seeded credentials: `admin` / `1q2w3E*`.

## Choosing an OCR provider

Paperbase ships two OCR providers — local **PaddleOCR** (default, CPU, no network) and cloud **Azure Document Intelligence**. PaddleOCR is the zero-config default for development; Azure DI is the recommended production option when data is allowed to leave the network.

Full selection guidance, configuration, and resource footprint: see [docs/text-extraction.md](./docs/text-extraction.md).

## Deploying to production

For database connection strings, OpenIddict signing certificate, string-encryption key, and the Docker layout, see [docs/deployment.md](./docs/deployment.md). For per-release smoke tests, see [docs/deployment-checklist.md](./docs/deployment-checklist.md).

## Documentation

Feature docs (start here for any specific topic):

* [Local development setup](./docs/local-development.md) — prerequisites, Docker sidecars, configuration, troubleshooting
* [Text extraction](./docs/text-extraction.md) — Markdown-first contract, PaddleOCR / Azure DI configuration
* [Classification](./docs/classification.md) — document-type pipeline and prompt tuning
* [Reprocessing](./docs/reprocessing.md) — bulk re-run of classification / field extraction over existing documents after a config change
* [AI provider](./docs/ai-provider.md) — provider wiring for the two keyed chat clients (title generator + structured)
* [Observability](./docs/observability.md) — OpenTelemetry pipeline, aspire-dashboard for local dev, switching OTLP backends
* [Pipeline runs](./docs/pipeline-runs.md) — run history and review-UI payloads
* [Deployment](./docs/deployment.md) — DB, certificate, Docker
* [Deployment checklist](./docs/deployment-checklist.md) — per-release smoke tests

External references:

* [Angular Application](./host/angular/README.md)
* [ABP Framework Documentation](https://abp.io/docs/latest)
* [Application (Single Layer) Startup Template](https://abp.io/docs/latest/solution-templates/application-single-layer)
* [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment)
