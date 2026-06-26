using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

/* Inherit from this class for your application layer tests.
 */
public abstract class VaultExtractApplicationTestBase<TStartupModule> : VaultExtractTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
