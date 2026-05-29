using System;
using System.ComponentModel.DataAnnotations;

namespace Dignite.Paperbase.Documents;

public class ConfirmClassificationInput
{
    /// <summary>确认分类的目标文档类型不可变 Id（#207：内部稳定句柄，TypeCode 可由 admin 重命名故不作引用键）。</summary>
    [Required]
    public Guid DocumentTypeId { get; set; }
}
