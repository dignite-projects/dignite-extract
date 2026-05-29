namespace Dignite.Paperbase.Slugging;

public static class SlugSuggestionConsts
{
    /// <summary>
    /// slug 建议输入（人类可读标签）的长度上限。这是 slug 服务**自有的**输入护栏——仅为防止超长文本
    /// 灌入 LLM prompt，与任何实体列长度无关。slug 服务是通用的"标签 → 机器标识"转换器，
    /// 不该借用某个具体实体（FieldDefinition / DocumentType）的 <c>DisplayName</c> 上限。
    /// 取 128，从容覆盖现有两套创建表单的显示名（均 128）。
    /// </summary>
    public static int MaxLabelLength { get; set; } = 128;
}
