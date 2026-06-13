using System;
using UglyToad.PdfPig.Content;

namespace Dignite.DocumentAI.TextExtraction.Pdf;

/// <summary>
/// Resolves an embedded <see cref="IPdfImage"/> into image-file bytes + MIME type suitable for feeding
/// to <c>IOcrProvider.RecognizeAsync</c>. Returns <c>null</c> for codecs PdfPig cannot turn into a
/// standalone image file (JBIG2 / JPX / CCITT, or raw bitmaps it cannot PNG-encode) — the caller treats
/// that as an undecodable image and trips the completeness signal.
/// </summary>
internal static class PdfImagePayload
{
    /// <returns>PNG/JPEG bytes + content type, or <c>null</c> if the image cannot be decoded to a file.</returns>
    public static (byte[] Bytes, string ContentType)? TryResolve(IPdfImage image)
    {
        // Preferred path: PdfPig reverses the PDF filters and re-encodes the bitmap as a valid PNG.
        // Covers Flate / LZW / raw-sample bitmaps (the common embedded-screenshot case).
        if (image.TryGetPng(out var png) && png is { Length: > 0 })
        {
            return (png, "image/png");
        }

        // PdfPig does not decode DCT (JPEG) without an external filter; for those, the raw stream IS a
        // valid JPEG file. TryGetBytesAsMemory reverses non-DCT filters; otherwise use the raw bytes.
        var raw = image.TryGetBytesAsMemory(out var decoded) ? decoded : image.RawMemory;

        // Inspect the signature on the span first; only materialize a byte[] when we will actually return
        // it (an unsupported/headerless image otherwise pays a full-buffer copy just to be discarded).
        var span = raw.Span;
        if (LooksLikeJpeg(span))
        {
            return (raw.ToArray(), "image/jpeg");
        }

        if (LooksLikePng(span))
        {
            return (raw.ToArray(), "image/png");
        }

        // Unsupported codec (JBIG2 / JPX / CCITT) or undecoded raw samples without a file header.
        return null;
    }

    private static bool LooksLikeJpeg(ReadOnlySpan<byte> b)
        => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool LooksLikePng(ReadOnlySpan<byte> b)
        => b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
           && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;
}
