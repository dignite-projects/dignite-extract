using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Paperbase.Documents.Fields;

/// <summary>
/// 字段定义管理（字段架构 v2 统一 API）。
/// <para>
/// 所有路径强制当前租户上下文（CurrentTenant.Id 绑定）；不允许跨租户读写。
/// 按所属层精确匹配单层、不跨层混合：Host admin（CurrentTenant.Id IS NULL）增删改 Host 字段
/// （TenantId IS NULL 行），租户 admin 增删改自己租户字段。两层都通过此 AppService CRUD 自管——
/// 不存在 seed contributor / Module 启动注册路径。
/// </para>
/// </summary>
public interface IFieldDefinitionAppService : IApplicationService
{
    Task<List<FieldDefinitionDto>> GetByDocumentTypeAsync(string documentTypeCode);

    /// <summary>
    /// 当前租户在指定文档类型下已软删除的字段定义列表（回收站视图）。
    /// </summary>
    Task<List<FieldDefinitionDto>> GetDeletedByDocumentTypeAsync(string documentTypeCode);

    Task<FieldDefinitionDto> CreateAsync(CreateFieldDefinitionDto input);

    Task<FieldDefinitionDto> UpdateAsync(Guid id, UpdateFieldDefinitionDto input);

    Task DeleteAsync(Guid id);

    /// <summary>
    /// 恢复单个软删除的字段定义。要求父 <see cref="DocumentType"/>（同 TenantId + TypeCode）存在且活跃；
    /// 父类型缺失或仍处于已删除状态时抛 <see cref="PaperbaseErrorCodes.FieldDefinitionParentTypeMissing"/>；
    /// 同名活跃字段已存在则抛 <see cref="PaperbaseErrorCodes.FieldDefinitionRestoreConflict"/>。
    /// 批量恢复请走 <see cref="IDocumentTypeAppService.RestoreAsync"/> 的级联路径。
    /// </summary>
    Task<FieldDefinitionDto> RestoreAsync(Guid id);
}
