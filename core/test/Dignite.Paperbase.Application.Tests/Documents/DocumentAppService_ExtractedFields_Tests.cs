using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Abstractions.Documents;
using NSubstitute;
using Shouldly;
using Volo.Abp;
using Volo.Abp.EventBus.Distributed;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// <see cref="DocumentAppService.UpdateExtractedFieldsAsync"/> 行为测试（元数据手改 #195）。
/// 复用 <see cref="DocumentAppServiceReviewTestModule"/> 的 mock 依赖 + 真实 DI（ObjectMapper 等）。
/// </summary>
public class DocumentAppService_ExtractedFields_Tests
    : PaperbaseApplicationTestBase<DocumentAppServiceReviewTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDistributedEventBus _eventBus;

    public DocumentAppService_ExtractedFields_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _eventBus = GetRequiredService<IDistributedEventBus>();
    }

    [Fact]
    public async Task Should_Write_Fields_And_Republish_FieldsExtractedEto()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount", "party");

        await _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
        {
            Fields = new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonString("1000"),
                ["party"] = JsonString("Acme")
            }
        });

        doc.ExtractedFields.ShouldNotBeNull();
        doc.ExtractedFields!.Count.ShouldBe(2);
        await _documentRepository.Received().UpdateAsync(doc, Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await _eventBus.Received().PublishAsync(
            Arg.Is<FieldsExtractedEto>(e => e.DocumentId == doc.Id && e.FieldCount == 2),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task Should_Reject_Unknown_Field_Key()
    {
        var doc = CreateClassifiedDocument("host.contract");
        StubGet(doc);
        StubFields("host.contract", "amount");

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement> { ["unknown"] = JsonString("x") }
            }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.UnknownExtractedField);
    }

    [Fact]
    public async Task Should_Reject_When_Document_Not_Classified()
    {
        var doc = CreateDocument(); // DocumentTypeCode 为 null
        StubGet(doc);

        var ex = await Should.ThrowAsync<BusinessException>(() =>
            _appService.UpdateExtractedFieldsAsync(doc.Id, new UpdateExtractedFieldsInput
            {
                Fields = new Dictionary<string, JsonElement>()
            }));

        ex.Code.ShouldBe(PaperbaseErrorCodes.DocumentNotClassified);
    }

    private void StubGet(Document doc)
    {
        _documentRepository.GetAsync(doc.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(doc);
    }

    private void StubFields(string typeCode, params string[] names)
    {
        var defs = names
            .Select(n => new FieldDefinition(
                Guid.NewGuid(), tenantId: null, documentTypeCode: typeCode,
                name: n, displayName: n, prompt: "extract " + n, dataType: FieldDataType.String))
            .ToList();
        _fieldDefinitionRepository.GetForExtractionAsync(typeCode, Arg.Any<CancellationToken>())
            .Returns(defs);
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

    private static Document CreateClassifiedDocument(string typeCode)
    {
        var doc = CreateDocument();
        // DocumentTypeCode 经 Domain internal 方法设置；测试项目只对 Application 开放 internal，
        // 不能调 Document.ConfirmClassification —— 用反射写 private setter 模拟"已分类"。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeCode))!.SetValue(doc, typeCode);
        return doc;
    }

    private static JsonElement JsonString(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
