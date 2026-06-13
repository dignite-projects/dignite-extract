using Dignite.DocumentAI.Documents;
using Dignite.DocumentAI.Documents.Exports;
using Dignite.DocumentAI.Documents.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace Dignite.DocumentAI.EntityFrameworkCore;

[DependsOn(
    typeof(DocumentAIDomainModule),
    typeof(AbpEntityFrameworkCoreModule)
)]
public class DocumentAIEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<DocumentAIDbContext>(options =>
        {
            options.AddDefaultRepositories();

            options.AddRepository<Document, EfCoreDocumentRepository>();
            options.AddRepository<DocumentType, EfCoreDocumentTypeRepository>();
            options.AddRepository<FieldDefinition, EfCoreFieldDefinitionRepository>();
            options.AddRepository<Cabinet, EfCoreCabinetRepository>();
            options.AddRepository<ExportTemplate, EfCoreExportTemplateRepository>();
            // #216: PipelineRun was promoted to an independent aggregate root.
            options.AddRepository<DocumentPipelineRun, EfCoreDocumentPipelineRunRepository>();
        });
    }
}
