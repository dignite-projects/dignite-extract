using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Paperbase.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.Paperbase.Documents.Fields;

[Authorize(PaperbasePermissions.Documents.ConfirmClassification)]
public class FieldDefinitionAppService : PaperbaseAppService, IFieldDefinitionAppService
{
    private readonly IFieldDefinitionRepository _repository;
    private readonly IDocumentTypeRepository _documentTypeRepository;

    public FieldDefinitionAppService(
        IFieldDefinitionRepository repository,
        IDocumentTypeRepository documentTypeRepository)
    {
        _repository = repository;
        _documentTypeRepository = documentTypeRepository;
    }

    public virtual async Task<List<FieldDefinitionDto>> GetByDocumentTypeAsync(string documentTypeCode)
    {
        // 仅当前租户层字段（CLAUDE.md "两层 mutually exclusive 不混"）——
        // 字段抽取由 Document.TenantId 决定唯一一层，不混合 Host + tenant。
        var list = await _repository.GetByDocumentTypeAsync(documentTypeCode);
        return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(list);
    }

    public virtual async Task<List<FieldDefinitionDto>> GetDeletedByDocumentTypeAsync(string documentTypeCode)
    {
        // 当前层回收站：Host admin（CurrentTenant.Id IS NULL）看 Host 字段；租户 admin 看自己租户。
        // Host 与 tenant 各自独立宇宙，不跨层。
        using (DataFilter.Disable<ISoftDelete>())
        {
            var queryable = await _repository.GetQueryableAsync();
            var list = await AsyncExecuter.ToListAsync(
                queryable
                    .Where(f =>
                        f.TenantId == CurrentTenant.Id &&
                        f.DocumentTypeCode == documentTypeCode &&
                        f.IsDeleted)
                    .OrderByDescending(f => f.DeletionTime));
            return ObjectMapper.Map<List<FieldDefinition>, List<FieldDefinitionDto>>(list);
        }
    }

    public virtual async Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input)
    {
        // 严格单层创建：Host admin 创建 TenantId IS NULL 字段；租户 admin 创建自己租户字段。
        // 关闭 ISoftDelete 过滤——同 (TenantId, DocumentTypeCode, Name) 即使处于软删除态也算占用，
        // 避免后续恢复时与新记录冲突。
        FieldDefinition? existing;
        using (DataFilter.Disable<ISoftDelete>())
        {
            existing = await _repository.FindByNameAsync(input.DocumentTypeCode, input.Name);
        }
        if (existing != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.FieldDefinitionAlreadyExists)
                .WithData("DocumentTypeCode", input.DocumentTypeCode)
                .WithData("Name", input.Name);
        }

        var entity = new FieldDefinition(
            GuidGenerator.Create(),
            CurrentTenant.Id,
            input.DocumentTypeCode,
            input.Name,
            input.DisplayName,
            input.Prompt,
            input.DataType,
            input.DisplayOrder,
            input.IsRequired);

        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    public virtual async Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input)
    {
        var entity = await _repository.GetAsync(id);

        // 跨层防御：只能改自己所在层（Host admin 改 TenantId IS NULL；租户 admin 改 TenantId == 自己）。
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }

        entity.Update(input.DisplayName, input.Prompt, input.DataType, input.DisplayOrder, input.IsRequired);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
    }

    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(FieldDefinition), id);
        }
        await _repository.DeleteAsync(entity);
    }

    public virtual async Task<FieldDefinitionDto> RestoreAsync(Guid id)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var entity = await _repository.GetAsync(id);
            if (entity.TenantId != CurrentTenant.Id)
            {
                throw new EntityNotFoundException(typeof(FieldDefinition), id);
            }

            // 幂等：未删除直接返回。
            if (!entity.IsDeleted)
            {
                return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
            }

            // 父类型必须存在且活跃——严格单层匹配（与 FieldExtractionEventHandler 一致：
            // 字段抽取按 Document.TenantId 精确查同层字段，不跨层）。
            // 父类型仍处于已删除态时，应走 IDocumentTypeAppService.RestoreAsync 的级联路径。
            var parentQueryable = await _documentTypeRepository.GetQueryableAsync();
            var parentActive = await AsyncExecuter.AnyAsync(
                parentQueryable.Where(t =>
                    t.TenantId == entity.TenantId &&
                    t.TypeCode == entity.DocumentTypeCode &&
                    !t.IsDeleted));
            if (!parentActive)
            {
                throw new BusinessException(PaperbaseErrorCodes.FieldDefinitionParentTypeMissing)
                    .WithData("DocumentTypeCode", entity.DocumentTypeCode)
                    .WithData("Name", entity.Name);
            }

            // 同名活跃字段冲突——CreateAsync 判重应当已防住，防御性补一道。
            var queryable = await _repository.GetQueryableAsync();
            var nameConflict = await AsyncExecuter.AnyAsync(
                queryable.Where(f =>
                    f.TenantId == entity.TenantId &&
                    f.DocumentTypeCode == entity.DocumentTypeCode &&
                    f.Name == entity.Name &&
                    !f.IsDeleted));
            if (nameConflict)
            {
                throw new BusinessException(PaperbaseErrorCodes.FieldDefinitionRestoreConflict)
                    .WithData("DocumentTypeCode", entity.DocumentTypeCode)
                    .WithData("Name", entity.Name);
            }

            entity.IsDeleted = false;
            entity.DeletionTime = null;
            entity.DeleterId = null;
            await _repository.UpdateAsync(entity);

            return ObjectMapper.Map<FieldDefinition, FieldDefinitionDto>(entity);
        }
    }
}
