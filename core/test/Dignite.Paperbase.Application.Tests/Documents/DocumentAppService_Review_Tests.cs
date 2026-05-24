using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class DocumentAppServiceReviewTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());
    }
}

/// <summary>
/// <see cref="DocumentAppService.RejectReviewAsync"/> 行为 + PendingReview 列表过滤测试。
/// <para>
/// #196 砍掉 OCR 置信度门槛后，进 PendingReview 的唯一来源是分类低置信度 / 无合适类型
/// （<see cref="DocumentPipelineRunManager.CompleteClassificationWithLowConfidenceAsync"/>）；
/// 操作员的审核动作只剩 Reclassify（指派类型）或 Reject，不再有"裸放行"(approve)。
/// </para>
/// </summary>
public class DocumentAppService_Review_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineRunManager _pipelineRunManager;

    public DocumentAppService_Review_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
    }

    [Fact]
    public async Task RejectReviewAsync_Transitions_Lifecycle_To_Failed()
    {
        var doc = await CreatePendingReviewDocumentAsync("ambiguous");
        StubGet(doc);

        await _appService.RejectReviewAsync(doc.Id, new RejectReviewInput { Reason = "scan unusable" });

        doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Failed);
        doc.ClassificationReason.ShouldBe("scan unusable");
    }

    [Fact]
    public async Task PendingReview_Filter_Excludes_Failed_Rejections_By_Default()
    {
        var activePending = await CreatePendingReviewDocumentAsync("needs review");

        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { activePending, rejected }.AsQueryable(),
                new GetDocumentListInput { ReviewStatus = DocumentReviewStatus.PendingReview })
            .ToList();

        result.ShouldContain(activePending);
        result.ShouldNotContain(rejected);
    }

    [Fact]
    public async Task PendingReview_Filter_Allows_Failed_Rejections_When_Lifecycle_Is_Explicit()
    {
        var rejected = await CreatePendingReviewDocumentAsync("low confidence");
        rejected.RejectReview("scan unusable");

        var result = ApplyFilterForTest(
                new[] { rejected }.AsQueryable(),
                new GetDocumentListInput
                {
                    ReviewStatus = DocumentReviewStatus.PendingReview,
                    LifecycleStatus = DocumentLifecycleStatus.Failed
                })
            .ToList();

        result.ShouldContain(rejected);
    }

    private void StubGet(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(),
            tenantId: null,
            originalFileBlobName: $"blobs/{Guid.NewGuid():N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }

    /// <summary>
    /// 造一个进入 PendingReview 的文档：text-extraction 成功 → classification 低置信度
    /// （#196 后这是 PendingReview 的唯一产生路径）。DocumentTypeCode 保持 null。
    /// </summary>
    private async Task<Document> CreatePendingReviewDocumentAsync(string reason)
    {
        var doc = CreateDocument();
        var textRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.TextExtraction);
        await _pipelineRunManager.CompleteAsync(doc, textRun);
        var classRun = await _pipelineRunManager.StartAsync(doc, PaperbasePipelines.Classification);
        await _pipelineRunManager.CompleteClassificationWithLowConfidenceAsync(doc, classRun, reason: reason);
        return doc;
    }

    private IQueryable<Document> ApplyFilterForTest(IQueryable<Document> query, GetDocumentListInput input)
    {
        var service = new DocumentAppService(
            Substitute.For<IDocumentRepository>(),
            Substitute.For<IDocumentTypeRepository>(),
            Substitute.For<IFieldDefinitionRepository>(),
            Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>(),
            new DocumentPipelineRunManager(),
            new DocumentPipelineJobScheduler(
                Substitute.For<IDocumentRepository>(),
                new DocumentPipelineRunManager(),
                Substitute.For<IBackgroundJobManager>()),
            Substitute.For<IDistributedEventBus>());

        var method = typeof(DocumentAppService).GetMethod(
            "ApplyFilter",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        return (IQueryable<Document>)method.Invoke(service, [query, input])!;
    }
}
