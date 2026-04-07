using System.IO.Compression;
using System.Text;
using System.Xml;
using OpenMcdf;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Document;

/// <summary>
/// Document integrity validator covering Office, OpenDocument, RTF, and XML formats.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Full validation opens each file with the appropriate library:
///   - OOXML (.docx, .xlsx, .pptx, .docm, .xlsm, .pptm): System.IO.Compression.ZipArchive
///     verifies ZIP structure + presence of [Content_Types].xml with expected content type
///   - ODF (.odt, .ods, .odp, .odg): ZipArchive verifies ZIP structure + mimetype entry
///   - OLE2 (.doc, .xls, .ppt, .msi, .msg): OpenMcdf RootStorage verifies compound binary structure
///   - RTF (.rtf): validates {\rtfN header and version digit
///   - XML (.xml): System.Xml.XmlReader verifies well-formed XML structure
///
/// Supported format families:
///   - OLE2 Compound Binary (.doc, .xls, .ppt, .msi, .msg): header D0 CF 11 E0 A1 B1 1A E1
///   - OOXML / ZIP-based (.docx, .xlsx, .pptx, .docm, .xlsm, .pptm): header PK 03 04
///   - OpenDocument / ZIP-based (.odt, .ods, .odp, .odg): header PK 03 04
///   - RTF (.rtf): header {\rtf (7B 5C 72 74 66)
///   - XML (.xml): header &lt;?xml (3C 3F 78 6D 6C) or BOM + &lt;?xml or &lt; (root element)
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
        ".rtf",
        // XML
        ".xml"
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
    /// ODF extensions subset for distinguishing ODF from OOXML in ZIP validation.
    /// </summary>
    private static readonly HashSet<string> OdfExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
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

    /// <summary>
    /// XML declaration "&lt;?xml" (5 bytes): 3C 3F 78 6D 6C.
    /// </summary>
    private static readonly byte[] XmlDeclarationSignature =
        { 0x3C, 0x3F, 0x78, 0x6D, 0x6C };

    /// <summary>
    /// UTF-8 BOM (3 bytes): EF BB BF.
    /// </summary>
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// UTF-16 LE BOM (2 bytes): FF FE.
    /// </summary>
    private static readonly byte[] Utf16LeBom = { 0xFF, 0xFE };

    /// <summary>
    /// UTF-16 BE BOM (2 bytes): FE FF.
    /// </summary>
    private static readonly byte[] Utf16BeBom = { 0xFE, 0xFF };

    /// <inheritdoc />
    public override string CategoryName => "Documents";

    /// <inheritdoc />
    public override bool SupportsFullValidation => true;

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

        // Check XML: <?xml declaration
        if (header.Length >= XmlDeclarationSignature.Length && MatchesSignature(header, XmlDeclarationSignature))
            return true;

        // UTF-8 BOM + <?xml
        if (header.Length >= Utf8Bom.Length + XmlDeclarationSignature.Length &&
            MatchesSignature(header, Utf8Bom) &&
            MatchesSignature(header[Utf8Bom.Length..], XmlDeclarationSignature))
            return true;

        // UTF-16 LE BOM + '<' (0x3C 0x00 in LE)
        if (header.Length >= 4 && MatchesSignature(header, Utf16LeBom) &&
            header[2] == 0x3C && header[3] == 0x00)
            return true;

        // UTF-16 BE BOM + '<' (0x00 0x3C in BE)
        if (header.Length >= 4 && MatchesSignature(header, Utf16BeBom) &&
            header[2] == 0x00 && header[3] == 0x3C)
            return true;

        // No BOM, starts with '<' (XML without declaration)
        if (header[0] == 0x3C && header[1] != 0x00)
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
    /// Performs format-specific structural validation:
    ///
    /// ZIP-based (OOXML/ODF): Opens the file with <see cref="ZipArchive"/> (built-in),
    /// iterates all entries to validate the central directory and local file headers,
    /// then checks for the required marker entry:
    ///   - OOXML: [Content_Types].xml must exist and contain the expected PartName
    ///     (word/ for .docx/.docm, xl/ for .xlsx/.xlsm, ppt/ for .pptx/.pptm)
    ///   - ODF: mimetype entry must exist
    ///
    /// OLE2 Compound Binary: Opens the file with OpenMcdf's <see cref="RootStorage.OpenRead"/>
    /// which parses the FAT/DIFAT/directory sectors. Then enumerates root entries to
    /// validate the compound binary directory tree. Corrupt sector chains, invalid
    /// directory entries, or truncated files will cause OpenMcdf to throw.
    ///
    /// RTF: Reads the first 64 bytes and validates the {\rtfN header followed by
    /// a version digit (typically 1).
    ///
    /// XML: Uses <see cref="XmlReader"/> (built-in) to read the entire file in
    /// forward-only mode, verifying well-formed XML (proper nesting, matching tags,
    /// valid character references, correct encoding). External DTD/entity resolution
    /// is disabled for security (XXE prevention).
    ///
    /// Note: ZipArchive, RootStorage.OpenRead, and XmlReader are synchronous
    /// (seek-based I/O). Since the base class pipeline runs consumers on thread pool
    /// workers, these synchronous calls are acceptable — same pattern as ATL Track,
    /// PdfPig, and ImageSharp.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(filePath);

        try
        {
            if (ZipBasedExtensions.Contains(extension))
                return ValidateZipBasedDocument(filePath, extension);

            if (Ole2Extensions.Contains(extension))
                return ValidateOle2Document(filePath);

            if (extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
                return ValidateRtfDocument(filePath);

            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                return ValidateXmlDocument(filePath);

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }

    /// <summary>
    /// Validates OOXML and ODF documents by opening as a ZIP archive.
    /// Iterates entries (validates central directory), then checks for the required marker:
    ///   - OOXML: [Content_Types].xml containing word/, xl/, or ppt/ PartName
    ///   - ODF: mimetype entry
    /// </summary>
    private static ValueTask<IntegrityStatus> ValidateZipBasedDocument(string filePath, string extension)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);

            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            // Iterate all entries to force full central directory parsing
            foreach (var entry in zip.Entries)
            {
                _ = entry.FullName;
                _ = entry.CompressedLength;
            }

            // Check for format-specific required entries
            if (OdfExtensions.Contains(extension))
            {
                // ODF: mimetype must be present as a file entry
                var mimetype = zip.GetEntry("mimetype");
                if (mimetype is null)
                    return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
            }
            else
            {
                // OOXML: [Content_Types].xml must exist and reference the correct content
                var contentTypes = zip.GetEntry("[Content_Types].xml");
                if (contentTypes is null)
                    return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);

                // Read [Content_Types].xml and check for the expected PartName
                using var entryStream = contentTypes.Open();
                using var reader = new StreamReader(entryStream, Encoding.UTF8);
                var content = reader.ReadToEnd();

                var expectedPartName = extension.ToLowerInvariant() switch
                {
                    ".docx" or ".docm" => "/word/",
                    ".xlsx" or ".xlsm" => "/xl/",
                    ".pptx" or ".pptm" => "/ppt/",
                    _ => null
                };

                if (expectedPartName is not null &&
                    !content.Contains(expectedPartName, StringComparison.OrdinalIgnoreCase))
                {
                    return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
                }
            }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (InvalidDataException)
        {
            // ZipArchive throws InvalidDataException for corrupt ZIP structures
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }

    /// <summary>
    /// Validates OLE2 Compound Binary documents using OpenMcdf.
    /// Opens the compound file and enumerates root-level entries to verify
    /// the sector chain, FAT/DIFAT, and directory structure.
    /// </summary>
    private static ValueTask<IntegrityStatus> ValidateOle2Document(string filePath)
    {
        try
        {
            using var root = RootStorage.OpenRead(filePath);

            // Enumerate entries to force directory tree parsing.
            // Corrupt sector chains, invalid directory entries, or truncated
            // files will cause OpenMcdf to throw.
            foreach (var entry in root.EnumerateEntries())
            {
                _ = entry.Name;
            }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // OpenMcdf throws various exceptions for structural problems:
            // - FileFormatException (invalid CFB structure)
            // - EndOfStreamException (truncated files)
            // - InvalidOperationException (corrupt directory)
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }

    /// <summary>
    /// Validates RTF documents by checking the {\rtfN header.
    /// A valid RTF must start with {\rtf followed by a version digit (typically 1).
    /// </summary>
    private static ValueTask<IntegrityStatus> ValidateRtfDocument(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);

            var buffer = new byte[64];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            if (bytesRead < 6)
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);

            // Verify {\rtf header
            if (buffer[0] != 0x7B || // {
                buffer[1] != 0x5C || // \
                buffer[2] != 0x72 || // r
                buffer[3] != 0x74 || // t
                buffer[4] != 0x66)   // f
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);

            // Version digit must follow (typically '1')
            if (!char.IsDigit((char)buffer[5]))
                return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }

    /// <summary>
    /// Validates XML files using <see cref="XmlReader"/> (built-in).
    /// Reads the entire file in forward-only mode to verify it is well-formed XML:
    /// proper nesting, matching open/close tags, valid character references, and
    /// correct encoding declaration. Does NOT validate against a schema (XSD/DTD).
    /// External DTD/entity resolution is disabled for security (XXE prevention).
    /// </summary>
    private static ValueTask<IntegrityStatus> ValidateXmlDocument(string filePath)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                CheckCharacters = true,
                ConformanceLevel = ConformanceLevel.Document
            };

            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);

            using var reader = XmlReader.Create(stream, settings);

            // Read through the entire document — XmlReader will throw
            // XmlException on any well-formedness error
            while (reader.Read()) { }

            return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
        }
        catch (XmlException)
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.DecodingFailed);
        }
    }
}
