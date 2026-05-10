using System.ComponentModel;

namespace Dignite.Paperbase.Documents.Pipelines.RelationDiscovery;

/// <summary>
/// Issue #115 L3: <see cref="RelationInferenceAgent"/> 的 LLM 结构化输出。
///
/// <para>
/// 字段含义直接喂给 <c>ChatClientAgent.RunAsync&lt;RelationInferenceResult&gt;()</c>，
/// 由 MAF 框架解析为 JSON schema 提示给 LLM。<see cref="DescriptionAttribute"/> 是给 LLM 看的字段说明，
/// 影响输出准确度——慎改措辞。
/// </para>
/// </summary>
public class RelationInferenceResult
{
    [Description("Whether the two documents reference the same business event/entity (same contract, same project, same parties, same case). Set true ONLY when there is concrete evidence in BOTH documents pointing to the same thing.")]
    public bool IsRelated { get; set; }

    [Description("Concise (under 100 chars) human-readable explanation of the relationship. Examples: '该发票对应订单 PO-2024-001 的货款', '本协议是主合同 HT-2024-001 的补充条款'. Empty when IsRelated is false.")]
    public string? Description { get; set; }

    [Description("Confidence in the inference, in [0, 1]. 0 = completely unrelated; 1 = certain. Below 0.7 is treated as 'not confident enough' by the discovery service and the relation is dropped.")]
    public double Confidence { get; set; }
}
