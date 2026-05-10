using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class RelationDiscoveryBackgroundJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRelationRepository>());

        // Replace RelationDiscoveryService entirely — this test only verifies the JOB's
        // PipelineRun lifecycle wiring, not the discovery logic (covered separately by
        // RelationDiscoveryService_Tests).
        context.Services.AddSingleton(Substitute.For<RelationDiscoveryService>(
            Array.Empty<IDocumentIdentifierProvider>(),
            Substitute.For<IDocumentRelationRepository>(),
            Substitute.For<ICurrentTenant>()));
    }
}

/// <summary>
/// Verifies <see cref="RelationDiscoveryBackgroundJob"/>'s short-UoW lifecycle:
/// PipelineRun is created → Running → Succeeded/Failed in three separate UoWs;
/// service throwing surfaces as Failed without crashing the job.
/// </summary>
public class RelationDiscoveryBackgroundJob_Tests
    : PaperbaseApplicationTestBase<RelationDiscoveryBackgroundJobTestModule>
{
    private readonly RelationDiscoveryBackgroundJob _job;
    private readonly IDocumentRepository _documentRepository;
    private readonly RelationDiscoveryService _discoveryService;

    public RelationDiscoveryBackgroundJob_Tests()
    {
        _job = GetRequiredService<RelationDiscoveryBackgroundJob>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _discoveryService = GetRequiredService<RelationDiscoveryService>();
    }

    [Fact]
    public async Task ExecuteAsync_Should_Mark_Run_Succeeded_When_Discovery_Returns_Successfully()
    {
        var doc = CreateDocument();
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        var run = doc.GetLatestRun(PaperbasePipelines.RelationDiscovery);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_Should_Mark_Run_Failed_When_Discovery_Throws()
    {
        var doc = CreateDocument();
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DocumentRelation>>(_ => throw new InvalidOperationException("DB connection lost"));

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = doc.Id });

        var run = doc.GetLatestRun(PaperbasePipelines.RelationDiscovery);
        run.ShouldNotBeNull();
        run.Status.ShouldBe(PipelineRunStatus.Failed);
        run.StatusMessage.ShouldBe("DB connection lost");
    }

    [Fact]
    public async Task ExecuteAsync_Should_Drop_Job_Silently_When_Document_Not_Found()
    {
        // Document hard-deleted between event publish and job pickup — drop without throwing.
        var documentId = Guid.NewGuid();
        _documentRepository
            .FindAsync(documentId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs { DocumentId = documentId });

        await _discoveryService.DidNotReceive().DiscoverAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Should_Reuse_Pending_Run_When_PipelineRunId_Provided()
    {
        // The scheduler creates a Pending run before enqueue and passes its id in args.
        // Job must pick it up rather than start a fresh one.
        var doc = CreateDocument();
        var pipelineRunManager = GetRequiredService<DocumentPipelineRunManager>();
        var pendingRun = await pipelineRunManager.QueueAsync(doc, PaperbasePipelines.RelationDiscovery);
        SetupDocumentRepository(doc);
        _discoveryService
            .DiscoverAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DocumentRelation>)new List<DocumentRelation>());

        await _job.ExecuteAsync(new RelationDiscoveryJobArgs
        {
            DocumentId = doc.Id,
            PipelineRunId = pendingRun.Id
        });

        pendingRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
        pendingRun.AttemptNumber.ShouldBe(1);
        doc.PipelineRuns.Count.ShouldBe(1);   // No duplicate run created
    }

    private void SetupDocumentRepository(Document doc)
    {
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        _documentRepository
            .GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
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
