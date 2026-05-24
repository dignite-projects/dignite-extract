namespace Dignite.Paperbase.Documents;

public static class DocumentConsts
{
    public static int MaxOriginalFileBlobNameLength { get; set; } = 512;

    public static int MaxDocumentTypeCodeLength { get; set; } = 128;

    public static int MaxTitleLength { get; set; } = 256;

    /// <summary>OCR / 抽取阶段检测到的语言 tag（ISO 639-1 或 IETF）。</summary>
    public static int MaxLanguageLength { get; set; } = 16;

    /// <summary>操作员列表 keyword 搜索框的最大长度。</summary>
    public static int MaxSearchKeywordLength { get; set; } = 128;

    /// <summary>
    /// 程序化 / LLM 触发检索（MCP 检索 tool 等）单次结果硬上限。
    /// fail-closed 安全门：防 prompt-injection 诱导宽泛查询炸 LLM context / 制造费用攻击。
    /// 设为编译期 <c>const</c>——安全边界不可被运行时配置放大。
    /// </summary>
    public const int MaxSearchResultCount = 50;

    /// <summary>
    /// 程序化 / LLM 触发检索按 ExtractedFields 字段值过滤时的字段值长度上限。
    /// fail-closed 安全门：超长输入直接空结果，不进 JSON_VALUE 全表扫，防 DB / CPU 滥用。
    /// </summary>
    public const int MaxSearchFieldValueLength = 512;
}
