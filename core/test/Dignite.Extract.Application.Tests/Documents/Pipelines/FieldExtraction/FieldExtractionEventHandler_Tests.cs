using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Extract.Abstractions.Documents;
using Dignite.Extract.Documents;
using Dignite.Extract.Documents.DocumentTypes;
using Dignite.Extract.Documents.Fields;
using Dignite.Extract.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Extract.Documents;

[DependsOn(typeof(ExtractApplicationTestModule))]
public class FieldExtractionEventHandlerTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentRepository>());
        context.Services.AddSingleton(Substitute.For<IDocumentTypeRepository>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionRepository>());
        context.Services.AddSingleton(Substitute.For<IDistributedEventBus>());

        // FieldExtractionWorkflow is a concrete class, so use ForPartsOf with fake constructor dependencies.
        // Each test case configures the virtual ExtractAsync with Returns / Throws.
        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(),
            NullLogger<FieldExtractionWorkflow>.Instance);
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// Behavior tests for <see cref="FieldExtractionEventHandler"/>, focused on three correctness constraints:
/// <list type="number">
///   <item>Reclassify race: stale events for documents reclassified by an operator while in flight must be discarded, so old-schema values cannot pollute ExtractedFields.</item>
///   <item>Cross-tenant defense: discard events when TenantId and Document.TenantId mismatch, preventing leaks through DataFilter disable paths.</item>
///   <item>ETO contract: FieldsExtractedEto is published with outbox semantics for both empty-field and populated-field paths.</item>
/// </list>
/// #207: handler reads field definitions according to the Document's current DocumentTypeId; event TypeCode is
/// only a stale-reclassify helper.
/// Tests derive stable Guids from name / code to keep mocks consistent.
/// </summary>
public class FieldExtractionEventHandler_Tests
    : ExtractApplicationTestBase<FieldExtractionEventHandlerTestModule>
{
    private readonly FieldExtractionEventHandler _handler;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IDistributedEventBus _eventBus;

    public FieldExtractionEventHandler_Tests()
    {
        _handler = GetRequiredService<FieldExtractionEventHandler>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Empty_DocumentTypeCode_Returns_Early_Without_Publishing()
    {
        var evt = new DocumentClassifiedEto
        {
            DocumentId = Guid.NewGuid(),
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = string.Empty,
            ClassificationConfidence = 0
        };

        await _handler.HandleEventAsync(evt);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
        await _fieldDefinitionRepository.DidNotReceive().GetListAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Publishes_Empty_FieldsExtractedEto()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // Publish an empty event even with no field definitions, so downstream DocumentReady can advance.
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());

        // LLM should not be called; no field definitions short-circuit directly.
        await _workflow.DidNotReceive().ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_Field_Definitions_Clears_Stale_Fields_From_Previous_Type()
    {
        // Reclassifying to a type with no field definitions must clear stale field rows from the old schema
        // (#206 acceptance: reclassify leaves no old-schema residue; review finding #1).
        var doc = CreateDocument(tenantId: null, typeCode: "blank.type");
        doc.SetFields(new[]
        {
            new DocumentFieldValue(FieldId("amount"), FieldDataType.Number, JsonDocument.Parse("100").RootElement)
        });
        doc.ExtractedFieldValues.ShouldNotBeEmpty();

        SetupType("blank.type");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetListAsync(TypeId("blank.type"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition>());

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "blank.type",
            ClassificationConfidence = 1.0
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Missing_Document_Logs_And_Returns_Without_Publishing()
    {
        var docId = Guid.NewGuid();
        SetupType("contract.general");
        var defs = new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") };
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });
        _documentRepository
            .FindAsync(docId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns((Document?)null);

        var evt = new DocumentClassifiedEto
        {
            DocumentId = docId,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // FieldsExtractedEto should not be published; the Document is gone, so downstream consumers cannot use it.
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Cross_Tenant_Event_Is_Discarded_Without_Writing_Fields()
    {
        // CLAUDE.md "Security covenant / multi-tenant isolation":
        // event TenantId and Document.TenantId mismatch -> prevent leaks through DataFilter disable paths.
        var eventTenant = Guid.NewGuid();
        var docTenant = Guid.NewGuid();   // Different tenant.
        var doc = CreateDocument(tenantId: docTenant, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount", tenantId: eventTenant) });
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = eventTenant,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Stale_TypeCode_From_Reclassify_Race_Is_Discarded()
    {
        // Reclassify race defense, the core safety constraint:
        // the event payload has DocumentTypeCode=contract.general, but the current Document has already been
        // reclassified by an operator to invoice.general with a different DocumentTypeId. Continuing extraction
        // would write old contract-schema values into ExtractedFields, creating a dirty state where TypeId=invoice
        // but fields come from contract. Correct behavior: discard the stale event and wait for the new
        // classification event to trigger another extraction.
        var doc = CreateDocument(tenantId: null, typeCode: "invoice.general");
        SetupType("contract.general");
        SetupType("invoice.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(new List<FieldDefinition> { CreateFieldDefinition("contract.general", "amount") });
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1000").RootElement });

        var staleEvent = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",   // ← stale typeCode（resolves to a different typeId than doc）
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(staleEvent);

        // Key assertion: fields extracted from the contract schema must not be written to the Document, whose
        // current typeId is invoice.
        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.DidNotReceive().UpdateAsync(
            Arg.Any<Document>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<FieldsExtractedEto>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Happy_Path_Writes_Fields_And_Publishes_FieldsExtractedEto()
    {
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);

        // Values align with DataType. In production, workflow has already validated types:
        // amount=Number numeric, party=Text string, date=Date.
        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Number),
            CreateFieldDefinition("contract.general", "party", FieldDataType.Text),
            CreateFieldDefinition("contract.general", "date", FieldDataType.Date)
        };
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement,
                ["party"] = JsonDocument.Parse("\"Acme Corp\"").RootElement,
                ["date"] = null   // LLM failed to extract it, so it should not enter the field set.
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        // Field values are written by FieldDefinitionId (#207); null values do not enter the field set.
        var fieldIds = doc.ExtractedFieldValues.Select(f => f.FieldDefinitionId).ToList();
        fieldIds.Count.ShouldBe(2);
        fieldIds.ShouldContain(FieldId("amount"));
        fieldIds.ShouldContain(FieldId("party"));
        fieldIds.ShouldNotContain(FieldId("date"));

        await _documentRepository.Received(1).UpdateAsync(
            doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // FieldCount equals actual non-null field count, not total definition count.
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 2),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Renamed_TypeCode_Event_Uses_Current_DocumentTypeId_And_Publishes_Current_Code()
    {
        // TypeCode rename race: event still has the old code, but DocumentTypeId is the stable internal relation.
        // When the old code cannot be resolved, extraction should not be skipped; it should extract by current
        // type Id and publish the current TypeCode.
        var typeId = TypeId("contract.general");
        var doc = CreateDocument(tenantId: null, documentTypeId: typeId);
        SetupType("contract.renamed", typeId: typeId);
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);

        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition(typeId, "amount", FieldDataType.Number)
        };
        _fieldDefinitionRepository
            .GetListAsync(typeId, Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",   // old code, now unresolvable
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.Count.ShouldBe(1);
        doc.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(FieldId("amount"));

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.renamed" &&
                e.FieldCount == 1),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task DataType_Changed_During_Extraction_Skips_Stale_Value()
    {
        // While the LLM call is in flight, an admin changes the field type from Number to Text. The number extracted
        // from the old descriptor must not be written into the current text field.
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository
            .FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
        // Empty-field clearing path + write-back path use FindWithFieldValuesAsync, eager-loading only field values,
        // not PipelineRuns.
        _documentRepository
            .FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>())
            .Returns(doc);

        var initialDefs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Number)
        };
        var currentDefs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Text)
        };
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(initialDefs, currentDefs);
        _workflow
            .ExtractAsync(
                Arg.Is<IReadOnlyList<FieldExtractionDescriptor>>(d =>
                    d.Count == 1 && d[0].Name == "amount" && d[0].DataType == FieldDataType.Number),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement
            });

        var evt = new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        };

        await _handler.HandleEventAsync(evt);

        doc.ExtractedFieldValues.ShouldBeEmpty();
        await _documentRepository.Received(1).UpdateAsync(
            doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<FieldsExtractedEto>(e =>
                e.DocumentId == doc.Id &&
                e.DocumentTypeCode == "contract.general" &&
                e.FieldCount == 0),
            Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task Missing_Required_Field_Sets_MissingRequiredFields_Reason()
    {
        // #284: required field was not extracted -> materialize MissingRequiredFields when extraction completes.
        // It is non-blocking and enters the operator queue.
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);

        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Number, isRequired: true),
            CreateFieldDefinition("contract.general", "party", FieldDataType.Text)
        };
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = null,   // Required value is missing.
                ["party"] = JsonDocument.Parse("\"Acme\"").RootElement
            });

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        });

        (doc.ReviewReasons & DocumentReviewReasons.MissingRequiredFields)
            .ShouldBe(DocumentReviewReasons.MissingRequiredFields);
    }

    [Fact]
    public async Task All_Required_Fields_Present_Does_Not_Set_MissingRequiredFields()
    {
        // #284: all required fields were extracted -> do not set MissingRequiredFields, so it does not enter the
        // required-field queue.
        var doc = CreateDocument(tenantId: null, typeCode: "contract.general");
        SetupType("contract.general");
        _documentRepository.FindAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(doc);
        _documentRepository.FindWithFieldValuesAsync(doc.Id, Arg.Any<CancellationToken>()).Returns(doc);

        var defs = new List<FieldDefinition>
        {
            CreateFieldDefinition("contract.general", "amount", FieldDataType.Number, isRequired: true)
        };
        _fieldDefinitionRepository
            .GetListAsync(TypeId("contract.general"), Arg.Any<CancellationToken>())
            .Returns(defs);
        _workflow
            .ExtractAsync(
                Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, JsonElement?>
            {
                ["amount"] = JsonDocument.Parse("1500").RootElement
            });

        await _handler.HandleEventAsync(new DocumentClassifiedEto
        {
            DocumentId = doc.Id,
            TenantId = null,
            EventTime = DateTime.UtcNow,
            DocumentTypeCode = "contract.general",
            ClassificationConfidence = 0.92
        });

        (doc.ReviewReasons & DocumentReviewReasons.MissingRequiredFields)
            .ShouldBe(DocumentReviewReasons.None);
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    private void SetupType(string code, Guid? tenantId = null, Guid? typeId = null)
    {
        var id = typeId ?? TypeId(code);
        var type = new DocumentType(id, tenantId, code, code);
        _documentTypeRepository
            .FindByTypeCodeAsync(code, Arg.Any<CancellationToken>())
            .Returns(type);
        _documentTypeRepository
            .FindAsync(id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(type);
    }

    private static Document CreateDocument(Guid? tenantId, string typeCode)
        => CreateDocument(tenantId, TypeId(typeCode));

    private static Document CreateDocument(Guid? tenantId, Guid documentTypeId)
    {
        var doc = new Document(
            Guid.NewGuid(), tenantId,
            new FileOrigin(
                blobName: $"blobs/{Guid.NewGuid():N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));

        // Use the internal channel to write DocumentTypeId and simulate the classified state (#207: classification
        // result is the internal Id).
        typeof(Document)
            .GetMethod("ApplyAutomaticClassificationResult",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, [documentTypeId, 0.99]);
        typeof(Document)
            .GetMethod("SetMarkdown",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(doc, ["# Body"]);

        return doc;
    }

    private static FieldDefinition CreateFieldDefinition(
        string documentTypeCode, string name,
        FieldDataType dataType = FieldDataType.Text, Guid? tenantId = null, bool isRequired = false) =>
        CreateFieldDefinition(TypeId(documentTypeCode), name, dataType, tenantId, isRequired);

    private static FieldDefinition CreateFieldDefinition(
        Guid documentTypeId, string name,
        FieldDataType dataType = FieldDataType.Text, Guid? tenantId = null, bool isRequired = false) =>
        new(
            id: FieldId(name),
            tenantId: tenantId,
            documentTypeId: documentTypeId,
            name: name,
            displayName: name,
            prompt: $"Extract the {name}.",
            dataType: dataType,
            displayOrder: 0,
            isRequired: isRequired);

    private static Guid TypeId(string code) => new(MD5.HashData(Encoding.UTF8.GetBytes("type:" + code)));
    private static Guid FieldId(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes("field:" + name)));
}
