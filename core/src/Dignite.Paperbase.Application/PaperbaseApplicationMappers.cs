using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Paperbase.Documents;
using Riok.Mapperly.Abstractions;
using Volo.Abp.Data;
using Volo.Abp.Mapperly;

namespace Dignite.Paperbase;

/// <summary>
/// Document -> DocumentDto
/// FileOrigin and PipelineRun nested mappings are consolidated here (Mapperly compile-time constraint).
/// <para>
/// <c>ExtractedFields</c>（出口 wire-format：<c>Dictionary&lt;string, JsonElement&gt;</c>）由 <see cref="AfterMap"/>
/// 从 <see cref="Document.ExtractedFieldValues"/> typed child 行即时组装（Issue #206）——不再是 Document 上的
/// 同名属性，故 <see cref="MapperIgnoreTargetAttribute"/> 忽略后手填。AfterMap 在本顶层 mapper 经 ABP
/// IObjectMapper 调用时会被触发，但显式 MapCore + AfterMap 包装保证任何调用路径都执行（与
/// <see cref="DocumentPipelineRunToDocumentPipelineRunDtoMapper"/> 同理）。
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentDtoMapper : MapperBase<Document, DocumentDto>
{
    [UseMapper]
    private readonly DocumentPipelineRunToDocumentPipelineRunDtoMapper _pipelineRunMapper = new();

    public override DocumentDto Map(Document source)
    {
        var destination = MapCore(source);
        AfterMap(source, destination);
        return destination;
    }

    public override void Map(Document source, DocumentDto destination)
    {
        MapCore(source, destination);
        AfterMap(source, destination);
    }

    public override void AfterMap(Document source, DocumentDto destination)
    {
        destination.ExtractedFields = ExtractedFieldsAssembler.ToDictionary(source.ExtractedFieldValues);
    }

    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    private partial DocumentDto MapCore(Document source);

    [MapperIgnoreTarget(nameof(DocumentDto.ExtractedFields))]
    private partial void MapCore(Document source, DocumentDto destination);
}

/// <summary>
/// DocumentPipelineRun -> DocumentPipelineRunDto.
/// <see cref="MapExtraPropertiesAttribute"/> 透传通用 <c>ExtraProperties</c> bag；同时把
/// <c>ExtraProperties["Candidates"]</c> 反序列化为强类型 <see cref="DocumentPipelineRunDto.Candidates"/>，
/// 让前端 / 下游 HttpApi.Client 不必按字符串 key cast。
/// <para>
/// <b>为什么手动在 <c>Map</c> 内调 <c>AfterMap</c></b>：<c>MapperBase.AfterMap</c> 的 auto-wire
/// 由 ABP <c>MapperlyAutoObjectMappingProvider</c>（IObjectMapper 层）做，<b>不是</b>
/// Mapperly source generator 做的。但本 mapper 是被 <see cref="DocumentToDocumentDtoMapper"/>
/// 通过 <c>[UseMapper]</c> 嵌套调用的子 mapper —— Mapperly 直接调 <c>Map(source)</c>，
/// 绕过 IObjectMapper layer，<c>AfterMap</c> 不会自动触发。手动在 <c>Map</c> wrapper 内
/// 调用是当前唯一可靠的方案。
/// 上游计划：Riok.Mapperly 团队不打算引入 attribute-based auto-wire（哲学：显式优于隐式，
/// 参 Mapperly Discussion #1421）；ABP 团队认为当前行为 by design（参 abpframework/abp#24592）。
/// </para>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
[MapExtraProperties]
public partial class DocumentPipelineRunToDocumentPipelineRunDtoMapper : MapperBase<DocumentPipelineRun, DocumentPipelineRunDto>
{
    public override DocumentPipelineRunDto Map(DocumentPipelineRun source)
    {
        var destination = MapCore(source);
        AfterMap(source, destination);
        return destination;
    }

    public override void Map(DocumentPipelineRun source, DocumentPipelineRunDto destination)
    {
        MapCore(source, destination);
        AfterMap(source, destination);
    }

    public override void AfterMap(DocumentPipelineRun source, DocumentPipelineRunDto destination)
    {
        destination.Candidates = ExtractCandidates(source.ExtraProperties);
    }

