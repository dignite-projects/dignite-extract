using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents.Exports;

/// <summary>
/// ExportTemplate / ExportColumn 实体层不变量测试（#207 收敛为 ExtractedField-only）。重点：
/// 列引用 <c>FieldDefinitionId</c>、列数与重复字段约束、按 Order 排序、DocumentTypeId 必填。
/// 列标题由导出引擎在运行时取 <c>FieldDefinition.DisplayName</c>，不在模板列上存储。
/// 系统字段（LifecycleStatus / ReviewStatus / Title）由导出引擎固定输出（不走列配置），故无系统列白名单测试。
/// </summary>
public class ExportTemplateTests
{
    private static readonly Guid TypeId = Guid.NewGuid();

    // --- ExportColumn ---

    [Fact]
    public void Should_Accept_Extracted_Column()
    {
        var fieldId = Guid.NewGuid();
        var col = new ExportColumn(fieldId, 3);
        col.FieldDefinitionId.ShouldBe(fieldId);
        col.Order.ShouldBe(3);
    }

    // --- ExportTemplate ---

    [Fact]
    public void Should_Order_Columns_By_Order_Ascending()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var template = CreateTemplate(
            new ExportColumn(second, 5),
            new ExportColumn(first, 1));

        template.Columns.Count.ShouldBe(2);
        template.Columns[0].FieldDefinitionId.ShouldBe(first);
        template.Columns[1].FieldDefinitionId.ShouldBe(second);
    }

    [Fact]
    public void Should_Reject_Template_With_No_Columns()
    {
        Should.Throw<BusinessException>(() => CreateTemplate())
            .Code.ShouldBe(PaperbaseErrorCodes.Export.TemplateRequiresColumn);
    }

    [Fact]
    public void Should_Reject_Template_With_Duplicate_Fields()
    {
        var duplicateFieldId = Guid.NewGuid();
        Should.Throw<BusinessException>(() => CreateTemplate(
                new ExportColumn(duplicateFieldId, 0),
                new ExportColumn(duplicateFieldId, 1)))
            .Code.ShouldBe(PaperbaseErrorCodes.Export.TemplateDuplicateField);
    }

    [Theory]
    [InlineData("Name\nInjection")]
    [InlineData("Tab\tName")]
    public void Should_Reject_Template_Name_With_Control_Chars(string name)
    {
        Should.Throw<BusinessException>(() => new ExportTemplate(
                Guid.NewGuid(), tenantId: null, name, ExportFormat.Csv, TypeId,
                new[] { new ExportColumn(Guid.NewGuid(), 0) }))
            .Code.ShouldBe(PaperbaseErrorCodes.Export.InvalidTemplateName);
    }

    [Fact]
    public void Update_Should_Replace_Name_Format_DocumentTypeId_And_Columns()
    {
        var newTypeId = Guid.NewGuid();
        var newFieldId = Guid.NewGuid();
        var template = CreateTemplate(new ExportColumn(Guid.NewGuid(), 0));

        template.Update("新名", ExportFormat.Xlsx, newTypeId,
            new[] { new ExportColumn(newFieldId, 0) });

        template.Name.ShouldBe("新名");
        template.Format.ShouldBe(ExportFormat.Xlsx);
        template.DocumentTypeId.ShouldBe(newTypeId);
        template.Columns.Count.ShouldBe(1);
        template.Columns[0].FieldDefinitionId.ShouldBe(newFieldId);
    }

    private static ExportTemplate CreateTemplate(params ExportColumn[] columns) =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            name: "Test",
            format: ExportFormat.Csv,
            documentTypeId: TypeId,
            columns: columns);
}
