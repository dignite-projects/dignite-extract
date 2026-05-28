using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Shouldly;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.MultiTenancy;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

/// <summary>
/// <see cref="EfCoreDocumentRepository.GetFieldMatchedIdsAsync"/> 的真实 EF 集成测试（SQLite）——字段架构 v2
/// / Issue #206。字段值改为 <see cref="DocumentExtractedField"/> 一等 child 的普通类型化列后，匹配查询是纯
/// EF Core LINQ（Documents-anchored + child EXISTS + 普通列比较），可在 SQLite 上端到端执行；不再像旧
/// JSON_VALUE / TRY_CONVERT 方案那样只能在内存断言生成的 SQL。
/// 覆盖：各 DataType 等值 / 范围、多字段 AND、租户隔离、软删除、reclassify 整组替换、loud fail-closed。
/// </summary>
public class EfCoreDocumentRepositorySearch_Tests : PaperbaseEntityFrameworkCoreTestBase
{
    private const string TypeCode = "contract.general";

    private readonly IDocumentRepository _documentRepository;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;

    public EfCoreDocumentRepositorySearch_Tests()
    {
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _currentTenant = GetRequiredService<ICurrentTenant>();
        _dataFilter = GetRequiredService<IDataFilter>();
    }

    // ─── loud fail-closed（触达普通列比较前短路 / 拒绝） ─────────────────────────

