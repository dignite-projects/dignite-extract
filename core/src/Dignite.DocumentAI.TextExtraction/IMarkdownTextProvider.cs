using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;

namespace Dignite.DocumentAI.TextExtraction;

/// <summary>
/// Provider abstraction for digital documents (PDF / Word / HTML / plain text / CSV / RTF / EPUB,
/// etc.) to Markdown. Handles files with a digital text layer and complements <c>IOcrProvider</c>,
/// which handles images / scans. The consumer is fixed to <c>DefaultTextExtractor</c>; implementations
/// are provided by independent provider modules, such as
/// <c>Dignite.DocumentAI.TextExtraction.ElBrunoMarkItDown</c>. The host selects one implementation
/// through <c>DependsOn</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Markdown-first contract</b>: implementations <b>must</b> populate extraction output into
/// <see cref="TextExtractionResult.Markdown"/> and <b>must not</b> fall back to plain text or add a
/// parallel plain-text field. Any "plain text fallback" is a design violation.
/// </para>
/// <para>
/// <b>For structured documents</b> (titled DOCX / well-laid-out PDF / CSV table), Markdown headings,
/// tables, and lists are real signals for downstream vectorization chunking (structure-aware) and LLM
/// understanding, so use them fully. <b>For unstructured content</b> (bare txt / single-paragraph
/// RTF), Markdown is a <b>container name</b>, not a signal gain; the contract is kept only so
/// downstream consumers handle one format.
/// </para>
/// </remarks>
public interface IMarkdownTextProvider
{
    Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default);
}
