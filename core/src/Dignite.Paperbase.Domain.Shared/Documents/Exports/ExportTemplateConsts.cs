namespace Dignite.Paperbase.Documents.Exports;

public static class ExportTemplateConsts
{
    public static int MaxNameLength { get; set; } = 128;

    public static int MaxColumnCount { get; set; } = 100;

    /// <summary>单次同步导出的文档数硬上限——防止宽泛筛选条件打爆内存 / 生成超大文件。host 可覆盖。</summary>
    public static int MaxExportDocumentCount { get; set; } = 10000;
}
