using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using DlaTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Layout reconstruction for a single PDF page: segments the text layer into column/region blocks,
/// orders the blocks into reading order, groups each block into visual lines, interleaves
/// embedded-figure transcriptions at their reading position, and (optionally) binds a figure to its
/// nearest caption line.
/// <para>
/// PDF user space has a bottom-left origin (Y increases upward), so "top of the page" = the highest
/// <see cref="PdfRectangle.Top"/>.
/// </para>
/// <para>
/// <b>Column-aware reading order (#310 Phase A).</b> <see cref="RenderPage"/> uses PdfPig's
/// <c>DocumentLayoutAnalysis</c> (<see cref="RecursiveXYCut"/> page segmenter +
/// <see cref="UnsupervisedReadingOrderDetector"/>) to order blocks before linearizing, so a multi-column
/// label/body page no longer splices a left-column label into the middle of a right-column sentence —
/// the failure mode of a flat <c>Top</c>-descending sort. Within a single block the order is still
/// <c>Top</c> descending then <c>Left</c> ascending (see <see cref="Render"/>), which is correct for one
/// column. Reconstructing a table's row of cells into a Markdown table row (so a wrapped cell is not
/// split across other cells) is <b>deferred to Phase B (v0.3.0)</b>; this round is column/reading-order
/// only. The flat path remains as a non-lossy fallback when segmentation finds a single region or faults.
/// </para>
/// <para>
/// Caption association is <b>placement/labeling only</b>: a caption is never sent into the OCR call.
/// </para>
/// </summary>
internal static class PdfReadingOrder
{
    /// <summary>An embedded image with its page placement and the OCR transcription of its content.</summary>
    public readonly record struct Figure(PdfRectangle Bounds, string Transcription);

    /// <summary>A reconstructed visual line of the text layer.</summary>
    public readonly record struct TextLine(PdfRectangle Bounds, string Text);

