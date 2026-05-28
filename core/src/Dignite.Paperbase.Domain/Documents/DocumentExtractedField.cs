using System;
using System.Globalization;
using System.Text.Json;
using Dignite.Paperbase.Documents.Fields;
using Volo.Abp;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents;

/// <summary>
/// 类型绑定字段的<b>字段值行</b>（字段架构 v2）——<see cref="Document"/> 聚合的 child entity，
/// 字段值查询与持久化的<b>唯一</b> truth source（替代旧的 <c>Document.ExtractedFields</c> JSON 列，Issue #206）。
/// <para>
/// 一行一个字段值。复合主键 <c>(DocumentId, Name)</c> 即字段集自然键——同文档同名字段唯一，
/// 整组重建 / 操作员手改走 reconcile（同名原地更新），不留重复行。值按 <see cref="DataType"/> 落到对应类型化列
/// （<see cref="StringValue"/> / <see cref="IntegerValue"/> / …），让 <c>GetFieldMatchedIdsAsync</c> 用普通列比较
/// （等值 + 范围）跨任意关系型数据库可移植——不再依赖 SQL Server <c>JSON_VALUE</c> / <c>TRY_CONVERT</c> 方言。
/// </para>
/// <para>
/// 隔离约定（CLAUDE.md "## 安全约定" + Issue #206 复核护栏）：
/// <list type="bullet">
///   <item>实现 <see cref="IMultiTenant"/>——child <c>DbSet</c> / navigation 被使用时 ABP 自动追加租户全局过滤，
///   且 <c>TenantId</c> 前缀索引稳定命中；但查询仍从 <see cref="Document"/> 聚合根起手，租户边界权威来源是 Document。</item>
///   <item><b>不</b>实现 <c>ISoftDelete</c>——避免跨实体级联软删的同步负担。软删 Document 时其字段行随聚合根
///   被父过滤器挡在查询之外；硬删 Document 时级联删除字段行。</item>
/// </list>
/// </para>
/// </summary>
public class DocumentExtractedField : Entity, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    public virtual Guid DocumentId { get; private set; }

    /// <summary>冗余存父文档当前 <see cref="Document.DocumentTypeCode"/>，仅用于 <c>(TenantId, DocumentTypeCode, Name)</c> 索引局部性；reconcile 时随聚合根同步。</summary>
    public virtual string DocumentTypeCode { get; private set; } = default!;

    /// <summary>字段名，等同 <c>FieldDefinition.Name</c>（与复合键的 Name 分量一致）。</summary>
    public virtual string Name { get; private set; } = default!;

    public virtual FieldDataType DataType { get; private set; }

    // 类型化值列——按 DataType 取用其一，其余为 null。普通列即可建 B-tree 索引、支持等值 + 范围。
    public virtual string? StringValue { get; private set; }
    public virtual bool? BooleanValue { get; private set; }
    public virtual long? IntegerValue { get; private set; }
    public virtual decimal? DecimalValue { get; private set; }
    public virtual DateOnly? DateValue { get; private set; }
    public virtual DateTime? DateTimeValue { get; private set; }

    protected DocumentExtractedField()
    {
    }

    internal DocumentExtractedField(Guid documentId, Guid? tenantId, string documentTypeCode, DocumentFieldValue value)
    {
        DocumentId = documentId;
        TenantId = tenantId;
        Name = value.Name;
        SetValue(documentTypeCode, value);
    }

    /// <summary>
    /// 原地写入 / 更新值（reconcile 时对同名字段调用）。把规范 JSON（已由 App 层 <c>ExtractedFieldValueValidator</c>
    /// 校验与 <paramref name="value"/>.DataType 对齐）拆进对应类型化列；先清空所有值列再按类型回填，
    /// 保证类型切换（如同名字段在新文档类型下换了 DataType）不残留旧列值。
    /// </summary>
    internal void SetValue(string documentTypeCode, DocumentFieldValue value)
    {
        DocumentTypeCode = documentTypeCode;
        DataType = value.DataType;

        StringValue = null;
        BooleanValue = null;
        IntegerValue = null;
        DecimalValue = null;
        DateValue = null;
        DateTimeValue = null;

        var element = value.Value;
        switch (value.DataType)
        {
            case FieldDataType.String:
                StringValue = element.GetString();
                break;
            case FieldDataType.Integer:
                IntegerValue = element.GetInt64();
                break;
            case FieldDataType.Decimal:
                DecimalValue = element.GetDecimal();
                break;
            case FieldDataType.Boolean:
                BooleanValue = element.GetBoolean();
                break;
            case FieldDataType.Date:
                DateValue = DateOnly.ParseExact(element.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case FieldDataType.DateTime:
                DateTimeValue = DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.DataType, "Unsupported field data type.");
        }
    }

    /// <summary>
    /// 从类型化列重建规范 <see cref="JsonElement"/>——DTO / MCP / REST 出口的 <c>ExtractedFields</c> 字典即时组装时使用
    /// （wire-format 与旧 JSON 列保持兼容）。是 <see cref="SetValue"/> 的逆向，往返一致。
    /// </summary>
    public JsonElement ToJsonElement() => DataType switch
    {
        FieldDataType.String => JsonSerializer.SerializeToElement(StringValue),
        FieldDataType.Integer => JsonSerializer.SerializeToElement(IntegerValue),
        FieldDataType.Decimal => JsonSerializer.SerializeToElement(DecimalValue),
        FieldDataType.Boolean => JsonSerializer.SerializeToElement(BooleanValue),
        FieldDataType.Date => JsonSerializer.SerializeToElement(DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        FieldDataType.DateTime => JsonSerializer.SerializeToElement(DateTimeValue?.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        // 与 SetValue 的 default 分支对称：未知 DataType loud fail，绝不吐 Undefined 毒值（否则组装进 DTO 后
        // 序列化响应会抛 "Cannot write a JsonElement with ValueKind Undefined"，整篇文档读取 500）。
        _ => throw new ArgumentOutOfRangeException(nameof(DataType), DataType, "Unsupported field data type.")
    };

    public override object[] GetKeys() => new object[] { DocumentId, Name };
}
