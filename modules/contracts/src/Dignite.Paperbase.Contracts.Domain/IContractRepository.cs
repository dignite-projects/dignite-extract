using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;

namespace Dignite.Paperbase.Contracts;

public interface IContractRepository : IRepository<Contract, Guid>
{
    Task<Contract?> FindByDocumentIdAsync(Guid documentId);

    /// <summary>
    /// Issue #115 L2: 查询当前租户内持有指定合同编号的所有合同。
    /// 同一编号理论上唯一，但保留 List 返回类型——AI 抽取出错或人工录入重复时
    /// 不希望调用方收到 InvalidOperationException 而无法上下文判断。
    /// 调用方负责按 (Tenant, ContractNumber) 唯一性的业务约定处理多结果。
    /// </summary>
    Task<List<Contract>> FindByContractNumberAsync(
        string contractNumber,
        CancellationToken cancellationToken = default);
}
