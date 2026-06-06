using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.AspNetCore.Authentication;

namespace Dignite.Paperbase.Host.Authentication;

/// <summary>
/// #278：仅对带 <see cref="McpDiscoveryChallengeMarker"/> 的端点（/mcp），把"未认证"的 challenge
/// 路由到 McpAuth scheme，使 401 携带 <c>WWW-Authenticate: Bearer resource_metadata="..."</c>
/// 发现指针（RFC 9728）。其余端点一律委托框架默认 handler。
///
/// 为什么不直接把 McpAuth 加进端点授权策略的 AuthenticationSchemes：那会让 PolicyEvaluator
/// 重新经 McpAuth → OpenIddict authenticate，得到一个未经 ABP dynamic claims 富化的 principal，
/// 覆盖 UseDynamicClaims 富化过的 ambient User —— 既丢失签发后变更的角色（可能误拒合法 token），
/// 也绕过"令牌签发后吊销/禁用用户"的实时失效（安全回归）。这里只覆盖 challenge：authenticate
/// 仍走端点默认策略（RequireAuthenticatedUser，无显式 scheme），复用 ambient 富化 User。
///
/// 也不把全局 DefaultChallengeScheme 改成 McpAuth —— 那会破坏管理后台 UI 的 cookie 登录重定向。
/// </summary>
public class McpDiscoveryAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private static readonly AuthorizationMiddlewareResultHandler Default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // 仅 401（未认证）走 McpAuth challenge 注入发现指针；403（已认证、权限不足）保持框架默认行为。
        if (authorizeResult.Challenged
            && !authorizeResult.Forbidden
            && context.GetEndpoint()?.Metadata.GetMetadata<McpDiscoveryChallengeMarker>() != null)
        {
            await context.ChallengeAsync(McpAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        await Default.HandleAsync(next, context, policy, authorizeResult);
    }
}

/// <summary>
/// 端点标记：声明该端点的未认证 challenge 走 MCP OAuth Protected Resource Metadata 发现链路（#278）。
/// 由 <see cref="McpDiscoveryAuthorizationResultHandler"/> 识别。
/// </summary>
public sealed class McpDiscoveryChallengeMarker
{
    public static readonly McpDiscoveryChallengeMarker Instance = new();

    private McpDiscoveryChallengeMarker()
    {
    }
}
