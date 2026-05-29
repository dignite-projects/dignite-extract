// Mirrors C# Dignite.Paperbase.Slugging.SuggestSlugInput / SlugSuggestionDto (issue #190).
// 显示名 → 机器标识（slug）建议，FieldDefinition 与 DocumentType 创建表单共用。

export interface SuggestSlugInput {
  label: string;
}

// slug 已由服务端 sanitize 成 [a-z0-9_]（≤64），同时满足 Name 与 TypeCode 白名单。
// 可能为空字符串（LLM 不可用 / 未翻译）——此时由调用方回退到本地占位。
export interface SlugSuggestionDto {
  slug: string;
}
