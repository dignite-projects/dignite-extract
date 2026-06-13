using System;
using System.Collections.Generic;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Builds single-page PDF fixtures in-code with PdfPig's <see cref="PdfDocumentBuilder"/>: text lines at
/// given baselines plus optional embedded PNG images at given placement rectangles. PDF user space is
/// bottom-left origin, so a larger Y is higher on the page.
/// </summary>
internal static class PdfFixtures
{
    public static byte[] Build(
        IReadOnlyList<(string Text, double BaselineY)> texts,
        IReadOnlyList<(byte[] Image, PdfRectangle Rect)>? images = null)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        foreach (var (text, baselineY) in texts)
        {
            page.AddText(text, 12, new PdfPoint(50, baselineY), font);
        }

        if (images is not null)
        {
            foreach (var (image, rect) in images)
            {
                page.AddPng(image, rect);
            }
        }

        return builder.Build();
    }
}
