using Dignite.Extract.Documents;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Extract.EntityFrameworkCore;

[ConnectionStringName(ExtractDbProperties.ConnectionStringName)]
public interface IExtractDbContext : IEfCoreDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentPipelineRun> DocumentPipelineRuns { get; }
    DbSet<DocumentSegment> DocumentSegments { get; }
    DbSet<DocumentType> DocumentTypes { get; }
    DbSet<FieldDefinition> FieldDefinitions { get; }
    DbSet<ExportTemplate> ExportTemplates { get; }
    DbSet<Cabinet> Cabinets { get; }
}
