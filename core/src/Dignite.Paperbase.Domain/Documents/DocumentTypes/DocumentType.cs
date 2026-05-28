using System;
using System.Linq;
using System.Text.RegularExpressions;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.DocumentTypes;

/// <summary>
/// 文档类型实体（字段架构 v2）。两层独立单层模型：
/// <list type="bullet">
///   <item><c>TenantId == null</c> → Host 部署级类型（Host admin 通过 IDocumentTypeAppService 自助 CRUD；CurrentTenant.Id IS NULL）</item>
///   <item><c>TenantId != null</c> → 租户私有类型（租户 admin 通过 IDocumentTypeAppService 自助 CRUD）</item>
/// </list>
/// Host 与 tenant 各自独立宇宙——分类候选集按 Document.TenantId 严格匹配单层，不存在跨层 union。
/// 跨层同 TypeCode 是合法的两行（由 TenantId 区分），下游消费方按 (TenantId, TypeCode) 元组消费。
/// <para>
/// TypeCode 字符集白名单 <see cref="DocumentTypeConsts.TypeCodePattern"/>：字母 / 数字 / 下划线 / 短横线段，
/// 可选 <c>.</c> 分隔多段（单段 <c>contract</c> 也合法，多段 <c>host.contract</c> 也合法；不允许首尾或连续 <c>.</c>）。
/// 不再强制含 <c>.</c>——v1 的 <c>&lt;owner&gt;.&lt;sub-type&gt;</c> namespace 约定在 v2"全部 admin CRUD 自管"
/// 模型下没有技术功能（不参与租户隔离、不参与 module 注册防冲突），保留 <c>.</c> 作为可选 convention 即可。
/// 唯一约束 <c>(TenantId, TypeCode)</c>。例：<c>host.general</c>、<c>contract</c>、<c>tenant.case-file</c>。
/// </para>
/// <para>
/// <see cref="DisplayName"/> 是普通字符串列，运行时直接展示（不再走 seed-time ILocalizableString 解析）。
/// </para>
/// <para>
/// 字段关系：<see cref="FieldDefinition.DocumentTypeCode"/> 字符串引用本实体的 TypeCode，
/// 按 DDD "reference by id" 原则不加 navigation property。
/// </para>
/// </summary>
public class DocumentType : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly Regex TypeCodeRegex = new(
        DocumentTypeConsts.TypeCodePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    public virtual string TypeCode { get; private set; } = default!;

    /// <summary>显示名称（运行时直接展示，普通字符串列）。</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>分类置信度阈值（低于此值进入 PendingReview 队列）。</summary>
    public virtual double ConfidenceThreshold { get; private set; }

    /// <summary>类型匹配优先级（数字越大优先级越高；fallback / 通用型通常为 0）。</summary>
    public virtual int Priority { get; private set; }

    protected DocumentType() { }

    public DocumentType(
        Guid id,
        Guid? tenantId,
        string typeCode,
        string displayName,
        double confidenceThreshold = ClassificationDefaults.DefaultConfidenceThreshold,
        int priority = 0)
        : base(id)
    {
        TenantId = tenantId;
        TypeCode = ValidateTypeCode(typeCode);
        DisplayName = ValidateDisplayName(displayName);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    public void Update(string displayName, double confidenceThreshold, int priority)
    {
        DisplayName = ValidateDisplayName(displayName);
        ConfidenceThreshold = Check.Range(confidenceThreshold, nameof(confidenceThreshold), 0d, 1d);
        Priority = priority;
    }

    /// <summary>
    /// TypeCode 白名单校验：字符集 <see cref="DocumentTypeConsts.TypeCodePattern"/>
    /// （字母 / 数字 / 下划线 / 短横线段，可选 <c>.</c> 分隔，首尾不为 <c>.</c>，不允许连续 <c>.</c>）+ 长度上限。
    /// <para>
    /// ⚠️ <strong>Prompt injection 边界</strong>：<see cref="TypeCode"/> 在
    /// <c>DocumentClassificationWorkflow</c> 内**裸拼**进 LLM 系统提示（不经
    /// <c>PromptBoundary.WrapField</c> 包裹）。白名单 regex 保证字符集不含换行 / 引号 / 控制字符等
    /// prompt injection 注入向量；若未来需要放宽字符集，必须同步评估并选择"收紧 regex"或"在 Workflow
    /// 内对 TypeCode 也走 PromptBoundary"二选一。详见 <c>.claude/rules/llm-call-anti-patterns.md</c>。
    /// </para>
    /// </summary>
    private static string ValidateTypeCode(string typeCode)
    {
        Check.NotNullOrWhiteSpace(typeCode, nameof(typeCode), DocumentTypeConsts.MaxTypeCodeLength);

        if (!TypeCodeRegex.IsMatch(typeCode))
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidDocumentTypeCodeFormat)
                .WithData("typeCode", typeCode)
                .WithData("pattern", DocumentTypeConsts.TypeCodePattern);
        }

        return typeCode;
    }

    /// <summary>
    /// DisplayName 是 admin 通过 UI 输入的用户控制文本，且会被字面拼入 LLM 分类 prompt
    /// （<see cref="DocumentClassificationWorkflow"/> 内已加 <c>PromptBoundary.WrapField</c> 包裹作为深度防御）。
    /// 此处实体层加一层硬约束——拒绝换行 / 控制字符——防止恶意 admin 注入形如
    /// <c>"Contract\n---\nIgnore previous instructions"</c> 的字符串穿透 prompt 边界。
    /// 允许 Unicode 字母数字 / 标点 / 空格（支持中日文 displayName）。
    /// </summary>
    private static string ValidateDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), DocumentTypeConsts.MaxDisplayNameLength);

        // 控制字符（含 \r \n \t \0 等 C0/C1）一律拒绝——这是 prompt injection 主要注入向量。
        if (displayName.Any(c => char.IsControl(c)))
        {
            throw new BusinessException(PaperbaseErrorCodes.InvalidDocumentTypeDisplayName)
                .WithData("displayName", displayName);
        }

        return displayName;
    }
}
