using Snap2HTML.Core.Models;
using UglyToad.PdfPig;

namespace Snap2HTML.Services.Validation.Pdf;

/// <summary>
/// PDF integrity validator using PdfPig (MIT, read-only).
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Full validation parses the PDF structure (header, xref table/stream, trailer, page tree)
/// without decoding content streams, keeping overhead to ~1-5ms per file.
/// </summary>
public class PdfIntegrityValidator : FileIntegrityValidatorBase, IPdfIntegrityValidator
{
    private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    /// <summary>
    /// PDF magic bytes: %PDF (0x25 0x50 0x44 0x46).
    /// </summary>
    private static readonly byte[] PdfSignature = { 0x25, 0x50, 0x44, 0x46 };

    /// <inheritdoc />
    public override string CategoryName => "PDF";

    /// <inheritdoc />
    public override bool SupportsFullValidation => true;

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => PdfExtensions;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < PdfSignature.Length) return false;

        for (var i = 0; i < PdfSignature.Length; i++)
        {
            if (header[i] != PdfSignature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Uses PdfPig's <see cref="PdfDocument.Open(string)"/> which parses:
    /// 1. PDF header (%PDF-X.Y) — validates version
    /// 2. Cross-reference table or xref stream — validates object layout
    /// 3. Trailer dictionary — validates document root reference
    /// 4. Page tree access via NumberOfPages — validates page catalog
    ///
    /// This does NOT decode content streams, fonts, or embedded images,
    /// making it extremely fast (~1-5ms per PDF on SSD) while still catching
    /// structural corruption: truncated files, broken xref tables, invalid
    /// trailers, missing page trees, and malformed object definitions.
    ///
    /// Note: PdfDocument.Open is synchronous (file I/O + parsing).
    /// Since the base class pipeline already runs consumers on thread pool workers,
    /// this synchronous call is acceptable — same pattern as ImageSharp's IdentifyAsync
    /// which is also internally synchronous on the stream read path.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            using var document = PdfDocument.Open(filePath);

            // Accessing NumberOfPages forces the page tree to be resolved,
            // catching corruption in the page catalog without reading content.
            if (document.NumberOfPages < 1)
            {
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
            }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // PdfPig throws various exceptions for structural problems:
            // - InvalidOperationException (malformed xref/trailer)
            // - PdfDocumentFormatException (format violations)
            // - EndOfStreamException (truncated files)
            // - etc.
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }
}
