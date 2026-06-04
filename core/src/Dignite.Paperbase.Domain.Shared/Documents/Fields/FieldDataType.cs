namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段数据类型——影响 LLM 抽取时的 schema 提示与下游解析行为。
/// 用于统一 <c>FieldDefinition</c> 实体（按 TenantId 区分 Host vs 租户层；详见 CLAUDE.md "类型绑定字段（B 机制）"）。
/// <para>
/// <see cref="Number"/> 统一表示整数与小数（decimal 存储，对整数精确且范围远超 long）——刻意不区分 Integer / Decimal：
/// 二者查询行为相同（数值等值 + 区间），合并消除"先选 Integer、后来要小数却被 DataType 变更守卫挡住"的错选面。
/// 同理保留 <see cref="Date"/> 与 <see cref="DateTime"/> 分开——纯日期是文档里最常见的时间字段，
/// 强并成 DateTime 会逼出不存在的时分秒、把日期等值退化成区间，得不偿失。
/// </para>
/// <para>
/// <see cref="Text"/> 与 <see cref="LongText"/> 刻意分开，二者是"短结构化值 vs 长内容"两种用途：
/// <list type="bullet">
///   <item><see cref="Text"/>：结构化短值（姓名 / 编号 / 币种 / 案由等），限长 256（<c>DocumentExtractedFieldConsts.MaxTextValueLength</c>），
///   落 <c>TextValue</c> 列、进复合索引键，支持等值查询 + 多值（#209 / #212）。</item>
///   <item><see cref="LongText"/>：长内容（摘要 / 描述 / 风险提示等，由租户用 B 机制自配字段抽取），落独立的
///   <c>LongTextValue</c> 列（<c>nvarchar(max)</c>），<b>不进任何索引、不可作查询条件、不支持多值</b>——
///   纯存储载荷，出口 DTO 照常渲染为字符串。注意这是 B 机制下用户自配的<b>类型绑定字段</b>，
///   与"系统不做全文档 Summary / 派生文本不持久化"（CLAUDE.md）正交——后者约束系统通用字段，不约束用户自配 schema。</item>
/// </list>
/// </para>
/// </summary>
public enum FieldDataType
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,
    DateTime = 4,
    LongText = 5
}
