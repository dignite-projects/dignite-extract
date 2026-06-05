namespace Dignite.Paperbase.Documents.DocumentTypes;

public static class DocumentTypeConsts
{
    public static int MaxTypeCodeLength { get; set; } = 128;
    public static int MaxDisplayNameLength { get; set; } = 128;

    /// <summary>
    /// <see cref="DocumentType.Description"/> 长度上限。Description 是可选的分类辅助文本，
    /// 仅喂入分类 prompt 帮助 LLM 判型——一两句特征描述足矣，过长反而稀释分类信号，故上限远小于文档正文。
    /// </summary>
    public static int MaxDescriptionLength { get; set; } = 512;

    /// <summary>
    /// <see cref="DocumentType.TypeCode"/> 白名单：仅允许字母 / 数字 / 下划线 / 短横线段，
    /// 段间用 <c>.</c> 分隔（单段合法，多段也合法；不允许首尾或连续 <c>.</c>）。
    /// 与 <see cref="FieldDefinitionConsts.NamePattern"/> 同源的 prompt injection 防御目的——
    /// TypeCode 在 <c>DocumentClassificationWorkflow</c> 内被裸拼进 LLM 系统提示，必须保证字符集
    /// 不含换行 / 引号 / Markdown 控制字符等注入向量。
    /// <para>
    /// 必须是 <c>const</c>：作为 LLM prompt injection 防御链路的安全边界，任何运行时 mutate 都会
    /// 打穿白名单；<see cref="DocumentType"/> 类型加载时本字段会被一次性 cache 成
    /// <c>static readonly Regex</c>，runtime 覆盖不会生效。
    /// </para>
    /// <para>
    /// 长度上限由 <see cref="MaxTypeCodeLength"/> 在 <c>Check.NotNullOrWhiteSpace</c> 中独立约束。
    /// </para>
    /// </summary>
    public const string TypeCodePattern = @"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*$";
}
