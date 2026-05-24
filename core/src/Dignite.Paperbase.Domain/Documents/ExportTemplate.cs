using System;
using System.Collections.Generic;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 导出模板聚合根。两层独立单层模型（对齐 <see cref="DocumentType"/> / <see cref="FieldDefinition"/>）：
/// <list type="bullet">
///   <item><c>TenantId == null</c> → Host 模板（Host admin 通过 IExportTemplateAppService 自助 CRUD）</item>
///   <item><c>TenantId != null</c> → 租户模板（租户 admin 自助 CRUD）</item>
/// </list>
/// 唯一约束 <c>(TenantId, Name)</c>；跨层同名是合法的两行。
/// <para>
/// 导出引擎是通道的"文件出口"——只做字段投影 + 重命名 + 排序 + 序列化，<strong>零业务转换</strong>
/// （不算税 / 不做科目映射 / 不做汇率换算）。业务格式靠租户拼模板组合，Paperbase 不预置行业模板。
/// </para>
/// </summary>
public class ExportTemplate : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual string Name { get; private set; } = default!;

    public virtual ExportFormat Format { get; private set; }

    /// <summary>限定适用的文档类型（引用 <see cref="DocumentType.TypeCode"/>）；null = 不限类型。存在性由 AppService 校验。</summary>
    public virtual string? DocumentTypeCode { get; private set; }

    /// <summary>列定义（按 Order 升序）。整体存 native <c>json</c> 列，无单列查询需求，不开子表。</summary>
    public virtual IReadOnlyList<ExportColumn> Columns { get; private set; } = new List<ExportColumn>();

    protected ExportTemplate() { }

    public ExportTemplate(
        Guid id,
        Guid? tenantId,
        string name,
        ExportFormat format,
        string? documentTypeCode,
        IReadOnlyList<ExportColumn> columns)
        : base(id)
    {
        TenantId = tenantId;
        Name = ValidateName(name);
        Format = format;
        DocumentTypeCode = NormalizeDocumentTypeCode(documentTypeCode);
        SetColumns(columns);
    }

    public void Update(
        string name,
        ExportFormat format,
        string? documentTypeCode,
        IReadOnlyList<ExportColumn> columns)
    {
        Name = ValidateName(name);
        Format = format;
        DocumentTypeCode = NormalizeDocumentTypeCode(documentTypeCode);
        SetColumns(columns);
    }

    private void SetColumns(IReadOnlyList<ExportColumn> columns)
    {
        Check.NotNull(columns, nameof(columns));

        if (columns.Count == 0)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateRequiresColumn);
        }

        if (columns.Count > ExportTemplateConsts.MaxColumnCount)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateTooManyColumns)
                .WithData("count", columns.Count)
                .WithData("max", ExportTemplateConsts.MaxColumnCount);
        }

        var duplicate = columns
            .GroupBy(c => c.ColumnName, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new BusinessException(PaperbaseErrorCodes.ExportTemplateDuplicateColumnName)
                .WithData("columnName", duplicate.Key);
        }

        Columns = columns.OrderBy(c => c.Order).ToList();
    }

    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), ExportTemplateConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidExportTemplateName)
                .WithData("name", name);
        }

        return name;
    }

    private static string? NormalizeDocumentTypeCode(string? documentTypeCode)
        => string.IsNullOrWhiteSpace(documentTypeCode) ? null : documentTypeCode.Trim();
}
