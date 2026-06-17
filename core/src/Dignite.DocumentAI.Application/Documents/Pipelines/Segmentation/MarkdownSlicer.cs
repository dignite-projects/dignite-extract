using System;
using System.Collections.Generic;

namespace Dignite.DocumentAI.Documents.Pipelines.Segmentation;

/// <summary>
/// A constituent boundary proposed by the segmentation LLM (#346): a <b>verbatim</b> start marker copied from
/// the container Markdown plus whether the slice it opens is itself a document (vs a cover / index / transmittal).
/// </summary>
public sealed record SegmentBoundary(string StartMarker, bool IsDocument);

/// <summary>A deterministically-cut constituent slice of a container's Markdown (#346).</summary>
public sealed record MarkdownSlice(string Text, bool IsDocument, int Ordinal);

/// <summary>
/// Deterministically cuts a container's Markdown at LLM-proposed boundaries (#346 decision: the LLM returns
/// <b>verbatim start markers</b>, never regenerated slice text; the code does the cutting, so there is no content
/// drift and the result is machine-verifiable). This is the highest-leverage stability lever of the born-digital
/// path: the LLM's unreliable job (boundary judgment) is bounded to short markers, while exact content is owned by
/// code.
/// </summary>
public static class MarkdownSlicer
{
    /// <summary>
    /// Cuts <paramref name="markdown"/> at <paramref name="boundaries"/>. Each marker is located by an ordinal
    /// forward search with an advancing cursor, so repeated markers (e.g. two invoices both starting "Invoice")
    /// map to successive occurrences in document order. Slices run marker-to-marker (last to end); any leading
    /// preamble before the first marker is folded into the first slice so no content is dropped.
    /// <para>
    /// Returns <c>false</c> (with an empty <paramref name="slices"/>) when the boundaries cannot be trusted —
    /// empty input, a marker not found verbatim, or markers out of order — so the caller raises a review signal
    /// instead of spawning garbage. A marker is matched against the raw Markdown first, then against a copy with
    /// <c>&amp;lt;</c> decoded back to <c>&lt;</c>, because the LLM reads the Markdown after
    /// <see cref="Dignite.DocumentAI.Ai.PromptBoundary.WrapDocument"/> has encoded <c>&lt;</c>.
    /// </para>
    /// </summary>
    public static bool TrySlice(
        string? markdown,
        IReadOnlyList<SegmentBoundary>? boundaries,
        out List<MarkdownSlice> slices)
    {
        slices = new List<MarkdownSlice>();

        if (string.IsNullOrEmpty(markdown) || boundaries is not { Count: > 0 })
        {
            return false;
        }

        var positions = new int[boundaries.Count];
        var cursor = 0;
        for (var i = 0; i < boundaries.Count; i++)
        {
            var pos = FindMarker(markdown, boundaries[i].StartMarker, cursor);
            if (pos < 0)
            {
                return false; // marker not found verbatim -> untrusted split
            }

            positions[i] = pos;
            cursor = pos + 1; // advance so a repeated marker maps to the next occurrence
        }

        for (var i = 0; i < boundaries.Count; i++)
        {
            // The first slice folds in any leading preamble (content before the first marker) so nothing is lost.
            var start = i == 0 ? 0 : positions[i];
            var end = i == boundaries.Count - 1 ? markdown.Length : positions[i + 1];

            var text = markdown[start..end].Trim();
            if (text.Length == 0)
            {
                continue; // defensively skip an empty slice (e.g. adjacent markers)
            }

            slices.Add(new MarkdownSlice(text, boundaries[i].IsDocument, slices.Count));
        }

        return slices.Count > 0;
    }

    private static int FindMarker(string markdown, string? marker, int cursor)
    {
        if (string.IsNullOrWhiteSpace(marker))
        {
            return -1;
        }

        var pos = markdown.IndexOf(marker, cursor, StringComparison.Ordinal);
        if (pos >= 0)
        {
            return pos;
        }

        // The LLM read the Markdown through PromptBoundary.WrapDocument, which encodes '<' as "&lt;". If the marker
        // came back carrying that encoding, decode it before retrying against the raw Markdown.
        if (marker.Contains("&lt;", StringComparison.Ordinal))
        {
            var decoded = marker.Replace("&lt;", "<", StringComparison.Ordinal);
            return markdown.IndexOf(decoded, cursor, StringComparison.Ordinal);
        }

        return -1;
    }
}