    // Only bind a nearby text line to a figure when it reads like a figure/table caption. Keeps ordinary
    // adjacent body text from being relocated into the figure block.
    // Latin labels use a word boundary (so "figured"/"tablet" don't match). CJK labels (图/圖/図/表 — zh-Hans,
    // zh-Hant, ja) cannot use \b: an ideograph and a following digit are both word chars, so "图1" has no
    // boundary and the common space-less form would never match. Instead require the CJK label to be
    // followed (after optional space) by a figure number or a colon — matches "图1" / "表2" / "図1：" / "图 3"
    // while rejecting ordinary words like "图书馆" / "表面".
    private static readonly Regex CaptionPattern = new(
        @"^\s*((figure|fig\.?|table|exhibit|chart|diagram|plate)\b|[图圖図表]\s*[0-9０-９:：])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bound caption must be within this many median-line-heights of the figure (squared centroid distance).
    private const double CaptionMaxDistanceLineHeights = 6.0;

    // New paragraph when the baseline-to-baseline pitch between consecutive lines exceeds this many
    // median-line-heights (sits between single spacing ~1.0 and double spacing ~2.0).
    private const double ParagraphPitchLineHeights = 1.6;

    /// <summary>
    /// Reconstructs visual lines from the page's words: clusters words whose vertical ranges overlap into
    /// one line, orders words left-to-right within a line, and returns lines top-to-bottom.
    /// </summary>
    public static IReadOnlyList<TextLine> GroupWordsIntoLines(IReadOnlyList<Word> words)
    {
        var meaningful = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (meaningful.Count == 0)
        {
            return Array.Empty<TextLine>();
        }

        // Process top-to-bottom so a line cluster accretes its words in vertical order.
        var ordered = meaningful.OrderByDescending(w => w.BoundingBox.Top).ToList();

        var clusters = new List<List<Word>>();
        var clusterBounds = new List<PdfRectangle>();      // union of the line's words — the line bbox
        var clusterReference = new List<PdfRectangle>();   // last word added — the overlap reference

        foreach (var word in ordered)
        {
            var placed = false;
            for (var i = 0; i < clusters.Count; i++)
            {
                // Compare against the line's most-recently-added word, NOT the accreted union. A single
                // tall glyph (multi-line bracket / integral / tall CJK punctuation) would otherwise stretch
                // the union's vertical extent and, via the min-height overlap denominator, let a word from
                // the next physical line score >= 0.5 and merge into the wrong line.
                if (VerticalOverlapRatio(clusterReference[i], word.BoundingBox) >= 0.5)
                {
                    clusters[i].Add(word);
                    clusterBounds[i] = Union(clusterBounds[i], word.BoundingBox);
                    clusterReference[i] = word.BoundingBox;
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                clusters.Add(new List<Word> { word });
                clusterBounds.Add(word.BoundingBox);
                clusterReference.Add(word.BoundingBox);
            }
        }

        var lines = new List<TextLine>(clusters.Count);
        for (var i = 0; i < clusters.Count; i++)
        {
            var text = string.Join(
                " ",
                clusters[i].OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
            lines.Add(new TextLine(clusterBounds[i], text));
        }

        return lines
            .OrderByDescending(l => l.Bounds.Top)
            .ThenBy(l => l.Bounds.Left)
            .ToList();
    }

    /// <summary>
    /// Index of the text line nearest the image by RAGFlow-style squared centroid distance
    /// (<c>dis = dx² + dy²</c>), or <c>null</c> when there are no lines.
    /// </summary>
    public static int? FindNearestCaptionIndex(PdfRectangle imageBounds, IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var (ix, iy) = Centroid(imageBounds);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < lines.Count; i++)
        {
            var (lx, ly) = Centroid(lines[i].Bounds);
            var distance = Sq(lx - ix) + Sq(ly - iy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Renders the page to Markdown: text lines folded into gap-delimited paragraphs, figure
    /// transcriptions inlined at their reading position. A figure whose nearest line reads like a caption
    /// (and is close enough) consumes that line and renders it as the figure block's label, so the caption
    /// is not duplicated in the body text.
    /// </summary>
    public static string Render(IReadOnlyList<TextLine> lines, IReadOnlyList<Figure> figures)
    {
        var medianLineHeight = lines.Count > 0
            ? Median(lines.Select(l => l.Bounds.Height))
            : 0.0;

        // 1. Bind captions (placement/labeling only — never sent to OCR). For each figure, bind the
        // NEAREST caption-like, not-yet-consumed line within range — looking past nearer non-caption or
        // already-bound lines so a genuine "Figure N:" caption is not left orphaned in the body text.
        var consumedLines = new HashSet<int>();
        var figureCaptions = new Dictionary<int, string>();
        if (lines.Count > 0)
        {
            var maxDistanceSq = Sq(CaptionMaxDistanceLineHeights * Math.Max(medianLineHeight, 1.0));
            for (var fi = 0; fi < figures.Count; fi++)
            {
                var (fx, fy) = Centroid(figures[fi].Bounds);
                var bestIndex = -1;
                var bestDistance = maxDistanceSq; // only consider lines within range
                for (var i = 0; i < lines.Count; i++)
                {
                    if (consumedLines.Contains(i) || !LooksLikeCaption(lines[i].Text))
                    {
                        continue;
                    }

                    var (lx, ly) = Centroid(lines[i].Bounds);
                    var distance = Sq(lx - fx) + Sq(ly - fy);
                    if (distance <= bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    consumedLines.Add(bestIndex);
                    figureCaptions[fi] = lines[bestIndex].Text;
                }
            }
        }

        // 2. Build the page-item list (unconsumed text lines + figures).
        var items = new List<Item>(lines.Count + figures.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!consumedLines.Contains(i))
            {
                // Inline-escape the digital-text-layer line so a "*"/"`"/"[" in the source cannot open an
                // emphasis / code / link span. The leading BLOCK marker is escaped later, once per folded
                // paragraph (FlushParagraph), since only a paragraph's first line sits at line start. A figure
                // transcription is OCR-provider Markdown (intentional structure) and is never escaped.
                items.Add(Item.ForText(lines[i].Bounds, MarkdownText.EscapeInline(lines[i].Text)));
            }
        }

        for (var fi = 0; fi < figures.Count; fi++)
        {
            figureCaptions.TryGetValue(fi, out var caption);
            items.Add(Item.ForFigure(figures[fi].Bounds, figures[fi].Transcription, caption));
        }

        if (items.Count == 0)
        {
            return string.Empty;
        }

        // 3. Reading order: strictly top-to-bottom (Top descending), then left-to-right for items that
        // genuinely share a row (e.g. a figure beside a line). Items are already line-level (one per
        // visual line) plus figures, so a pure Top sort needs no banding — and banding would wrongly
        // tie a figure that sits just below an indented line into the line's row and reorder by Left.
        var orderedItems = items
            .OrderByDescending(it => it.Bounds.Top)
            .ThenBy(it => it.Bounds.Left)
            .ToList();

        // 4. Fold consecutive text lines into paragraphs; figures are standalone blocks. Split when the
        // baseline-to-baseline pitch (previous Top -> current Top, both descending) exceeds the threshold.
        var paragraphPitch = (medianLineHeight > 0 ? medianLineHeight : 0.0) * ParagraphPitchLineHeights;
        var blocks = new List<string>();
        var paragraph = new List<string>();
        double? previousTop = null;

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                // Lines were inline-escaped at item creation; now neutralize a leading block marker on the
                // folded paragraph (e.g. a contract line "1. Definitions" / "- clause" / "# heading" that
                // would otherwise be re-parsed as a list / heading). The lines are space-joined into one
                // output line, so only its start is a line-start position.
                blocks.Add(MarkdownText.EscapeLineStarts(string.Join(" ", paragraph)));
                paragraph.Clear();
            }

            previousTop = null;
        }

        foreach (var item in orderedItems)
        {
            if (item.IsFigure)
            {
                FlushParagraph();
                // The caption is a digital-text-layer line bound to this figure, so escape it; item.Text is
                // the OCR transcription (already Markdown) and is emitted verbatim.
                blocks.Add(item.Caption is { Length: > 0 } caption
                    ? MarkdownText.EscapeBlockText(caption) + "\n\n" + item.Text
                    : item.Text);
                continue;
            }

            if (previousTop is double top && paragraphPitch > 0 && (top - item.Bounds.Top) > paragraphPitch)
            {
                FlushParagraph();
            }

            paragraph.Add(item.Text);
            previousTop = item.Bounds.Top;
        }

        FlushParagraph();

        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// Renders a page to Markdown with column-aware reading order (#310 Phase A): segments the page's
    /// words into layout blocks (<see cref="RecursiveXYCut"/>), orders the blocks
    /// (<see cref="UnsupervisedReadingOrderDetector"/>, column-wise), then renders each block in reading
    /// order through the single-region <see cref="Render(IReadOnlyList{TextLine}, IReadOnlyList{Figure})"/>
    /// path (line grouping + caption binding + paragraph folding) and joins the blocks. Embedded figures
    /// are merged into the block order by their placement bbox (nearest block by squared centroid
    /// distance), so figure inlining + caption association (#301) keep working on the column-aware order.
    /// <para>
    /// <b>Caption binding is per-block here.</b> Because each block is rendered through its own
    /// <see cref="Render(IReadOnlyList{TextLine}, IReadOnlyList{Figure})"/> pass, a figure binds a caption
    /// only from <i>its own</i> block. In the normal case a figure and its caption sit together in the
    /// same column/block, so this is unchanged from #301; the only degradation is the rare multi-column
    /// case where a figure's caption lands in a different block — the caption then stays as body text and
    /// the figure renders unlabeled. This is non-lossy (no text is dropped) and accepted for Phase A.
    /// </para>
    /// <para>
    /// <b>Non-lossy fallback.</b> When the page is a single region, segmentation faults, or a block layout
    /// would drop a word, this returns the flat single-region rendering instead — layout analysis is a
    /// best-effort quality improvement, never a correctness dependency, so no content is ever lost.
    /// </para>
    /// </summary>
    public static string RenderPage(IReadOnlyList<Word> words, IReadOnlyList<Figure> figures)
    {
        IReadOnlyList<DlaTextBlock> orderedBlocks;
        try
        {
            orderedBlocks = SegmentIntoReadingOrder(words);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any segmenter/reading-order fault degrades to the flat path: the page still renders and no
            // content is lost (the digital text layer is the channel's primary, non-negotiable payload).
            // A cancellation is NOT swallowed — it propagates so a host/job shutdown aborts promptly.
            return Render(GroupWordsIntoLines(words), figures);
        }

        // A single region (or no/one-block segmentation) is exactly the #301 behavior — render the whole
        // page as one column. This is also the path the existing single-column fixtures take.
        if (orderedBlocks.Count <= 1)
        {
            return Render(GroupWordsIntoLines(words), figures);
        }

        // Non-lossy guard: RecursiveXYCut partitions every word into a leaf, so block words should account
        // for every meaningful input word. If a word went missing, discard the block layout and fall back
        // rather than emit a page that silently dropped contract text.
        var inputMeaningful = words.Count(w => !string.IsNullOrWhiteSpace(w.Text));
        var blockMeaningful = orderedBlocks.Sum(
            block => block.TextLines.Sum(line => line.Words.Count(w => !string.IsNullOrWhiteSpace(w.Text))));
        if (blockMeaningful < inputMeaningful)
        {
            return Render(GroupWordsIntoLines(words), figures);
        }

        // Attach each figure to the block it reads with (nearest block centroid) so figure inlining +
        // caption binding run within that block's single-region stream.
        var figuresByBlock = AssignFiguresToBlocks(orderedBlocks, figures);

        var rendered = new List<string>(orderedBlocks.Count);
        for (var b = 0; b < orderedBlocks.Count; b++)
        {
            // Re-group the block's words into visual lines with the existing robust clustering (rather than
            // the segmenter's own lines), so tall-glyph / CJK handling and paragraph folding are unchanged.
            var blockWords = orderedBlocks[b].TextLines.SelectMany(line => line.Words).ToList();
            var blockLines = GroupWordsIntoLines(blockWords);
            var blockFigures = figuresByBlock[b];
            if (blockLines.Count == 0 && blockFigures.Count == 0)
            {
                continue;
            }

            var blockMarkdown = Render(blockLines, blockFigures);
            if (!string.IsNullOrWhiteSpace(blockMarkdown))
            {
                rendered.Add(blockMarkdown);
            }
        }

        return string.Join("\n\n", rendered);
    }

    /// <summary>
    /// Segments a page's words into layout blocks and returns them in reading order. Uses
    /// <see cref="RecursiveXYCut"/> — a top-down "Manhattan layout" segmenter that cuts the page along
    /// whitespace valleys wider than the dominant font metrics, separating columns (vertical gutter cut)
    /// and paragraphs (horizontal line-gap cut). This fits the column/table-structured target corpus
    /// (contracts / invoices) better than the bottom-up Docstrum clustering. Blocks are ordered by
    /// <see cref="UnsupervisedReadingOrderDetector"/> in <see cref="UnsupervisedReadingOrderDetector.SpatialReasoningRules.ColumnWise"/>
    /// mode (read each column top-to-bottom, then move right), which keeps a column's lines contiguous.
    /// Returns an empty list when there are no meaningful words.
    /// </summary>
    private static IReadOnlyList<DlaTextBlock> SegmentIntoReadingOrder(IReadOnlyList<Word> words)
    {
        // The segmenter needs glyph geometry; whitespace-only tokens carry none and only add noise.
        var meaningful = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        if (meaningful.Count == 0)
        {
            return Array.Empty<DlaTextBlock>();
        }

        var blocks = new RecursiveXYCut().GetBlocks(meaningful);
        if (blocks.Count <= 1)
        {
            return blocks;
        }

        return new UnsupervisedReadingOrderDetector(
                T: 5,
                spatialReasoningRule: UnsupervisedReadingOrderDetector.SpatialReasoningRules.ColumnWise,
                useRenderingOrder: false)
            .Get(blocks)
            .ToList();
    }

    /// <summary>
    /// Buckets figures by the block they read with: each figure is assigned to the block whose bounding
    /// box is nearest by squared centroid distance. Every figure lands in exactly one bucket (no figure is
    /// dropped); <paramref name="blocks"/> is expected to be non-empty.
    /// </summary>
    private static IReadOnlyList<List<Figure>> AssignFiguresToBlocks(
        IReadOnlyList<DlaTextBlock> blocks, IReadOnlyList<Figure> figures)
    {
        var byBlock = new List<List<Figure>>(blocks.Count);
        for (var b = 0; b < blocks.Count; b++)
        {
            byBlock.Add(new List<Figure>());
        }

        foreach (var figure in figures)
        {
            var (fx, fy) = Centroid(figure.Bounds);
            var best = 0;
            var bestDistance = double.MaxValue;
            for (var b = 0; b < blocks.Count; b++)
            {
                var (bx, by) = Centroid(blocks[b].BoundingBox);
                var distance = Sq(bx - fx) + Sq(by - fy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = b;
                }
            }

            byBlock[best].Add(figure);
        }

        return byBlock;
    }

    private static bool LooksLikeCaption(string text) => CaptionPattern.IsMatch(text);

    private static double VerticalOverlapRatio(PdfRectangle a, PdfRectangle b)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (overlap <= 0)
        {
            return 0;
        }

        var minHeight = Math.Min(a.Height, b.Height);
        return minHeight <= 0 ? 0 : overlap / minHeight;
    }

    private static PdfRectangle Union(PdfRectangle a, PdfRectangle b)
        => new(
            Math.Min(a.Left, b.Left),
            Math.Min(a.Bottom, b.Bottom),
            Math.Max(a.Right, b.Right),
            Math.Max(a.Top, b.Top));

    private static (double X, double Y) Centroid(PdfRectangle r)
        => ((r.Left + r.Right) / 2.0, (r.Bottom + r.Top) / 2.0);

    private static double Sq(double v) => v * v;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private readonly record struct Item(PdfRectangle Bounds, string Text, bool IsFigure, string? Caption)
    {
        public static Item ForText(PdfRectangle bounds, string text) => new(bounds, text, false, null);

        public static Item ForFigure(PdfRectangle bounds, string text, string? caption)
            => new(bounds, text, true, caption);
    }
}
