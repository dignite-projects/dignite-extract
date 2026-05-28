using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Volo.Abp.Guids;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// 出口 DTO 的 <c>ExtractedFields</c> wire-format 由 App / Mapper 层从 <see cref="DocumentExtractedField"/> typed child 行
/// 即时组装（Issue #206）。本测试走真实 EF（SQLite）验证这条读路径的两个机制：
/// <list type="bullet">
///   <item><c>WithDetailsAsync(选择器)</c>——<c>GetListAsync</c> 用来 eager-load child 行的 ABP 仓储 API：一次 JOIN
///   取回，不依赖 lazy loading（lazy 在测试 / 生产都未启用，此测试通过即证明 mapper 组装不触发 N+1 / lazy）；</item>
///   <item><see cref="DocumentExtractedField.ToJsonElement"/>——把各 DataType 的类型化列重建为规范 JSON（mapper 组装字典的逐项转换），
///   与写入侧 <c>SetValue</c> 往返一致。</item>
/// </list>
/// （DTO/mapper 的字典封装 = <c>ExtractedFieldValues.ToDictionary(f =&gt; f.Name, f =&gt; f.ToJsonElement())</c>，
/// 即本测试断言的形态；mapper 接线另由 Application.Tests 在内存层覆盖。）
/// </summary>
public class DocumentReadAssembly_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private const string TypeCode = "host.invoice";

    private readonly IDocumentRepository _documentRepository;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentReadAssembly_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task WithDetails_eager_loads_child_rows_and_round_trips_each_DataType()
    {
        var id = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => InsertAsync(id,
            Field("amount", FieldDataType.Decimal, 1000.50m),
            Field("partner", FieldDataType.String, "Acme"),
            Field("paid", FieldDataType.Boolean, true),
            Field("count", FieldDataType.Integer, 7L),
            Field("issued", FieldDataType.Date, "2024-03-09"),
            Field("created", FieldDataType.DateTime, "2024-03-09T13:45:00")));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            // mapper 即时组装出的字典形态。
            var fields = doc.ExtractedFieldValues.ToDictionary(f => f.Name, f => f.ToJsonElement());

            fields.Count.ShouldBe(6);
            fields["amount"].GetDecimal().ShouldBe(1000.50m);
            fields["partner"].GetString().ShouldBe("Acme");
            fields["paid"].GetBoolean().ShouldBeTrue();
            fields["count"].GetInt64().ShouldBe(7L);
            fields["issued"].GetString().ShouldBe("2024-03-09");
            fields["created"].GetString().ShouldBe("2024-03-09T13:45:00");
        });
    }

    [Fact]
    public async Task Document_without_fields_has_empty_child_collection()
    {
        var id = _guidGenerator.Create();
        await WithUnitOfWorkAsync(() => InsertAsync(id));

        await WithUnitOfWorkAsync(async () =>
        {
            var query = await _documentRepository.WithDetailsAsync(d => d.ExtractedFieldValues);
            var doc = (await query.Where(d => d.Id == id).ToListAsync()).Single();

            // 空集合 → mapper 组装出 null（与旧 JSON 列"未抽取时 null"语义一致）。
            doc.ExtractedFieldValues.ShouldBeEmpty();
        });
    }

    private async Task InsertAsync(Guid id, params DocumentFieldValue[] fields)
    {
        var doc = new Document(
            id,
            tenantId: null,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin("test-user", "application/pdf", $"{Guid.NewGuid():N}{Guid.NewGuid():N}", 1024, "f.pdf"));
        typeof(Document).GetProperty(nameof(Document.DocumentTypeCode))!.SetValue(doc, TypeCode);
        if (fields.Length > 0)
        {
            doc.SetFields(fields);
        }
        await _documentRepository.InsertAsync(doc, autoSave: true);
    }

    private static DocumentFieldValue Field<T>(string name, FieldDataType dataType, T value)
        => new(name, dataType, JsonSerializer.SerializeToElement(value));
}
