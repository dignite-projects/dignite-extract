using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>Input for <see cref="IDocumentTypePackAppService.ImportAsync"/>: one or more packs plus the
/// reconciliation <see cref="Mode"/>. All packs apply to the caller's current layer.</summary>
public class ImportDocumentTypePacksInput
{
    [Required]
    [MinLength(1)]
    [MaxLength(DocumentTypePackConsts.MaxPacksPerImport)]
    public List<DocumentTypePackDto> Packs { get; set; } = new();

    public PackImportMode Mode { get; set; } = PackImportMode.CreateOrUpdate;
}
