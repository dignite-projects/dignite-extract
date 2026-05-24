using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Content;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents;

[Authorize]
public class ExportTemplateAppService : PaperbaseAppService, IExportTemplateAppService
{
    private readonly IExportTemplateRepository _templateRepository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;

    public ExportTemplateAppService(
        IExportTemplateRepository templateRepository,
        IDocumentRepository documentRepository,
        IDocumentTypeRepository documentTypeRepository)
    {
        _templateRepository = templateRepository;
        _documentRepository = documentRepository;
        _documentTypeRepository = documentTypeRepository;
    }

    public virtual async Task<ExportTemplateDto> GetAsync(Guid id)
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Templates.Default);
        var entity = await GetOwnedTemplateAsync(id);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    public virtual async Task<List<ExportTemplateDto>> GetListAsync()
    {
        await CheckPolicyAsync(PaperbasePermissions.Documents.Templates.Default);
        var list = await _templateRepository.GetByTenantAsync();
        return ObjectMapper.Map<List<ExportTemplate>, List<ExportTemplateDto>>(list);
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Create)]
    public virtual async Task<ExportTemplateDto> CreateAsync(CreateExportTemplateDto input)
    {
        await EnsureTemplateNameAvailableAsync(input.Name);
        await EnsureDocumentTypeValidAsync(input.DocumentTypeCode);

        var entity = new ExportTemplate(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.Name,
            input.Format,
            input.DocumentTypeCode,
            MapColumns(input.Columns));

        await _templateRepository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Update)]
    public virtual async Task<ExportTemplateDto> UpdateAsync(Guid id, UpdateExportTemplateDto input)
    {
        var entity = await GetOwnedTemplateAsync(id);

        // 仅在改名时判重——同名未变不必查（避免误判到自身）。
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await EnsureTemplateNameAvailableAsync(input.Name);
        }

        await EnsureDocumentTypeValidAsync(input.DocumentTypeCode);

        entity.Update(input.Name, input.Format, input.DocumentTypeCode, MapColumns(input.Columns));
        await _templateRepository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<ExportTemplate, ExportTemplateDto>(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Templates.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await GetOwnedTemplateAsync(id);
        await _templateRepository.DeleteAsync(entity);
    }

    [Authorize(PaperbasePermissions.Documents.Export)]
    public virtual async Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input)
    {
        var template = await GetOwnedTemplateAsync(input.TemplateId);

        // 显式租户谓词 — fail closed，不依赖 ambient DataFilter（CLAUDE.md "## 安全约定"）。
        var tenantId = CurrentTenant.Id;
        var query = (await _documentRepository.GetQueryableAsync())
            .Where(d => d.TenantId == tenantId);

        if (input.DocumentIds is { Count: > 0 } ids)
        {
            query = query.Where(d => ids.Contains(d.Id));
        }
        else
        {
            if (input.LifecycleStatus.HasValue)
            {
                query = query.Where(d => d.LifecycleStatus == input.LifecycleStatus.Value);
            }

            if (!string.IsNullOrWhiteSpace(input.DocumentTypeCode))
            {
                query = query.Where(d => d.DocumentTypeCode == input.DocumentTypeCode);
            }

            query = DocumentQueryFilters.WhereKeyword(query, input.Keyword);
        }

        // 模板可限定适用文档类型——在筛选 / 勾选基础上再收窄一层。
        if (!string.IsNullOrWhiteSpace(template.DocumentTypeCode))
        {
            query = query.Where(d => d.DocumentTypeCode == template.DocumentTypeCode);
        }

        // 单次 fetch (Max + 1) 投影到 ExportProjection（非实体类型 → 不 SELECT Markdown、不进 tracker）。
        // 多取 1 条用于原子判定超限——消除 count + Take 两次查询间并发插入导致的静默截断。
        // 会计场景漏导凭证比报错更危险，故超限 fail-fast。
        var limit = ExportTemplateConsts.MaxExportDocumentCount;
        var rows = await AsyncExecuter.ToListAsync(
            query
                .OrderByDescending(d => d.CreationTime)
                .Select(d => new ExportProjection
                {
                    Id = d.Id,
                    Title = d.Title,
                    DocumentTypeCode = d.DocumentTypeCode,
                    LifecycleStatus = d.LifecycleStatus,
                    ReviewStatus = d.ReviewStatus,
                    Language = d.Language,
                    OcrConfidence = d.OcrConfidence,
                    ClassificationConfidence = d.ClassificationConfidence,
                    CreationTime = d.CreationTime,
                    OriginalFileName = d.FileOrigin.OriginalFileName,
                    ContentType = d.FileOrigin.ContentType,
                    FileSize = d.FileOrigin.FileSize,
                    ExtractedFields = d.ExtractedFields,
                })
                .Take(limit + 1));

        if (rows.Count > limit)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportDocumentLimitExceeded)
                .WithData("count", limit + "+")
                .WithData("max", limit);
        }

        var headers = template.Columns.Select(c => c.ColumnName).ToList();
        var dataRows = rows
            .Select(r => template.Columns.Select(c => GetCellValue(r, c)).ToArray())
            .ToList();

        var bytes = ExportFileBuilder.Build(template.Format, headers, dataRows);

        var (fileName, contentType) = template.Format switch
        {
            ExportFormat.Xlsx => (template.Name + ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => (template.Name + ".csv", "text/csv")
        };

        return new RemoteStreamContent(new MemoryStream(bytes), fileName, contentType);
    }

    protected virtual async Task<ExportTemplate> GetOwnedTemplateAsync(Guid id)
    {
        var entity = await _templateRepository.GetAsync(id);

        // 跨层防御：只能访问自己所在层（Host admin 操作 TenantId IS NULL；租户 admin 操作自己租户）。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(ExportTemplate), id);
        }

        return entity;
    }

    protected virtual async Task EnsureTemplateNameAvailableAsync(string name)
    {
        var existing = await _templateRepository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateNameAlreadyExists)
                .WithData("Name", name);
        }
    }

    protected virtual async Task EnsureDocumentTypeValidAsync(string? documentTypeCode)
    {
        if (string.IsNullOrWhiteSpace(documentTypeCode))
        {
            return;
        }

        var type = await _documentTypeRepository.FindByTypeCodeAsync(documentTypeCode);
        if (type == null)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateInvalidDocumentTypeCode)
                .WithData("documentTypeCode", documentTypeCode);
        }
    }

    private static IReadOnlyList<ExportColumn> MapColumns(IEnumerable<ExportColumnInput> columns)
        => columns.Select(c => new ExportColumn(c.SourceKind, c.Key, c.ColumnName, c.Order)).ToList();

    private static string? GetCellValue(ExportProjection d, ExportColumn column)
        => column.SourceKind switch
        {
            ExportColumnSourceKind.System => GetSystemValue(d, column.Key),
            ExportColumnSourceKind.Extracted => GetExtractedValue(d, column.Key),
            _ => null
        };

    private static string? GetSystemValue(ExportProjection d, string key) => key switch
    {
        ExportSystemFields.Id => d.Id.ToString(),
        ExportSystemFields.Title => d.Title,
        ExportSystemFields.DocumentTypeCode => d.DocumentTypeCode,
        ExportSystemFields.LifecycleStatus => d.LifecycleStatus.ToString(),
        ExportSystemFields.ReviewStatus => d.ReviewStatus.ToString(),
        ExportSystemFields.Language => d.Language,
        ExportSystemFields.OcrConfidence => d.OcrConfidence?.ToString(CultureInfo.InvariantCulture),
        ExportSystemFields.ClassificationConfidence => d.ClassificationConfidence.ToString(CultureInfo.InvariantCulture),
        ExportSystemFields.CreationTime => d.CreationTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        ExportSystemFields.OriginalFileName => d.OriginalFileName,
        ExportSystemFields.ContentType => d.ContentType,
        ExportSystemFields.FileSize => d.FileSize.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    private static string? GetExtractedValue(ExportProjection d, string key)
    {
        if (d.ExtractedFields != null && d.ExtractedFields.TryGetValue(key, out var element))
        {
            return JsonElementToString(element);
        }

        return null;
    }

    private static string? JsonElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };
}
