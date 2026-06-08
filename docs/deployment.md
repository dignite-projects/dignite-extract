# Deployment

This page covers what a host operator needs to configure to run Paperbase: the relational database, the authentication signing certificate, the OCR sidecar, and the Docker layout. For per-feature configuration (OCR, AI provider) see the matching feature doc.

> **Channel positioning**: Paperbase outputs Markdown + structured metadata to downstream consumers (RAG platforms, business systems, MCP clients). It does **not** ship a vector database, embedding pipeline, or chat platform — those belong on the downstream side. See `CLAUDE.md` → "OUT of scope".

## Topology

```text
Paperbase Host (ASP.NET Core)
  ├─► SQL Server — relational application database (entities, audit, identity, OpenIddict, OutboxEvent)
  └─► OCR sidecar (PaddleOCR) — or Azure Document Intelligence (cloud) — text extraction
                                                                                                    
                  ↓ exports                                                                         
   REST API / MCP server / DistributedEventBus / Webhook — downstream consumers (RAG / business systems)
```

All Paperbase state lives in the single SQL Server database. Markdown + event payloads flow out to downstream consumers; downstream consumers are responsible for their own storage (vector DB / business aggregates / search index).

## Connection strings

Paperbase uses SQL Server as the only persistence backend.

```json
"ConnectionStrings": {
  "Default": "Server=YOUR_DB_SERVER;Database=Paperbase;User ID=YOUR_USER;Password=__SET_FROM_SECRETS__;TrustServerCertificate=true"
}
```

Production deployments should source the password from the platform's secret store (Azure Key Vault, AWS Secrets Manager, env vars injected by the orchestrator, etc.), not from `appsettings.Production.json`.

## Authentication and signing certificate

Paperbase uses OpenIddict. Development mode auto-generates ephemeral certificates; production needs a real signing certificate.

Generate one with:

```bash
dotnet dev-certs https -v -ep openiddict.pfx -p <your-certificate-passphrase>
```

Place `openiddict.pfx` in the host working directory and configure:

```json
"AuthServer": {
  "Authority": "https://your-host.example.com",
  "SwaggerClientId": "Paperbase_Swagger",
  "CertificatePassPhrase": "<your-certificate-passphrase>"
}
```

`CertificatePassPhrase` should also come from the platform's secret store, not from a checked-in file.

For deeper OpenIddict configuration (token lifetimes, encryption-credential rotation, etc.) see the upstream guide: [Configuring OpenIddict for Production](https://abp.io/docs/latest/Deployment/Configuring-OpenIddict#production-environment).

## String encryption key

ABP stores some configuration values (e.g. tenant connection strings) encrypted at rest using `StringEncryption:DefaultPassPhrase`. **Never change this key once data has been written** — encrypted values become unreadable.

```json
"StringEncryption": {
  "DefaultPassPhrase": "<a strong random passphrase, never rotated>"
}
```

`appsettings.Development.json` is git-ignored; `appsettings.Production.json` should be created at deploy time and never committed.

## OCR sidecar

Paperbase ships three OCR options ([comparison](text-extraction.md#ocr--choosing-a-provider)):

- **PaddleOCR** (default) — local sidecar, CPU, never leaves the network. Runs as a Docker container; see [ocr-paddleocr.md](ocr-paddleocr.md).
- **Azure Document Intelligence** — cloud option for production workloads that can leave the network. See [ocr-azure-document-intelligence.md](ocr-azure-document-intelligence.md).
- **Vision-LLM** — cloud, `IChatClient`-based, for phone-photo / thermal-receipt inputs where layout OCR fails (#259). See [ocr-vision-llm.md](ocr-vision-llm.md).

Host module wires exactly one via `[DependsOn(...)]` + matching `<ProjectReference>` in `host/src/Dignite.Paperbase.Host.csproj`.

## AI provider

The two keyed `IChatClient` registrations (title generator + structured) and their model id selection are covered in [ai-provider.md](ai-provider.md). Provider wiring is host-only — credentials never reach the Application or Domain layer.

> **CLAUDE.md constraint**: LLM provider + API key are configured at the host deployment layer, **not** exposed for end-user configuration. Letting business users fill API keys is a product-philosophy mistake (they are not technical users).

## Docker

The deployment Docker Compose layout in `host/etc/docker/docker-compose.yml` wires:

- `paperbase-web` — Angular SPA
- `paperbase-api` — ASP.NET Core API
- `db-migrator` — runs `dotnet run --migrate-database` once at startup
- `sql-server` — SQL Server (Azure SQL Edge image for local-equivalent dev)

```bash
# Build images locally
cd host/etc/build
./build-images-locally.ps1

# Start the stack
cd host/etc/docker
./run-docker.ps1

# Stop containers
cd host/etc/docker
./stop-docker.ps1
```

For local development without the full image build, see [local-development.md](local-development.md) — it runs the API via `dotnet run` against a local SQL Server (LocalDB or container) and only spins up the PaddleOCR / observability sidecars via `host/docker-compose.yml`.

### Serving the Angular UI under a sub-path

By default the Angular UI is built for the domain root (`<base href="/">`) and the `paperbase-web` container serves it at `/`. To host it under a sub-path (e.g. `https://host.example.com/admin`) the **publisher picks the path at build time** — nothing is hard-coded in the repo:

```bash
cd angular
npx nx build host --configuration production --base-href=/admin/
```

The container reads `<base href>` from the built `index.html` on startup (`apps/host/docker-entrypoint.d/40-spa-base-path.sh`) and serves the SPA at exactly that path — no separate nginx knob, no drift. The reverse proxy in front must forward `/admin/*` to the container **as-is (do not strip the prefix)**.

The `<base href>` only fixes static-asset loading. The OAuth callback URLs must carry the sub-path too, or login breaks after the MIME error is gone:

| Where | Key | Value for `/admin` |
|-------|-----|--------------------|
| API config | `App:ClientUrl` | `https://host.example.com/admin` |
| API config | `App:RedirectAllowedUrls` | include `https://host.example.com/admin` |
| API config | `App:CorsOrigins` | include `https://host.example.com` (origin only — no path) |
| API config | `OpenIddict:Applications:Paperbase_App:RootUrl` | `https://host.example.com/admin` |
| Mounted `dynamic-env.json` | `oAuthConfig.redirectUri` + `application.baseUrl` | `https://host.example.com/admin` (no trailing slash) |

Re-run the DB seed / DbMigrator after changing `RootUrl` so the OpenIddict client's stored redirect URIs update (`OpenIddictDataSeedContributor` uses `CreateOrUpdateApplicationAsync`).

## Migrations

EF Core migrations live under `host/src/Migrations/`. Apply them with:

```bash
cd host/src
dotnet run -- --migrate-database
```

Or use ABP's `Dignite.Paperbase.DbMigrator` console runner if your deployment topology calls for a separate migration step (it also seeds initial admin / OpenIddict client data).

## Verifying a release

When deploying to a new environment, upgrading critical dependencies, or shipping changes that touch the core pipeline, run through [deployment-checklist.md](deployment-checklist.md). Treat it as a per-release ticket template — copy the relevant sections and tick boxes as you verify.

## See also

- [Local development setup](local-development.md) — running on a developer laptop
- [Text extraction](text-extraction.md) — choosing and configuring an OCR provider
- [AI provider](ai-provider.md) — wiring the two keyed `IChatClient` registrations
- [Deployment checklist](deployment-checklist.md) — release smoke tests
- [Observability](observability.md) — OpenTelemetry export targets
