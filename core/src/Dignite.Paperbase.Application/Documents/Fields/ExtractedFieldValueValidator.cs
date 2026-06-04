using System;
using System.Globalization;
using System.Text.Json;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 判定一个 <see cref="JsonElement"/> 值是否符合声明的 <see cref="FieldDataType"/>——字段值类型自洽的单一事实源。
/// 校验通过的值才会被拆进 <c>DocumentExtractedField</c> 对应的类型化列（Issue #206）。
/// <para>
/// 两条写入路径共用此校验器，保证落库的字段值永远"合类型或为 null"：
/// <list type="bullet">
///   <item><b>操作员手改</b>（<c>DocumentAppService.UpdateExtractedFieldsAsync</c>）：交互式路径，
///   不符 → 抛 <see cref="PaperbaseErrorCodes.ExtractedField.InvalidValue"/> 让操作员纠正。</item>
///   <item><b>LLM 抽取</b>（<c>FieldExtractionWorkflow</c>）：后台非交互式路径，不符 → 存 null + log
///   （归一化责任在 prompt，由 AI 输出规范形；校验器是兜底护栏）。</item>
/// </list>
/// 干净的字段值让 <c>GetFieldMatchedIdsAsync</c> 的类型化列查询（普通等值 / 范围比较）建立在可信数据上，
/// 也保证 <c>DocumentExtractedField</c> 的 <c>SetValue</c> 把 JSON 拆进类型化列时不会因类型不符抛错。
/// </para>
/// <para>
/// 严格语义（不做强制转换）：声明类型即承诺值可表达为该 JSON 类型。数字字段须为 JSON number、
/// 布尔字段须为 JSON true/false、日期字段须为 ISO-8601 字符串。要存自由文本有两个档位：
/// <see cref="FieldDataType.String"/>（结构化短值，长度须 ≤ <see cref="DocumentExtractedFieldConsts.MaxStringValueLength"/>，
/// 与持久化列长同源、入复合索引、可等值查询，#209）或 <see cref="FieldDataType.LongText"/>
/// （长内容如摘要 / 描述，长度须 ≤ <see cref="DocumentExtractedFieldConsts.MaxLongTextValueLength"/>，落 nvarchar(max) 列、不索引不可查询）。
/// 真正的全文载荷仍归 Document.Markdown，不在类型绑定字段承载。
/// </para>
/// </summary>
internal static class ExtractedFieldValueValidator
{
    /// <summary>
    /// 多值感知校验（#212）。<paramref name="allowMultiple"/> 为 true 时（仅 <see cref="FieldDataType.String"/> 字段，
    /// 由 <c>FieldDefinition</c> 实体层保证）：<paramref name="value"/> 必须是 JSON 数组，且每个元素都是合法标量值；
    /// 空数组合法（零行）。为 false 时退化为标量校验（与原 <see cref="IsValid(JsonElement, FieldDataType)"/> 一致）。
    /// </summary>
    public static bool IsValid(JsonElement value, FieldDataType dataType, bool allowMultiple)
    {
        if (!allowMultiple)
        {
            return IsValid(value, dataType);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        // 结果集硬上限（#212）：超过 MaxMultiValueCount 个值整组判不合法——防恶意文档诱导 LLM 吐超长数组造成行膨胀
        // （LLM 路径据此存 null + log，操作员手改路径 loud fail）。schema 的 maxItems 是软提示，此处是硬护栏。
        if (value.GetArrayLength() > DocumentExtractedFieldConsts.MaxMultiValueCount)
        {
            return false;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (!IsValid(element, dataType))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValid(JsonElement value, FieldDataType dataType)
    {
        return dataType switch
        {
            FieldDataType.String => value.ValueKind == JsonValueKind.String &&
                                    (value.GetString() ?? string.Empty).Length <= DocumentExtractedFieldConsts.MaxStringValueLength,
            FieldDataType.LongText => value.ValueKind == JsonValueKind.String &&
                                      (value.GetString() ?? string.Empty).Length <= DocumentExtractedFieldConsts.MaxLongTextValueLength,
            FieldDataType.Number => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            FieldDataType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            FieldDataType.Date => IsValidDateString(value),
            FieldDataType.DateTime => IsValidDateTimeString(value),
            _ => false
        };
    }

    private static bool IsValidDateString(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParseExact(
                   value.GetString(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out _);
    }

    private static bool IsValidDateTimeString(JsonElement value)
    {
        // 只接受无偏移的 wall-clock ISO-8601（YYYY-MM-DDThh:mm:ss）。带偏移 / Z 的串会被 .NET
        // 换算到本地时区，与存储 / 查询侧 datetime2 列的 wall-clock 语义不一致（比较随服务器时区
        // 漂移）——DateTimeKind.Unspecified 即表示输入未携带时区信息。带时区的瞬时值不在通道
        // DateTime 字段的范畴；要存这类值应在下游业务聚合根处理。
        return value.ValueKind == JsonValueKind.String &&
               DateTime.TryParse(
                   value.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var parsed) &&
               parsed.Kind == DateTimeKind.Unspecified;
    }
}
