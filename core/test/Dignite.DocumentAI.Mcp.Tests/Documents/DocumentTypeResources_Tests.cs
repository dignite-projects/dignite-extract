using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.DocumentAI.Ai;
using Dignite.DocumentAI.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.DocumentAI.Mcp.Documents;

[DependsOn(typeof(DocumentAITestBaseModule))]
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
/// resources/list 的投影逻辑（<see cref="DocumentTypeResources.ListVisibleAsync"/>，由 module handler 委托）
/// 也在此覆盖：按 TypeCode 稳定排序 + 硬上限截断（<see cref="DocumentAIMcpConsts.MaxDocumentTypeResults"/>）。
/// </summary>
public class DocumentTypeResources_Tests : DocumentAITestBase<DocumentTypeResourcesTestModule>
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
                    Prompt = "Extract party A name", DataType = FieldDataType.Text, DisplayOrder = 0
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
        schema.Fields[0].DataType.ShouldBe("Text");
        schema.Fields[0].DisplayName.ShouldBe(PromptBoundary.WrapField("甲方"));
        schema.Fields[1].Name.ShouldBe("amount");
        schema.Fields[1].DataType.ShouldBe("Number");
        schema.Fields[1].IsRequired.ShouldBeTrue();
    }

    [Fact]
    public async Task Exposes_AllowMultiple_so_clients_know_a_field_returns_an_array()
    {
        // #212：多值字段在检索结果 extractedFields 里是 string[]——schema 必须透出 AllowMultiple，
        // 否则 MCP 客户端按"文本标量"解析数组会出错。
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
                    Prompt = "Extract tags", DataType = FieldDataType.Text, DisplayOrder = 0,
                    IsRequired = false, AllowMultiple = true
                },
                new()
                {
                    Id = Guid.NewGuid(), DocumentTypeId = typeId, Name = "partyName", DisplayName = "甲方",
                    Prompt = "Extract party A name", DataType = FieldDataType.Text, DisplayOrder = 1
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

    [Fact]
    public async Task Resources_list_projects_visible_types_ordered_by_type_code()
    {
        // 上限内行为：每个可见类型一条 Resource（URI / Name 按 TypeCode），按 TypeCode 稳定排序。
        _documentTypeAppService
            .GetVisibleAsync()
            .Returns(new List<DocumentTypeDto>
            {
                new() { Id = Guid.NewGuid(), TypeCode = "invoice.vat", DisplayName = "增值税发票" },
                new() { Id = Guid.NewGuid(), TypeCode = "contract.general", DisplayName = "合同" }
            });

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);

        result.Resources.Count.ShouldBe(2);
        result.Resources[0].Name.ShouldBe("contract.general");
        result.Resources[0].Uri.ShouldBe(DocumentTypeResourceUri.Format("contract.general"));
        result.Resources[0].MimeType.ShouldBe("application/json");
        result.Resources[1].Name.ShouldBe("invoice.vat");
        result.Resources[1].Uri.ShouldBe(DocumentTypeResourceUri.Format("invoice.vat"));
    }

    [Fact]
    public async Task Resources_list_truncates_beyond_cap()
    {
        // 结果集硬上限（llm-call-anti-patterns 反例 B 要点 3）：租户 admin 可自建任意多类型——
        // resources/list 协议条目无处携带截断信号，直接截断（完整发现走 list_document_types tool）。
        var total = DocumentAIMcpConsts.MaxDocumentTypeResults + 3;
        var types = Enumerable.Range(0, total)
            .Select(i => new DocumentTypeDto
            {
                Id = Guid.NewGuid(),
                TypeCode = $"type.{i:D4}",
                DisplayName = $"Type {i}"
            })
            // 乱序交给投影——截断必须建立在投影自己的 TypeCode 稳定排序之上。
            .OrderByDescending(t => t.TypeCode, StringComparer.Ordinal)
            .ToList();
        _documentTypeAppService.GetVisibleAsync().Returns(types);

        var result = await DocumentTypeResources.ListVisibleAsync(_documentTypeAppService);

        result.Resources.Count.ShouldBe(DocumentAIMcpConsts.MaxDocumentTypeResults);
        // 保留 TypeCode 字典序最前的一段、丢弃尾部。
        result.Resources[0].Name.ShouldBe("type.0000");
        result.Resources[^1].Name.ShouldBe($"type.{DocumentAIMcpConsts.MaxDocumentTypeResults - 1:D4}");
    }
}
