using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/* Inherit from this class for your domain layer tests.
 */
public abstract class VaultExtractDomainTestBase<TStartupModule> : VaultExtractTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
