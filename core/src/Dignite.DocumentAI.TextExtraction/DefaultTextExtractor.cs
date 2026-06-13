using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ocr;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace Dignite.DocumentAI.TextExtraction;

public class DefaultTextExtractor : ITextExtractor, ITransientDependency
{
    private readonly IOcrProvider _ocrProvider;
    private readonly IMarkdownTextProvider _markdownProvider;
    private readonly DocumentAIOcrOptions _ocrOptions;

    public ILogger<DefaultTextExtractor> Logger { get; set; } = NullLogger<DefaultTextExtractor>.Instance;

    public DefaultTextExtractor(
        IOcrProvider ocrProvider,
        IMarkdownTextProvider markdownProvider,
        IOptions<DocumentAIOcrOptions> ocrOptions)
    {
        _ocrProvider = ocrProvider;
        _markdownProvider = markdownProvider;
        _ocrOptions = ocrOptions.Value;
    }

    public virtual async Task<TextExtractionResult> ExtractAsync(
        Stream fileStream,
        TextExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        if (IsImageFormat(context.FileExtension))
        {
            return await ExtractByOcrAsync(fileStream, context, cancellationToken);
        }

        // Use one MemoryStream across the Markdown provider and possible OCR fallback, both of which
        // may read the stream. Input streams from blob storage may be non-seekable, and parsers inside
        // ElBruno such as PdfPig/OpenXml require a seekable stream, so buffering is required. Known
        // limit: very large files, such as GB-scale scanned PDFs, stay fully in memory; switch to a
        // temporary file path if needed.
        using var seekable = new MemoryStream();
        await fileStream.CopyToAsync(seekable, cancellationToken);
        seekable.Position = 0;

        var md = await _markdownProvider.ExtractAsync(seekable, context, cancellationToken);

        if (!HasMeaningfulText(md.Markdown) && IsPdfExtension(context.FileExtension))
        {
            Logger.LogDebug("Markdown provider produced no meaningful text for PDF; falling back to OCR.");
            seekable.Position = 0;
            return await ExtractByOcrAsync(seekable, context, cancellationToken);
        }

        Logger.LogDebug("Markdown extraction completed using {Provider}", _markdownProvider.GetType().Name);
        return md;
    }

    protected virtual async Task<TextExtractionResult> ExtractByOcrAsync(
        Stream fileStream,
        TextExtractionContext ctx,
        CancellationToken cancellationToken)
    {
        Stream seekable;
        bool ownsStream;
        if (fileStream is MemoryStream { CanSeek: true })
        {
            seekable = fileStream;
            ownsStream = false;
        }
        else
        {
            var ms = new MemoryStream();
            await fileStream.CopyToAsync(ms, cancellationToken);
            seekable = ms;
            ownsStream = true;
        }

        try
        {
            var languageHints = ctx.LanguageHints?.Count > 0
                ? ctx.LanguageHints
                : (IList<string>)_ocrOptions.DefaultLanguageHints;

            seekable.Position = 0;
            var result = await _ocrProvider.RecognizeAsync(seekable, new OcrOptions
            {
                ContentType = ctx.ContentType ?? string.Empty,
                LanguageHints = languageHints
            }, cancellationToken);

            Logger.LogDebug("OCR completed using {Provider}.", result.ProviderName ?? _ocrProvider.GetType().Name);

            return new TextExtractionResult
            {
                Markdown = result.Markdown,
                DetectedLanguage = result.DetectedLanguage,
                UsedOcr = true,
                ProviderName = result.ProviderName,
                IsComplete = result.IsComplete,
                IncompleteReason = result.IncompleteReason,
                NativePayload = MapNativePayload(result)
            };
        }
        finally
        {
            if (ownsStream)
            {
                await seekable.DisposeAsync();
            }
        }
    }

    // Cross-contract mapping from OcrResult native payload to TextExtractionResult. The Ocr project
    // does not reference Abstractions; flat fields avoid creating the same wrapper class twice and are
    // mapped in the orchestration layer like Markdown / DetectedLanguage.
    private NativePayload? MapNativePayload(OcrResult result)
    {
        if (result.NativePayloadContent is not { Length: > 0 } content)
        {
            // No payload, usually because the provider has no spatial model. This is a normal path.
            return null;
        }

        if (string.IsNullOrEmpty(result.NativePayloadContentType) || string.IsNullOrEmpty(result.NativePayloadSchemaName))
        {
            // Content exists but ContentType / SchemaName is missing: the provider half-filled the
            // flat fields and archival cannot label the schema. Drop it, but log a warning instead of
            // swallowing silently, otherwise spatial signals disappear without evidence.
            Logger.LogWarning(
                "OCR provider {Provider} produced {Bytes} bytes of native payload but left ContentType/SchemaName unset; dropping it.",
                result.ProviderName, content.Length);
            return null;
        }

        return new NativePayload(content, result.NativePayloadContentType, result.NativePayloadSchemaName);
    }

    protected virtual bool IsImageFormat(string? fileExtension)
    {
        if (string.IsNullOrWhiteSpace(fileExtension)) return false;
        var ext = fileExtension.ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".tiff" or ".tif" or ".bmp" or ".webp" or ".gif";
    }

    protected virtual bool HasMeaningfulText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        return markdown.Any(c => char.IsLetter(c) || char.IsDigit(c));
    }

    protected virtual bool IsPdfExtension(string? fileExtension)
        => string.Equals(fileExtension, ".pdf", StringComparison.OrdinalIgnoreCase);
}
