# Local Development Setup

This guide covers everything needed to run Paperbase on a local machine.

## Prerequisites

| Requirement | Minimum version | Notes |
|-------------|----------------|-------|
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet) | 10.0 | |
| [Node.js](https://nodejs.org) | 18 | Required for Angular frontend |
| [Docker Desktop](https://www.docker.com/products/docker-desktop) | any recent | Runs Qdrant and PaddleOCR |
| PostgreSQL | 16+ | Must have **pgvector** extension installed |
| ABP CLI | latest | `dotnet tool install -g Volo.Abp.Cli` |

### Installing pgvector

**Ubuntu / Debian (including WSL):**

```bash
sudo apt install -y postgresql-16-pgvector
```

If the package is not found, add the PGDG repository first:

```bash
sudo sh -c 'echo "deb https://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list'
wget -qO- https://www.postgresql.org/media/keys/ACCC4CF8.asc | sudo tee /etc/apt/trusted.gpg.d/postgresql.asc > /dev/null
sudo apt update
sudo apt install -y postgresql-16-pgvector
```

**Docker:** use the `pgvector/pgvector:pg17` image instead of `postgres:17`.

**Other platforms:** see the [pgvector installation guide](https://github.com/pgvector/pgvector#installation).

---

## Infrastructure Services

Paperbase requires two services that run as Docker containers, plus one optional one for local observability:

| Service | Port | Purpose | Profile |
|---------|------|---------|---------|
| **Qdrant** | 6333 (HTTP), 6334 (gRPC) | Vector store for document embeddings and hybrid search | default |
| **PaddleOCR** | 8866 | OCR sidecar for scanned documents (PP-StructureV3, CPU mode) | default |
| **aspire-dashboard** | 18888 (UI), 4317 (OTLP gRPC) | Local OpenTelemetry backend — renders traces / metrics / logs from the app at `http://localhost:18888`. Optional but recommended; the project's launchSettings.json defaults to sending OTel signals to `localhost:4317`. | `observability` |

Start the required services:

```bash
cd host
docker compose up -d
```

Add the dashboard when you want to see traces / metrics:

```bash
docker compose --profile observability up -d aspire-dashboard
```

See [observability.md](./observability.md) for what's emitted, how to point at a different OTLP backend (Jaeger / Datadog / Tempo / Azure Monitor), and the tagging policy.

Verify Qdrant is ready:

```bash
curl http://localhost:6333/healthz
# Expected: {"title":"qdrant - healthy","status":"ok"}
```

Verify PaddleOCR is ready:

```bash
curl http://localhost:8866/ping
# Expected: {"status":"success"}
```

To stop the services:

```bash
docker compose down
```

Qdrant data is persisted in a Docker-managed volume (`qdrant_data`). To wipe it:

```bash
docker compose down -v
```

---

## Backend Configuration

Create `host/src/appsettings.Development.json`. This file is git-ignored.

### Minimal configuration (no AI features)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  },
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=Paperbase-Dev;Username=YOUR_USER;Password=YOUR_PASSWORD"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  }
}
```

### Full configuration (with AI)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug"
    }
  },
  "ConnectionStrings": {
    "Default": "Host=localhost;Database=Paperbase-Dev;Username=YOUR_USER;Password=YOUR_PASSWORD"
  },
  "StringEncryption": {
    "DefaultPassPhrase": "any-random-string-here"
  },
  "PaperbaseAI": {
    "Endpoint": "https://api.openai.com/v1",
    "ApiKey": "sk-...",
    "ChatModelId": "gpt-4o-mini",
    "EmbeddingModelId": "text-embedding-3-small"
  }
}
```

To use Azure OpenAI instead of OpenAI, set `Endpoint` to your Azure OpenAI endpoint and `ApiKey` to your Azure key. See [docs/ai-provider.md](./ai-provider.md) for full provider configuration.

> **OpenIddict certificates**: In Development mode, temporary signing and encryption certificates are generated automatically. No `.pfx` file is needed.

---

## Running the Backend

Install client-side libraries (run once after cloning, or when dependencies change):

```bash
cd host/src
abp install-libs
```

Apply database migrations and seed initial data, then start the API:

```bash
cd host/src
dotnet run
```

The API will be available at `https://localhost:44348`. Swagger UI: `https://localhost:44348/swagger`.

---

## Running the Angular Frontend

```bash
cd host/angular
npm install
npm start
```

The SPA will be available at `http://localhost:4200`.

Default credentials (seeded on first run):

| Field | Value |
|-------|-------|
| Username | `admin` |
| Password | `1q2w3E*` |

---

## Full Startup Checklist

1. PostgreSQL is running and `pgvector` extension is installed in the target database
2. `docker compose up -d` completed successfully in `host/`
3. `host/src/appsettings.Development.json` exists with valid connection string and passphrase
4. `dotnet run` started without errors in `host/src`
5. `npm start` started in `host/angular`

---

## Troubleshooting

### Port conflicts

If Docker fails to bind a port, another process is already using it. Check with:

```bash
# Windows
netstat -ano | findstr ":6333 :6334 :8866"

# Linux / WSL
ss -tlnp | grep -E '6333|6334|8866'
```

### Qdrant not ready after `docker compose up`

Qdrant initializes its storage on first start. The healthcheck in `docker-compose.yml` waits up to 50 seconds. If `dotnet run` starts before Qdrant is healthy, the embedding pipeline will fail on the first document upload. Re-upload the document once Qdrant is ready.

### Database migration errors

If migrations fail, ensure the PostgreSQL user has `CREATE TABLE` and `CREATE EXTENSION` privileges, and that `pgvector` is installed:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
```

### PaddleOCR slow on first request

PaddleOCR loads the PP-StructureV3 model into memory on the first request. Subsequent requests are fast. The first upload after a cold start may take 30–60 seconds.
