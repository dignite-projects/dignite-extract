namespace Dignite.Paperbase.Documents;

/// <summary>
/// 输出：服务端 sanitize 后的 snake_case 机器标识建议。
/// <para>
/// <see cref="Slug"/> 已限定为 <c>[a-z0-9_]</c>（小写、下划线分词、≤64 字符），
/// 同时满足 <see cref="FieldDefinition.Name"/>（<c>FieldDefinitionConsts.NamePattern</c>）与
/// <see cref="DocumentType.TypeCode"/>（<c>DocumentTypeConsts.TypeCodePattern</c> 单段形态）两套白名单——
/// 一个建议两边表单共用。
/// </para>
/// <para>
/// 可能为**空字符串**：LLM 不可用、返回非 JSON、或 sanitize 后无合法字符（如未翻译的纯 CJK）。
/// 此时由前端回退到本地占位（<c>field_{n}</c> / <c>type_{n}</c>）。
/// </para>
/// </summary>
public class SlugSuggestionDto
{
    public string Slug { get; set; } = string.Empty;
}
