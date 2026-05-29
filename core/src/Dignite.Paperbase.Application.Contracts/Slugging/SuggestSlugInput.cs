using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// 输入：admin 在创建表单填的人类可读标签（通常是某实体的显示名，中文 / 日文 / 任意语言）。
/// 服务端用 LLM 英译 + slug 化，回吐一个可作为 <see cref="FieldDefinition.Name"/> /
/// <see cref="DocumentType.TypeCode"/> 的机器标识建议（admin 可手动覆盖）。
/// </summary>
public class SuggestSlugInput
{
    // 通用输入护栏：防止超长文本灌入 LLM prompt，不借用任何具体实体的 DisplayName 上限（见 SlugSuggestionConsts）。
    [Required]
    [DynamicStringLength(typeof(SlugSuggestionConsts), nameof(SlugSuggestionConsts.MaxLabelLength))]
    public string Label { get; set; } = default!;
}
