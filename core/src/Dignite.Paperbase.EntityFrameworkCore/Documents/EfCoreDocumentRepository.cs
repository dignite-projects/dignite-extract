using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Paperbase;
using Dignite.Paperbase.Documents;
using Dignite.Paperbase.Documents.Fields;
using Dignite.Paperbase.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.Data;
using Volo.Abp.Domain.Repositories.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;

namespace Dignite.Paperbase.Documents;

public class EfCoreDocumentRepository
    : EfCoreRepository<PaperbaseDbContext, Document, Guid>, IDocumentRepository
{
    public EfCoreDocumentRepository(
        IDbContextProvider<PaperbaseDbContext> dbContextProvider)
        : base(dbContextProvider)
    {
    }

    public virtual async Task<Document?> FindByBlobNameAsync(
        string blobName,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .FirstOrDefaultAsync(
                d => d.OriginalFileBlobName == blobName,
                GetCancellationToken(cancellationToken));
    }

    public virtual async Task<Document?> FindByContentHashAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        using (DataFilter.Disable<ISoftDelete>())
        {
            var dbSet = await GetDbSetAsync();
            return await dbSet
                .FirstOrDefaultAsync(
                    d => d.FileOrigin.ContentHash == contentHash,
                    GetCancellationToken(cancellationToken));
        }
    }

    public override async Task<IQueryable<Document>> WithDetailsAsync()
    {
        return (await GetQueryableAsync()).IncludeDetails();
    }

    public virtual async Task HardDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextAsync();
        await dbContext.Set<Document>()
            .IgnoreQueryFilters()
            .Where(d => d.Id == id)
            .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task<List<Guid>> GetFieldMatchedIdsAsync(
        string documentTypeCode,
        IReadOnlyList<DocumentFieldQuery> fieldQueries,
        CancellationToken cancellationToken = default)
    {
        // 调用层（DocumentAppService.GetListAsync）仅在有字段过滤器时调用，且已校验 documentTypeCode 必填、
        // 字段数量 / 长度 / 至少一个值（DTO + AppService 层，loud AbpValidationException）。此处防御空入参。
        if (fieldQueries is not { Count: > 0 })
        {
            return new List<Guid>();
        }

        var dbSet = await GetDbSetAsync();

        // 字段值过滤从 Documents 聚合根起手——租户（IMultiTenant）+ 软删（ISoftDelete）全局过滤器按 ambient 状态
        // 自动施加（Issue #206：不再禁用过滤器、不手写 TenantId 谓词）。documentTypeCode 锚定单一类型
        // （字段值离开类型无确定含义）。每个字段过滤编译成一个 ExtractedFieldValues.Any（EXISTS），多字段之间
        // AND（结构化检索惯例：不同字段互相收窄）。普通列比较（= / 范围），跨任意关系型数据库可移植——
        // 不再依赖 SQL Server JSON_VALUE / TRY_CONVERT / raw SQL，注入面归零。
        var query = dbSet.Where(d => d.DocumentTypeCode == documentTypeCode);

        foreach (var fieldQuery in fieldQueries)
        {
            query = ApplyFieldValueFilter(query, documentTypeCode, fieldQuery);
        }

        return await query
            .AsNoTracking()
            .Select(d => d.Id)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    /// <summary>
    /// 把单字段值查询编译成一个对 <see cref="Document.ExtractedFieldValues"/> 的 <c>Any</c>（EXISTS）谓词，
    /// 按 <see cref="FieldDataType"/> 分派到对应类型化列做普通比较：
    /// <list type="bullet">
    ///   <item><c>String</c> / <c>Boolean</c>：仅等值（红线：永不 LIKE）；传区间 → 抛
    ///   <see cref="PaperbaseErrorCodes.FieldTypeDoesNotSupportRange"/>（给 AI 客户端可纠正信号）。</item>
    ///   <item><c>Integer</c> / <c>Decimal</c> / <c>Date</c> / <c>DateTime</c>：等值或区间（含界）。
    ///   入参无法解析为声明类型 → 抛 <see cref="PaperbaseErrorCodes.InvalidExtractedFieldValue"/>（loud，不静默空）。</item>
    /// </list>
    /// 等值统一表达为退化区间 <c>[v, v]</c>，与区间共用同一谓词形，消除等值 / 区间分支重复。
    /// </summary>
    private static IQueryable<Document> ApplyFieldValueFilter(
        IQueryable<Document> query,
        string documentTypeCode,
        DocumentFieldQuery fieldQuery)
    {
        var name = fieldQuery.FieldName;

        // fail-closed：等值 / 区间至少给其一。全空是残缺查询——loud fail（与 DocumentFieldQuery 契约一致），
        // 绝不退化成「该类型全捞」。调用层 DTO 已校验，此处是直连仓储的纵深防御。
        if (fieldQuery.FieldValue == null && fieldQuery.FieldValueMin == null && fieldQuery.FieldValueMax == null)
        {
            throw InvalidValue(name, documentTypeCode, fieldQuery.FieldDataType);
        }

        var isRange = fieldQuery.FieldValue == null
            && (fieldQuery.FieldValueMin != null || fieldQuery.FieldValueMax != null);

        switch (fieldQuery.FieldDataType)
        {
            case FieldDataType.String:
                if (isRange)
                {
                    throw RangeNotSupported(name, fieldQuery.FieldDataType);
                }
                var stringValue = fieldQuery.FieldValue!;
                return query.Where(d => d.ExtractedFieldValues
                    .Any(f => f.Name == name && f.StringValue == stringValue));

            case FieldDataType.Boolean:
                if (isRange)
                {
                    throw RangeNotSupported(name, fieldQuery.FieldDataType);
                }
                if (!bool.TryParse(fieldQuery.FieldValue, out var boolValue))
                {
                    throw InvalidValue(name, documentTypeCode, fieldQuery.FieldDataType);
                }
                return query.Where(d => d.ExtractedFieldValues
                    .Any(f => f.Name == name && f.BooleanValue == boolValue));

            case FieldDataType.Integer:
            {
                var (min, max) = ParseRange(fieldQuery, documentTypeCode, ParseLong);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.Name == name
                    && (min == null || f.IntegerValue >= min)
                    && (max == null || f.IntegerValue <= max)));
            }

            case FieldDataType.Decimal:
            {
                var (min, max) = ParseRange(fieldQuery, documentTypeCode, ParseDecimal);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.Name == name
                    && (min == null || f.DecimalValue >= min)
                    && (max == null || f.DecimalValue <= max)));
            }

            case FieldDataType.Date:
            {
                var (min, max) = ParseRange(fieldQuery, documentTypeCode, ParseDate);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.Name == name
                    && (min == null || f.DateValue >= min)
                    && (max == null || f.DateValue <= max)));
            }

            case FieldDataType.DateTime:
            {
                var (min, max) = ParseRange(fieldQuery, documentTypeCode, ParseDateTime);
                return query.Where(d => d.ExtractedFieldValues.Any(f =>
                    f.Name == name
                    && (min == null || f.DateTimeValue >= min)
                    && (max == null || f.DateTimeValue <= max)));
            }

            default:
                throw InvalidValue(name, documentTypeCode, fieldQuery.FieldDataType);
        }
    }

    /// <summary>
    /// 把字段查询解析成类型化的 <c>(min, max)</c> 闭区间界：等值退化为 <c>[v, v]</c>；区间取 min / max
    /// （任一可空）。任一入参解析失败抛 <see cref="PaperbaseErrorCodes.InvalidExtractedFieldValue"/>（loud）。
    /// </summary>
    private static (T? Min, T? Max) ParseRange<T>(
        DocumentFieldQuery fieldQuery, string documentTypeCode, Func<string, T?> parse)
        where T : struct
    {
        if (fieldQuery.FieldValue != null)
        {
            var value = parse(fieldQuery.FieldValue)
                ?? throw InvalidValue(fieldQuery.FieldName, documentTypeCode, fieldQuery.FieldDataType);
            return (value, value);
        }

        T? min = null;
        T? max = null;
        if (fieldQuery.FieldValueMin != null)
        {
            min = parse(fieldQuery.FieldValueMin)
                ?? throw InvalidValue(fieldQuery.FieldName, documentTypeCode, fieldQuery.FieldDataType);
        }
        if (fieldQuery.FieldValueMax != null)
        {
            max = parse(fieldQuery.FieldValueMax)
                ?? throw InvalidValue(fieldQuery.FieldName, documentTypeCode, fieldQuery.FieldDataType);
        }
        return (min, max);
    }

    private static long? ParseLong(string s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? ParseDecimal(string s)
        => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateOnly? ParseDate(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? DateOnly.FromDateTime(v)
            : null;

    // 只认无偏移的 wall-clock ISO 串（与存储侧 datetime2 / DocumentExtractedField.SetValue 一致）。带偏移 / Z 的串
    // 会被 .NET 换算到服务器本地时区、与存储的 wall-clock 语义不一致——判脏入参返回 null（调用方 loud fail）。
    private static DateTime? ParseDateTime(string s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            && v.Kind == DateTimeKind.Unspecified
            ? v
            : null;

    private static BusinessException RangeNotSupported(string fieldName, FieldDataType dataType) =>
        new BusinessException(PaperbaseErrorCodes.FieldTypeDoesNotSupportRange)
            .WithData("FieldName", fieldName)
            .WithData("DataType", dataType.ToString());

    private static BusinessException InvalidValue(string fieldName, string documentTypeCode, FieldDataType dataType) =>
        new BusinessException(PaperbaseErrorCodes.InvalidExtractedFieldValue)
            .WithData("FieldName", fieldName)
            .WithData("DocumentTypeCode", documentTypeCode)
            .WithData("DataType", dataType.ToString());
}
