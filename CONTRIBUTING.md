# Contributing to Dignite Document AI

Thank you for considering a contribution. This page covers the practical workflow; the architectural contract lives in [CLAUDE.md](./CLAUDE.md) and is the truth source for what belongs in this repository (and what is explicitly out of scope).

## Development environment

Follow [README → Getting started](./README.md#getting-started-local-development). In short: .NET SDK 10, Node.js 20+, SQL Server (LocalDB works), optionally Docker Desktop for the PaddleOCR sidecar and the local OpenTelemetry dashboard. An OpenAI-compatible LLM API key is mandatory — see [docs/ai-provider.md](./docs/ai-provider.md).

## Running tests

### Backend (xUnit)

All backend test projects are part of the root solution:

```bash
dotnet test Dignite.DocumentAI.slnx
```

Or run individual projects:

```bash
# Core
dotnet test core/test/Dignite.DocumentAI.Domain.Tests
dotnet test core/test/Dignite.DocumentAI.Application.Tests
dotnet test core/test/Dignite.DocumentAI.EntityFrameworkCore.Tests
dotnet test core/test/Dignite.DocumentAI.Mcp.Tests
dotnet test core/test/Dignite.DocumentAI.Ocr.VisionLlm.Tests

# Host
dotnet test host/test/Dignite.DocumentAI.Host.Tests
```

### Frontend (Vitest)

```bash
cd angular
npm install
npm test          # vitest run
npm run lint
```

## Code conventions

- **ABP conventions** — `.claude/rules/abp-core.md` and the other files under [`.claude/rules/`](./.claude/rules/) are normative (dependency direction, base classes, `IClock`, repositories, anti-patterns). They are written for AI coding assistants but apply equally to human contributors.
- **Architecture rules** — [CLAUDE.md](./CLAUDE.md) defines the channel boundary: Markdown-first data flow, the two-layer document-type model, the exit contracts, and the security covenant for LLM call paths (`.claude/rules/llm-call-anti-patterns.md`).
- Middleware is configured **only** in the host application, never in core modules.

## Issue-first principle

Any change that touches a **channel boundary** — the OCR / text-extraction pipeline, exit contracts (REST / MCP / EventBus / Webhook, event payloads), the field architecture, the document-type tier system, the Markdown-first contract, or the security covenant — must start with a GitHub Issue and reach consensus there **before** any code is written. This is rule 3 of CLAUDE.md's processing rules.

Pure implementation details (bug fixes, wording corrections) don't need an Issue — a descriptive commit message is enough.

Also note CLAUDE.md's "OUT of scope" list: business modules (contract / invoice / HR management), RAG features (vectorization, retrieval, chat), and end-user LLM configuration are not accepted into this repository — downstream consumers build those in their own repositories against the exit contracts.

## Commit style

The repository uses [Conventional Commits](https://www.conventionalcommits.org/): `feat(scope): …`, `fix: …`, `docs: …`, `chore: …`, `refactor(scope): …`. Existing commit descriptions are mostly written in Chinese; both Chinese and English descriptions are accepted — pick whichever expresses the change most clearly.

## Pull requests

- Target the `main` branch.
- CI (`.github/workflows/ci.yml`) must pass: it builds the core solution and the host, runs the backend test suites (Domain, Application, EntityFrameworkCore, MCP, OCR VisionLlm, and host), and builds / lints / tests the Angular workspace.
- Keep PRs scoped to one concern, and reference the related Issue (required for boundary-touching changes, see above).
- Use the PR template checklist (`.github/pull_request_template.md`).
