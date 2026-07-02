namespace Dignite.Vault.Extract.Documents.DocumentTypes.Packs;

/// <summary>What an import did to a single type (or is reported per-field in the result counts).</summary>
public enum PackItemAction
{
    Created = 0,
    Updated = 1,
    Skipped = 2
}
