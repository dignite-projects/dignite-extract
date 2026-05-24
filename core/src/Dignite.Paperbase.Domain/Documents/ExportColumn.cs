using System.Linq;
using System.Text.RegularExpressions;
using Volo.Abp;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出模板的一列定义（值对象）。统一引用三类字段，由 <see cref="SourceKind"/> 决定取值来源。
/// <para>
/// 作为 <see cref="ExportTemplate.Columns"/> 整体序列化进 native <c>json</c> 列——
/// get-only 属性 + 唯一带参构造函数让 System.Text.Json 反序列化时复用同一构造（参数名匹配属性名），
/// 构造期校验在 DB round-trip 时复跑（DB 内数据本应合法）。
/// </para>
/// </summary>
public class ExportColumn
{
    private static readonly Regex ExtractedKeyRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>取值来源：System=Document 顶层列；Extracted=ExtractedFields 字典。</summary>
    public ExportColumnSourceKind SourceKind { get; }

    /// <summary>System 列须命中 <see cref="ExportSystemFields"/> 白名单；Extracted 列即 <c>FieldDefinition.Name</c>。</summary>
    public string Key { get; }

    /// <summary>输出文件中的列标题（人类可读，允许中日文，拒绝控制字符）。</summary>
    public string ColumnName { get; }

    /// <summary>列在输出中的排序（升序）。</summary>
    public int Order { get; }

    public ExportColumn(ExportColumnSourceKind sourceKind, string key, string columnName, int order)
    {
        SourceKind = sourceKind;
        Key = ValidateKey(sourceKind, key);
        ColumnName = ValidateColumnName(columnName);
        Order = order;
    }

    private static string ValidateKey(ExportColumnSourceKind sourceKind, string key)
    {
        Check.NotNullOrWhiteSpace(key, nameof(key), ExportTemplateConsts.MaxColumnKeyLength);

        var valid = sourceKind switch
        {
            ExportColumnSourceKind.System => ExportSystemFields.All.Contains(key),
            ExportColumnSourceKind.Extracted => ExtractedKeyRegex.IsMatch(key),
            _ => false
        };

        if (!valid)
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidExportColumnKey)
                .WithData("key", key)
                .WithData("sourceKind", sourceKind.ToString());
        }

        return key;
    }

    private static string ValidateColumnName(string columnName)
    {
        Check.NotNullOrWhiteSpace(columnName, nameof(columnName), ExportTemplateConsts.MaxColumnNameLength);

        if (columnName.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidExportColumnName)
                .WithData("columnName", columnName);
        }

        return columnName;
    }
}
