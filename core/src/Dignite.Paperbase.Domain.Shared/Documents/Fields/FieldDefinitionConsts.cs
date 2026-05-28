namespace Dignite.Paperbase.Documents.Fields;

public static class FieldDefinitionConsts
{
    public static int MaxNameLength { get; set; } = 64;
    public static int MaxDisplayNameLength { get; set; } = 128;
    public static int MaxPromptLength { get; set; } = 1024;
    public static int MaxDocumentTypeCodeLength { get; set; } = 128;

    /// <summary>
    /// Field <see cref="FieldDefinition.Name"/> 白名单：仅允许字母 / 数字 / 下划线 / 短横线，1-64 字符。
    /// 防 prompt injection——Name 会被字面拼进 LLM prompt 的 JSON schema 描述，
    /// 不能允许换行 / 标点 / Markdown 控制字符渗入 prompt 上下文。
    /// <para>
    /// 必须是 <c>const</c>：这是 LLM prompt injection 防御链路的安全边界
    /// （参见 <see cref="FieldDefinition"/> XML doc 与 <c>FieldExtractionWorkflow</c> 的相关注释）。
    /// 任何运行时 mutate 都会把白名单打穿，攻击面凭空多一条；
    /// 同时 <c>FieldDefinition</c> 类型加载时将本字段一次性 cache 成 static readonly Regex，
    /// runtime 覆盖也不会生效，制造 footgun。
    /// </para>
    /// </summary>
    public const string NamePattern = @"^[A-Za-z0-9_\-]{1,64}$";
}
