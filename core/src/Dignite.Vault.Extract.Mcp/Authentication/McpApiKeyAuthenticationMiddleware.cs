using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Security.Claims;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Optional static API-key fallback authentication for the <c>/mcp</c> egress (#428), for MCP clients that
/// cannot run the #278 OAuth discovery flow (e.g. OpenAI Codex, ABP AI Management) but can send a static
/// request header.
///
/// <para><b>Behaviour.</b> Read the configured header; on a constant-time match, set <c>context.User</c> to
/// a synthetic authenticated principal for the mapped least-privilege service-account user. On a
/// missing/invalid key, <b>do nothing</b> (fail open, leave the principal unauthenticated — never write 401
/// or 403 here) so the request falls through to the OpenIddict Bearer chain and the #278 discovery
/// challenge stay byte-for-byte intact. Emitting a 403 here would make
/// <see cref="McpDiscoveryAuthorizationResultHandler"/> skip the <c>resource_metadata</c> pointer and break
/// discovery for OAuth clients.</para>
///
/// <para><b>Pipeline placement.</b> Runs BEFORE <c>UseAuthentication</c>. Verified (ASP.NET Core +
/// ABP 10.2.0): <c>AuthenticationMiddleware</c> only overwrites <c>context.User</c> when the default scheme
/// returns a non-null principal, so a no-Bearer request (<c>NoResult</c>) preserves the key principal; a
/// valid Bearer, when present, wins (acceptable). ABP <c>UseAbpOpenIddictValidation</c> is gated on
/// <c>!IsAuthenticated</c> and no-ops over an already-authenticated principal.</para>
///
/// <para><b>Authorization.</b> The synthetic principal carries only <c>AbpClaimTypes.UserId</c> (+ tenant /
/// label); permissions resolve from the permission store at the tools' <c>CheckPolicyAsync</c> (ABP does
/// not read permissions from claims). The endpoint keeps the bare scheme-free <c>RequireAuthorization()</c>
/// policy (the #278 invariant) which authorizes the ambient <c>context.User</c>.</para>
///
/// <para><b>Known limitation.</b> The key principal does NOT pass through <c>UseDynamicClaims</c> (it has no
/// <c>IAuthenticateResultFeature</c>), so live dynamic-claims revocation does not apply — revoke by removing
/// the key from config (or removing the service account's grant, which the permission cache picks up). A
/// future upgrade to a real <c>AuthenticationHandler</c>/scheme would restore enrichment parity; see #428.</para>
/// </summary>
public sealed class McpApiKeyAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpApiKeyAuthenticationMiddleware> _logger;
    private readonly string _headerName;
    private readonly bool _requireHttps;
    private readonly IReadOnlyList<CompiledKey> _keys;

    public McpApiKeyAuthenticationMiddleware(
        RequestDelegate next,
        IOptions<McpApiKeyOptions> options,
        ILogger<McpApiKeyAuthenticationMiddleware> logger)
    {
        _next = next;
        _logger = logger;

        var value = options.Value;
        _headerName = value.HeaderName;
        _requireHttps = value.RequireHttps;

        // Precompute SHA-256 digests once. Comparing fixed-length digests (not the raw keys) removes the
        // length side-channel and lets FixedTimeEquals run over a constant-size buffer.
        _keys = value.Keys
            .Select(k => new CompiledKey(SHA256.HashData(Encoding.UTF8.GetBytes(k.Key)), k))
            .ToList();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Exactly one header instance is expected. An absent header (Count == 0) is the normal OAuth path;
        // a duplicated header (Count > 1, e.g. a reverse proxy that appends instead of replaces) is
        // ambiguous. In both cases do nothing and fall through to the Bearer chain (fail open) rather than
        // matching a comma-joined StringValues that could never equal a real key.
        var values = context.Request.Headers[_headerName];
        if (values.Count == 1)
        {
            var presented = values[0];
            if (!string.IsNullOrEmpty(presented) && !RejectInsecure(context))
            {
                var matched = Match(presented);
                if (matched != null)
                {
                    context.User = BuildPrincipal(matched);
                    _logger.LogDebug(
                        "MCP request authenticated via API key (label: {Label}).",
                        string.IsNullOrWhiteSpace(matched.Label) ? "<unlabeled>" : matched.Label);
                }
                else
                {
                    // Present-but-invalid key. Logged at Debug (not Warning) so an unauthenticated caller
                    // cannot flood Warning-level logs / alerts; a rate-limited security event is the proper
                    // signal (deferred, see #428). Never log the value.
                    _logger.LogDebug(
                        "An invalid MCP API key was presented in header '{Header}'; falling through to Bearer authentication.",
                        _headerName);
                }
            }
        }

        await _next(context);
    }

    private bool RejectInsecure(HttpContext context)
    {
        if (!_requireHttps || context.Request.IsHttps)
        {
            return false;
        }

        // The key is a long-lived bearer-equivalent secret; refuse it over clear text and fall through to
        // the Bearer chain. Never log the value. Disable via Mcp:ApiKey:RequireHttps only for a deliberate
        // plain-HTTP deployment (e.g. local testing).
        _logger.LogDebug(
            "An MCP API key was presented over a non-HTTPS request; ignoring it (Mcp:ApiKey:RequireHttps=true).");
        return true;
    }

    private McpApiKeyEntry? Match(string presented)
    {
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));

        // Compare against every configured key with no early-exit, so neither the match position nor the
        // key count is timing-observable; SHA-256 digests are a fixed 32 bytes for FixedTimeEquals.
        McpApiKeyEntry? matched = null;
        foreach (var candidate in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, candidate.Hash))
            {
                matched = candidate.Entry;
            }
        }

        return matched;
    }

    private static ClaimsPrincipal BuildPrincipal(McpApiKeyEntry entry)
    {
        var claims = new List<Claim>
        {
            new(AbpClaimTypes.UserId, entry.ServiceAccountUserId)
        };

        if (!string.IsNullOrWhiteSpace(entry.TenantId))
        {
            claims.Add(new Claim(AbpClaimTypes.TenantId, entry.TenantId));
        }

        // Non-empty authenticationType => IsAuthenticated == true (load-bearing; see class remarks). The
        // key's Label is used only for log attribution, NOT stamped as AbpClaimTypes.UserName: faking a
        // user name would mislead audit correlation against the real Identity service-account user.
        var identity = new ClaimsIdentity(claims, McpApiKeyDefaults.AuthenticationType);
        return new ClaimsPrincipal(identity);
    }

    private sealed record CompiledKey(byte[] Hash, McpApiKeyEntry Entry);
}
