namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段数据类型——影响 LLM 抽取时的 schema 提示与下游解析行为。
/// 用于统一 <c>FieldDefinition</c> 实体（按 TenantId 区分 Host vs 租户层；详见 CLAUDE.md "类型绑定字段（B 机制）"）。
/// </summary>
public enum FieldDataType
{
    String = 0,
    Integer = 1,
    Decimal = 2,
    Boolean = 3,
    Date = 4,
    DateTime = 5
}
