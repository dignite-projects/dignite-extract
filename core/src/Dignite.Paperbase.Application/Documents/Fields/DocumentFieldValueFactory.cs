using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 把一个（已经过 <see cref="ExtractedFieldValueValidator"/> 校验的）字段值展开成一个或多个
/// <see cref="DocumentFieldValue"/> 行（#212）。两条写入路径共用——LLM 抽取
/// （<c>FieldExtractionEventHandler</c>）与操作员手改（<c>DocumentAppService.UpdateExtractedFieldsAsync</c>）：
/// <list type="bullet">
///   <item>单值字段（<c>allowMultiple == false</c>）：标量 <paramref name="value"/> → 1 行，<c>Order = 0</c>。</item>
///   <item>多值 String 字段（<c>allowMultiple == true</c>）：JSON 数组 <paramref name="value"/> → 每元素 1 行，
///   <c>Order</c> 按数组顺序取 0,1,2…（空数组 → 0 行）。</item>
/// </list>
/// 调用前必须已 <c>IsValid(value, dataType, allowMultiple)</c>——多值路径假定 <paramref name="value"/> 是数组。
/// </summary>
internal static class DocumentFieldValueFactory
{
    public static IEnumerable<DocumentFieldValue> Expand(
        Guid fieldDefinitionId, FieldDataType dataType, bool allowMultiple, JsonElement value)
    {
        if (!allowMultiple)
        {
            yield return new DocumentFieldValue(fieldDefinitionId, dataType, value, 0);
            yield break;
        }

        var order = 0;
        foreach (var element in value.EnumerateArray())
        {
            // Clone：脱离原 JsonDocument 缓冲，独立持有（与 workflow 侧 root.Clone() 一致的生命周期保险）。
            yield return new DocumentFieldValue(fieldDefinitionId, dataType, element.Clone(), order++);
        }
    }
}
