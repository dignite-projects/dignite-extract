namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出列的字段来源。统一抹平三类字段的引用差异：
/// <list type="bullet">
///   <item><see cref="System"/> → 取自 <c>Document</c> 顶层 typed column（系统通用字段），
///   <c>ExportColumn.Key</c> 须命中 <see cref="ExportSystemFields"/> 白名单</item>
///   <item><see cref="Extracted"/> → 取自 <c>Document.ExtractedFields</c> 字典（Host / 租户类型绑定字段），
///   <c>ExportColumn.Key</c> = <c>FieldDefinition.Name</c></item>
/// </list>
/// Host 字段与租户字段在执行期无需区分——按 <c>Document.TenantId</c> 单层匹配，
/// 一篇文档只有一层抽取结果，Key 不会撞（字段架构 v2 "两层 mutually exclusive"）。
/// </summary>
public enum ExportColumnSourceKind
{
    System = 0,
    Extracted = 1
}
