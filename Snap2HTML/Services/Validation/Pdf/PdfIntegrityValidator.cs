using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Pdf;

/// <summary>
/// PDF integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
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
    /// TODO: Implement full PDF validation using a PDF library (e.g., PdfPig, iText7) in a future phase.
    /// For now, if magic bytes passed, we consider the file valid at FullDecode level.
    /// This means FullDecode behaves the same as MagicBytesOnly for PDFs until a library is integrated.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        // TODO: Implement full PDF structural validation with a PDF library.
        // Candidates: PdfPig (MIT, lightweight read-only), iText7 (AGPL/commercial).
        // When implemented, this should attempt to parse the PDF structure/cross-reference table
        // and return DecodingFailed if the PDF is structurally invalid.
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
