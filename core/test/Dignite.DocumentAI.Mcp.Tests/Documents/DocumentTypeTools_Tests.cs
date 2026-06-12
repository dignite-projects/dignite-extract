using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.DocumentAI.Mcp.Documents;

[DependsOn(typeof(DocumentAITestBaseModule))]
public class DocumentTypeToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
    }
}

/// <summary>
/// <see cref="DocumentTypeTools.ListAsync"/> 薄壳行为：
/// 委托 <see cref="IDocumentTypeAppService.GetVisibleAsync"/> + <see cref="IFieldDefinitionAppService.GetListAsync"/>
/// （单次批量、DocumentTypeId 留空——消 per-type N+1）并把结果映射为 <see cref="DocumentTypeListResult"/>
/// （displayName 经 <c>PromptBoundary</c> 包裹；按 TypeCode 排序截断到
/// <see cref="DocumentAIMcpConsts.MaxDocumentTypeResults"/>，超限带 truncated/totalCount 截断信号）。
/// </summary>
public class DocumentTypeTools_Tests : DocumentAITestBase<DocumentTypeToolsTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;

    public DocumentTypeTools_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
    }

    [Fact]
    public async Task Returns_types_with_fields_and_wraps_display_names()
    {
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>
            {
                new()
                {
                    Id = typeId,
                    TypeCode = "contract.general",
                    DisplayName = "General Contract"
                }
            });
        // 批量路径：DocumentTypeId 留空一次取当前层全部字段定义，tool 内存按 DocumentTypeId 分组（消 N+1）。
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == null && !i.OnlyDeleted))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    DocumentTypeId = typeId,
                    Name = "amount",
                    DataType = FieldDataType.Number,
                    AllowMultiple = false,
                    DisplayName = "Amount",
                    IsRequired = true,
                    DisplayOrder = 0
                },
                new()
                {
                    DocumentTypeId = typeId,
                    Name = "party_name",
                    DataType = FieldDataType.Text,
                    AllowMultiple = false,
                    DisplayName = "Party Name",
                    IsRequired = false,
                    DisplayOrder = 1
                }
            });

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.TotalCount.ShouldBe(1);
        result.Truncated.ShouldBeFalse();
        result.Types.Count.ShouldBe(1);
        var schema = result.Types[0];
        schema.TypeCode.ShouldBe("contract.general");
        // DisplayName 必须经 PromptBoundary 包裹。
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("General Contract"));
        schema.Fields.Count.ShouldBe(2);

        var amountField = schema.Fields[0];
        amountField.Name.ShouldBe("amount");
        amountField.DataType.ShouldBe("Number");
        amountField.AllowMultiple.ShouldBeFalse();
        amountField.IsRequired.ShouldBeTrue();
        amountField.DisplayName.ShouldBe(PromptBoundary.WrapField("Amount"));

        var partyField = schema.Fields[1];
        partyField.Name.ShouldBe("party_name");
        partyField.DataType.ShouldBe("Text");
        partyField.IsRequired.ShouldBeFalse();

        // 消 N+1 守护：字段定义只允许一次批量调用（不再 per-type 循环查询）。
        await _fieldDefinitionAppService.Received(1).GetListAsync(Arg.Any<GetFieldDefinitionListInput>());
    }

    [Fact]
    public async Task Returns_empty_list_when_no_visible_types()
    {
        _documentTypeAppService.GetVisibleAsync().Returns(new List<DocumentTypeDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.Truncated.ShouldBeFalse();
        await _fieldDefinitionAppService.DidNotReceive().GetListAsync(Arg.Any<GetFieldDefinitionListInput>());
    }

    [Fact]
    public async Task Within_cap_returns_all_types_without_truncation_signal()
    {
        // 恰好等于上限：全量返回、无截断信号（上限内行为不变）。
        var total = DocumentAIMcpConsts.MaxDocumentTypeResults;
        _documentTypeAppService.GetVisibleAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.Count.ShouldBe(total);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task Truncates_types_beyond_cap_and_signals_truncation()
    {
        // 结果集硬上限（llm-call-anti-patterns 反例 B 要点 3）：租户 admin 可自建任意多类型，
        // 超限必须截断并以 truncated + totalCount 显式告知 LLM 还有更多。
        var total = DocumentAIMcpConsts.MaxDocumentTypeResults + 5;
        _documentTypeAppService.GetVisibleAsync().Returns(BuildTypes(total));
        _fieldDefinitionAppService
            .GetListAsync(Arg.Any<GetFieldDefinitionListInput>())
            .Returns(new List<FieldDefinitionDto>());

        var result = await DocumentTypeTools.ListAsync(
            _documentTypeAppService, _fieldDefinitionAppService);

        result.Types.Count.ShouldBe(DocumentAIMcpConsts.MaxDocumentTypeResults);
        result.TotalCount.ShouldBe(total);
        result.Truncated.ShouldBeTrue();
        // 截断前按 TypeCode 稳定排序（不依赖 AppService 返回顺序）——保留字典序最前的一段、丢弃尾部。
        result.Types[0].TypeCode.ShouldBe(TypeCodeOf(0));
        result.Types[^1].TypeCode.ShouldBe(TypeCodeOf(DocumentAIMcpConsts.MaxDocumentTypeResults - 1));
        // 截断不放大查询数：字段定义仍只允许一次批量调用。
        await _fieldDefinitionAppService.Received(1).GetListAsync(
            Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == null));
    }

    private static string TypeCodeOf(int index) => $"type.{index:D4}";

    private static List<DocumentTypeDto> BuildTypes(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new DocumentTypeDto
            {
                Id = Guid.NewGuid(),
                TypeCode = TypeCodeOf(i),
                DisplayName = $"Type {i}"
            })
            // 乱序交给 tool——截断必须建立在 tool 自己的 TypeCode 稳定排序之上。
            .OrderByDescending(t => t.TypeCode, StringComparer.Ordinal)
            .ToList();
    }
}
