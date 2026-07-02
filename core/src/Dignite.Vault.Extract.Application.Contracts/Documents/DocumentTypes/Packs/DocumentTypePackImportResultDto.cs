using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>Summary of an import: per-pack outcomes plus rolled-up totals so a caller can report exactly
/// what was created / updated / skipped without re-diffing.</summary>
public class DocumentTypePackImportResultDto
{
    public List<DocumentTypePackItemResultDto> Items { get; set; } = new();

    public int TypesCreated { get; set; }
    public int TypesUpdated { get; set; }
    public int TypesSkipped { get; set; }
    public int FieldsCreated { get; set; }
    public int FieldsUpdated { get; set; }
    public int FieldsSkipped { get; set; }
}

/// <summary>Per-pack outcome: what happened to the type, and how its fields were reconciled.</summary>
public class DocumentTypePackItemResultDto
{
    public string TypeCode { get; set; } = default!;
    public PackItemAction TypeAction { get; set; }
    public int FieldsCreated { get; set; }
    public int FieldsUpdated { get; set; }
    public int FieldsSkipped { get; set; }
}
