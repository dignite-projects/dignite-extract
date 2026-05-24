using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;
using Volo.Abp.Content;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出模板管理 + 执行（per-tenant）。两层独立单层：Host admin 操作 Host 模板（TenantId IS NULL），
/// 租户 admin 操作自己租户模板。导出是通道的"文件出口"——仅字段投影 + 序列化，零业务转换。
/// </summary>
public interface IExportTemplateAppService : IApplicationService
{
    Task<ExportTemplateDto> GetAsync(Guid id);

    Task<List<ExportTemplateDto>> GetListAsync();

    Task<ExportTemplateDto> CreateAsync(CreateExportTemplateDto input);

    Task<ExportTemplateDto> UpdateAsync(Guid id, UpdateExportTemplateDto input);

    Task DeleteAsync(Guid id);

    /// <summary>按模板生成导出文件（CSV / XLSX）。同步返回文件流。</summary>
    Task<IRemoteStreamContent> ExportAsync(ExportDocumentsInput input);
}
