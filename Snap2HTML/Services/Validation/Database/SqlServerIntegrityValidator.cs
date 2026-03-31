using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Database;

/// <summary>
/// SQL Server data file (MDF/NDF) integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
///
/// SQL Server data files are organized in 8 KiB pages. The first page (page 0)
/// is always a File Header Page (type 15 / 0x0F). Each page starts with a 96-byte
/// header whose first 4 bytes identify the page:
///
///   Offset 0: m_headerVersion  = 0x01 (always 1)
///   Offset 1: m_type           = 0x0F (15 = PG_FILE_HEADER)
///   Offset 2: m_typeFlagBits   = 0x00
///   Offset 3: m_level          = 0x00
///
/// See: https://learn.microsoft.com/en-us/sql/relational-databases/pages-and-extents-architecture-guide
/// </summary>
public class SqlServerIntegrityValidator : FileIntegrityValidatorBase, ISqlServerIntegrityValidator
{
    private static readonly HashSet<string> SqlServerExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mdf",  // Primary data file
        ".ndf"   // Secondary data file
    };

    /// <summary>
    /// SQL Server File Header Page signature — first 4 bytes of page 0:
    ///   0x01 = m_headerVersion (always 1 for modern SQL Server)
    ///   0x0F = m_type (15 = PG_FILE_HEADER, File Header Page)
    ///   0x00 = m_typeFlagBits
    ///   0x00 = m_level
    /// </summary>
    private static readonly byte[] MdfSignature = { 0x01, 0x0F, 0x00, 0x00 };

    /// <inheritdoc />
    public override string CategoryName => "SQL Server";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => SqlServerExtensions;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < MdfSignature.Length) return false;

        for (var i = 0; i < MdfSignature.Length; i++)
        {
            if (header[i] != MdfSignature[i])
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO: Implement full MDF validation.
    /// A possible approach is to verify additional page 0 header fields:
    ///   - Bytes 4-5: m_flagBits (should contain valid flag combinations)
    ///   - Bytes 32-33: m_pageId.m_pageId (should be 0 for page 0)
    ///   - Check that page size aligns with 8192-byte boundaries
    /// Full structural validation (DBCC CHECKDB) requires a running SQL Server instance
    /// and is not practical for offline file scanning.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