    [Fact]
    public async Task Empty_field_queries_returns_empty()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, Array.Empty<DocumentFieldQuery>());

            ids.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Range_on_string_field_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode,
                new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValueMin: "a", FieldValueMax: "z") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.FieldTypeDoesNotSupportRange);
        });
    }

    [Fact]
    public async Task Range_on_boolean_field_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode,
                new[] { new DocumentFieldQuery("active", FieldDataType.Boolean, FieldValueMin: "false", FieldValueMax: "true") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.FieldTypeDoesNotSupportRange);
        });
    }

    [Fact]
    public async Task Value_not_matching_declared_type_throws()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // Integer 字段传 "abc" → 值无法解析为声明类型 → loud fail（不静默空）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode,
                new[] { new DocumentFieldQuery("count", FieldDataType.Integer, FieldValue: "abc") }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.InvalidExtractedFieldValue);
        });
    }

    [Fact]
    public async Task Field_query_with_no_value_throws_fail_closed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 等值 / 区间全空是残缺查询——必须 loud fail，绝不退化成「该类型全捞」（纵深防御，非 DTO 校验路径）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode,
                new[] { new DocumentFieldQuery("count", FieldDataType.Integer) }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.InvalidExtractedFieldValue);
        });
    }

    [Theory]
    [InlineData("2024-01-01T10:00:00+08:00")]   // 显式偏移
    [InlineData("2024-01-01T10:00:00Z")]        // UTC 'Z'
    public async Task DateTime_offset_bearing_value_throws(string offsetInput)
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 带时区的 DateTime 入参与存储侧 wall-clock 语义不一致 → 判脏入参 loud fail（不静默空）。
            var ex = await Should.ThrowAsync<BusinessException>(() => _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode,
                new[] { new DocumentFieldQuery("created", FieldDataType.DateTime, FieldValue: offsetInput) }));

            ex.Code.ShouldBe(PaperbaseErrorCodes.InvalidExtractedFieldValue);
        });
    }

    // ─── 各 DataType 等值 ───────────────────────────────────────────────────────

    [Fact]
    public async Task String_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            await InsertDocumentAsync(Field("party", FieldDataType.String, "Globex"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Boolean_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("active", FieldDataType.Boolean, true));
            await InsertDocumentAsync(Field("active", FieldDataType.Boolean, false));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("active", FieldDataType.Boolean, FieldValue: "true") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Integer_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("count", FieldDataType.Integer, 7L));
            await InsertDocumentAsync(Field("count", FieldDataType.Integer, 9L));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("count", FieldDataType.Integer, FieldValue: "7") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Decimal_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("amount", FieldDataType.Decimal, 123.45m));
            await InsertDocumentAsync(Field("amount", FieldDataType.Decimal, 999.99m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("amount", FieldDataType.Decimal, FieldValue: "123.45") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task Date_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-01-15"));
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-02-20"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("signed_on", FieldDataType.Date, FieldValue: "2024-01-15") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    [Fact]
    public async Task DateTime_equality_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var hit = await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-15T10:30:00"));
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-15T18:45:00"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("created", FieldDataType.DateTime, FieldValue: "2024-01-15T10:30:00") });

            ids.ShouldHaveSingleItem().ShouldBe(hit);
        });
    }

    // ─── 范围（含界）───────────────────────────────────────────────────────────

    [Fact]
    public async Task Integer_range_matches_inclusive()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("count", FieldDataType.Integer, 100L));   // 下界（含）
            var mid = await InsertDocumentAsync(Field("count", FieldDataType.Integer, 150L));
            await InsertDocumentAsync(Field("count", FieldDataType.Integer, 250L));   // 越上界

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("count", FieldDataType.Integer, FieldValueMin: "100", FieldValueMax: "200") });

            ids.Count.ShouldBe(2);
            ids.ShouldContain(mid);
        });
    }

    [Fact]
    public async Task Decimal_range_matches_inclusive()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            // 同位数（3 整数位）取值——既验证数值区间逻辑，又规避 SQLite 把 decimal 存为 TEXT、按字典序比较的怪异
            // （生产 SQL Server 是真 decimal 列、数值比较）。代码路径与 Integer/Date/DateTime 区间完全一致。
            await InsertDocumentAsync(Field("amount", FieldDataType.Decimal, 200m));
            var mid = await InsertDocumentAsync(Field("amount", FieldDataType.Decimal, 300m));
            await InsertDocumentAsync(Field("amount", FieldDataType.Decimal, 400m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("amount", FieldDataType.Decimal, FieldValueMin: "250", FieldValueMax: "350") });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    [Fact]
    public async Task Date_range_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2023-12-31"));
            var mid = await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2024-06-15"));
            await InsertDocumentAsync(Field("signed_on", FieldDataType.Date, "2025-01-01"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("signed_on", FieldDataType.Date, FieldValueMin: "2024-01-01", FieldValueMax: "2024-12-31") });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    [Fact]
    public async Task DateTime_range_matches()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-01-01T00:00:00"));
            var mid = await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2024-06-15T12:00:00"));
            await InsertDocumentAsync(Field("created", FieldDataType.DateTime, "2025-01-01T00:00:00"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[]
                {
                    new DocumentFieldQuery("created", FieldDataType.DateTime,
                        FieldValueMin: "2024-01-02T00:00:00", FieldValueMax: "2024-12-31T23:59:59")
                });

            ids.ShouldHaveSingleItem().ShouldBe(mid);
        });
    }

    // ─── 多字段 AND ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Multiple_field_filters_are_ANDed()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var both = await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Acme"),
                Field("amount", FieldDataType.Decimal, 300m));
            // 只满足一个条件 → 不应命中（AND，不同字段互相收窄）。
            await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Acme"),
                Field("amount", FieldDataType.Decimal, 999m));
            await InsertDocumentAsync(
                Field("party", FieldDataType.String, "Globex"),
                Field("amount", FieldDataType.Decimal, 300m));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[]
                {
                    new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme"),
                    new DocumentFieldQuery("amount", FieldDataType.Decimal, FieldValueMin: "250", FieldValueMax: "350")
                });

            ids.ShouldHaveSingleItem().ShouldBe(both);
        });
    }

    // ─── 锚定文档类型 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Field_match_anchors_to_document_type()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var contract = await InsertDocumentAsync(TypeCode, Field("party", FieldDataType.String, "Acme"));
            // 同字段值但不同类型——不应命中（查询锚定单一 documentTypeCode）。
            await InsertDocumentAsync("invoice.general", Field("party", FieldDataType.String, "Acme"));

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });

            ids.ShouldHaveSingleItem().ShouldBe(contract);
        });
    }

    // ─── 软删除隔离（沿用 Document 聚合根的 ISoftDelete 全局过滤器） ──────────────

    [Fact]
    public async Task Soft_deleted_documents_are_excluded_by_default()
    {
        await WithUnitOfWorkAsync(async () =>
        {
            var id = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            await _documentRepository.DeleteAsync(id, autoSave: true); // 软删

            var ids = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });

            ids.ShouldBeEmpty();

            // 回收站语义：在 Disable<ISoftDelete> 作用域内（GetListAsync 的 IsDeleted 路径）应能命中。
            using (_dataFilter.Disable<ISoftDelete>())
            {
                var deletedIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });
                deletedIds.ShouldHaveSingleItem().ShouldBe(id);
            }
        });
    }

    // ─── 租户隔离（沿用 Document 聚合根的 IMultiTenant 全局过滤器，不手写 TenantId 谓词） ──

    [Fact]
    public async Task Tenant_isolation_is_enforced_by_document_filter()
    {
        var tenantId = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            Guid tenantDocId;
            using (_currentTenant.Change(tenantId))
            {
                tenantDocId = await InsertDocumentAsync(Field("party", FieldDataType.String, "Acme"));
            }

            // Host 上下文（CurrentTenant.Id == null）查不到租户文档。
            var hostIds = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });
            hostIds.ShouldBeEmpty();

            // 切到该租户上下文即可查到。
            using (_currentTenant.Change(tenantId))
            {
                var tenantIds = await _documentRepository.GetFieldMatchedIdsAsync(
                    TypeCode, new[] { new DocumentFieldQuery("party", FieldDataType.String, FieldValue: "Acme") });
                tenantIds.ShouldHaveSingleItem().ShouldBe(tenantDocId);
            }
        });
    }

    // ─── 整组替换 / 原地更新（reconcile） ───────────────────────────────────────

    [Fact]
    public async Task Reclassify_replaces_whole_field_set()
    {
        Guid id = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(
                CreateDocument(id, _currentTenant.Id, TypeCode, Field("amount", FieldDataType.Decimal, 100m)),
                autoSave: true);
        });

        // reclassify 到新类型 + 新字段集（含 details 加载现有字段行供 reconcile diff）。
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            typeof(Document).GetProperty(nameof(Document.DocumentTypeCode))!.SetValue(doc, "invoice.general");
            doc.SetFields(new[] { Field("total", FieldDataType.Decimal, 200m) });
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(id, includeDetails: true);

            // 旧 schema 字段（amount，contract）不残留；只剩新 schema 字段（total，invoice）。
            reloaded.ExtractedFieldValues.Select(f => f.Name).ShouldBe(new[] { "total" });
            reloaded.ExtractedFieldValues.Single().DocumentTypeCode.ShouldBe("invoice.general");

            // 旧字段已查不到。
            var oldHits = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("amount", FieldDataType.Decimal, FieldValue: "100") });
            oldHits.ShouldBeEmpty();
        });
    }

    [Fact]
    public async Task Same_name_field_is_updated_in_place()
    {
        Guid id = Guid.NewGuid();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentRepository.InsertAsync(
                CreateDocument(id, _currentTenant.Id, TypeCode, Field("amount", FieldDataType.Decimal, 100m)),
                autoSave: true);
        });

        // 操作员手改：同名字段值改 100 → 200（复合键 (DocumentId, Name) 下 reconcile 原地更新，不产生重复行 / PK 冲突）。
        await WithUnitOfWorkAsync(async () =>
        {
            var doc = await _documentRepository.GetAsync(id, includeDetails: true);
            doc.SetFields(new[] { Field("amount", FieldDataType.Decimal, 200m) });
            await _documentRepository.UpdateAsync(doc, autoSave: true);
        });

        await WithUnitOfWorkAsync(async () =>
        {
            var reloaded = await _documentRepository.GetAsync(id, includeDetails: true);
            reloaded.ExtractedFieldValues.ShouldHaveSingleItem().DecimalValue.ShouldBe(200m);

            var newHits = await _documentRepository.GetFieldMatchedIdsAsync(
                TypeCode, new[] { new DocumentFieldQuery("amount", FieldDataType.Decimal, FieldValue: "200") });
            newHits.ShouldHaveSingleItem().ShouldBe(id);
        });
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private Task<Guid> InsertDocumentAsync(params DocumentFieldValue[] fields)
        => InsertDocumentAsync(TypeCode, fields);

    private async Task<Guid> InsertDocumentAsync(string typeCode, params DocumentFieldValue[] fields)
    {
        var id = Guid.NewGuid();
        await _documentRepository.InsertAsync(
            CreateDocument(id, _currentTenant.Id, typeCode, fields), autoSave: true);
        return id;
    }

    private static Document CreateDocument(Guid id, Guid? tenantId, string typeCode, params DocumentFieldValue[] fields)
    {
        var doc = new Document(
            id,
            tenantId,
            originalFileBlobName: $"blobs/{id:N}.pdf",
            sourceType: SourceType.Digital,
            fileOrigin: new FileOrigin(
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "f.pdf"));

        // DocumentTypeCode 为 Domain private setter——测试用反射模拟"已分类"。
        typeof(Document).GetProperty(nameof(Document.DocumentTypeCode))!.SetValue(doc, typeCode);

        if (fields.Length > 0)
        {
            doc.SetFields(fields);
        }

        return doc;
    }

    private static DocumentFieldValue Field<T>(string name, FieldDataType dataType, T value)
        => new(name, dataType, JsonSerializer.SerializeToElement(value));
}
