using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Paperbase.Documents;

public class UpdateExtractedFieldsInput
{
    /// <summary>
    /// 字段值（key = <see cref="FieldDefinition.Name"/>）。整体替换 <c>Document.ExtractedFields</c>——
    /// 调用方提交该文档当前全部字段值；每个 key 必须是该文档所属层、该 DocumentType 下已定义的字段名。
    /// 值保留为原始 JSON（不做 DataType 强制转换，消费侧按 <see cref="FieldDefinition.DataType"/> 反序列化）。
    /// </summary>
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}
