using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Slugging;

/// <summary>
/// 显示名 → 机器标识（slug）建议（issue #190）。
/// <para>
/// 抹平 admin "填两个字段（显示名 + 机器键）录入成本高"的痛点：admin 只填 DisplayName，
/// 前端调本服务用 LLM 出英译候选预填 <see cref="FieldDefinition.Name"/> /
/// <see cref="DocumentType.TypeCode"/>，admin 可手动覆盖。
/// </para>
/// <para>
/// FieldDefinition 与 DocumentType 两个创建表单**共用**此单一端点——slug 格式同时满足两套白名单。
/// </para>
/// </summary>
public interface ISlugSuggestionAppService : IApplicationService
{
    Task<SlugSuggestionDto> SuggestAsync(SuggestSlugInput input, CancellationToken cancellationToken = default);
}
