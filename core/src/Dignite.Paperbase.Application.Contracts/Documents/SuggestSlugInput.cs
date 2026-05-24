using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 输入：admin 在创建表单填的人类可读显示名（中文 / 日文 / 任意语言）。
/// 服务端用 LLM 英译 + slug 化，回吐一个可作为 <see cref="FieldDefinition.Name"/> /
/// <see cref="DocumentType.TypeCode"/> 的机器标识建议（admin 可手动覆盖）。
/// </summary>
public class SuggestSlugInput
{
    // 长度上限对齐 DisplayName（Host / 租户两类显示名同为 128），防止超长文本灌入 LLM prompt。
    [Required]
    [DynamicStringLength(typeof(FieldDefinitionConsts), nameof(FieldDefinitionConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;
}
