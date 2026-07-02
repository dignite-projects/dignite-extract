using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// Config import/export ("pack") mechanism for document types + their field definitions (#444). Export
/// serializes the caller's-layer config to portable, declarative packs; import applies packs back
/// idempotently (match type by TypeCode, field by Name; re-import produces no duplicates). Layer-aware:
/// reads and writes only the caller's current layer, never crossing the Host/tenant boundary.
/// </summary>
public interface IDocumentTypePackAppService : IApplicationService
{
    /// <summary>Export a single document type (in the caller's layer) and its fields as a pack.</summary>
    Task<DocumentTypePackDto> ExportAsync(Guid id);

    /// <summary>Export every document type in the caller's layer as a list of packs.</summary>
    Task<List<DocumentTypePackDto>> ExportAllAsync();

    /// <summary>Idempotently apply one or more packs to the caller's layer, per the chosen mode.</summary>
    Task<DocumentTypePackImportResultDto> ImportAsync(ImportDocumentTypePacksInput input);
}
