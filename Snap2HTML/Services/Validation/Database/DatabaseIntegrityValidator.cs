using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation.Database;

/// <summary>
/// SQLite database integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
/// Currently implements magic bytes validation only.
/// </summary>
public class DatabaseIntegrityValidator : FileIntegrityValidatorBase, IDatabaseIntegrityValidator
{
    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sqlite", ".sqlite3", ".db", ".db3", ".s3db", ".sl3"
    };

    /// <summary>
    /// SQLite magic bytes: "SQLite format 3\0" — 16 bytes at offset 0.
    /// Every valid SQLite database file starts with this exact header string.
    /// See: https://www.sqlite.org/fileformat.html#the_header_string
    /// </summary>
    private static readonly byte[] SqliteSignature =
    {
        0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, 0x20, 0x66, // "SQLite f"
        0x6F, 0x72, 0x6D, 0x61, 0x74, 0x20, 0x33, 0x00  // "ormat 3\0"
    };

    /// <inheritdoc />
    public override string CategoryName => "Database";

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => DatabaseExtensions;

    /// <summary>
    /// Need 16 bytes for the full SQLite header string.
    /// </summary>
    protected override int MagicBytesBufferSize => 16;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < SqliteSignature.Length) return false;

        for (var i = 0; i < SqliteSignature.Length; i++)
        {
            if (header[i] != SqliteSignature[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// TODO: Implement full SQLite validation using PRAGMA integrity_check or a library.
    /// Candidates: Microsoft.Data.Sqlite (MIT), System.Data.SQLite.
    /// When implemented, open the database and run PRAGMA integrity_check to verify
    /// structural consistency of the B-tree pages and index entries.
    /// </remarks>
    protected override ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        return new ValueTask<IntegrityStatus>(IntegrityStatus.Valid);
    }
}
