using System;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class RelationDiscoveryEventHandlerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        // Substitute the scheduler entirely so we verify the handler queues with the correct
        // pipeline code without exercising the real PipelineRun creation / job enqueue path.
        // The scheduler ctor needs valid args; pass cheap substitutes.
        context.Services.AddSingleton(provider =>
            Substitute.For<DocumentPipelineJobScheduler>(
                Substitute.For<IDocumentRepository>(),
                provider.GetRequiredService<DocumentPipelineRunManager>(),
                Substitute.For<IBackgroundJobManager>()));
    }
}

public class RelationDiscoveryEventHandler_Tests
    : PaperbaseApplicationTestBase<RelationDiscoveryEventHandlerTestModule>
{
    private readonly RelationDiscoveryEventHandler _handler;
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _scheduler;

    public RelationDiscoveryEventHandler_Tests()
    {
        _handler = GetRequiredService<RelationDiscoveryEventHandler>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _scheduler = GetRequiredService<DocumentPipelineJobScheduler>();
    }

    [Fact]
    public async Task HandleEventAsync_Should_Queue_Job_When_Document_Exists()
    {
        var document = CreateDocument();
        _documentRepository
            .FindAsync(document.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(document);
        _scheduler
            .QueueAsync(Arg.Any<Document>(), Arg.Any<string>())
            .Returns(Task.FromResult<DocumentPipelineRun>(null!));

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = document.Id,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.95
        });

        await _scheduler.Received(1).QueueAsync(
            Arg.Is<Document>(d => d.Id == document.Id),
            PaperbasePipelines.RelationDiscovery);
    }

    [Fact]
    public async Task HandleEventAsync_Should_Skip_When_Document_Has_Empty_Id()
    {
        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = Guid.Empty,
            DocumentTypeCode = "contract.general"
        });

        await _scheduler.DidNotReceive().QueueAsync(Arg.Any<Document>(), Arg.Any<string>());
        await _documentRepository.DidNotReceive().FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEventAsync_Should_Skip_When_Document_No_Longer_Exists()
    {
        // Hard-deleted between classification publish and handler dispatch.
        var documentId = Guid.NewGuid();
        _documentRepository
            .FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = documentId,
            DocumentTypeCode = "contract.general"
        });

        await _scheduler.DidNotReceive().QueueAsync(Arg.Any<Document>(), Arg.Any<string>());
    }

    private static Document CreateDocument()
    {
        return new Document(
            Guid.NewGuid(), tenantId: null,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
