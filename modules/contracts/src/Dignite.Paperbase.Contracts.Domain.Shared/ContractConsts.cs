namespace Dignite.Paperbase.Contracts;

public static class ContractConsts
{
    public static int MaxDocumentTypeCodeLength { get; set; } = 128;

    public static int MaxTitleLength { get; set; } = 256;

    public static int MaxContractNumberLength { get; set; } = 64;

    public static int MaxPartyNameLength { get; set; } = 256;

    public static int MaxCurrencyLength { get; set; } = 8;

    public static int MaxGoverningLawLength { get; set; } = 128;

    public static int MaxSummaryLength { get; set; } = 2000;
}
