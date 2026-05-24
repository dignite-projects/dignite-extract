# Export Templates

Export templates are Paperbase's **file-based egress** — the "last mile" to downstream systems that have no API and can only ingest files (Yayoi, freee, mid-market Yonyou, and similar accounting packages that import CSV). They sit alongside the four programmatic egresses (REST / MCP / EventBus / Webhook) but serve a different audience: a human who downloads a file and imports it into another system.

A template is **per-tenant configuration** following the same two-layer model as `DocumentType` and `FieldDefinition`: Host admins (`CurrentTenant.Id IS NULL`) own Host templates; tenant admins own their own. Paperbase ships **no built-in templates** — there is no industry vertical schema baked in. The export engine only does **field projection → rename → ordering → serialization**, with **zero business transformation** (no tax calculation, no account-code mapping, no currency conversion). Business formats are something tenants *compose* out of a template; Paperbase enables them rather than doing them.

This is the concrete demonstration of the channel's "enable, don't do" philosophy: the OUT-of-scope rule in `CLAUDE.md` forbids pre-baked vertical templates, and this feature is exactly the mechanism that makes that rule livable.

## How it works

```
ExportTemplate (Name, Format, optional DocumentTypeCode, Columns[])
        │
        ▼
ExportAsync(TemplateId, DocumentIds? | Filter)
        │   explicit TenantId predicate  ──►  count > limit ? → fail (ExportDocumentLimitExceeded)
        ▼
documents ──► per-column value projection ──► ExportFileBuilder ──► CSV / XLSX stream
                     │
                     ├─ SourceKind=System    → Document top-level column (whitelisted key)
                     └─ SourceKind=Extracted → Document.ExtractedFields[key]  (key = FieldDefinition.Name)
```

Each `ExportColumn` carries `{ SourceKind, Key, ColumnName, Order }`. The `SourceKind` abstracts away the three kinds of fields:

- **`System`** — a system-common field computed by the pipeline. `Key` must be one of the whitelisted names (see below). These map to `Document`'s top-level typed columns.
- **`Extracted`** — a type-bound field. `Key` is the `FieldDefinition.Name`; the value is read from `Document.ExtractedFields[key]`. Host fields and tenant fields need no distinction here — a document only ever carries one layer's extraction result (field architecture v2's "two layers mutually exclusive"), so keys never collide.

`ColumnName` is the header text written to the file (Unicode is allowed, so Japanese/Chinese headers like `金額` work; control characters are rejected). `Order` sorts columns ascending.

## Whitelisted system fields

`ExportColumn` with `SourceKind=System` accepts only these keys (`ExportSystemFields`):

| Key | Source |
|---|---|
| `Id` | `Document.Id` |
| `Title` | `Document.Title` |
| `DocumentTypeCode` | classification result |
| `LifecycleStatus` / `ReviewStatus` | pipeline state |
| `Language` / `OcrConfidence` / `ClassificationConfidence` | OCR / classification metadata |
| `CreationTime` | upload timestamp |
| `OriginalFileName` / `ContentType` / `FileSize` | `Document.FileOrigin` |

`Markdown` (the full body — too large for a cell; pull it via REST if needed) and `ClassificationReason` (an AI explanation, not document data) are deliberately **excluded**.

## Formats

- **CSV** — UTF-8 with BOM (so Excel renders CJK correctly), RFC-4180 quoting. The mainstream format for accounting-software import.
- **XLSX** — generated with [ClosedXML](https://github.com/ClosedXML/ClosedXML) (MIT). For human review or systems that ingest `.xlsx`.

JSON file export is intentionally **not** offered — programmatic consumers should pull JSON over the REST API rather than download a file.

## Triggering an export

Two paths, both backed by the same `IExportTemplateAppService.ExportAsync`:

- **Operator UI** — pick a template, select documents (checkbox) or apply a filter, download.
- **API** — `POST` `ExportDocumentsInput { TemplateId, DocumentIds? | (LifecycleStatus, DocumentTypeCode, Keyword) }`. When `DocumentIds` is non-empty it wins; otherwise the filter applies.

> EventBus-triggered export is **not** offered: a subscriber that already consumes the EventBus has the structured data — having Paperbase generate a file and hand it back closes no loop.

## Limits & safety

- **Tenant isolation** is enforced with an explicit `TenantId` predicate in the query (not ambient `DataFilter`), per the `CLAUDE.md` security conventions.
- **Per-export document cap** (`ExportTemplateConsts.MaxExportDocumentCount`, default 10000): if the selection matches more rows than the cap, the export **fails** (`ExportDocumentLimitExceeded`) rather than silently truncating — for accounting data, dropping vouchers is more dangerous than an error. Narrow the filter or select fewer documents.
- Permissions: managing templates needs `Paperbase.Documents.Templates.*`; running an export needs `Paperbase.Documents.Export`.

## Example: composing a freee-style import CSV

Paperbase ships nothing freee-specific. You compose the format from your own type-bound fields.

Suppose a tenant has an `invoice` document type with tenant fields `issue_date`, `amount`, `partner_name` (defined via `IFieldDefinitionAppService`, extracted automatically after classification). freee's deal-import CSV wants columns `発生日,金額,取引先`. Configure one template:

| SourceKind | Key | ColumnName | Order |
|---|---|---|---|
| `Extracted` | `issue_date` | `発生日` | 0 |
| `Extracted` | `amount` | `金額` | 1 |
| `Extracted` | `partner_name` | `取引先` | 2 |

Set `Format = Csv` and `DocumentTypeCode = invoice` (so the template only applies to invoices). At month-end, filter the document list to the invoices you want and export — you get a CSV whose header row and column order match what freee expects, ready to import.

The same mechanism produces a Yayoi 仕訳日記帳 layout, a Yonyou voucher CSV, or any other ingest format: define the fields, map them to the target column names, pick the order. If a target format needs a value Paperbase doesn't capture, add a `FieldDefinition` for it — the channel still doesn't *know* what freee is, it just lets you describe the shape.
