using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dignite.DocumentAI.Permissions;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace Dignite.DocumentAI.Documents.Cabinets;

/// <summary>
/// Cabinet management (#194). Matches the current layer only and never unions across layers.
/// Permissions fail closed through method-level Cabinets.* assertions.
/// </summary>
[Authorize(DocumentAIPermissions.Cabinets.Default)]
public class CabinetAppService : DocumentAIAppService, ICabinetAppService
{
    private readonly ICabinetRepository _repository;
    private readonly IDocumentRepository _documentRepository;

    public CabinetAppService(
        ICabinetRepository repository,
        IDocumentRepository documentRepository)
    {
        _repository = repository;
        _documentRepository = documentRepository;
    }

    public virtual async Task<List<CabinetDto>> GetListAsync()
    {
        // All cabinets in the current layer, isolated by the ambient IMultiTenant filter using
        // CurrentTenant.Id.
        var list = await _repository.GetListAsync();
        return ObjectMapper.Map<List<Cabinet>, List<CabinetDto>>(list);
    }

    [Authorize(DocumentAIPermissions.Cabinets.Create)]
    public virtual async Task<CabinetDto> CreateAsync(CreateCabinetDto input)
    {
        await EnsureNameAvailableAsync(input.Name);

        var entity = new Cabinet(GuidGenerator.Create(), CurrentTenant.Id, input.Name, input.Description);
        await _repository.InsertAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(DocumentAIPermissions.Cabinets.Update)]
    public virtual async Task<CabinetDto> UpdateAsync(Guid id, UpdateCabinetDto input)
    {
        var entity = await _repository.GetAsync(id);

        // Cross-layer defense: callers can update only their own layer.
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // Name is an editable unique key. Check uniqueness only on rename; unchanged name skips the
        // check to avoid falsely treating itself as a conflict.
        if (!string.Equals(entity.Name, input.Name, StringComparison.Ordinal))
        {
            await EnsureNameAvailableAsync(input.Name);
        }

        entity.Update(input.Name, input.Description);
        await _repository.UpdateAsync(entity, autoSave: true);
        return ObjectMapper.Map<Cabinet, CabinetDto>(entity);
    }

    [Authorize(DocumentAIPermissions.Cabinets.Delete)]
    public virtual async Task DeleteAsync(Guid id)
    {
        var entity = await _repository.GetAsync(id);
        if (entity.TenantId != CurrentTenant.Id)
        {
            throw new EntityNotFoundException(typeof(Cabinet), id);
        }

        // Atomically clear CabinetId on documents in this cabinet before deleting it, truly unfiling
        // them. Otherwise documents would dangle to a deleted cabinet (stale ID, and recreating a
        // cabinet with the same name cannot restore membership). Unlike DocumentType InUse protection,
        // deletion is not blocked. Only active documents are cleared; stale CabinetId on soft-deleted
        // documents is harmless because the frontend cannot map it and displays uncategorized. Switch
        // to ExecuteUpdateAsync if one cabinet can contain very many documents.
        var orphans = await _documentRepository.GetListAsync(
            d => d.TenantId == CurrentTenant.Id && d.CabinetId == entity.Id);
        if (orphans.Count > 0)
        {
            foreach (var doc in orphans)
            {
                doc.UnassignCabinet();
            }
            await _documentRepository.UpdateManyAsync(orphans, autoSave: true);
        }

        await _repository.DeleteAsync(entity);
    }

    /// <summary>
    /// Checks cabinet-name uniqueness in the current layer, considering only active cabinets and not
    /// soft-deleted ones. Cabinets do not have a recycle bin; soft delete means forgotten, so the name
    /// can be reused by a new cabinet. The unique index <c>(TenantId, Name)</c> is filtered by
    /// <c>IsDeleted = 0</c>, so soft-deleted cabinets do not participate in the active constraint.
    /// </summary>
    protected virtual async Task EnsureNameAvailableAsync(string name)
    {
        var existing = await _repository.FindByNameAsync(name);
        if (existing != null)
        {
            throw new BusinessException(DocumentAIErrorCodes.Cabinet.NameAlreadyExists)
                .WithData("Name", name);
        }
    }
}
