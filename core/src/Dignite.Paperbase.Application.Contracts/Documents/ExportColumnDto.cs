namespace Dignite.Paperbase.Documents;

public class ExportColumnDto
{
    public ExportColumnSourceKind SourceKind { get; set; }
    public string Key { get; set; } = default!;
    public string ColumnName { get; set; } = default!;
    public int Order { get; set; }
}
