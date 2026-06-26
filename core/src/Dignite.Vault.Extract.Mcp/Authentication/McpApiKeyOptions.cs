using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Options for the optional static API-key fallback auth channel on <c>/mcp</c> (#428). An empty
/// <see cref="Keys"/> list (the shipped default) means the feature is <b>disabled</b> — an OAuth-only
/// deployment — and the channel adds nothing to the request pipeline.
/// </summary>
public class McpApiKeyOptions
{
    /// <summary>Request header carrying the key. Default <see cref="McpApiKeyDefaults.DefaultHeaderName"/>.</summary>
    public string HeaderName { get; set; } = McpApiKeyDefaults.DefaultHeaderName;

    /// <summary>
    /// Endpoint path prefix the channel is scoped to. Default <see cref="McpApiKeyDefaults.DefaultPathPrefix"/>.
    /// MUST match the host's <c>MapMcp</c> path — if the host remaps the MCP endpoint, set this to match,
    /// otherwise the channel silently stops matching (the middleware no-ops and key auth breaks).
    /// </summary>
    public string PathPrefix { get; set; } = McpApiKeyDefaults.DefaultPathPrefix;

    /// <summary>
    /// When <c>true</c> (default), a key presented over a non-HTTPS request is ignored (the request falls
    /// through to Bearer) — the key is a long-lived bearer-equivalent secret and must not travel in clear
    /// text. Behind a TLS-terminating reverse proxy this relies on the host's forwarded-headers handling so
    /// <c>Request.IsHttps</c> reflects the original scheme. Set <c>false</c> only for a deliberately
    /// plain-HTTP deployment (e.g. local testing).
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Configured keys. Empty = feature disabled.</summary>
    public List<McpApiKeyEntry> Keys { get; set; } = new();

    /// <summary>
    /// Fail-fast shape validation, mirroring <c>ConfigureAI</c>'s placeholder/empty guards. A no-op when no
    /// keys are configured (the feature is simply off). When keys ARE present, every entry must be a real,
    /// sufficiently-long, non-placeholder secret mapped to a parseable user id (and tenant id if given).
    /// It does NOT verify the user exists or check its grants (there is no DB at config time) — that is
    /// enforced fail-closed at call time by the tools' <c>CheckPolicyAsync</c>.
    /// </summary>
    public void Validate()
    {
        if (Keys is null || Keys.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HeaderName))
        {
            throw new AbpException("Mcp:ApiKey:HeaderName must not be empty when API keys are configured.");
        }

        if (string.IsNullOrWhiteSpace(PathPrefix) || !PathPrefix.StartsWith('/'))
        {
            throw new AbpException(
                "Mcp:ApiKey:PathPrefix must be a non-empty path starting with '/' and must match the host's " +
                "MapMcp path (default \"/mcp\").");
        }

        for (var i = 0; i < Keys.Count; i++)
        {
            var entry = Keys[i];
            var where = $"Mcp:ApiKey:Keys[{i}]";

            if (string.IsNullOrWhiteSpace(entry.Key)
                || entry.Key == McpApiKeyDefaults.PlaceholderKey
                || entry.Key.Length < McpApiKeyDefaults.MinKeyLength)
            {
                throw new AbpException(
                    $"{where}.Key is missing, the placeholder, or shorter than {McpApiKeyDefaults.MinKeyLength} characters. " +
                    "Supply a CSPRNG-generated secret (>= 32 chars) via environment variables or user-secrets — never commit it. " +
                    "Leave Mcp:ApiKey:Keys empty to disable the API-key channel (OAuth-only).");
            }

            if (!Guid.TryParse(entry.ServiceAccountUserId, out var userId) || userId == Guid.Empty)
            {
                throw new AbpException(
                    $"{where}.ServiceAccountUserId must be the Guid of a provisioned ABP service-account user " +
                    "granted only VaultExtract.Documents (least privilege, no roles).");
            }

            if (!string.IsNullOrWhiteSpace(entry.TenantId) && !Guid.TryParse(entry.TenantId, out _))
            {
                throw new AbpException($"{where}.TenantId, when set, must be a Guid.");
            }
        }

        var duplicate = Keys.GroupBy(k => k.Key).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new AbpException(
                "Mcp:ApiKey:Keys contains duplicate Key values; each key must be distinct so audit " +
                "attribution and independent revocation stay meaningful.");
        }
    }
}
