using System;

namespace Dignite.Paperbase.Mcp.Documents;

/// <summary>
/// MCP 文档资源 URI 的单一来源——资源模板（read 路径）与检索 tool 返回行共用同一 scheme，
/// 防止在多处手写 <c>paperbase://documents/...</c> 漂移导致 read-after-search 断裂。
/// </summary>
public static class DocumentResourceUri
{
    private const string Prefix = "paperbase://documents/";

    /// <summary>资源 URI 模板。用于 <c>[McpServerResource(UriTemplate = ...)]</c>，必须是编译期常量。</summary>
    public const string Template = Prefix + "{id}";

    public static string Format(Guid documentId) => Prefix + documentId;
}
