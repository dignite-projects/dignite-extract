using System.Text.RegularExpressions;

namespace Dignite.DocumentAI.Documents;

/// <summary>
/// 语言标签（ISO 639-1 / IETF tag，如 <c>en</c> / <c>zh-Hans</c> / <c>ja</c>）白名单校验器。
/// <para>
/// <c>Document.Language</c> 在 MCP 出口的资源元数据 header 以<b>裸值</b>透出（不经
/// <see cref="Ai.PromptBoundary"/> 包裹），并会被插值进内部 LLM system prompt 的语言子句——
/// 与 <c>DocumentTypeConsts.TypeCodePattern</c> 同源的"白名单即注入防线"：仅放行
/// <see cref="Pattern"/>，不匹配的候选一律按"未检测到语言"丢弃（不截断修补）。
/// </para>
/// </summary>
public static class LanguageTagValidator
{
    /// <summary>
    /// 合法语言标签白名单（ASCII 字母 / 数字 / 连字符，1~16 字符，上限与
    /// <see cref="DocumentConsts.MaxLanguageLength"/> 默认值对齐）。
    /// 编译期 <c>const</c>——安全边界不可被运行时配置放大（同 <see cref="DocumentConsts.MaxSearchResultCount"/> 例）。
    /// </summary>
    public const string Pattern = "^[A-Za-z0-9-]{1,16}$";

    private static readonly Regex TagRegex = new(
        Pattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 先 Trim 再按 <see cref="Pattern"/> 白名单校验。
    /// null / 空白 / 不匹配（含空格 / 标点 / 控制字符 / 超长）→ 返回 <c>null</c>，
    /// 调用方据此按"未检测到语言"处理。
    /// </summary>
    public static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var trimmed = candidate.Trim();
        return TagRegex.IsMatch(trimmed) ? trimmed : null;
    }
}
