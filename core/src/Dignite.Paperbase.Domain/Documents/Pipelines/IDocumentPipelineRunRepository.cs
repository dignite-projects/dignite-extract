using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// <see cref="DocumentPipelineRun"/> 的自定义仓储（拆于 #216：从 Document child entity 升为独立聚合根）。
/// 通用 CRUD 由继承的 <see cref="IRepository{TEntity, TKey}"/> 提供；这里只声明 <see cref="DocumentPipelineRunManager"/>
/// / <see cref="Document"/> 编排路径所需的自定义查询。
/// </summary>
public interface IDocumentPipelineRunRepository : IRepository<DocumentPipelineRun, Guid>
{
    /// <summary>
    /// 取 (<paramref name="documentId"/>, <paramref name="pipelineCode"/>) 下 <see cref="DocumentPipelineRun.AttemptNumber"/>
    /// 最大的 run；找不到返回 <c>null</c>。
    /// 用于：<see cref="DocumentPipelineRunManager.QueueAsync"/> 计算下一个 AttemptNumber；
    /// <c>DocumentAppService.RetryPipelineAsync</c> 判可重试；<c>DocumentPipelineRunAccessor.BeginOrStartAsync</c>
    /// 找最新 Pending fallback。
    /// </summary>
    Task<DocumentPipelineRun?> FindLatestByDocumentAndCodeAsync(
        Guid documentId,
        string pipelineCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次性查 <paramref name="documentId"/> 下、<paramref name="pipelineCodes"/> 中每个 PipelineCode 的最新 run，
    /// 字典 key = PipelineCode（仅返回有数据的 code）。
    /// 用于：<see cref="DocumentPipelineRunManager.DeriveLifecycleAsync"/> 算 <see cref="Document.LifecycleStatus"/>
    /// （避免 N 次 round-trip）。
    /// <para>
    /// <b>契约语义</b>：结果必须反映本 UoW 内尚未 flush 的修改（DeriveLifecycle 紧跟 Manager 的
    /// <c>UpdateAsync(run, autoSave:false)</c> / Insert 调用）。EFCore 实现合并 change-tracker 的 Local entries；
    /// in-memory fake 因直接持有 run 引用天然满足。实现方不得只返回"已落库"的陈旧视图。
    /// </para>
    /// </summary>
    Task<Dictionary<string, DocumentPipelineRun>> GetLatestRunsByCodesAsync(
        Guid documentId,
        IReadOnlyCollection<string> pipelineCodes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 <paramref name="documentId"/> 查所有 run（按 (PipelineCode, AttemptNumber) 排序）。
    /// 用于：独立 <c>IDocumentPipelineRunAppService.GetListByDocumentAsync</c> 暴露给前端文档详情页。
    /// </summary>
    Task<List<DocumentPipelineRun>> GetListByDocumentAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 插入一条全新的 pipeline 尝试（attempt），并立即落库（autoSave）。
    /// 若撞 <c>(DocumentId, PipelineCode, AttemptNumber)</c> 唯一索引（唯一现实成因：同一 Failed pipeline
    /// 被并发重试——见 <see cref="DocumentPipelineRunManager.QueueAsync"/>），抛
    /// <c>BusinessException(PaperbaseErrorCodes.Pipeline.RetryInProgress)</c>——此刻赢家的新 run 正是 Pending，
    /// "已有进行中的尝试"语义精确命中。
    /// <para>
    /// <b>跨库纪律（#239）</b>：唯一约束冲突的识别收敛在持久化层，抓的是 EF Core <b>provider 无关</b>的
    /// <c>DbUpdateException</c> 类型（所有 provider 都把唯一约束冲突包成它），<b>不</b>嗅探异常 message /
    /// SQL Server 错误码。Domain 层因此不再引用任何 EF Core / SqlClient 类型，也不做字符串侦测。
    /// </para>
    /// </summary>
    Task InsertNewAttemptAsync(DocumentPipelineRun run, CancellationToken cancellationToken = default);
}
