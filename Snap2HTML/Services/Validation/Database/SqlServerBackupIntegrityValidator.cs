using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Data.SqlClient;
using Snap2HTML.Core.Models;
using Snap2HTML.Infrastructure.Prerequisites.SqlLocalDb;

namespace Snap2HTML.Services.Validation.Database;

/// <summary>
/// SQL Server backup file (.bak) integrity validator.
/// Inherits Channel-based batch pipeline from <see cref="FileIntegrityValidatorBase"/>.
///
/// SQL Server backups use the Microsoft Tape Format (MTF). The file starts with a
/// TAPE descriptor block (DBLK) followed by a SSET (Start of Set) DBLK, both
/// containing well-known fields. Within the first 0xE0 (224) bytes we can identify
/// two stable signatures:
///
///   1. The UTF-16LE string "Microsoft SQL Server" typically located around
///      offset 14 inside the first MTF header block.
///   2. The ASCII string "RAID" appearing shortly after, which identifies the
///      backup stream type.
///
/// These two markers are present across SQL Server versions 2008 – 2022 and do not
/// vary with the backup type (full, differential, log) or compression settings.
///
/// Full validation uses SQL Server Express LocalDB to execute RESTORE VERIFYONLY,
/// which reads the entire backup and checks structural integrity and optional
/// BACKUP CHECKSUM data without restoring the database.
///
/// LocalDB installation and detection is delegated to <see cref="ISqlLocalDbPrerequisite"/>.
///
/// Reference: Microsoft Tape Format Specification Revision 1.00a (Seagate, 1997).
///            The format has been extended by SQL Server but the DBLK headers remain
///            compatible with the MTF spec.
/// </summary>
public class SqlServerBackupIntegrityValidator : FileIntegrityValidatorBase, ISqlServerBackupIntegrityValidator
{
    /// <summary>
    /// Name of the dedicated LocalDB instance created and managed by Snap2HTML.
    /// Using a separate instance avoids interfering with the user's default MSSQLLocalDB.
    /// </summary>
    private const string LocalDbInstanceName = "Snap2HTMLValidator";

    /// <summary>
    /// Threshold in bytes above which a backup file is considered "large" and
    /// receives an extended command timeout. 10 GB.
    /// </summary>
    private const long LargeFileThresholdBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>
    /// Base command timeout in seconds for RESTORE VERIFYONLY on normal-sized backups.
    /// </summary>
    private const int BaseCommandTimeoutSeconds = 3600; // 1 hour

    /// <summary>
    /// Extended command timeout in seconds for backups larger than <see cref="LargeFileThresholdBytes"/>.
    /// Large backups (10 GB+) can take several hours to verify.
    /// </summary>
    private const int ExtendedCommandTimeoutSeconds = 14400; // 4 hours

