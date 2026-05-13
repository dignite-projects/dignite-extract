using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Chat.Telemetry;
using Dignite.Paperbase.Tests.Vectors;
using Dignite.Paperbase.Vectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Volo.Abp.Auditing;
using Xunit;
using MEAI = Microsoft.Extensions.AI;

namespace Dignite.Paperbase.Chat;

public class ChatToolInvocationTestModule : ChatAppServiceTestModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        base.ConfigureServices(context);

        context.Services.AddSingleton<ScriptedToolCallingChatClient>();
        context.Services.AddSingleton<IChatClient>(sp =>
            new FunctionInvokingChatClient(
                sp.GetRequiredService<ScriptedToolCallingChatClient>(),
                NullLoggerFactory.Instance,
                sp)
            {
                MaximumIterationsPerRequest = 5
            });
    }
}

/// <summary>
/// Integration-level guard for the successful MAF tool-calling path:
/// <c>ChatAppService → ChatClientAgent → FunctionInvokingChatClient →
/// search_paperbase_documents AIFunction → DocumentSearchCapture → citations</c>.
/// </summary>
public class ChatToolInvocation_Tests
    : PaperbaseApplicationTestBase<ChatToolInvocationTestModule>
{
    private readonly IChatAppService _appService;
    private readonly IChatConversationRepository _repository;
    private readonly ScriptedToolCallingChatClient _innerChatClient;
    private readonly FakeDocumentChunkCollection _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ICurrentPrincipalAccessor _principalAccessor;
    private readonly IAuditingManager _auditingManager;

    private static readonly Guid OwnerUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public ChatToolInvocation_Tests()
    {
        _appService = GetRequiredService<IChatAppService>();
        _repository = GetRequiredService<IChatConversationRepository>();
        _innerChatClient = GetRequiredService<ScriptedToolCallingChatClient>();
        _collection = GetRequiredService<FakeDocumentChunkCollection>();
        _embeddingGenerator = GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        _principalAccessor = GetRequiredService<ICurrentPrincipalAccessor>();
        _auditingManager = GetRequiredService<IAuditingManager>();

        SetupDefaultEmbedding();
    }

    [Fact]
    public async Task SendMessageAsync_Invokes_Search_Tool_And_Persists_Citations()
    {
        var docId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        StageSearchHit(chunkId, docId, chunkIndex: 0, pageNumber: 3,
            text: "Payment is due within 30 days.");

        var conversationId = await CreateConversationAsync();

        ChatTurnResultDto result = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                result = await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "What are the payment terms?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        result.IsDegraded.ShouldBeFalse();
        // Issue #99: GroundingSource on the DTO. Only search_paperbase_documents was
        // invoked → Vector (Mixed only when a structured tool is also called).
        result.GroundingSource.ShouldBe(GroundingSource.Vector);
        result.Citations.Count.ShouldBe(1);
        result.Citations[0].DocumentId.ShouldBe(docId);
        result.Citations[0].ChunkIndex.ShouldBe(0);
        result.Citations[0].PageNumber.ShouldBe(3);

        _innerChatClient.Calls.ShouldBe(2);
        _innerChatClient.FirstOptions.ShouldNotBeNull();
        _innerChatClient.FirstOptions!.ToolMode.ShouldBe(ChatToolMode.Auto);
        _innerChatClient.FirstOptions.Tools.ShouldNotBeNull();
        _innerChatClient.FirstOptions.Tools!.ShouldContain(t => t.Name == "search_paperbase_documents");

        // Issue #100: defaults flow from PaperbaseAIBehaviorOptions — no DocumentTypeCode
        // pinning on the request because the conversation no longer carries it.
        _collection.SearchCalls.ShouldBe(1);
        AssertFilterDoesNotPinDocumentType(_collection.LastSearchFilter);

        var conversation = await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                return await _repository.FindByIdWithMessagesAsync(conversationId, 50);
            }
        });

        var assistant = conversation!.Messages.Single(m => m.Role == ChatMessageRole.Assistant);
        assistant.CitationsJson.ShouldNotBeNullOrEmpty();
        assistant.CitationsJson.ShouldContain(docId.ToString());
    }

    [Fact]
    public async Task SendMessageAsync_Uses_Default_Filter_When_No_Document_Type_Pinned()
    {
        // Issue #100: every conversation is "unscoped" at the aggregate level —
        // there is no longer a per-conversation MinScore to override the default.
        // Defaults come from PaperbaseAIBehaviorOptions (the model can override per
        // tool call when intent calls for it).
        StageSearchHit(Guid.NewGuid(), Guid.NewGuid(), chunkIndex: 0,
            text: "The contract amount is 1,000,000 JPY.");

        var conversationId = await CreateConversationAsync();

        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "八月株式会社の契約金額はいくらですか?",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        _collection.SearchCalls.ShouldBe(1);
        AssertFilterDoesNotPinDocumentType(_collection.LastSearchFilter);
        AssertFilterDoesNotPinSingleDocument(_collection.LastSearchFilter);
    }

    [Fact]
    public async Task SendMessageAsync_Records_Tool_Call_In_Abp_AuditLog_Scope()
    {
        StageSearchHit(Guid.NewGuid(), Guid.NewGuid(), chunkIndex: 0,
            text: "Audited context.");

        var conversationId = await CreateConversationAsync();

        using var auditScope = _auditingManager.BeginScope();
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "Audit the tool call",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        var currentAuditScope = _auditingManager.Current;
        currentAuditScope.ShouldNotBeNull();
        var auditLog = currentAuditScope.Log;
        auditLog.ExtraProperties.ShouldContainKey(ChatTelemetryRecorder.AuditToolCallsPropertyName);
        auditLog.ExtraProperties.ShouldContainKey(ChatTelemetryRecorder.AuditTurnPropertyName);

        var toolCalls = auditLog.ExtraProperties[ChatTelemetryRecorder.AuditToolCallsPropertyName]
            .ShouldBeOfType<List<ChatToolAuditEntry>>();
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].ToolName.ShouldBe("search_paperbase_documents");
        toolCalls[0].Outcome.ShouldBe(ChatTelemetryOutcome.Success);
        toolCalls[0].ArgumentsSummary.ShouldContainKey("query");

        // Sanitization: the raw query string ("payment terms") must NOT survive into
        // the audit log. Only structural metadata (length + hash) is acceptable.
        var auditJson = System.Text.Json.JsonSerializer.Serialize(toolCalls);
        auditJson.ShouldNotContain("payment terms");

        // Token counts are not on the audit entry — they are emitted by Microsoft.Extensions.AI's
        // gen_ai.client.token.usage histogram (see PaperbaseHostModule.ConfigureAI's
        // chatBuilder.UseOpenTelemetry()). The audit entry carries only business-domain
        // fields (tenant/user/conversation/document) plus the project-specific IsDegraded /
        // CitationCount.
        var turn = auditLog.ExtraProperties[ChatTelemetryRecorder.AuditTurnPropertyName]
            .ShouldBeOfType<ChatTurnAuditEntry>();
        turn.ConversationId.ShouldBe(conversationId);
        turn.CitationCount.ShouldBe(1);
        turn.IsDegraded.ShouldBeFalse();
        turn.Outcome.ShouldBe(ChatTelemetryOutcome.Success);

        // Issue #98: per-turn telemetry derives ToolCallSummary / ToolCallDepth /
        // GroundingSource from the per-tool entries on the same audit scope. Here
        // only search_paperbase_documents was invoked once → Vector grounding.
        turn.ToolCallDepth.ShouldBe(1);
        turn.ToolCallSummary.ShouldNotBeNull();
        turn.ToolCallSummary!.ShouldContainKeyAndValue(ChatToolNames.SearchPaperbaseDocuments, 1);
        turn.GroundingSource.ShouldBe(GroundingSource.Vector);
    }

    [Fact]
    public async Task SendMessageAsync_Records_Tool_Failure_When_Search_Throws()
    {
        // The tool implementation rejects with a typed exception. The wrapper at
        // AuditedChatFunction must still emit an audit entry with
        // Outcome=Failure and ExceptionType populated, and the underlying exception
        // must propagate so the model sees the failure (rather than silently
        // "succeeding" with no result).
        _collection.ThrowOnSearch = true;
        _collection.SearchException = new InvalidOperationException("vector store down");

        var conversationId = await CreateConversationAsync();

        using var auditScope = _auditingManager.BeginScope();
        await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                // The chat path catches knowledge-index failures and degrades the answer
                // (IsDegraded=true). The tool-call audit entry is still emitted with
                // Outcome=Failure inside the wrapper.
                await _appService.SendMessageAsync(conversationId, new SendChatMessageInput
                {
                    Message = "Audit the failing tool call",
                    ClientTurnId = Guid.NewGuid()
                });
            }
        });

        var auditLog = _auditingManager.Current!.Log;
        var toolCalls = auditLog.ExtraProperties[ChatTelemetryRecorder.AuditToolCallsPropertyName]
            .ShouldBeOfType<List<ChatToolAuditEntry>>();
        toolCalls.Count.ShouldBe(1);
        toolCalls[0].Outcome.ShouldBe(ChatTelemetryOutcome.Failure);
        toolCalls[0].ExceptionType.ShouldNotBeNullOrEmpty();
    }

    private void StageSearchHit(Guid chunkId, Guid documentId, int chunkIndex, string text, int? pageNumber = null)
    {
        var record = new DocumentChunkRecord
        {
            Id = chunkId,
            TenantId = DocumentChunkPayloadEncoding.HostTenantId,
            DocumentId = DocumentChunkPayloadEncoding.EncodeDocumentId(documentId),
            ChunkIndex = chunkIndex,
            Text = text,
            PageNumber = pageNumber,
        };
        _collection.StagedSearchResults.Enqueue(new[]
        {
            new VectorSearchResult<DocumentChunkRecord>(record, score: 0.9)
        });
    }

    private static void AssertFilterDoesNotPinDocumentType(Func<DocumentChunkRecord, bool>? filter)
    {
        filter.ShouldNotBeNull();
        // A filter that pins DocumentTypeCode would reject probe records with a
        // different (or null) code. Verify by passing two distinct probes and
        // asserting both can pass through (modulo other filter clauses).
        var probeA = NewProbe(documentTypeCode: "contract.general");
        var probeB = NewProbe(documentTypeCode: null);
        (filter!(probeA) || filter(probeB)).ShouldBeTrue(
            "filter should accept at least one document-type-agnostic probe");
    }

    private static void AssertFilterDoesNotPinSingleDocument(Func<DocumentChunkRecord, bool>? filter)
    {
        filter.ShouldNotBeNull();
        // Probe two different DocumentIds — at least one should pass the filter when
        // no single-document pin is in effect.
        var probeA = NewProbe(documentId: Guid.NewGuid());
        var probeB = NewProbe(documentId: Guid.NewGuid());
        (filter!(probeA) || filter(probeB)).ShouldBeTrue(
            "filter should accept records under multiple document ids");
    }

    private static DocumentChunkRecord NewProbe(string? documentTypeCode = null, Guid? documentId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = DocumentChunkPayloadEncoding.HostTenantId,
            DocumentId = DocumentChunkPayloadEncoding.EncodeDocumentId(documentId ?? Guid.NewGuid()),
            DocumentTypeCode = documentTypeCode,
            ChunkIndex = 0,
            Text = "probe",
        };

    private async Task<Guid> CreateConversationAsync()
        => await WithUnitOfWorkAsync(async () =>
        {
            using (ChangeUser(OwnerUserId))
            {
                var dto = await _appService.CreateConversationAsync(new CreateChatConversationInput
                {
                    Title = "Tool Invocation Test"
                });
                return dto.Id;
            }
        });

    private IDisposable ChangeUser(Guid userId)
    {
        var claims = new List<Claim> { new(AbpClaimTypes.UserId, userId.ToString()) };
        return _principalAccessor.Change(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
    }

    private void SetupDefaultEmbedding()
    {
        var embedding = new Embedding<float>(new float[] { 0.1f, 0.2f, 0.3f });
        _embeddingGenerator
            .GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));
    }
}

public sealed class ScriptedToolCallingChatClient : IChatClient
{
    public int Calls { get; private set; }
    public ChatOptions? FirstOptions { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<MEAI.ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Calls == 1)
        {
            FirstOptions = options;
            return Task.FromResult(new ChatResponse(new MEAI.ChatMessage(
                ChatRole.Assistant,
                new List<AIContent>
                {
                    new FunctionCallContent(
                        "call-1",
                        "search_paperbase_documents",
                        new Dictionary<string, object?> { ["query"] = "payment terms" })
                })));
        }

        return Task.FromResult(new ChatResponse(
            new MEAI.ChatMessage(ChatRole.Assistant, "Payment is due within 30 days. [chunk 0]"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 11,
                OutputTokenCount = 7,
                TotalTokenCount = 18
            }
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<MEAI.ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