    [MapperIgnoreTarget(nameof(DocumentPipelineRunDto.Candidates))]
    private partial DocumentPipelineRunDto MapCore(DocumentPipelineRun source);

    [MapperIgnoreTarget(nameof(DocumentPipelineRunDto.Candidates))]
    private partial void MapCore(DocumentPipelineRun source, DocumentPipelineRunDto destination);

    private static IReadOnlyList<PipelineRunCandidate>? ExtractCandidates(ExtraPropertyDictionary? extra)
    {
        if (extra is null
            || !extra.TryGetValue(PipelineRunExtraPropertyNames.ClassificationCandidates, out var raw)
            || raw is null)
        {
            return null;
        }

        // 同一 UoW 内尚未持久化往返时是写入的原始类型。
        if (raw is IReadOnlyList<PipelineRunCandidate> alreadyTyped)
        {
            return alreadyTyped;
        }

        // EF Core / ABP 持久化读回时 ExtraProperties 的 value 是 JsonElement
        // （参考 docs/pipeline-runs.md "Server-Side Notes"）。
        if (raw is JsonElement json && json.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<PipelineRunCandidate>>(json.GetRawText());
        }

        return null;
    }
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentToDocumentListItemDtoMapper : MapperBase<Document, DocumentListItemDto>
{
    // ExtractedFields 由 AfterMap 从 ExtractedFieldValues typed child 行即时组装（Issue #206）；
    // 列表路径已 WithDetailsAsync(ExtractedFieldValues) 批量 eager-load，组装不触发 N+1。
    public override DocumentListItemDto Map(Document source)
    {
        var destination = MapCore(source);
        AfterMap(source, destination);
        return destination;
    }

    public override void Map(Document source, DocumentListItemDto destination)
    {
        MapCore(source, destination);
        AfterMap(source, destination);
    }

    public override void AfterMap(Document source, DocumentListItemDto destination)
    {
        destination.ExtractedFields = ExtractedFieldsAssembler.ToDictionary(source.ExtractedFieldValues);
    }

    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    private partial DocumentListItemDto MapCore(Document source);

    [MapperIgnoreTarget(nameof(DocumentListItemDto.ExtractedFields))]
    private partial void MapCore(Document source, DocumentListItemDto destination);
}

/// <summary>
/// 把 <see cref="Document.ExtractedFieldValues"/> typed child 行组装回出口 wire-format
/// <c>Dictionary&lt;string, JsonElement&gt;</c>（key = 字段名，value = <see cref="DocumentExtractedField.ToJsonElement"/>
/// 重建的规范 JSON）。空集合 → null（与旧 JSON 列"未抽取时 null"语义一致）。
/// </summary>
internal static class ExtractedFieldsAssembler
{
    public static Dictionary<string, JsonElement>? ToDictionary(IReadOnlyCollection<DocumentExtractedField> fields)
        => fields.Count == 0
            ? null
            : fields.ToDictionary(f => f.Name, f => f.ToJsonElement(), StringComparer.Ordinal);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class DocumentTypeToDtoMapper : MapperBase<DocumentType, DocumentTypeDto>
{
    public override partial DocumentTypeDto Map(DocumentType source);
    public override partial void Map(DocumentType source, DocumentTypeDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class FieldDefinitionToDtoMapper : MapperBase<FieldDefinition, FieldDefinitionDto>
{
    public override partial FieldDefinitionDto Map(FieldDefinition source);
    public override partial void Map(FieldDefinition source, FieldDefinitionDto destination);
}

/// <summary>
/// ExportTemplate -> ExportTemplateDto。Columns（IReadOnlyList&lt;ExportColumn&gt; → List&lt;ExportColumnDto&gt;）
/// 元素映射由 Mapperly 按同名属性自动 inline。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class ExportTemplateToDtoMapper : MapperBase<ExportTemplate, ExportTemplateDto>
{
    public override partial ExportTemplateDto Map(ExportTemplate source);
    public override partial void Map(ExportTemplate source, ExportTemplateDto destination);
}

[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class CabinetToDtoMapper : MapperBase<Cabinet, CabinetDto>
{
    public override partial CabinetDto Map(Cabinet source);
    public override partial void Map(Cabinet source, CabinetDto destination);
}
