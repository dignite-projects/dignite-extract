namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出模板输出格式。仅 CSV + XLSX——JSON 文件导出价值薄（程序化消费走 REST 即可拿 JSON）。
/// </summary>
public enum ExportFormat
{
    Csv = 0,
    Xlsx = 1
}
