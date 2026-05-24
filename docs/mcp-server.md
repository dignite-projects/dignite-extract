# MCP Server

Paperbase exposes an **MCP (Model Context Protocol) server** as one of its channel exits, alongside REST, EventBus, and Webhook. It lets AI clients (Claude Desktop, Cursor, any MCP client) read Paperbase documents and search them — without writing custom integration code.

> **Paperbase is a channel layer.** The MCP server exposes documents as resources plus a keyword/metadata **search tool**. It does **not** do semantic / vector retrieval (that belongs to a downstream RAG consumer — see CLAUDE.md "OUT of scope"). It is an MCP **server** only; Paperbase never acts as an MCP client.

## What v1 exposes

| MCP primitive | Paperbase mapping |
| --- | --- |
| `resources/read` (template `paperbase://documents/{id}`) | A small system-metadata header (type, lifecycle, language, created-at) followed by the document's Markdown body wrapped in `<document>` tags. The wrapped body is external, untrusted content — the header tells clients to treat it as data, not instructions |
| `tools/call` → `search_paperbase_documents` | Keyword (title / file name / Markdown) + metadata (`documentTypeCode`, `lifecycleStatus`) + `ExtractedFields` field-value filter. Returns up to 50 thin rows; each row carries the `paperbase://documents/{id}` uri to read the full document |

The server declares only the bare `resources` capability — **no `subscribe` / `listChanged`**. v1 is pull-only: clients read on demand. Push (resource subscriptions + `notifications/resources/updated` / `list_changed`) is a follow-up increment (see issue #197).

The transport is **Streamable HTTP** at `/mcp`. (The legacy SSE transport is not exposed.)

## Authentication

The `/mcp` endpoint reuses the host's existing **OpenIddict Bearer** auth — the same scheme as the REST API (audience `Paperbase`). There is no separate API-key system in v1.

Every request to `/mcp` requires a valid Bearer token (`RequireAuthorization` on the endpoint). In addition, each tool/resource call performs an explicit server-side permission assertion: the caller must be granted **`Paperbase.Documents`** (`PaperbasePermissions.Documents.Default`). A token without that permission gets an authorization error even though the endpoint accepted the connection (fail-closed, defense in depth).

Obtain a token from the Paperbase auth server (`AuthServer:Authority`) using your normal OAuth flow (e.g. client-credentials for a service client, or an interactive user token), then grant the client/user the `Paperbase.Documents` permission via the admin UI.

> Multi-tenancy is currently disabled (`PaperbaseHostModule.IsMultiTenant = false`), so all access resolves to the host document space. Tenant isolation is still enforced fail-closed in code (explicit `TenantId` predicate), so it stays correct if multi-tenancy is later enabled.

## Connect Claude Desktop

Claude Desktop talks to remote HTTP MCP servers through the `mcp-remote` stdio bridge. In `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "paperbase": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://your-paperbase-host/mcp",
        "--header", "Authorization: Bearer ${PAPERBASE_TOKEN}"
      ],
      "env": { "PAPERBASE_TOKEN": "<your-bearer-token>" }
    }
  }
}
```

Restart Claude Desktop; the `search_paperbase_documents` tool and `paperbase://documents/{id}` resources become available.

## Connect Cursor

Cursor reads remote HTTP MCP servers directly. In `.cursor/mcp.json` (project) or the global Cursor MCP settings:

```json
{
  "mcpServers": {
    "paperbase": {
      "url": "https://your-paperbase-host/mcp",
      "headers": { "Authorization": "Bearer <your-bearer-token>" }
    }
  }
}
```

## Typical flow

1. Client calls `search_paperbase_documents` with a `keyword` (and optionally `documentTypeCode`, or a `fieldName` + `fieldValue` pair to filter on an extracted field).
2. The tool returns thin rows, each with a `paperbase://documents/{id}` uri.
3. Client calls `resources/read` on a uri to pull that document's full Markdown.

## Notes & limits

- **Result cap.** The search tool returns at most `DocumentConsts.MaxSearchResultCount` (50) rows. This is a fail-closed safety limit, not a paging window.
- **`ExtractedFields` search performance.** Field-value filtering runs `JSON_VALUE` over the `ExtractedFields` `json` column. SQL Server 2025 `CREATE JSON INDEX` is still in preview, so there is **no JSON index yet** — these queries do a table scan. The index lands once the feature reaches GA (issue #198).
- **Field-value semantics.** `fieldName` + `fieldValue` is a string equality match against the extracted field's JSON scalar value. `fieldName` is validated against the field-name whitelist (letters, digits, underscore, hyphen — the same `FieldDefinition.Name` contract) and emitted as a quoted JSON path key before it touches SQL.
- **Input length caps.** Over-length `keyword`, `documentTypeCode`, or `fieldValue` are rejected (empty result, no scan) to keep an authorized client from forcing expensive table scans through the AI-facing tool.
- **Untrusted body.** A document's Markdown is wrapped in `<document>` tags when read as a resource. Embedded text is never treated as instructions by Paperbase, but consuming clients should still treat document content as untrusted.
- **Single instance.** The Streamable HTTP transport keeps session state in-process. Running multiple host instances behind a load balancer requires session affinity (or a future stateless/distributed-store configuration).
