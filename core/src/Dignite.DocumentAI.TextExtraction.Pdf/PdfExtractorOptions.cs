namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Tuning for <see cref="PdfExtractor"/>. Each embedded image becomes one
/// <c>IOcrProvider.RecognizeAsync</c> call (potentially one paid vision-LLM call), so both knobs guard
/// cost and noise. Defaults are conservative; a host can override via <c>Configure&lt;PdfExtractorOptions&gt;</c>.
/// </summary>
public class PdfExtractorOptions
{
    /// <summary>
    /// Hard cap on how many embedded images are transcribed per document. Images beyond the cap are
    /// skipped and the result is marked incomplete (#268) — the digital text layer is still returned, so
    /// an image-heavy PDF degrades gracefully instead of failing or running away on OCR cost.
    /// </summary>
    public int MaxImagesPerPdf { get; set; } = 50;

    /// <summary>
    /// Minimum pixel count (<c>WidthInSamples * HeightInSamples</c>) for an embedded image to be
    /// transcribed. Smaller images (icons, bullets, rules, 1px spacers) are decorative, not figure
    /// content, and are skipped silently (not counted against completeness). Default ≈ 64×64.
    /// </summary>
    public int MinImagePixels { get; set; } = 64 * 64;
}
