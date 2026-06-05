using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents.Cabinets;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentCabinetSuggestionJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<ICabinetRepository>());

        var workflow = Substitute.ForPartsOf<CabinetSuggestionWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new PaperbaseAIBehaviorOptions()));
        context.Services.AddSingleton(workflow);

        context.Services.Configure<PaperbaseAIBehaviorOptions>(_ => { });
    }
}

/// <summary>
/// <see cref="DocumentCabinetSuggestionBackgroundJob"/> 行为测试（#265）：人工优先门控、弃选 / 阈值、
/// 竞态复检、fail-open、LLM 调用在 UoW 之外。IChatClient / workflow 均 NSubstitute 替代，无真实 LLM。
/// </summary>
public class DocumentCabinetSuggestionBackgroundJob_Tests
    : PaperbaseApplicationTestBase<DocumentCabinetSuggestionJobTestModule>
{
    private readonly DocumentCabinetSuggestionBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly ICabinetRepository _cabinetRepository;
    private readonly CabinetSuggestionWorkflow _workflow;
    private readonly IUnitOfWorkManager _unitOfWorkManager;

    public DocumentCabinetSuggestionBackgroundJob_Tests()
    {
        _job = GetRequiredService<DocumentCabinetSuggestionBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _cabinetRepository = GetRequiredService<ICabinetRepository>();
        _workflow = GetRequiredService<CabinetSuggestionWorkflow>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
    }

    [Fact]
    public async Task Writes_CabinetId_When_Confident_Match()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "業務委託契約書の内容です。", cabinetId: null);
        SetupRepositories(doc, cabinet);

        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.9 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(cabinet.Id);
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_CabinetId_Already_Set_Manually()
    {
        var existing = Guid.NewGuid();
        var doc = CreateDocument(markdown: "anything", cabinetId: existing);
        SetupRepositories(doc, cabinet: null);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(existing);
        // 人工优先：自门控命中，连 LLM 都不调。
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_Markdown_Empty()
    {
        var doc = CreateDocument(markdown: null, cabinetId: null);
        SetupRepositories(doc, cabinet: null);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_When_No_Cabinets_In_Layer()
    {
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet: null); // empty cabinet list

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.DidNotReceive().RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_Not_Write_When_Workflow_Abstains()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        StubWorkflow(CabinetSuggestionOutcome.None);

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
    }

    [Fact]
    public async Task Does_Not_Write_When_Below_Confidence_Threshold()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // 默认阈值 0.6；0.4 应被拒。
        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.4 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
    }

    [Fact]
    public async Task FailsOpen_When_Workflow_Throws()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CabinetSuggestionOutcome>(_ => throw new TimeoutException("LLM down"));

        // Fail-open：不 rethrow，文档保持未归类。
        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        // 断言 workflow 确被调用——否则 CabinetId==null 可能因前置门控短路（假阳性），catch 根本没被走到。
        await _workflow.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_Not_Overwrite_When_Operator_Reassigns_During_Llm()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var manualCabinetId = Guid.NewGuid();

        // Begin 段加载到未归类文档；Complete 段重载时已被操作员手动改派（CabinetId 已设）。
        var docAtBegin = CreateDocument(markdown: "some text", cabinetId: null);
        var docAtComplete = CreateDocument(markdown: "some text", cabinetId: manualCabinetId, id: docAtBegin.Id);

        _documentRepository
            .GetAsync(docAtBegin.Id, false, Arg.Any<CancellationToken>())
            .Returns(docAtBegin, docAtComplete);
        _cabinetRepository
            .GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<Cabinet> { cabinet });
        _cabinetRepository
            .FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet);

        StubWorkflow(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.95 });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = docAtBegin.Id });

        // 人工改派的值不被 AI 覆盖。
        docAtComplete.CabinetId.ShouldBe(manualCabinetId);
        // load-bearing：任何文档实例都不应被写回（防御复检若误用 docAtBegin 实例 SetCabinet 的假阳性）。
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Swallows_Provider_Timeout_FailOpen_Without_Triggering_Retry()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // 本作业非 PipelineRun、不可重试：provider per-call 超时（TaskCanceledException : OperationCanceledException）
        // 必须被 fail-open 吞掉、绝不逃逸出 ExecuteAsync（否则 ABP 会把它当失败重排 → 重试风暴）。
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CabinetSuggestionOutcome>(_ => throw new TaskCanceledException("provider per-call timeout"));

        // 不抛——吞掉（若 catch 误加 `when (ex is not OperationCanceledException)` 过滤，此调用会抛、测试失败）。
        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBeNull();
        await _workflow.Received(1).RunAsync(
            Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Llm_Call_Runs_Outside_Any_UnitOfWork()
    {
        var cabinet = new Cabinet(Guid.NewGuid(), null, "法务");
        var doc = CreateDocument(markdown: "some text", cabinetId: null);
        SetupRepositories(doc, cabinet);

        // background-jobs.md：外部慢工作（LLM）必须在 UoW 之外执行。
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWorkManager.Current.ShouldBeNull();
                return Task.FromResult(new CabinetSuggestionOutcome { CabinetId = cabinet.Id, Confidence = 0.9 });
            });

        await _job.ExecuteAsync(new DocumentCabinetSuggestionJobArgs { DocumentId = doc.Id });

        doc.CabinetId.ShouldBe(cabinet.Id);
    }

    private void StubWorkflow(CabinetSuggestionOutcome outcome)
    {
        _workflow
            .RunAsync(Arg.Any<IReadOnlyList<Cabinet>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
    }

    private void SetupRepositories(Document doc, Cabinet? cabinet)
    {
        _documentRepository
            .GetAsync(doc.Id, false, Arg.Any<CancellationToken>())
            .Returns(doc);

        _cabinetRepository
            .GetListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(cabinet == null ? new List<Cabinet>() : new List<Cabinet> { cabinet });

        if (cabinet != null)
        {
            _cabinetRepository
                .FindAsync(cabinet.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(cabinet);
        }
    }

    private static Document CreateDocument(string? markdown, Guid? cabinetId, Guid? id = null)
    {
        var doc = new Document(
            id ?? Guid.NewGuid(), null,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"),
            cabinetId: cabinetId);

        if (!string.IsNullOrEmpty(markdown))
        {
            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [markdown]);
        }

        return doc;
    }
}
