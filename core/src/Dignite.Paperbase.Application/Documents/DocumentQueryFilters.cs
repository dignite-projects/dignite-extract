using System.Linq;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 跨 AppService 共享的 <see cref="Document"/> 查询谓词。提取出来避免文档列表与导出两条路径
/// 各自手抄同一套过滤逻辑导致 drift（如新增搜索字段时只改一处）。
/// </summary>
internal static class DocumentQueryFilters
{
    /// <summary>
    /// Keyword 子串匹配：命中 Title / 原始文件名 / Markdown 全文任一。Markdown.Contains 留在 WHERE
    /// （SQL 端 LIKE，不物化 Markdown）；规模化后可在 Markdown 上建 SQL Server 全文索引替换。
    /// </summary>
    public static IQueryable<Document> WhereKeyword(IQueryable<Document> query, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return query;
        }

        var trimmed = keyword.Trim();
        return query.Where(d =>
            (d.Title != null && d.Title.Contains(trimmed)) ||
            (d.FileOrigin.OriginalFileName != null && d.FileOrigin.OriginalFileName.Contains(trimmed)) ||
            (d.Markdown != null && d.Markdown.Contains(trimmed)));
    }
}
