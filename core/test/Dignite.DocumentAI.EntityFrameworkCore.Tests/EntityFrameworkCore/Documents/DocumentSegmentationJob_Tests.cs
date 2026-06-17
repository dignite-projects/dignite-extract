using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.Documents;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Pipelines.Segmentation;
using Dignite.DocumentAI.Documents.Pipelines.TextExtraction;
using Dignite.DocumentAI.Documents.Segments;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.DocumentAI.EntityFrameworkCore.Documents;

[DependsOn(typeof(DocumentAIEntityFrameworkCoreTestModule))]
public class DocumentSegmentationJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBlobContainer<DocumentAIDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // Split seam: a partial substitute of the segmentation workflow whose RunAsync each test stubs to a chosen
        // boundary set — no real LLM call (mirrors the figure routing test's classification-workflow seam).
        var workflow = Substitute.ForPartsOf<DocumentSegmentationWorkflow>(
            Substitute.For<IChatClient>(),
            Options.Create(new DocumentAIBehaviorOptions()),
            new DefaultPromptProvider());
        context.Services.AddSingleton(workflow);
    }
}

public class DocumentSegmentationJob_Tests : DocumentAITestBase<DocumentSegmentationJobTestModule>
{
    private readonly DocumentSegmentationJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly IRepository<DocumentSegment, Guid> _segmentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IDistributedEventBus _eventBus;
    private readonly DocumentSegmentationWorkflow _workflow;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentSegmentationJob_Tests()
    {
        _job = GetRequiredService<DocumentSegmentationJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _segmentRepository = GetRequiredService<IRepository<DocumentSegment, Guid>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
        _workflow = GetRequiredService<DocumentSegmentationWorkflow>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Container_Splits_Into_Seeded_Derived_Documents()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var segments = await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId);
            segments.Count.ShouldBe(2);
            segments.ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);

            var derived = await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId);
            derived.Count.ShouldBe(2);
            // Each derived sub-document is keyed by its slice hash (= the segment key) and carries a Markdown FileOrigin.
            derived.ShouldAllBe(d => d.FileOrigin.ContentType == "text/markdown");
            foreach (var d in derived)
            {
                d.OriginConstituentKey.ShouldNotBeNull();
                segments.ShouldContain(s => s.SegmentKey == d.OriginConstituentKey && s.RoutedDocumentId == d.Id);
                d.FileOrigin.ContentHash.ShouldBe(d.OriginConstituentKey);
            }
        });

        await _eventBus.Received(2).PublishAsync(Arg.Any<DocumentUploadedEto>());
        // Each derived sub-document runs the full normal pipeline (text-extraction enqueued).
        await _backgroundJobManager.Received(2).EnqueueAsync(
            Arg.Any<DocumentTextExtractionJobArgs>(), Arg.Any<BackgroundJobPriority>(), Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task Untrusted_Split_Flags_Container_And_Spawns_Nothing()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        // Markers the LLM "returned" but that do not appear verbatim -> MarkdownSlicer rejects the split.
        StubSplit(("Phantom marker one", true), ("Phantom marker two", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            var container = await _documentRepository.GetAsync(containerId);
            container.ReviewReasons.ShouldBe(DocumentReviewReasons.SegmentationIncomplete);

            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId)).ShouldBeEmpty();
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });

        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<DocumentUploadedEto>());
    }

    [Fact]
    public async Task Fewer_Than_Two_Document_Slices_Flags_Container()
    {
        var containerId = await ArrangeContainerAsync("Invoice A only");
        StubSplit(("Invoice A", true)); // a single document slice is not a real bundle

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetAsync(containerId)).ReviewReasons
                .ShouldBe(DocumentReviewReasons.SegmentationIncomplete);
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Resumes_From_Existing_Segments_Without_Re_Splitting()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");

        // Simulate a crash after the split persisted but before any spawn: two Pending segments already exist.
        await WithUnitOfWorkAsync(async () =>
        {
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice A first", 0), autoSave: true);
            await _segmentRepository.InsertAsync(NewSegment(containerId, "Invoice B second", 1), autoSave: true);
        });

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        // The LLM split must be skipped on resume (segments already exist), and the Pending segments spawn.
        await _workflow.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await WithUnitOfWorkAsync(async () =>
        {
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2);
            (await _segmentRepository.GetListAsync(s => s.SourceDocumentId == containerId))
                .ShouldAllBe(s => s.Status == DocumentSegmentStatus.Spawned);
        });
    }

    [Fact]
    public async Task Rerun_Is_Idempotent_And_Does_Not_Duplicate_Sub_Documents()
    {
        var containerId = await ArrangeContainerAsync("Invoice A first\nInvoice B second");
        StubSplit(("Invoice A", true), ("Invoice B", true));

        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });
        await _job.ExecuteAsync(new DocumentSegmentationJobArgs { ContainerDocumentId = containerId });

        await WithUnitOfWorkAsync(async () =>
            (await _documentRepository.GetListAsync(d => d.OriginDocumentId == containerId)).Count.ShouldBe(2));
    }

    private async Task<Guid> ArrangeContainerAsync(string markdown)
    {
        var containerId = _guidGenerator.Create();
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = new Document(
                containerId,
                tenantId: null,
                fileOrigin: new FileOrigin(
                    blobName: $"blobs/{containerId:N}.pdf",
                    uploadedByUserName: "test-user",
                    contentType: "application/pdf",
                    contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}"[..64],
                    fileSize: 2048,
                    originalFileName: "bundle.pdf"));

            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, [markdown]);

            await _documentRepository.InsertAsync(doc, autoSave: true);
        });

        return containerId;
    }

    private DocumentSegment NewSegment(Guid containerId, string sliceText, int ordinal)
        => new(
            _guidGenerator.Create(),
            tenantId: null,
            sourceDocumentId: containerId,
            segmentKey: ContentHasher.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(sliceText)),
            sliceText: sliceText,
            ordinal: ordinal);

    private void StubSplit(params (string Marker, bool IsDocument)[] boundaries)
    {
        var outcome = new DocumentSegmentationOutcome();
        foreach (var (marker, isDocument) in boundaries)
        {
            outcome.Boundaries.Add(new SegmentBoundary(marker, isDocument));
        }

        _workflow.RunAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(outcome);
    }
}
