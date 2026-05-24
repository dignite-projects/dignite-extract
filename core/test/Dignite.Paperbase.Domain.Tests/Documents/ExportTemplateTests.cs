using System;
using Shouldly;
using Volo.Abp;
using Xunit;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// ExportTemplate / ExportColumn 实体层不变量测试。重点：
/// System 列 Key 须命中白名单、Extracted 列 Key 须符合 FieldDefinition 名规则、
/// 列名控制字符过滤、列数与重名约束、按 Order 排序。
/// </summary>
public class ExportTemplateTests
{
    // --- ExportColumn ---

    [Theory]
    [InlineData(ExportSystemFields.Title)]
    [InlineData(ExportSystemFields.DocumentTypeCode)]
    [InlineData(ExportSystemFields.CreationTime)]
    [InlineData(ExportSystemFields.OriginalFileName)]
    public void Should_Accept_System_Column_With_Whitelisted_Key(string key)
    {
        var col = new ExportColumn(ExportColumnSourceKind.System, key, "Col", 0);
        col.Key.ShouldBe(key);
    }

    [Theory]
    [InlineData("Markdown")]   // 故意排除（整段正文不进单元格）
    [InlineData("NotAField")]
    [InlineData("ClassificationReason")] // AI 解释，非文档数据
    public void Should_Reject_System_Column_With_Unknown_Key(string key)
    {
        Should.Throw<BusinessException>(() =>
                new ExportColumn(ExportColumnSourceKind.System, key, "Col", 0))
            .Code.ShouldBe(PaperbaseErrorCodes.InvalidExportColumnKey);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("party-name")]
    [InlineData("Contract_Number")]
    public void Should_Accept_Extracted_Column_With_Valid_FieldName(string key)
    {
        var col = new ExportColumn(ExportColumnSourceKind.Extracted, key, "金额", 0);
        col.Key.ShouldBe(key);
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("中文")]
    [InlineData("name.dot")]
    [InlineData("name;sql")]
    public void Should_Reject_Extracted_Column_With_Invalid_FieldName(string key)
    {
        Should.Throw<BusinessException>(() =>
                new ExportColumn(ExportColumnSourceKind.Extracted, key, "Col", 0))
            .Code.ShouldBe(PaperbaseErrorCodes.InvalidExportColumnKey);
    }

    [Theory]
    [InlineData("Col\nName")]
    [InlineData("Tab\tHere")]
    [InlineData("Null\0byte")]
    public void Should_Reject_Column_Name_With_Control_Chars(string columnName)
    {
        Should.Throw<BusinessException>(() =>
                new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, columnName, 0))
            .Code.ShouldBe(PaperbaseErrorCodes.InvalidExportColumnName);
    }

    [Theory]
    [InlineData("合同金额")]      // 中文 OK
    [InlineData("契約金額")]      // 日文 OK
    [InlineData("Amount (CNY)")]  // 括号空格 OK
    public void Should_Accept_Unicode_Column_Name(string columnName)
    {
        var col = new ExportColumn(ExportColumnSourceKind.Extracted, "amount", columnName, 0);
        col.ColumnName.ShouldBe(columnName);
    }

    // --- ExportTemplate ---

    [Fact]
    public void Should_Order_Columns_By_Order_Ascending()
    {
        var template = CreateTemplate(
            new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "标题", 5),
            new ExportColumn(ExportColumnSourceKind.Extracted, "amount", "金额", 1));

        template.Columns.Count.ShouldBe(2);
        template.Columns[0].Key.ShouldBe("amount");
        template.Columns[1].Key.ShouldBe(ExportSystemFields.Title);
    }

    [Fact]
    public void Should_Reject_Template_With_No_Columns()
    {
        Should.Throw<BusinessException>(() => CreateTemplate())
            .Code.ShouldBe(PaperbaseErrorCodes.ExportTemplateRequiresColumn);
    }

    [Fact]
    public void Should_Reject_Template_With_Duplicate_Column_Names()
    {
        Should.Throw<BusinessException>(() => CreateTemplate(
                new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "重复", 0),
                new ExportColumn(ExportColumnSourceKind.Extracted, "amount", "重复", 1)))
            .Code.ShouldBe(PaperbaseErrorCodes.ExportTemplateDuplicateColumnName);
    }

    [Theory]
    [InlineData("Name\nInjection")]
    [InlineData("Tab\tName")]
    public void Should_Reject_Template_Name_With_Control_Chars(string name)
    {
        Should.Throw<BusinessException>(() => new ExportTemplate(
                Guid.NewGuid(), tenantId: null, name, ExportFormat.Csv, documentTypeCode: null,
                new[] { new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "T", 0) }))
            .Code.ShouldBe(PaperbaseErrorCodes.InvalidExportTemplateName);
    }

    [Fact]
    public void Update_Should_Replace_Name_Format_TypeCode_And_Columns()
    {
        var template = CreateTemplate(
            new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "标题", 0));

        template.Update("新名", ExportFormat.Xlsx, "host.contract",
            new[] { new ExportColumn(ExportColumnSourceKind.Extracted, "amount", "金额", 0) });

        template.Name.ShouldBe("新名");
        template.Format.ShouldBe(ExportFormat.Xlsx);
        template.DocumentTypeCode.ShouldBe("host.contract");
        template.Columns.Count.ShouldBe(1);
        template.Columns[0].Key.ShouldBe("amount");
    }

    [Fact]
    public void Should_Normalize_Blank_DocumentTypeCode_To_Null()
    {
        var template = new ExportTemplate(
            Guid.NewGuid(), tenantId: null, "T", ExportFormat.Csv, documentTypeCode: "   ",
            new[] { new ExportColumn(ExportColumnSourceKind.System, ExportSystemFields.Title, "T", 0) });

        template.DocumentTypeCode.ShouldBeNull();
    }

    private static ExportTemplate CreateTemplate(params ExportColumn[] columns) =>
        new(
            id: Guid.NewGuid(),
            tenantId: null,
            name: "Test",
            format: ExportFormat.Csv,
            documentTypeCode: null,
            columns: columns);
}
