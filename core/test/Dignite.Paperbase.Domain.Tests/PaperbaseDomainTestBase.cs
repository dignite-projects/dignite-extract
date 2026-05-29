using Volo.Abp.Modularity;

namespace Dignite.Paperbase;

/* Inherit from this class for your domain layer tests.
 */
public abstract class PaperbaseDomainTestBase<TStartupModule> : PaperbaseTestBase<TStartupModule>
    where TStartupModule : IAbpModule
{

}
