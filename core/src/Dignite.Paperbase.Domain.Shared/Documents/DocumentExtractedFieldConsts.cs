namespace Dignite.Paperbase.Documents;

public static class DocumentExtractedFieldConsts
{
    /// <summary>
    /// 类型绑定字段 <c>String</c> 值的最大长度。
    /// <para>
    /// 同时是 DB 列长度（<c>StringValue nvarchar(256)</c>）与 App 层校验上限（<c>ExtractedFieldValueValidator</c>）——
    /// 二者必须一致：校验放过的值都要存得下。改动此值需重新生成 EF migration。
    /// </para>
    /// <para>
    /// 限长（而非 <c>nvarchar(max)</c>）让 <c>StringValue</c> 能进 <c>(TenantId, FieldDefinitionId, StringValue,
    /// DocumentId)</c> 复合索引键，String 字段等值查询走纯 index seek（#209）。类型绑定 String 字段是从 Markdown
    /// 抽取的结构化短值（姓名 / 编号 / 币种 / 案由等），不承载长文本（长文本归 <c>Document.Markdown</c>），故 256
    /// 是充裕上限；且 256×2 bytes + 三个 Guid（48 bytes）= 560 bytes，安全落在 SQL Server 非聚集索引键 1700 bytes 上限内。
    /// </para>
    /// </summary>
    public static int MaxStringValueLength { get; set; } = 256;

    /// <summary>
    /// 类型绑定字段 <c>LongText</c> 值的最大长度（App 层校验上限 + LLM schema 软提示）。
    /// <para>
    /// 与 <see cref="MaxStringValueLength"/> 的 256 不同源：<c>LongText</c> 落独立的 <c>LongTextValue nvarchar(max)</c> 列，
    /// <b>不进任何索引、不可作查询条件</b>（DB 列本身不限长）。此上限是<b>反滥用护栏</b>——防恶意 / 失控文档诱导 LLM
    /// 对长文本字段吐出超大字符串，灌爆 prompt 往返与 DB 行。8000 字符（约数千汉字）从容覆盖摘要 / 描述 / 风险提示等
    /// 长内容抽取场景；真正的全文载荷归 <c>Document.Markdown</c>，不在类型绑定字段承载。改动此值无需重建索引（列不限长），
    /// 但需同步 <c>ExtractedFieldValueValidator</c> 与 <c>FieldExtractionWorkflow</c> 的 schema maxLength。
    /// </para>
    /// </summary>
    public static int MaxLongTextValueLength { get; set; } = 8000;

    /// <summary>
    /// 多值字段（<c>FieldDefinition.AllowMultiple</c>，#212）单字段的最大值个数 = 单文档单字段展开行数硬上限。
    /// <para>
    /// 这是 LLM 触发写入路径的「结果集硬上限」（CLAUDE.md 安全约定 / <c>llm-call-anti-patterns.md</c> § 2.9 在写入侧的同构）：
    /// 恶意文档可诱导 LLM 对多值字段吐超长数组，逐元素落 <c>DocumentExtractedField</c> 行造成行膨胀。
    /// 双层护栏——LLM schema 下发 <c>maxItems</c> 软约束 + <c>ExtractedFieldValueValidator</c> 硬校验（超限整组判不合法 → LLM 路径存 null、
    /// 操作员手改路径 loud fail），与项目「schema 提示 + validator 兜底」既有风格一致。多值仅承载短结构化值列表
    /// （标签 / 关键词 / 多当事人），100 是充裕上限。
    /// </para>
    /// </summary>
    public static int MaxMultiValueCount { get; set; } = 100;
}