    private static readonly HashSet<string> BackupExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bak"   // SQL Server backup file
    };

    /// <summary>
    /// "Microsoft SQL Server" encoded in UTF-16LE (40 bytes).
    /// This string is embedded in the MTF media label area.
    /// </summary>
    private static readonly byte[] MsSqlUtf16Le = Encoding.Unicode.GetBytes("Microsoft SQL Server");

    /// <summary>
    /// "RAID" in ASCII — identifies the backup stream type in the MTF header.
    /// </summary>
    private static readonly byte[] RaidAscii = Encoding.ASCII.GetBytes("RAID");

    private readonly ISqlLocalDbPrerequisite _localDb;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="localDb">
    /// The LocalDB prerequisite whose <see cref="IPrerequisite.Status"/> determines
    /// whether full validation is available.
    /// </param>
    public SqlServerBackupIntegrityValidator(ISqlLocalDbPrerequisite localDb)
    {
        _localDb = localDb;
    }

    /// <summary>
    /// We read the first 512 bytes — enough to cover the MTF TAPE DBLK
    /// header region where both signatures reside.
    /// </summary>
    protected override int MagicBytesBufferSize => 512;

    /// <inheritdoc />
    public override string CategoryName => "SQL Server Backup";

    /// <inheritdoc />
    public override bool SupportsFullValidation
        => _localDb.Status == PrerequisiteStatus.Installed;

    /// <inheritdoc />
    public override IReadOnlySet<string> SupportedExtensions => BackupExtensions;

    /// <inheritdoc />
    protected override bool CheckMagicBytes(ReadOnlySpan<byte> header)
    {
        if (header.Length < MagicBytesBufferSize)
            return false;

        // Look for "Microsoft SQL Server" (UTF-16LE) anywhere in the header region
        var foundMsSql = ContainsSequence(header, MsSqlUtf16Le);

        // Look for "RAID" (ASCII) anywhere in the header region
        var foundRaid = ContainsSequence(header, RaidAscii);

        return foundMsSql && foundRaid;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Executes RESTORE VERIFYONLY WITH CHECKSUM against a LocalDB instance.
    /// This reads the entire backup file and verifies:
    ///   - The backup set structure and headers are readable.
    ///   - Page checksums match (if the backup was created WITH CHECKSUM).
    ///   - The backup is not truncated.
    ///
    /// For large files (10 GB+), the command timeout is extended to 4 hours and
    /// SQL Server informational messages (percentage complete) are captured via
    /// the <see cref="SqlConnection.InfoMessage"/> event using STATS = 5.
    ///
    /// If LocalDB is not available, falls back to returning Valid (header-only validation).
    /// </remarks>
    protected override async ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        if (_localDb.Status != PrerequisiteStatus.Installed)
            return IntegrityStatus.Valid;

        var connectionString = $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=30";

        long fileSize;
        try
        {
            fileSize = new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.TraceWarning(
                "[SqlServerBackup] Cannot read file size for '{0}': {1}", filePath, ex.Message);
            return IntegrityStatus.DecodingFailed;
        }

        var isLargeFile = fileSize >= LargeFileThresholdBytes;
        var fileSizeGb = fileSize / (1024.0 * 1024.0 * 1024.0);
        var commandTimeout = isLargeFile ? ExtendedCommandTimeoutSeconds : BaseCommandTimeoutSeconds;

        if (isLargeFile)
        {
            Trace.TraceInformation(
                "[SqlServerBackup] Large backup detected: '{0}' ({1:F2} GB). " +
                "Using extended timeout of {2} seconds. Verification may take a long time.",
                filePath, fileSizeGb, commandTimeout);
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);

            // Subscribe to SQL Server informational messages (severity < 11) to capture
            // progress reports that RESTORE VERIFYONLY emits for large backups.
            connection.InfoMessage += (_, args) =>
            {
                foreach (SqlError error in args.Errors)
                {
                    if (error.Class == 0)
                    {
                        // Informational messages: progress percentage, backup set info, etc.
                        Trace.TraceInformation(
                            "[SqlServerBackup] [{0}] Info: {1}", Path.GetFileName(filePath), error.Message);
                    }
                    else
                    {
                        Trace.TraceWarning(
                            "[SqlServerBackup] [{0}] Warning (Class {1}, Number {2}): {3}",
                            Path.GetFileName(filePath), error.Class, error.Number, error.Message);
                    }
                }
            };

            // FireInfoMessageEventOnUserErrors ensures we get ALL messages, including
            // those with severity > 0 that would otherwise only appear as exceptions.
            connection.FireInfoMessageEventOnUserErrors = true;

            await connection.OpenAsync(ct);

            // RESTORE VERIFYONLY reads the backup and validates its integrity
            // WITH CHECKSUM also verifies page-level checksums when present
            // STATS=5 emits progress messages every 5% — useful for large backups
            var sql = isLargeFile
                ? "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM, STATS = 5"
                : "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@path", filePath);
            command.CommandTimeout = commandTimeout;

            var stopwatch = Stopwatch.StartNew();

            await command.ExecuteNonQueryAsync(ct);

            stopwatch.Stop();

            if (isLargeFile)
            {
                Trace.TraceInformation(
                    "[SqlServerBackup] Verification of '{0}' ({1:F2} GB) completed successfully in {2}.",
                    filePath, fileSizeGb, FormatElapsed(stopwatch.Elapsed));
            }

            return IntegrityStatus.Valid;
        }
        catch (SqlException ex) when (ex.Number is 3013 or 3180 or 3183 or 3201
                                          or 3241 or 3242 or 3243 or 3244
                                          or 3456 or 3271)
        {
            // 3013 = RESTORE VERIFYONLY is terminating abnormally
            // 3180 = Backup set cannot be restored (corrupt or incompatible)
            // 3183 = RESTORE detected an error on page
            // 3201 = Cannot open backup device (permissions / path)
            // 3241 = The media family is not recognized (corrupt media header)
            // 3242 = The file is not a valid Microsoft Tape Format backup set
            // 3243 = The media loaded is formatted with a newer version
            // 3244 = Page size mismatch in the backup
            // 3456 = Could not redo log record (transaction log corruption)
            // 3271 = A nonrecoverable I/O error occurred on file
            Trace.TraceError(
                "[SqlServerBackup] Verification FAILED for '{0}' ({1:F2} GB). " +
                "SQL Error {2}: {3}",
                filePath, fileSizeGb, ex.Number, ex.Message);
            return IntegrityStatus.DecodingFailed;
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
            // -2 = Timeout expired
            Trace.TraceError(
                "[SqlServerBackup] Verification TIMED OUT for '{0}' ({1:F2} GB) " +
                "after {2} seconds. The backup may be too large for the configured timeout.",
                filePath, fileSizeGb, commandTimeout);
            return IntegrityStatus.DecodingFailed;
        }
        catch (SqlException ex)
        {
            // Any other SQL error not explicitly handled
            Trace.TraceError(
                "[SqlServerBackup] Unexpected SQL error verifying '{0}' ({1:F2} GB). " +
                "Error {2} (Class {3}): {4}",
                filePath, fileSizeGb, ex.Number, ex.Class, ex.Message);
            return IntegrityStatus.DecodingFailed;
        }
        catch (OperationCanceledException)
        {
            Trace.TraceWarning(
                "[SqlServerBackup] Verification of '{0}' ({1:F2} GB) was cancelled.",
                filePath, fileSizeGb);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError(
                "[SqlServerBackup] Unexpected error verifying '{0}' ({1:F2} GB): {2}",
                filePath, fileSizeGb, ex.Message);
            return IntegrityStatus.DecodingFailed;
        }
    }

    /// <summary>
    /// Searches for a byte subsequence within a larger span.
    /// Uses a simple sliding-window approach, which is sufficient for short patterns
    /// within a small header buffer.
    /// </summary>
    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0) return true;
        if (haystack.Length < needle.Length) return false;

        var limit = haystack.Length - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a TimeSpan as a human-readable string (e.g., "2h 15m 30s" or "45m 12s").
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m {elapsed.Seconds:D2}s";

        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        return $"{elapsed.TotalSeconds:F1}s";
    }
}
