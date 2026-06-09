using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.DocumentTypes;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.Documents.Pipelines;
using Dignite.Paperbase.Documents.Pipelines.FieldExtraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Guids;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;
using Xunit;

namespace Dignite.Paperbase.EntityFrameworkCore.Documents;

[DependsOn(typeof(PaperbaseEntityFrameworkCoreTestModule))]
public class FieldExtractionJobTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IBackgroundJobManager>());

        // 直接注册 stub workflow 实例（ForPartsOf 经真实 ctor 构造，DI 取该单例，绕开 keyed IChatClient 解析）。
        var workflow = Substitute.ForPartsOf<FieldExtractionWorkflow>(
            Substitute.For<IChatClient>(), NullLogger<FieldExtractionWorkflow>.Instance);
        context.Services.AddSingleton(workflow);
    }
}

/// <summary>
/// <see cref="DocumentFieldExtractionBackgroundJob"/> 的 UoW 边界回归测试（<c>.claude/rules/background-jobs.md</c>
/// "Tests" 要求）：外部 LLM 调用（<see cref="FieldExtractionWorkflow.ExtractAsync"/>）必须在任何 ambient UoW 之外执行。
/// 同时验证 field-extraction run 落地 Succeeded + 字段值写入（端到端经真实 EF）。
/// </summary>
public class DocumentFieldExtractionBackgroundJob_Tests
    : PaperbaseTestBase<FieldExtractionJobTestModule>
{
    private readonly DocumentFieldExtractionBackgroundJob _job;
    private readonly FieldExtractionWorkflow _workflow;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;
    private readonly IDocumentPipelineRunRepository _runRepository;
    private readonly IGuidGenerator _guidGenerator;

    public DocumentFieldExtractionBackgroundJob_Tests()
    {
        _job = GetRequiredService<DocumentFieldExtractionBackgroundJob>();
        _workflow = GetRequiredService<FieldExtractionWorkflow>();
        _unitOfWorkManager = GetRequiredService<IUnitOfWorkManager>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _fieldDefinitionRepository = GetRequiredService<IFieldDefinitionRepository>();
        _runRepository = GetRequiredService<IDocumentPipelineRunRepository>();
        _guidGenerator = GetRequiredService<IGuidGenerator>();
    }

    [Fact]
    public async Task Runs_LLM_Outside_Ambient_UoW_And_Persists_Succeeded_Run_With_Fields()
    {
        var typeId = _guidGenerator.Create();
        var fieldId = _guidGenerator.Create();
        var documentId = _guidGenerator.Create();

        await WithUnitOfWorkAsync(async () =>
        {
            await _documentTypeRepository.InsertAsync(
                new DocumentType(typeId, null, "type.a", "Type A"), autoSave: true);
            await _fieldDefinitionRepository.InsertAsync(
                new FieldDefinition(fieldId, null, typeId, "amount", "Amount", "extract", FieldDataType.Number),
                autoSave: true);

            var doc = NewDocument(documentId);
            doc.SetMarkdown("# Body\n\nAmount: 1500");
            doc.ApplyAutomaticClassificationResult(typeId, 0.99);
            await _documentRepository.InsertAsync(doc, autoSave: true);
        });

        // 核心断言：LLM 调用发生时不在任何 ambient UoW 内（background-jobs.md 短 UoW 硬约束）。
        _workflow
            .ExtractAsync(Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _unitOfWorkManager.Current.ShouldBeNull();
                return new Dictionary<string, JsonElement?> { ["amount"] = JsonDocument.Parse("1500").RootElement };
            });

        // PipelineRunId=null —— 批量路径形态：作业内 StartAsync 自建 field-extraction run。
        await _job.ExecuteAsync(new DocumentFieldExtractionJobArgs { DocumentId = documentId, PipelineRunId = null });

        await _workflow.Received(1).ExtractAsync(
            Arg.Any<IReadOnlyList<FieldExtractionDescriptor>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await WithUnitOfWorkAsync(async () =>
        {
            var run = await _runRepository.FindLatestByDocumentAndCodeAsync(documentId, PaperbasePipelines.FieldExtraction);
            run.ShouldNotBeNull();
            run!.Status.ShouldBe(PipelineRunStatus.Succeeded);

            var doc = await _documentRepository.FindWithFieldValuesAsync(documentId);
            doc!.ExtractedFieldValues.Single().FieldDefinitionId.ShouldBe(fieldId);

            // 生命周期中性：field-extraction 非 key pipeline，未把文档推进/打回。
            doc.LifecycleStatus.ShouldBe(DocumentLifecycleStatus.Processing);
        });
    }

    private static Document NewDocument(Guid id) =>
        new(
            id,
            tenantId: null,
            fileOrigin: new FileOrigin(
                blobName: $"blobs/{id:N}.pdf",
                uploadedByUserName: "test-user",
                contentType: "application/pdf",
                contentHash: $"{Guid.NewGuid():N}{Guid.NewGuid():N}",
                fileSize: 1024,
                originalFileName: "test.pdf"));
}
