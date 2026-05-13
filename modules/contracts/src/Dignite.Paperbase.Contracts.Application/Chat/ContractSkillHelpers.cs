using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Contracts.Contracts;
using Dignite.Paperbase.Contracts.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Contracts.Chat;

/// <summary>
/// Shared infrastructure for <see cref="ContractsSkill"/>'s three scripts
/// (<c>search</c> / <c>get-detail</c> / <c>aggregate</c>). Centralises the
/// fail-closed safety contract — auth check + explicit tenant predicate +
/// bounded result set — so the scripts cannot diverge from it.
///
/// <para>
/// fail-closed safety contract — see <c>.claude/rules/doc-chat-anti-patterns.md</c>
/// reverse example C: explicit <see cref="ContractsPermissions.Contracts.Default"/>
/// permission check, explicit <c>TenantId</c> predicate (never rely on ambient ABP
/// <c>DataFilter</c>), hard <see cref="MaxResultRows"/> upper bound.
/// </para>
/// </summary>
internal static class ContractSkillHelpers
{
    /// <summary>
    /// Hard upper bound on rows returned by skill scripts to the LLM. Beyond this
    /// the context window cost outweighs the marginal value of more matches, and
    /// a single prompt-injected query could otherwise drag thousands of rows into
    /// the model's context.
    /// </summary>
    public const int MaxResultRows = 20;

    /// <summary>
    /// Asserts the caller holds the contracts read permission. Throws
    /// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationFailedException"/> /
    /// ABP's translated authorization exception when not granted, which propagates
    /// up to the chat agent as a 403-equivalent tool error.
    /// </summary>
    public static Task RequireContractsReadAsync(IAuthorizationService authorizationService)
        => authorizationService.CheckAsync(ContractsPermissions.Contracts.Default);

    /// <summary>
    /// Returns an <see cref="IQueryable{Contract}"/> already scoped to the current
    /// tenant via an **explicit** predicate. Skill scripts must never rely on ABP's
    /// ambient <c>DataFilter</c> alone — that can be disabled on non-HTTP code paths
    /// and would silently widen the boundary.
    /// </summary>
    public static async Task<IQueryable<Contract>> ScopedQueryableAsync(
        IContractRepository repository,
        ICurrentTenant currentTenant)
    {
        var queryable = await repository.GetQueryableAsync();

        var tenantId = currentTenant.Id;
        return tenantId.HasValue
            ? queryable.Where(c => c.TenantId == tenantId)
            : queryable.Where(c => c.TenantId == null);
    }
}
