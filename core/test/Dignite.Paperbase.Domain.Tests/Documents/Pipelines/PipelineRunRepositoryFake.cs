using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using NSubstitute;
using Volo.Abp;

namespace Dignite.Paperbase.Documents.Pipelines;

/// <summary>
/// Application.Tests 共享：<see cref="IDocumentPipelineRunRepository"/> 的 NSubstitute fake
/// 工厂，配 closure-state 列表。Manager.QueueAsync/Begin/Complete 等路径需要回查"已有 run"
/// 来算 AttemptNumber + 派生 LifecycleStatus——简单的 <c>Substitute.For&lt;...&gt;()</c> 默认返回
/// null 会让 DeriveLifecycle 永远进 Processing 分支，掩盖状态流转 bug。
/// <para>
/// 每个 test class 调一次 <see cref="Create"/> 拿独立的 fake 实例（singleton 注册在该 class 的 test module）。
/// 同一 class 内所有 [Fact] 共享同一 list——靠"每 Fact 用新 doc.Id"保证查询隔离。
/// </para>
/// </summary>
public static class PipelineRunRepositoryFake
{
    public static IDocumentPipelineRunRepository Create()
    {
        var runs = new List<DocumentPipelineRun>();
        var mock = Substitute.For<IDocumentPipelineRunRepository>();

        mock.InsertAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.Arg<DocumentPipelineRun>();
                runs.Add(run);
                return Task.FromResult(run);
            });

        // InsertNewAttemptAsync：模拟 (DocumentId, PipelineCode, AttemptNumber) 唯一索引——撞键时
        // 抛 RetryInProgress（与 EfCoreDocumentPipelineRunRepository 翻译 DbUpdateException 后的行为对齐），
        // 否则等价于普通 InsertAsync 入列。happy-path 测试每个 Fact 用新 doc.Id，正常态不会撞。
        mock.InsertNewAttemptAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var run = call.Arg<DocumentPipelineRun>();
                var collides = runs.Any(r =>
                    r.DocumentId == run.DocumentId
                    && r.PipelineCode == run.PipelineCode
                    && r.AttemptNumber == run.AttemptNumber);
                if (collides)
                {
                    throw new BusinessException(PaperbaseErrorCodes.Pipeline.RetryInProgress)
                        .WithData("PipelineCode", run.PipelineCode)
                        .WithData("DocumentId", run.DocumentId);
                }
                runs.Add(run);
                return Task.CompletedTask;
            });

        mock.UpdateAsync(Arg.Any<DocumentPipelineRun>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<DocumentPipelineRun>()));

        mock.FindLatestByDocumentAndCodeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var code = call.Arg<string>();
                var match = runs
                    .Where(r => r.DocumentId == docId && r.PipelineCode == code)
                    .OrderByDescending(r => r.AttemptNumber)
                    .FirstOrDefault();
                return Task.FromResult<DocumentPipelineRun?>(match);
            });

        mock.GetLatestRunsByCodesAsync(
                Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var codes = call.Arg<IReadOnlyCollection<string>>();
                var dict = runs
                    .Where(r => r.DocumentId == docId && codes.Contains(r.PipelineCode))
                    .GroupBy(r => r.PipelineCode)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).First());
                return Task.FromResult(dict);
            });

        mock.GetListByDocumentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var docId = call.Arg<Guid>();
                var list = runs
                    .Where(r => r.DocumentId == docId)
                    .OrderBy(r => r.PipelineCode)
                    .ThenBy(r => r.AttemptNumber)
                    .ToList();
                return Task.FromResult(list);
            });

        mock.FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var runId = call.Arg<Guid>();
                var match = runs.FirstOrDefault(r => r.Id == runId);
                return Task.FromResult<DocumentPipelineRun?>(match);
            });

        return mock;
    }
}
