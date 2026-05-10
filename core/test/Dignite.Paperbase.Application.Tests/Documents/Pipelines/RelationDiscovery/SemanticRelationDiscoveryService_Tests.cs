using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;
using Dignite.Paperbase.KnowledgeIndex;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

[DependsOn(typeof(PaperbaseApplicationTestModule))]
public class SemanticRelationDiscoveryServiceTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentRelationRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentKnowledgeIndex>());
        context.Services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());

        // Replace RelationInferenceAgent entirely so tests don't need a real IChatClient.
        // Construct with cheap substitutes for its dependencies.
        context.Services.AddSingleton(Substitute.For<RelationInferenceAgent>(
            Substitute.For<IChatClient>(),
            Options.Create(new PaperbaseAIBehaviorOptions())));

        // Default: enable semantic discovery so most tests exercise the full path.
        // The "disabled" test overrides this via direct field manipulation on the resolved options.
        context.Services.Configure<PaperbaseAIBehaviorOptions>(opts =>
        {
            opts.EnableSemanticRelationDiscovery = true;
            opts.SemanticRelationDiscoveryTopK = 5;
            opts.SemanticRelationDiscoveryMinScore = 0.65;
            opts.SemanticRelationDiscoveryConfidenceThreshold = 0.7;
        });
    }
}

