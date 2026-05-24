using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出文件序列化器。两种已知格式内联 dispatch——不抽 IExportFormatWriter provider 框架
/// （CLAUDE.md：三行相似好过过早抽象）。未来若需自定义格式（固定宽度 / XML）再抽契约。
/// </summary>
internal static class ExportFileBuilder
{
    public static byte[] Build(ExportFormat format, IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
        => format switch
        {
            ExportFormat.Xlsx => BuildXlsx(headers, rows),
            _ => BuildCsv(headers, rows)
        };

    private static byte[] BuildCsv(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
        }

        // UTF-8 BOM——让 Excel 正确识别中日文 UTF-8 内容（无 BOM 时 Excel 按本地编码解析会乱码）。
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }

    private static byte[] BuildXlsx(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Export");

        for (var c = 0; c < headers.Count; c++)
        {
            ws.Cell(1, c + 1).Value = headers[c];
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < headers.Count; c++)
            {
                ws.Cell(r + 2, c + 1).Value = c < row.Length ? (row[c] ?? string.Empty) : string.Empty;
            }
        }

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var neutralized = NeutralizeFormula(value);

        if (neutralized.Contains(',') || neutralized.Contains('"') || neutralized.Contains('\n') || neutralized.Contains('\r'))
        {
            return "\"" + neutralized.Replace("\"", "\"\"") + "\"";
        }

        return neutralized;
    }

    // CSV / spreadsheet formula injection 防御：值（忽略前导空白后）以 = + - @ 或 TAB / CR 开头时，
    // 前缀单引号，阻止 Excel / Sheets 把它当公式执行。表头与单元格都来自用户控制文本
    // （模板名 / 列名 / 文件名 / 抽取字段值），且本 builder 为 Excel 加了 UTF-8 BOM，风险被放大。
    // XLSX 不需要此处理——ClosedXML 把 string 值写成明确的文本 cell（非 formula cell），不会被执行。
    private static string NeutralizeFormula(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // 检查原始首字符（捕获以 TAB/CR 开头）+ trim 后首字符（捕获前导空白后的 = + - @）——
        // Excel 会在解析公式前 trim 前导空格，故两者都算触发面。
        var trimmed = value.TrimStart();
        var dangerous = IsFormulaTrigger(value[0])
            || (trimmed.Length > 0 && IsFormulaTrigger(trimmed[0]));

        return dangerous ? "'" + value : value;
    }

    private static bool IsFormulaTrigger(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}
