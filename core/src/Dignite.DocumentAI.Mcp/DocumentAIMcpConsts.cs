namespace Dignite.DocumentAI.Mcp;

/// <summary>
/// MCP 出口适配器的传输层常量。与 <c>DocumentConsts.MaxSearchResultCount</c> 同源纪律
/// （.claude/rules/llm-call-anti-patterns.md 反例 B 要点 3）：LLM 触发路径的结果集硬上限
/// 一律编译期 <c>const</c>——安全边界不可被运行时配置放大。
/// </summary>
public static class DocumentAIMcpConsts
{
    /// <summary>
    /// 文档类型枚举（<c>list_document_types</c> tool 与 <c>resources/list</c>）单次返回的类型数硬上限。
    /// 租户 admin 可自建任意多文档类型——无上限枚举会炸 LLM context / 形成费用攻击面。
    /// 取 100（<c>DocumentConsts.MaxSearchResultCount</c> 的 2 倍）：类型是 schema 级元数据，
    /// 单条体积远小于文档检索行（无 Markdown / 字段值载荷），而正常部署的类型数在几十以内——
    /// 100 覆盖正常发现场景，同时把病态规模（数千类型）挡在边界外。
    /// 截断按 TypeCode 稳定排序后进行；tool 出口以 <c>truncated</c> + <c>totalCount</c> 显式告知 LLM 还有更多。
    /// </summary>
    public const int MaxDocumentTypeResults = 100;
}
