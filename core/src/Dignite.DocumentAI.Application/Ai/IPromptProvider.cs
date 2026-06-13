namespace Dignite.DocumentAI.Ai;

/// <summary>
/// Provides system prompts for MAF workflows.
/// Implementations may return different templates by language, tenant, or business scenario. Tests
/// inject substitute implementations to isolate LLM calls.
/// </summary>
public interface IPromptProvider
{
    PromptTemplate GetClassificationPrompt(string language);

    /// <summary>
    /// Title generation prompt. It <b>does not</b> accept a language parameter because the title
    /// strategy is "follow the document language"; the prompt includes "Respond in the same language
    /// as the document." It is not affected by <c>DocumentAIBehaviorOptions.DefaultLanguage</c>.
    /// </summary>
    PromptTemplate GetTitleGenerationPrompt();
}
