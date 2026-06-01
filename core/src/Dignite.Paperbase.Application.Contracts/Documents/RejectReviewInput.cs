using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents;

public class RejectReviewInput
{
    [DynamicStringLength(typeof(DocumentConsts), nameof(DocumentConsts.MaxClassificationReasonLength))]
    public string? Reason { get; set; }
}
