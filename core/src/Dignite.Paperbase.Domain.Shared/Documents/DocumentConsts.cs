namespace Dignite.Paperbase.Documents;

public static class DocumentConsts
{
    public static int MaxTitleLength { get; set; } = 256;

    /// <summary>OCR / 抽取阶段检测到的语言 tag（ISO 639-1 或 IETF）。</summary>
    public static int MaxLanguageLength { get; set; } = 16;

    /// <summary>
    /// 原生 payload 归档大小上限（字节，默认 16 MiB，#210）。
    /// 超限 → 记 warning、manifest 置 null、文本提取仍成功（归档 fail-open）。
    /// 可被测试或 host 覆盖（与 MaxTitleLength 等同为 static mutable）。
    /// </summary>
    public static long MaxNativePayloadArchiveBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// 程序化 / LLM 触发检索（MCP 检索 tool 等）单次结果硬上限。
    /// fail-closed 安全门：防 prompt-injection 诱导宽泛查询炸 LLM context / 制造费用攻击。
    /// 设为编译期 <c>const</c>——安全边界不可被运行时配置放大。
    /// </summary>
    public const int MaxSearchResultCount = 50;

    /// <summary>
    /// 程序化 / LLM 触发检索按字段值过滤时的字段值长度上限。
    /// fail-closed 安全门：超长输入直接空结果，不进字段值列扫描，防 DB / CPU 滥用。
    /// </summary>
    public const int MaxSearchFieldValueLength = 512;

    /// <summary>
    /// 单次检索可叠加的 ExtractedFields 字段过滤器数量上限（多字段之间取 AND）。
    /// fail-closed 安全门：防 prompt-injection 灌入海量过滤器撑爆拼接 SQL / 参数数量。
    /// 设为编译期 <c>const</c>——安全边界不可被运行时配置放大。
    /// </summary>
    public const int MaxSearchFieldFilters = 10;
}
