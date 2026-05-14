namespace Dignite.Paperbase.Contracts.Extraction;

/// <summary>
/// 合同字段提取的结构化输出契约。
/// 由 ChatClientAgent 通过 ResponseFormat = JsonSchema&lt;ContractExtractionResult&gt; 直接反序列化。
/// </summary>
public class ContractExtractionResult
{
    /// <summary>契約タイトル（例: 業務委託基本契約書）</summary>
    public string? Title { get; set; }

    /// <summary>契約番号（例: 2024-001）</summary>
    public string? ContractNumber { get; set; }

    /// <summary>甲（委託者）の名称（例: 株式会社ABC）</summary>
    public string? PartyAName { get; set; }

    /// <summary>乙（受託者）の名称（例: 株式会社XYZ）</summary>
    public string? PartyBName { get; set; }

    /// <summary>契約締結日（ISO 8601: yyyy-MM-dd）</summary>
    public string? SignedDate { get; set; }

    /// <summary>契約開始日（ISO 8601: yyyy-MM-dd）</summary>
    public string? EffectiveDate { get; set; }

    /// <summary>契約終了日（ISO 8601: yyyy-MM-dd）</summary>
    public string? ExpirationDate { get; set; }

    /// <summary>契約金額（数値のみ、単位・カンマ不要）</summary>
    public decimal? TotalAmount { get; set; }

    /// <summary>通貨コード（例: JPY）</summary>
    public string? Currency { get; set; }

    /// <summary>自動更新の有無</summary>
    public bool? AutoRenewal { get; set; }

    /// <summary>解除通知期間（日数、整数）</summary>
    public int? TerminationNoticeDays { get; set; }

    /// <summary>準拠法（例: 日本法）</summary>
    public string? GoverningLaw { get; set; }

    /// <summary>契約概要（一文程度）</summary>
    public string? Summary { get; set; }

    /// <summary>抽出結果全体の信頼度（0.0-1.0）。判断できない場合は null。</summary>
    public double? ExtractionConfidence { get; set; }
}
