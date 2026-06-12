using System.Collections.Generic;

namespace Dignite.DocumentAI.Mcp.Documents;

/// <summary>
/// <c>list_document_types</c> tool 的结构化返回值（LLM-facing）。
/// <see cref="Types"/> 按 TypeCode 稳定排序并截断到 <see cref="DocumentAIMcpConsts.MaxDocumentTypeResults"/>
/// （结果集硬上限，llm-call-anti-patterns 反例 B 要点 3）；超限时 <see cref="Truncated"/> +
/// <see cref="TotalCount"/> 显式告知 LLM 还有更多——截断是安全边界而非分页，本 tool 不提供分页参数。
/// </summary>
public sealed record DocumentTypeListResult
{
    /// <summary>当前主体可见的文档类型字段 schema（按 TypeCode 升序，最多 MaxDocumentTypeResults 个）。</summary>
    public required IReadOnlyList<DocumentTypeSchema> Types { get; init; }

    /// <summary>当前主体可见的类型总数（含因超限未返回的）。</summary>
    public required int TotalCount { get; init; }

    /// <summary><c>true</c> 表示可见类型数超过单次返回上限，<see cref="Types"/> 仅含 TypeCode 字典序最前的一段。</summary>
    public required bool Truncated { get; init; }
}