public class SemanticRelationDiscoveryService_Tests
    : PaperbaseApplicationTestBase<SemanticRelationDiscoveryServiceTestModule>
{
    private readonly SemanticRelationDiscoveryService _service;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentRelationRepository _relationRepository;
    private readonly IDocumentKnowledgeIndex _knowledgeIndex;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly RelationInferenceAgent _inferenceAgent;
    private readonly PaperbaseAIBehaviorOptions _aiOptions;

    public SemanticRelationDiscoveryService_Tests()
    {
        _service = GetRequiredService<SemanticRelationDiscoveryService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _relationRepository = GetRequiredService<IDocumentRelationRepository>();
        _knowledgeIndex = GetRequiredService<IDocumentKnowledgeIndex>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _inferenceAgent = GetRequiredService<RelationInferenceAgent>();
        _aiOptions = GetRequiredService<IOptions<PaperbaseAIBehaviorOptions>>().Value;
    }

    [Fact]
    public async Task DiscoverAsync_Should_Short_Circuit_When_Disabled()
    {
        _aiOptions.EnableSemanticRelationDiscovery = false;
        var sourceId = Guid.NewGuid();

        var created = await _service.DiscoverAsync(sourceId);

        created.ShouldBeEmpty();
        // Short-circuit means NO IO at all — not even a document load.
        await _documentRepository.DidNotReceive().FindAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Reject_Empty_Source_Id()
    {
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _service.DiscoverAsync(Guid.Empty));
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Source_Not_Found()
    {
        var sourceId = Guid.NewGuid();
        _documentRepository.FindAsync(sourceId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var created = await _service.DiscoverAsync(sourceId);

        created.ShouldBeEmpty();
        await _embeddingGenerator.DidNotReceive().GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Source_Has_No_Markdown()
    {
        var source = CreateDocument(markdown: null);
        SetupSource(source);

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Return_Empty_When_Vector_Search_Yields_No_Candidates()
    {
        var source = CreateDocument(markdown: "合同内容");
        SetupSource(source);
        SetupEmbedding();
        SetupVectorSearch(source.Id, Array.Empty<VectorSearchResult>());

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Filter_Self_From_Vector_Results()
    {
        var source = CreateDocument(markdown: "合同内容");
        SetupSource(source);
        SetupEmbedding();
        // Vector search returns the source itself as the top hit (always — every doc
        // is most similar to itself). Service must filter it out.
        SetupVectorSearch(source.Id, new[]
        {
            CreateVectorResult(source.Id, 0.95),
        });

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Already_Linked_Candidates()
    {
        var source = CreateDocument(markdown: "合同内容");
        var linkedPeer = CreateDocument(markdown: "已关联文档");
        SetupSource(source);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[]
        {
            CreateVectorResult(linkedPeer.Id, 0.85),
        });
        // Pre-existing relation → must skip.
        _relationRepository.GetListByDocumentIdAsync(source.Id, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>
            {
                new(Guid.NewGuid(), null, source.Id, linkedPeer.Id,
                    "manual link", RelationSource.Manual)
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_When_LLM_Says_Not_Related()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "无关文档");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[] { CreateVectorResult(candidate.Id, 0.7) });
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult { IsRelated = false, Confidence = 0.2 });

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
        await _relationRepository.DidNotReceive().InsertAsync(
            Arg.Any<DocumentRelation>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_When_Confidence_Below_Threshold()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "弱相关文档");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[] { CreateVectorResult(candidate.Id, 0.7) });
        SetupNoExistingRelations(source.Id);

        // LLM says related but confidence is below threshold (0.7).
        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 0.5,
                Description = "可能有关"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_Should_Create_AiSuggested_When_LLM_Confirms()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[] { CreateVectorResult(candidate.Id, 0.85) });
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true,
                Confidence = 0.85,
                Description = "该发票对应该合同"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Count.ShouldBe(1);
        var rel = created.Single();
        rel.SourceDocumentId.ShouldBe(source.Id);
        rel.TargetDocumentId.ShouldBe(candidate.Id);
        rel.Source.ShouldBe(RelationSource.AiSuggested);
        rel.Confidence.ShouldBe(0.85);
        rel.Description.ShouldBe("该发票对应该合同");
        // autoSave: true — LLM/DB rule (no UoW during external work).
        await _relationRepository.Received(1).InsertAsync(
            Arg.Any<DocumentRelation>(), autoSave: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Continue_When_LLM_Throws_For_One_Candidate()
    {
        var source = CreateDocument(markdown: "合同内容");
        var failCandidate = CreateDocument(markdown: "LLM 调用失败");
        var goodCandidate = CreateDocument(markdown: "正常候选");
        SetupSource(source);
        SetupCandidate(failCandidate);
        SetupCandidate(goodCandidate);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[]
        {
            CreateVectorResult(failCandidate.Id, 0.85),
            CreateVectorResult(goodCandidate.Id, 0.8),
        });
        SetupNoExistingRelations(source.Id);

        _inferenceAgent.EvaluateAsync(
                Arg.Is<DocumentSnapshot>(s => s.Markdown == "合同内容"),
                Arg.Is<DocumentSnapshot>(c => c.Markdown == "LLM 调用失败"),
                Arg.Any<CancellationToken>())
            .Returns<RelationInferenceResult>(_ => throw new InvalidOperationException("LLM provider down"));

        _inferenceAgent.EvaluateAsync(
                Arg.Is<DocumentSnapshot>(s => s.Markdown == "合同内容"),
                Arg.Is<DocumentSnapshot>(c => c.Markdown == "正常候选"),
                Arg.Any<CancellationToken>())
            .Returns(new RelationInferenceResult
            {
                IsRelated = true, Confidence = 0.8, Description = "match"
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Count.ShouldBe(1);
        created.Single().TargetDocumentId.ShouldBe(goodCandidate.Id);
    }

    [Fact]
    public async Task DiscoverAsync_Should_Run_LLM_And_Vector_Search_Without_Ambient_UoW()
    {
        // .claude/rules/background-jobs.md § Tests: regression guard against future code
        // accidentally wrapping L3 in an outer UoW that holds a DB connection during LLM calls.
        // L3 service itself opens NO UoW; when called from the background job (after L2's UoW
        // has been disposed), ambient ICurrentUnitOfWork must be null at every external boundary.
        var uowManager = GetRequiredService<Volo.Abp.Uow.IUnitOfWorkManager>();
        var source = CreateDocument(markdown: "合同内容");
        var candidate = CreateDocument(markdown: "强相关发票");
        SetupSource(source);
        SetupCandidate(candidate);
        SetupNoExistingRelations(source.Id);

        // Assert NO ambient UoW at the embedding boundary.
        _embeddingGenerator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uowManager.Current.ShouldBeNull();
                return new GeneratedEmbeddings<Embedding<float>>(new[]
                {
                    new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f })
                });
            });

        // Assert NO ambient UoW at the vector search boundary.
        _knowledgeIndex.SearchAsync(
                Arg.Any<VectorSearchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uowManager.Current.ShouldBeNull();
                return (IReadOnlyList<VectorSearchResult>)new[] { CreateVectorResult(candidate.Id, 0.9) };
            });

        // Assert NO ambient UoW at the LLM evaluation boundary.
        _inferenceAgent.EvaluateAsync(
                Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                uowManager.Current.ShouldBeNull();
                return new RelationInferenceResult
                {
                    IsRelated = true, Confidence = 0.85, Description = "match"
                };
            });

        var created = await _service.DiscoverAsync(source.Id);

        created.Count.ShouldBe(1);
        // Sanity: all three external boundaries were actually hit (the assertion ran).
        await _embeddingGenerator.Received(1).GenerateAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions>(), Arg.Any<CancellationToken>());
        await _knowledgeIndex.Received(1).SearchAsync(Arg.Any<VectorSearchRequest>(), Arg.Any<CancellationToken>());
        await _inferenceAgent.Received(1).EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_Should_Skip_Candidate_With_No_Markdown()
    {
        var source = CreateDocument(markdown: "合同内容");
        var candidateWithoutMarkdown = CreateDocument(markdown: null);
        SetupSource(source);
        SetupCandidate(candidateWithoutMarkdown);
        SetupEmbedding();
        SetupVectorSearch(source.Id, new[] { CreateVectorResult(candidateWithoutMarkdown.Id, 0.9) });
        SetupNoExistingRelations(source.Id);

        var created = await _service.DiscoverAsync(source.Id);

        created.ShouldBeEmpty();
        await _inferenceAgent.DidNotReceive().EvaluateAsync(
            Arg.Any<DocumentSnapshot>(), Arg.Any<DocumentSnapshot>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupSource(Document doc)
    {
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void SetupCandidate(Document doc)
    {
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void SetupNoExistingRelations(Guid sourceId)
    {
        _relationRepository.GetListByDocumentIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new List<DocumentRelation>());
    }

    private void SetupEmbedding()
    {
        _embeddingGenerator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>(new[]
            {
                new Embedding<float>(new[] { 0.1f, 0.2f, 0.3f })
            }));
    }

    private void SetupVectorSearch(Guid sourceDocId, IReadOnlyList<VectorSearchResult> results)
    {
        _knowledgeIndex.SearchAsync(
                Arg.Any<VectorSearchRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(results);
    }

    private static VectorSearchResult CreateVectorResult(Guid documentId, double score)
    {
        return new VectorSearchResult
        {
            RecordId = Guid.NewGuid(),
            DocumentId = documentId,
            ChunkIndex = 0,
            Text = "chunk",
            Score = score,
        };
    }

    private static Document CreateDocument(string? markdown)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId: null,
            $"blobs/{Guid.NewGuid():N}.pdf",
            SourceType.Digital,
            new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        if (markdown != null)
        {
            typeof(Document)
                .GetProperty(nameof(Document.Markdown))!
                .GetSetMethod(nonPublic: true)!
                .Invoke(doc, new object[] { markdown });
        }

        return doc;
    }
}
