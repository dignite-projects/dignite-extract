using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Host.Data;

public class VaultExtractHostDbSchemaMigrator : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;

    public VaultExtractHostDbSchemaMigrator(
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task MigrateAsync()
    {
        /* We intentionally resolve DbContexts from IServiceProvider
         * to properly get the connection string of the current tenant
         * in the current scope.
         */

        await _serviceProvider
            .GetRequiredService<VaultExtractHostDbContext>()
            .Database
            .MigrateAsync();
    }
}
