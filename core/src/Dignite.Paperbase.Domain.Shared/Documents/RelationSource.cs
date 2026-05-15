namespace Dignite.Paperbase.Documents;

public enum RelationSource
{
    /// <summary>用户手动创建的关系</summary>
    Manual = 1,

    /// <summary>AI 推断建议、待人工确认的关系</summary>
    AiSuggested = 2,
}
