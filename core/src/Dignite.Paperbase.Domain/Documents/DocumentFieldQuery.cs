using Dignite.Paperbase.Documents.Fields;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 已解析的单字段值查询——<see cref="IDocumentRepository.GetFieldMatchedIdsAsync"/> 的 <c>fieldQueries</c> 列表元素。
/// 同一次检索可带多个，仓储把各元素编译成对 <see cref="Document.ExtractedFieldValues"/> 的一个 <c>Any</c>（EXISTS）
/// 谓词、之间用 <c>AND</c> 拼接（结构化检索惯例：不同字段互相收窄）。
/// <see cref="FieldDataType"/> 由调用层（出口适配器）从 <c>FieldDefinition</c> 解析后填入——
/// 仓储据此分派到对应类型化列做普通等值 / 区间比较；仓储不依赖其它聚合的仓储。
/// 等值（<see cref="FieldValue"/>）与区间（<see cref="FieldValueMin"/> / <see cref="FieldValueMax"/>）
/// 至少给其一，否则该查询残缺 → 仓储 fail-closed 空结果，绝不退化成"该类型全捞"。
/// </summary>
public sealed record DocumentFieldQuery(
    string FieldName,
    FieldDataType FieldDataType,
    string? FieldValue = null,
    string? FieldValueMin = null,
    string? FieldValueMax = null);
