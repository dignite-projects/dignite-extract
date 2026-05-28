using System.ComponentModel.DataAnnotations;
using Volo.Abp.Validation;

namespace Dignite.Paperbase.Documents.Cabinets;

public class UpdateCabinetDto
{
    [Required]
    [DynamicStringLength(typeof(CabinetConsts), nameof(CabinetConsts.MaxDisplayNameLength))]
    public string DisplayName { get; set; } = default!;
}
