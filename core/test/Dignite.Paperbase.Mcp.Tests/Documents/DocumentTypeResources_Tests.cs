using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Ai;
using Dignite.Paperbase.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Paperbase.Mcp.Documents;

[DependsOn(typeof(PaperbaseTestBaseModule))]
public class DocumentTypeResourcesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 出口是薄壳，委托 AppService（权限断言 / 租户隔离在 AppService 内、此处以 mock 替身）；
        // 以 mock 注入断言 code 过滤、schema 投影、PromptBoundary 包裹、not-found 行为。
        context.Services.AddSingleton(Substitute.For<IDocumentTypeAppService>());
        context.Services.AddSingleton(Substitute.For<IFieldDefinitionAppService>());
    }
}

/// <summary>
/// <see cref="DocumentTypeResources"/> read 行为：按 type code 返回字段 schema——DisplayName（类型 + 字段）
/// 经 <c>PromptBoundary</c> 包裹、字段按 DisplayOrder 排序、找不到类型抛 <see cref="McpException"/>。
/// 权限断言、参数校验、租户隔离都在 AppService 内（此处以 mock 替身），故那些行为由 AppService 测试覆盖、不在此重复。
/// resources/list 动态枚举是 MCP server 集成行为，不在单元测试范畴。
/// </summary>
public class DocumentTypeResources_Tests : PaperbaseTestBase<DocumentTypeResourcesTestModule>
{
    private readonly IDocumentTypeAppService _documentTypeAppService;
    private readonly IFieldDefinitionAppService _fieldDefinitionAppService;

    public DocumentTypeResources_Tests()
    {
        _documentTypeAppService = GetRequiredService<IDocumentTypeAppService>();
        _fieldDefinitionAppService = GetRequiredService<IFieldDefinitionAppService>();
    }

    [Fact]
    public async Task Returns_schema_with_wrapped_display_names_ordered_by_display_order()
    {
        // #222：ReadAsync 委托 GetVisibleAsync 按 code 过滤拿类型，再 GetListAsync(DocumentTypeId) 取字段（#207）。
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = typeId, TypeCode = "contract.general", DisplayName = "合同" }
            });
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == typeId))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "amount", DisplayName = "合同金额",
                    Prompt = "Extract the total contract amount", DataType = FieldDataType.Number,
                    DisplayOrder = 1, IsRequired = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Prompt = "Extract party A name", DataType = FieldDataType.String, DisplayOrder = 0
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.TypeCode.ShouldBe("contract.general");
        // 类型 / 字段 DisplayName 是 admin 配置文本，经 PromptBoundary 包裹防 indirect prompt injection。
        schema.DisplayName.ShouldBe(PromptBoundary.WrapField("合同"));
        // 字段按 DisplayOrder 升序：partyName(0) 先于 amount(1)。
        schema.Fields.Count.ShouldBe(2);
        schema.Fields[0].Name.ShouldBe("partyName");
        schema.Fields[0].DataType.ShouldBe("String");
        schema.Fields[0].DisplayName.ShouldBe(PromptBoundary.WrapField("甲方"));
        schema.Fields[1].Name.ShouldBe("amount");
        schema.Fields[1].DataType.ShouldBe("Number");
        schema.Fields[1].IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Exposes_AllowMultiple_so_clients_know_a_field_returns_an_array()
    {
        // #212：多值字段在检索结果 extractedFields 里是 string[]——schema 必须透出 AllowMultiple，
        // 否则 MCP 客户端按"String 标量"解析数组会出错。
        var typeId = Guid.NewGuid();
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = typeId, TypeCode = "contract.general", DisplayName = "合同" }
            });
        _fieldDefinitionAppService
            .GetListAsync(Arg.Is<GetFieldDefinitionListInput>(i => i.DocumentTypeId == typeId))
            .Returns(new List<FieldDefinitionDto>
            {
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "tags", DisplayName = "标签",
                    Prompt = "Extract tags", DataType = FieldDataType.String, DisplayOrder = 0,
                    IsRequired = false, AllowMultiple = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Prompt = "Extract party A name", DataType = FieldDataType.String, DisplayOrder = 1
                }
            });

        var result = await DocumentTypeResources.ReadAsync(
            "contract.general", _documentTypeAppService, _fieldDefinitionAppService);

        var schema = JsonSerializer.Deserialize<DocumentTypeSchema>(((TextResourceContents)result).Text)!;

        schema.Fields[0].Name.ShouldBe("tags");
        schema.Fields[0].AllowMultiple.ShouldBeTrue();
        schema.Fields[1].Name.ShouldBe("partyName");
        schema.Fields[1].AllowMultiple.ShouldBeFalse();
    }

    [Fact]
    public async Task Throws_when_type_not_found()
    {
        // 跨租户 / 不存在的 code → 不在 GetVisibleAsync 返回的当前层类型集中（租户隔离由 ambient 过滤器施加）。
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>());

        await Should.ThrowAsync<McpException>(async () =>
            await DocumentTypeResources.ReadAsync(
                "nonexistent", _documentTypeAppService, _fieldDefinitionAppService));
    }
}
