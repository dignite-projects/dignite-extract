using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dignite.Vault.Extract.Mcp.Authentication;

/// <summary>
/// Reusable wiring for the optional static API-key fallback auth channel on <c>/mcp</c> (#428), exported by
/// the Mcp egress module so any host enables it with one <c>Add</c> + one <c>Use</c> call. Mirrors the #422
/// <c>AddExtractMcpDiscovery</c> split: the deployment-agnostic MECHANISM (header matching, constant-time
/// compare, synthetic-principal construction) lives in this module; the HOST owns all configuration and the
/// key → service-account mapping, and calls <see cref="UseExtractMcpApiKey"/> from its
/// <c>OnApplicationInitialization</c> — so the "middleware only wired in host" rule holds.
/// </summary>
public static class McpApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers and fail-fast-validates the API-key options. A no-op feature when no keys are configured
    /// (OAuth-only deployment). Pair with <see cref="UseExtractMcpApiKey"/> in the pipeline, before
    /// <c>UseAuthentication</c>.
    /// </summary>
    public static IServiceCollection AddExtractMcpApiKey(
        this IServiceCollection services,
        Action<McpApiKeyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Validate eagerly against a throwaway bound instance so misconfiguration fails fast at startup
        // (mirroring ConfigureAI's placeholder guard); a no-op when no keys are configured.
        var validation = new McpApiKeyOptions();
        configure(validation);
        validation.Validate();

        // Register the SAME delegate so every option property binds to the DI instance — avoids a manual
        // field-copy that a future property could silently drift out of.
        services.Configure(configure);

        return services;
    }

    /// <summary>
    /// Inserts the API-key middleware, scoped (segment-aware) to the configured <c>/mcp</c> path prefix so
    /// it never runs for the admin UI / REST / Swagger. A no-op when no keys are configured. MUST be called
    /// before <c>UseAuthentication</c>.
    /// </summary>
    public static IApplicationBuilder UseExtractMcpApiKey(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ApplicationServices.GetRequiredService<IOptions<McpApiKeyOptions>>().Value;
        var logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Dignite.Vault.Extract.Mcp.Authentication.McpApiKey");

        if (options.Keys is null || options.Keys.Count == 0)
        {
            logger.LogInformation(
                "MCP API-key channel is disabled (no Mcp:ApiKey:Keys configured); /mcp uses OpenIddict Bearer + #278 discovery only.");
            return app; // disabled: OAuth-only deployment
        }

        logger.LogInformation(
            "MCP API-key channel enabled with {KeyCount} key(s) on header '{Header}', scoped to '{Path}'.",
            options.Keys.Count, options.HeaderName, options.PathPrefix);

        var prefix = new PathString(options.PathPrefix);
        return app.UseWhen(
            context => context.Request.Path.StartsWithSegments(prefix),
            branch => branch.UseMiddleware<McpApiKeyAuthenticationMiddleware>());
    }
}
