using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// ExportTemplateAppService.ExportAsync 集成测试（SQLite 真实 EF）。覆盖 Codex adversarial review
/// 后两条修复的运行时行为：
/// <list type="bullet">
///   <item>ExtractedFields（json 列 + ValueConverter）能否被 Select 投影到非实体类型并正确取值</item>
///   <item>over-cap fail-fast（fetch Max+1，超限抛错而非静默截断）</item>
/// </list>
/// </summary>
public class ExportTemplateExport_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private readonly IExportTemplateAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IExportTemplateRepository _templateRepository;
    private readonly IGuidGenerator _guidGenerator;

    public ExportTemplateExport_Tests()
    {
        _appService = GetRequiredService<IExportTemplateAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _templateRepository = GetRequiredService<IExportTemplateRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Export_Should_Project_System_And_Extracted_Columns_From_Db()
    {
        var templateId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(
                CreateDocument(
                    _guidGenerator.Create(),
                    "host.invoice",
                    "Invoice A",
                    new Dictionary<string, JsonElement>
                    {
                        ["amount"] = Json("1000"),
                        ["partner"] = Json("Acme"),
                    }),
                autoSave: true);

            await _templateRepository.InsertAsync(
                new ExportTemplate(
                    templateId,
                    tenantId: null,
                    name: "Invoice Export",
                    format: ExportFormat.Csv,
                    documentTypeCode: "host.invoice",
                    new[]
                    {
                        new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "标题", 0),
                        new ExportColumn(ExportColumnSourceKind.Extracted, "amount", "金额", 1),
                        new ExportColumn(ExportColumnSourceKind.Extracted, "partner", "对方", 2),
                    }),
                autoSave: true);
        });

        string csv = null!;
        await WithUnitOfWorkAsync(async () =>
        {
            var content = await _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId });
            using var reader = new StreamReader(content.GetStream());
            csv = await reader.ReadToEndAsync();
        });

        // 表头按列定义顺序，Extracted 列从 json 投影正确取值。
        csv.ShouldContain("标题,金额,对方");
        csv.ShouldContain("Invoice A,1000,Acme");
    }

    [Fact]
    public async Task Export_Should_Fail_When_Over_Cap_Instead_Of_Truncating()
    {
        var originalMax = ExportTemplateConsts.MaxExportDocumentCount;
        ExportTemplateConsts.MaxExportDocumentCount = 2;
        try
        {
            var templateId = _guidGenerator.Create();

            await WithUnitOfWorkAsync(async () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    await _documentRepository.InsertAsync(
                        CreateDocument(_guidGenerator.Create(), "host.invoice", $"Doc {i}", fields: null),
                        autoSave: true);
                }

                await _templateRepository.InsertAsync(
                    new ExportTemplate(
                        templateId,
                        tenantId: null,
                        name: "Capped",
                        format: ExportFormat.Csv,
                        documentTypeCode: null,
                        new[] { new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "T", 0) }),
                    autoSave: true);
            });

            await WithUnitOfWorkAsync(async () =>
            {
                var ex = await Should.ThrowAsync<BusinessException>(() =>
                    _appService.ExportAsync(new ExportDocumentsInput { TemplateId = templateId }));
                ex.Code.ShouldBe(PaperbaseErrorCodes.ExportDocumentLimitExceeded);
            });
        }
        finally
        {
            ExportTemplateConsts.MaxExportDocumentCount = originalMax;
        }
    }

    private static Document CreateDocument(
        Guid id,
        string typeCode,
        string title,
        Dictionary<string, JsonElement>? fields)
    {
        var document = new Document(
            id,
            tenantId: null,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeCode / Title 为 private setter——测试用反射模拟"已分类 + 已提取标题"。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeCode))!.SetValue(document, typeCode);
        typeof(Document).GetProperty(nameof(Document.Title))!.SetValue(document, title);

        if (fields != null)
        {
            document.SetExtractedFields(fields);
        }

        return document;
    }

    private static JsonElement Json(string value)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return doc.RootElement.Clone();
    }
}
