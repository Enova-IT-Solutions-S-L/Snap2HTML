using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Document;

/// <summary>
/// Document integrity validator covering Office, OpenDocument, and RTF formats.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
///
/// Supported format families:
///   - OLE2 Compound Binary (.doc, .xls, .ppt, .msi, .msg): header D0 CF 11 E0 A1 B1 1A E1
///   - OOXML / ZIP-based (.docx, .xlsx, .pptx, .docm, .xlsm, .pptm): header PK 03 04
///   - OpenDocument / ZIP-based (.odt, .ods, .odp, .odg): header PK 03 04
///   - RTF (.rtf): header {\rtf (7B 5C 72 74 66)
/// </summary>
public class DocumentIntegrityValidator : FileIntegrityValidatorBase, IDocumentIntegrityValidator
{
    /// <summary>
    /// All document extensions handled by this validator.
    /// </summary>
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Legacy Office (OLE2 Compound Binary)
        ".doc", ".xls", ".ppt", ".msi", ".msg",
        // Modern Office (OOXML — ZIP container)
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm",
        // OpenDocument Format (ODF — ZIP container)
        ".odt", ".ods", ".odp", ".odg",
        // Rich Text Format
        ".rtf"
    };

    /// <summary>
    /// Extensions that use the OLE2 Compound Binary format.
    /// </summary>
    private static readonly HashSet<string> Ole2Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt", ".msi", ".msg"
    };

    /// <summary>
    /// Extensions that use a ZIP container (OOXML or ODF).
    /// </summary>
    private static readonly HashSet<string> ZipBasedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm",
        ".odt", ".ods", ".odp", ".odg"
    };

    /// <summary>
    /// OLE2 Compound Binary File header (8 bytes).
    /// Used by legacy Office formats (.doc, .xls, .ppt) and others (.msi, .msg).
    /// </summary>
    private static readonly byte[] Ole2Signature =
        { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    /// <summary>
    /// ZIP local file header "PK\x03\x04" (4 bytes).
    /// Used by OOXML (.docx, .xlsx, .pptx) and ODF (.odt, .ods, .odp) formats.
    /// </summary>
    private static readonly byte[] ZipSignature =
        { 0x50, 0x4B, 0x03, 0x04 };

    /// <summary>
    /// RTF header "{\rtf" (5 bytes).
    /// </summary>
    private static readonly byte[] RtfSignature =
        { 0x7B, 0x5C, 0x72, 0x74, 0x66 };

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => DocumentExtensions;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4) return false;

        // Check OLE2 (8 bytes) — legacy Office
        if (header.Length >= Ole2Signature.Length && MatchesSignature(header, Ole2Signature))
            return true;

        // Check ZIP (4 bytes) — OOXML and ODF
        if (MatchesSignature(header, ZipSignature))
            return true;

        // Check RTF (5 bytes)
        if (header.Length >= RtfSignature.Length && MatchesSignature(header, RtfSignature))
            return true;

        return false;
    }

    /// <summary>
    /// Compares the header bytes against a known signature starting at offset 0.
    /// </summary>
    private static bool MatchesSignature(ReadOnlySpan<byte> header, byte[] signature)
    {
        for (var i = 0; i < signature.Length; i++)
        {
            if (header[i] != signature[i])
                return false;
        }
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO: Implement full document validation in a future phase.
    /// For ZIP-based formats (OOXML/ODF), use System.IO.Compression.ZipArchive to verify:
    ///   - OOXML: presence of [Content_Types].xml
    ///   - ODF: presence of mimetype file as first entry
    /// For OLE2 formats, use OpenMcdf (MIT) to verify compound binary structure.
    /// For RTF, parse the {\rtfN header and verify basic structure.
    /// For now, if magic bytes passed, we consider the file valid at FullDecode level.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        // TODO: Implement full document structural validation.
        // Candidates:
        //   - System.IO.Compression.ZipArchive (built-in) for OOXML/ODF ZIP structure
        //   - OpenMcdf (MIT, lightweight) for OLE2 compound binary structure
        // When implemented, verify internal structure matches the expected format
        // and return DecodingFailed if the document is structurally invalid.
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
