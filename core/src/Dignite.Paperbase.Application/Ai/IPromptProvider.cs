namespace Dignite.Paperbase.Ai;

/// <summary>
/// 为各 MAF Workflow 提供系统提示词。
/// 实现侧可按语言、租户或业务场景返回不同模板；
/// 测试侧注入替代实现以隔离 LLM 调用。
/// </summary>
public interface IPromptProvider
{
    PromptTemplate GetClassificationPrompt(string language);

    PromptTemplate GetQaPrompt(string language);

    PromptTemplate GetRerankPrompt(string language);

    PromptTemplate GetConversationTitlePrompt(string language);

    PromptTemplate GetTitleGenerationPrompt(string language);
}
