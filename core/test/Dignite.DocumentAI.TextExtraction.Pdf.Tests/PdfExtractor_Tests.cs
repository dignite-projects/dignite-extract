using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dignite.DocumentAI.Abstractions.TextExtraction;
using Dignite.DocumentAI.Ocr;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

public class PdfExtractor_Tests
{
    private readonly IOcrProvider _ocr = Substitute.For<IOcrProvider>();

    private PdfExtractor CreateExtractor(
        int minImagePixels = 0,
        int maxImagesPerPdf = 50,
        DocumentAIOcrOptions? ocrOptions = null)
        => new(
            _ocr,
            Options.Create(new PdfExtractorOptions
            {
                MinImagePixels = minImagePixels,
                MaxImagesPerPdf = maxImagesPerPdf
            }),
            // Default to empty hints so unrelated tests don't get defaults injected unexpectedly.
            Options.Create(ocrOptions ?? new DocumentAIOcrOptions { DefaultLanguageHints = new List<string>() }));

    private static TextExtractionContext PdfContext()
        => new() { ContentType = "application/pdf", FileExtension = ".pdf" };

    private void StubOcr(string markdown, bool isComplete = true, string? reason = null)
        => _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Returns(new OcrResult
            {
                Markdown = markdown,
                ProviderName = "FakeOcr",
                IsComplete = isComplete,
                IncompleteReason = reason
            });

    [Fact]
    public async Task Inlines_image_transcription_at_its_reading_position()
    {
        StubOcr("BRAVO");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("ALPHA", 760.0), ("CHARLIE", 100.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        var alpha = result.Markdown.IndexOf("ALPHA", StringComparison.Ordinal);
        var bravo = result.Markdown.IndexOf("BRAVO", StringComparison.Ordinal);
        var charlie = result.Markdown.IndexOf("CHARLIE", StringComparison.Ordinal);

        alpha.ShouldBeGreaterThanOrEqualTo(0);
        bravo.ShouldBeGreaterThan(alpha, "the figure transcription must come after the text above it");
        charlie.ShouldBeGreaterThan(bravo, "the figure transcription must come before the text below it");

        // Primary text is the digital text layer; figures used OCR but this is a digital extraction.
        result.UsedOcr.ShouldBeFalse();
        result.ProviderName.ShouldBe(PdfExtractor.ProviderIdentifier);
        result.IsComplete.ShouldBeTrue();
        result.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public async Task Feeds_image_bytes_to_the_ocr_provider()
    {
        StubOcr("FIGURE");

        var png = TinyPng.CreateSolid(48, 48);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        // Exactly one image → exactly one OCR call, with an image content type (transcription only).
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.ContentType.StartsWith("image/", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Marks_incomplete_when_ocr_truncates_a_figure()
    {
        StubOcr("PARTIAL", isComplete: false, reason: "truncated at the token limit");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNullOrEmpty();
        result.Markdown.ShouldContain("PARTIAL");
    }

    [Fact]
    public async Task Returns_empty_and_skips_ocr_when_pdf_has_no_text_layer()
    {
        StubOcr("SHOULD_NOT_BE_CALLED");

        // Scanned / image-only PDF: a page with an image but no digital text layer. PdfExtractor must NOT
        // OCR images here — it returns empty so DefaultTextExtractor's whole-page OCR fallback owns it.
        var png = TinyPng.CreateSolid(80, 80);
        var pdf = PdfFixtures.Build(
            texts: Array.Empty<(string, double)>(),
            images: new[] { (png, new PdfRectangle(50, 300, 400, 700)) });

        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldBeNullOrEmpty();
        result.ProviderName.ShouldBe(PdfExtractor.ProviderIdentifier);
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_decorative_images_below_min_pixels()
    {
        StubOcr("SHOULD_NOT_APPEAR");

        var icon = TinyPng.CreateSolid(8, 8); // 64 px, below the threshold
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (icon, new PdfRectangle(50, 400, 60, 410)) });

        var result = await CreateExtractor(minImagePixels: 1000).ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("Body text");
        result.Markdown.ShouldNotContain("SHOULD_NOT_APPEAR");
        // Decorative images are not figure content → not counted against completeness.
        result.IsComplete.ShouldBeTrue();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caps_images_per_document_and_marks_incomplete()
    {
        StubOcr("FIG");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[]
            {
                (png, new PdfRectangle(50, 500, 200, 650)),
                (png, new PdfRectangle(50, 200, 200, 350))
            });

        var result = await CreateExtractor(maxImagesPerPdf: 1).ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.IsComplete.ShouldBeFalse();
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("cap");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Keeps_the_text_layer_when_an_embedded_image_OCR_throws()
    {
        // A figure's OCR failing (provider timeout / rate-limit / auth / one bad image) must degrade,
        // not nuke the whole digital extraction: the digital text layer is the primary payload, figure
        // OCR is the auxiliary add-on.
        _ocr.RecognizeAsync(Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("vision provider down"));

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("DIGITALBODYTEXT", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        // Must NOT throw — the whole extraction does not fail because one figure's OCR did.
        var result = await CreateExtractor().ExtractAsync(new MemoryStream(pdf), PdfContext());

        result.Markdown.ShouldContain("DIGITALBODYTEXT"); // digital text layer preserved
        result.IsComplete.ShouldBeFalse();                // #268 signal tripped
        result.IncompleteReason.ShouldNotBeNull();
        result.IncompleteReason!.ShouldContain("OCR");
        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Applies_default_language_hints_when_the_context_has_none()
    {
        StubOcr("FIGURE");

        var png = TinyPng.CreateSolid(32, 32);
        var pdf = PdfFixtures.Build(
            texts: new[] { ("Body text", 700.0) },
            images: new[] { (png, new PdfRectangle(50, 400, 200, 550)) });

        // Context carries no hints; the host default {ja,en} must be applied (same as the whole-page path).
        var extractor = CreateExtractor(ocrOptions: new DocumentAIOcrOptions());
        await extractor.ExtractAsync(new MemoryStream(pdf), PdfContext());

        await _ocr.Received(1).RecognizeAsync(
            Arg.Any<Stream>(),
            Arg.Is<OcrOptions>(o => o.LanguageHints.Contains("ja") && o.LanguageHints.Contains("en")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_empty_for_a_non_pdf_stream()
    {
        var notPdf = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("this is not a pdf"));

        var result = await CreateExtractor().ExtractAsync(notPdf, PdfContext());

        // Open failed → empty Markdown so the orchestrator's OCR fallback can try.
        result.Markdown.ShouldBeNullOrEmpty();
        await _ocr.DidNotReceive().RecognizeAsync(
            Arg.Any<Stream>(), Arg.Any<OcrOptions>(), Arg.Any<CancellationToken>());
    }
}
