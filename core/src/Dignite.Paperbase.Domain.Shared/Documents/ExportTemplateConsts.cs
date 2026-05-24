namespace Dignite.Paperbase.Documents;

public static class ExportTemplateConsts
{
    public static int MaxNameLength { get; set; } = 128;
    public static int MaxColumnNameLength { get; set; } = 128;

    /// <summary>列 Key 长度上限。对齐 <see cref="FieldDefinitionConsts.MaxNameLength"/>——Extracted 列的 Key 即 FieldDefinition.Name。</summary>
    public static int MaxColumnKeyLength { get; set; } = 64;

    public static int MaxColumnCount { get; set; } = 100;

    /// <summary>单次同步导出的文档数硬上限——防止宽泛筛选条件打爆内存 / 生成超大文件。host 可覆盖。</summary>
    public static int MaxExportDocumentCount { get; set; } = 10000;
}
