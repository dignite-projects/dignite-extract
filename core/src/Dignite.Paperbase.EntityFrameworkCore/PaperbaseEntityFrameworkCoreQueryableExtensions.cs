using System.Linq;
using Dignite.Paperbase.Documents;
using Microsoft.EntityFrameworkCore;

namespace Dignite.Paperbase;

public static class PaperbaseEntityFrameworkCoreQueryableExtensions
{
    public static IQueryable<Document> IncludeDetails(
        this IQueryable<Document> queryable,
        bool include = true)
    {
        if (!include)
        {
            return queryable;
        }

        // 两个集合导航（PipelineRuns + ExtractedFieldValues / Issue #206）。单查询会产生
        // PipelineRuns × ExtractedFieldValues 笛卡尔积、并把大字段 Markdown 在每行重复——
        // 用 AsSplitQuery 拆成多条查询各取一个集合，避免行爆炸 / Markdown 重复传输。
        return queryable
            .Include(x => x.PipelineRuns)
            .Include(x => x.ExtractedFieldValues)
            .AsSplitQuery();
    }
}
