namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>
/// How <see cref="IDocumentTypePackAppService.ImportAsync"/> reconciles a pack against the current layer.
/// Neither mode ever deletes a locally-present type or field the pack omits — import is additive by design;
/// removal stays a deliberate CRUD action.
/// </summary>
public enum PackImportMode
{
    /// <summary>Create what is missing and overwrite existing types/fields (matched by TypeCode / Name) to
    /// match the pack. The default: the pack is the source of truth.</summary>
    CreateOrUpdate = 0,

    /// <summary>Create only what is missing; leave every existing type/field untouched (preserving local
    /// customizations). Existing rows are reported as skipped.</summary>
    CreateOnly = 1
}
