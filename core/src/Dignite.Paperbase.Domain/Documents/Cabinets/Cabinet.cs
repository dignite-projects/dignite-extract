using System;
using System.Linq;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Paperbase.Documents.Cabinets;

/// <summary>
/// 文件柜实体——人工组织归属维度（#194）。
/// 与 <see cref="DocumentType"/> 正交：DocumentType 答"这是什么"（AI 分类），Cabinet 答"属于哪个组 / 批次"（人工指定）。
/// 一个文档可同时"在法务部柜里" + "类型是合同"。
/// <para>
/// 与 DocumentType 的关键区别——<b>无字符串标识码</b>：DocumentType 的 TypeCode 之所以是字符串，是因为要喂
/// LLM 分类 prompt、被下游业务按 <c>(TenantId, TypeCode)</c> 元组路由、允许跨层同码。Cabinet 三者皆无
/// （#194 约束：正交于<b>内容</b> pipeline，不进出口契约，只用于内部查询 / 筛选 / 分组），故用 Guid 主键
/// + <see cref="Name"/> 层内唯一即可，<see cref="Document.CabinetId"/> 以可空 Guid 外键引用。
/// <para>
/// <b>#265 例外（不破坏 #194 正交）</b>：上传时若操作员留空柜，「AI 兜底选柜」会把本层柜的 <see cref="Name"/> 作为
/// <b>候选编号列表</b>喂给 LLM 选一个（经 <c>PromptBoundary.WrapField</c> 包裹防注入）。这是<b>独立、一次性</b>的
/// 上传时步骤，<b>分类 / 字段抽取 pipeline 仍完全不读不写 <see cref="Document.CabinetId"/></b>——柜没有退化成第二个
/// DocumentType，AI 内容维度与人工组织维度依旧解耦（详见 <c>CabinetSuggestionWorkflow</c>）。
/// </para>
/// </para>
/// </summary>
public class Cabinet : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public virtual Guid? TenantId { get; private set; }

    /// <summary>柜名（运行时直接展示）。唯一约束 <c>(TenantId, Name)</c>，层内不可重名。</summary>
    public virtual string Name { get; private set; } = default!;

    protected Cabinet() { }

    public Cabinet(Guid id, Guid? tenantId, string name)
        : base(id)
    {
        TenantId = tenantId;
        Name = ValidateName(name);
    }

    public void Update(string name)
    {
        Name = ValidateName(name);
    }

    /// <summary>
    /// Name 卫生校验：拒绝控制字符（换行 / 制表符等）。
    /// 不同于 <see cref="DocumentType"/> 同名校验的 prompt injection 边界目的（DocumentType 进 LLM）——
    /// Cabinet 正交于 pipeline 不进 LLM，此处纯为防 UI / CSV 注入的基础卫生。
    /// </summary>
    private static string ValidateName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), CabinetConsts.MaxNameLength);

        if (name.Any(char.IsControl))
        {
            throw new BusinessException(PaperbaseErrorCodes.Cabinet.InvalidName)
                .WithData("name", name);
        }

        return name;
    }
}
