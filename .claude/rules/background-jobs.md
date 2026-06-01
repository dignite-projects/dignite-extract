---
description: "ABP background job unit of work boundaries and long-running work rules"
paths:
  - "**/*BackgroundJob*.cs"
  - "**/*Job.cs"
  - "**/*JobArgs.cs"
---

# ABP Background Job Rules

## Unit Of Work Boundaries

Do not wrap an entire background job `ExecuteAsync` in one long `[UnitOfWork]` when the job performs slow or external work, including but not limited to:

- blob or file IO
- OCR
- LLM or AI provider calls
- HTTP calls or other network IO
- long CPU-bound processing

Use short unit-of-work phases instead:

1. Begin phase: load the required aggregate(s), create or mark execution state as running, save, and commit.
2. External phase: perform slow IO, AI, network, or CPU-bound work outside any ambient UoW.
3. Complete phase: reload the aggregate(s), apply success/failure/skipped state, save, and commit.

This avoids holding database connections, locks, or transactions while external work is running. Long ambient UoW scopes can block normal HTTP reads and cause SQL command timeouts even for simple primary-key queries.

## Aggregate Persistence

- Modify child entities through their aggregate root.
- Do not introduce repositories for child entities to work around persistence issues.
- If a job carries an execution/run identifier in its args, persist that same identifier before enqueueing or beginning the job, and use it when completing or failing the job.

### DocumentPipelineRun 例外（#216）

`DocumentPipelineRun` 本身就是 `AggregateRoot<Guid>`，通过 `IDocumentPipelineRunRepository` 直接操作，**不再经 `Document` 聚合根**。后台作业（`DocumentTextExtractionBackgroundJob` / `DocumentClassificationBackgroundJob`）三阶段 UoW 的 BeginRun / CompleteRun / FailRun 都按这个模式：

- 用 `DocumentRepository.GetAsync(id, includeDetails: false)` 加载 Document（**不再** `GetWithPipelineRunsAsync`）
- 用 `RunRepository.FindAsync(runId)` 加载具体的 run（**不再** `document.GetRun(runId)`）
- 通过 `DocumentPipelineRunManager` 修改 run 状态（manager 内部 `_runRepo.UpdateAsync` 显式持久化）
- 同 UoW commit 时 Document 主行 UPDATE + PipelineRun 行 UPDATE / INSERT 一并 flush

CompleteRun / FailRun 阶段共享的「加载 Document + 按 runId 定位 run（找不到则 fallback 重建）」前导，以及两类作业完全一致的失败收尾，收敛在基类 `DocumentPipelineBackgroundJobBase<TArgs>`（`LoadDocumentAndRunAsync` / `FailRunAsync`，#216 follow-up #2）；两个作业各自只实现差异化的 Begin / Complete 主体。

**这是该实体的唯一例外**——其他流水线相关 child 实体（如未来的 ChunkBlock）仍按"通过聚合根访问"的标准模式管理。具体决策详见 Issue #216。

## Tests

When changing a background job that performs slow or external work, keep or add tests that verify the external work runs without an ambient UoW. A direct assertion such as `_unitOfWorkManager.Current.ShouldBeNull()` at the external call boundary is preferred.
