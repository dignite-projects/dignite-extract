using Dignite.DocumentAI.Documents.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;

namespace Dignite.DocumentAI;

[DependsOn(
    typeof(DocumentAIDomainModule),
    typeof(DocumentAITestBaseModule)
)]
public class DocumentAIDomainTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Manager depends on IDocumentPipelineRunRepository (#216). Use the closure-state fake shared by
        // Domain.Tests so QueueAsync / DeriveLifecycle DB-query paths can run completely in memory.
        context.Services.AddSingleton(PipelineRunRepositoryFake.Create());
    }
}
