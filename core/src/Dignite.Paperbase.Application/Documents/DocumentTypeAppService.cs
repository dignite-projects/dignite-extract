using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents;

[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class DocumentTypeAppService : PaperbaseAppService, IDocumentTypeAppService
{
    private readonly IDocumentTypeRepository _repository;
    private readonly IDocumentRepository _documentRepository;
    private readonly IFieldDefinitionRepository _fieldDefinitionRepository;

    public DocumentTypeAppService(
        IDocumentTypeRepository repository,
        IDocumentRepository documentRepository,
        IFieldDefinitionRepository fieldDefinitionRepository)
    {
        _repository = repository;
        _documentRepository = documentRepository;
        _fieldDefinitionRepository = fieldDefinitionRepository;
    }

    public virtual async Task<List<DocumentTypeDto>> GetVisibleAsync()
    {
        // 当前层文档类型（Host admin 看 TenantId IS NULL 行；租户 admin 看自己租户行）。
        // 解读 X + 没有继承关系：不做 Host ∪ Tenant union。
        var list = await _repository.GetByTenantAsync();
        return ObjectMapper.Map<List<DocumentType>, List<DocumentTypeDto>>(list);
    }

    public virtual async Task<List<DocumentTypeDto>> GetDeletedAsync()
    {
        // 显式 TenantId 谓词（不依赖 ambient DataFilter）+ 关闭 ISoftDelete 看到已删除行。
        // 当前层回收站：Host admin（CurrentTenant.Id IS NULL）看 Host 类型；租户 admin 看自己租户。
        // Host 与 tenant 各自独立宇宙，不跨层。
        using (DataFilter.Disable<ISoftDelete>())
        {
            var queryable = await _repository.GetQueryableAsync();
            var list = await AsyncExecuter.ToListAsync(
                queryable
                    .Where(t => t.TenantId == CurrentTenant.Id && t.IsDeleted)
                    .OrderByDescending(t => t.DeletionTime));
            return ObjectMapper.Map<List<DocumentType>, List<DocumentTypeDto>>(list);
        }
    }

    public virtual async Task<DocumentTypeDto> CreateAsync(CreateDocumentTypeDto input)
    {
        // 严格单层判重——TypeCode 是 per-layer 命名空间，Host 与 tenant 各自独立，
        // 跨层同 TypeCode 是合法的两行（由 TenantId 区分）。下游消费方按
        // (TenantId, DocumentTypeCode) 元组消费。
        // 关闭 ISoftDelete 过滤，让软删除记录也参与判重——否则"删除→重建同 TypeCode→
        // 恢复旧记录"路径会触发唯一索引冲突或两行同 (TenantId, TypeCode) 活跃。
        DocumentType? existing;
        using (DataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByTypeCodeAsync(input.TypeCode);
        }
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentTypeCodeAlreadyExists)
                .WithData("TypeCode", input.TypeCode);
        }

        var entity = new DocumentType(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.TypeCode,
            input.DisplayName,
            input.ConfidenceThreshold,
            input.Priority);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    public virtual async Task<DocumentTypeDto> UpdateAsync(Guid id, UpdateDocumentTypeDto input)
    {
        var entity = await _repository.GetAsync(id);

        // 跨层防御：只能改自己所在层（Host admin 改 TenantId IS NULL；租户 admin 改 TenantId == 自己）。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        entity.Update(input.DisplayName, input.ConfidenceThreshold, input.Priority);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
    }

    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(DocumentType), id);
        }

        // Fail-closed：仍有文档引用此类型时阻止删除——强制租户先 reclassify 这些文档。
        // 显式 TenantId 谓词，不依赖 ambient DataFilter（CLAUDE.md "## 安全约定"）。
        var documentQueryable = await _documentRepository.GetQueryableAsync();
        var inUse = await AsyncExecuter.AnyAsync(
            documentQueryable.Where(d =>
                d.TenantId == entity.TenantId &&
                d.DocumentTypeCode == entity.TypeCode));
        if (inUse)
        {
            throw new BusinessException(PaperbaseErrorCodes.DocumentTypeInUse)
                .WithData("TypeCode", entity.TypeCode);
        }

        // 级联软删除：同 (TenantId, TypeCode) 下的 FieldDefinition 随 DocumentType 一并下线，
        // 否则会留下孤儿字段定义且未来重建同 TypeCode 时无法复用同名字段。
        var fields = await _fieldDefinitionRepository.GetByDocumentTypeAsync(entity.TypeCode);
        if (fields.Count > 0)
        {
            await _fieldDefinitionRepository.DeleteManyAsync(fields);
        }

        await _repository.DeleteAsync(entity);
    }

    public virtual async Task<DocumentTypeDto> RestoreAsync(Guid id)
    {
        // 整段恢复在禁用 ISoftDelete 的 scope 里执行：查询能看到已删除行，写入能把 IsDeleted=false。
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(DocumentType), id);
            }

            // 幂等：未删除的直接返回当前状态。
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
            }

            // 防御：同 (TenantId, TypeCode) 已有活跃行——CreateAsync 的判重应当已防住，
            // 但极端情况下（手工 DB / seed bypass）仍可能发生，避免唯一索引冲突。
            var typeQueryable = await _repository.GetQueryableAsync();
            var typeConflict = await AsyncExecuter.AnyAsync(
                typeQueryable.Where(t =>
                    t.TenantId == entity.TenantId &&
                    t.TypeCode == entity.TypeCode &&
                    !t.IsDeleted));
            if (typeConflict)
            {
                throw new BusinessException(PaperbaseErrorCodes.DocumentTypeRestoreConflict)
                    .WithData("TypeCode", entity.TypeCode);
            }

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            // 级联恢复：同 (TenantId, TypeCode) 下软删除的 FieldDefinition 一并恢复。
            // 与单字段 RestoreAsync 不同——这里跳过冲突字段（记录 warning），不中断整体恢复。
            var fieldQueryable = await _fieldDefinitionRepository.GetQueryableAsync();
            var deletedFields = await AsyncExecuter.ToListAsync(
                fieldQueryable.Where(f =>
                    f.TenantId == entity.TenantId &&
                    f.DocumentTypeCode == entity.TypeCode &&
                    f.IsDeleted));

            foreach (var field in deletedFields)
            {
                var nameConflict = await AsyncExecuter.AnyAsync(
                    fieldQueryable.Where(f =>
                        f.TenantId == entity.TenantId &&
                        f.DocumentTypeCode == entity.TypeCode &&
                        f.Name == field.Name &&
                        !f.IsDeleted));
                if (nameConflict)
                {
                    Logger.LogWarning(
                        "Skip cascade restore of FieldDefinition {FieldId} (Name={Name}) under DocumentType {TypeCode}: an active field with the same name already exists.",
                        field.Id, field.Name, entity.TypeCode);
                    continue;
                }

                field.IsDeleted = false;
                field.DeletionTime = null;
                field.DeleterId = null;
                await _fieldDefinitionRepository.UpdateAsync(field);
            }

            return ObjectMapper.Map<DocumentType, DocumentTypeDto>(entity);
        }
    }
}
