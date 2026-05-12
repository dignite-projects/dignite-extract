using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.TextExtraction;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.Classification;
using Dignite.Paperbase.Documents.Pipelines.TextExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.BlobStoring;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

[DependsOn(typeof(PaperbaseEntityFrameworkCoreTestModule))]
public class DocumentPipelineBackgroundJobPersistenceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<ITextExtractor>());
        context.Services.AddSingleton(Substitute.For<IBlobContainer<PaperbaseDocumentContainer>>());
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());
        context.Services.AddSingleton(Substitute.For<IChatClient>());
        context.Services.AddSingleton(Substitute.For<IPromptProvider>());
        // DocumentTextExtractionBackgroundJob now depends on the title-generator keyed
        // IChatClient (see PaperbaseAIConsts.TitleGeneratorChatClientKey); register a
        // substitute so DI can construct the job. Title generation is best-effort and
        // its failures are swallowed, so the substitute returning null is fine.
        context.Services.AddKeyedSingleton(
            PaperbaseAIConsts.TitleGeneratorChatClientKey,
            Substitute.For<IChatClient>());
    }
}

public class DocumentPipelineBackgroundJobPersistence_Tests
    : PaperbaseTestBase<DocumentPipelineBackgroundJobPersistenceTestModule>
{
    private readonly IDocumentRepository _documentRepository;
    private readonly DocumentPipelineJobScheduler _pipelineJobScheduler;
    private readonly DocumentTextExtractionBackgroundJob _textExtractionJob;
    private readonly ITextExtractor _textExtractor;
    private readonly IBlobContainer<PaperbaseDocumentContainer> _blobContainer;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentPipelineBackgroundJobPersistence_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _pipelineJobScheduler = GetRequiredService<DocumentPipelineJobScheduler>();
        _textExtractionJob = GetRequiredService<DocumentTextExtractionBackgroundJob>();
        _textExtractor = GetRequiredService<ITextExtractor>();
        _blobContainer = GetRequiredService<IBlobContainer<PaperbaseDocumentContainer>>();
        _backgroundJobManager = GetRequiredService<IBackgroundJobManager>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Text_Extraction_Job_Should_Persist_Run_Status_And_Queued_Classification_Run()
    {
        var documentId = _guidGenerator.Create();
        Guid textExtractionRunId = default;

        await WithUnitOfWorkAsync(async () =>
        {
            var document = CreateDocument(documentId);
            await _documentRepository.InsertAsync(document, autoSave: true);

            var run = await _pipelineJobScheduler.QueueAsync(document, PaperbasePipelines.TextExtraction);
            textExtractionRunId = run.Id;
        });

        _blobContainer.GetAsync(Arg.Any<string>())
            .Returns(Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _textExtractor.ExtractAsync(
                Arg.Any<Stream>(),
                Arg.Any<TextExtractionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new TextExtractionResult
            {
                Markdown = "# Contract\n\nThis is a contract.",
                Confidence = 0.98,
                DetectedLanguage = "en",
                PageCount = 1,
                UsedOcr = false
            });

        await _textExtractionJob.ExecuteAsync(new DocumentTextExtractionJobArgs
        {
            DocumentId = documentId,
            PipelineRunId = textExtractionRunId
        });

        Guid classificationRunId = default;
        await WithUnitOfWorkAsync(async () =>
        {
            var document = await _documentRepository.GetAsync(documentId, includeDetails: true);
            var textExtractionRun = document.GetRun(textExtractionRunId);
            var classificationRuns = document.PipelineRuns
                .Where(x => x.PipelineCode == PaperbasePipelines.Classification)
                .ToList();

            textExtractionRun.ShouldNotBeNull();
            textExtractionRun.Status.ShouldBe(PipelineRunStatus.Succeeded);
            classificationRuns.Count.ShouldBe(1);
            classificationRuns[0].Status.ShouldBe(PipelineRunStatus.Pending);
            classificationRunId = classificationRuns[0].Id;
        });

        await _backgroundJobManager.Received(1).EnqueueAsync(
            Arg.Is<DocumentClassificationJobArgs>(x =>
                x.DocumentId == documentId &&
                x.PipelineRunId == classificationRunId),
            Arg.Any<BackgroundJobPriority>(),
            Arg.Any<TimeSpan?>());
    }

    private static Document CreateDocument(Guid id)
    {
        return new Document(
            id,
            tenantId: null,
            originalFileBlobName: "blobs/test.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
    }
}
