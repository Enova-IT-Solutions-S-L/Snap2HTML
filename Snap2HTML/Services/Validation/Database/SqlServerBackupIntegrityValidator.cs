using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using Snap2HTML.Core.Models;

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
/// LocalDB is installed from an embedded SqlLocalDB.msi resource (fully offline,
/// no internet connectivity required).
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
    /// Name of the embedded resource containing the SqlLocalDB.msi installer.
    /// The MSI is bundled as an embedded resource for fully offline installation
    /// without requiring internet connectivity.
    /// </summary>
    private const string LocalDbMsiResourceName = "Snap2HTML.Resources.Installers.SqlLocalDB.msi";

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

    /// <summary>
    /// Lazy-initialized flag: true when LocalDB is available and the instance is ready.
    /// </summary>
    private static readonly Lazy<bool> LocalDbAvailable = new(EnsureLocalDbInstance);

    /// <summary>
    /// We read the first 512 bytes — enough to cover the MTF TAPE DBLK
    /// header region where both signatures reside.
    /// </summary>
    protected override int MagicBytesBufferSize => 512;

    /// <inheritdoc />
    public override string CategoryName => "SQL Server Backup";

    /// <inheritdoc />
    public override bool SupportsFullValidation => LocalDbAvailable.Value;

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
        if (!LocalDbAvailable.Value)
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
    /// Checks whether SqlLocalDB is installed, and ensures the dedicated
    /// Snap2HTML instance exists and is running.
    /// If LocalDB is not installed, extracts the embedded MSI and installs it
    /// silently from the bundled resource (fully offline, no internet required).
    /// Returns true if LocalDB is ready for use, false otherwise.
    /// </summary>
    /// <remarks>
    /// Installation flow when LocalDB is absent:
    ///   1. Extract the SqlLocalDB.msi from the embedded resource to a temp directory.
    ///   2. Install the MSI silently with msiexec (triggers a UAC prompt).
    ///   3. Create and start the Snap2HTML dedicated instance.
    ///   4. Verify connectivity with a test connection.
    /// </remarks>
    private static bool EnsureLocalDbInstance()
    {
        try
        {
            // Check if LocalDB is already installed
            if (!IsLocalDbInstalled())
            {
                Trace.TraceInformation("[SqlServerBackup] LocalDB not found. Attempting offline installation from embedded resource...");

                if (!InstallLocalDbFromEmbeddedResource())
                {
                    Trace.TraceError("[SqlServerBackup] Failed to install LocalDB from embedded resource. " +
                                     "Full .bak validation will be disabled.");
                    return false;
                }

                Trace.TraceInformation("[SqlServerBackup] LocalDB installed successfully from embedded resource.");
            }

            // Create instance (idempotent — ignore failure if it already exists)
            RunProcess("sqllocaldb.exe", $"create \"{LocalDbInstanceName}\"");

            // Start instance (idempotent — ignore failure if already running)
            RunProcess("sqllocaldb.exe", $"start \"{LocalDbInstanceName}\"");

            // Verify connectivity with a real test connection
            var connectionString = $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=30";
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            Trace.TraceInformation("[SqlServerBackup] LocalDB instance '{0}' is ready.", LocalDbInstanceName);

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError("[SqlServerBackup] Failed to initialize LocalDB instance: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Checks the Windows Registry for any installed version of SQL Server LocalDB.
    /// </summary>
    private static bool IsLocalDbInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions");

            return key?.SubKeyCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts the SqlLocalDB.msi from the embedded resource and installs it silently.
    /// This is a fully offline installation — no internet access is required.
    /// The MSI is bundled in the assembly as <see cref="LocalDbMsiResourceName"/>.
    /// </summary>
    private static bool InstallLocalDbFromEmbeddedResource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Snap2HTML_LocalDB_Setup");

        try
        {
            Directory.CreateDirectory(tempDir);

            var msiPath = Path.Combine(tempDir, "SqlLocalDB.msi");

            // Step 1: Extract MSI from embedded resource
            if (!ExtractEmbeddedResource(LocalDbMsiResourceName, msiPath))
            {
                Trace.TraceError("[SqlServerBackup] Failed to extract embedded SqlLocalDB.msi resource. " +
                                 "Ensure the resource '{0}' is included in the assembly.", LocalDbMsiResourceName);
                return false;
            }

            Trace.TraceInformation("[SqlServerBackup] Extracted SqlLocalDB.msi to '{0}'. Starting silent install...", msiPath);

            // Step 2: Install silently via msiexec (triggers UAC elevation)
            // /l*v enables verbose logging for diagnostics if the install fails
            var installResult = RunProcess("msiexec.exe",
                $"/i \"{msiPath}\" /qn IACCEPTSQLLOCALDBLICENSETERMS=YES /l*v \"{Path.Combine(tempDir, "install.log")}\"",
                elevated: true,
                timeoutMs: 300_000); // 5 min for install

            if (installResult is null)
            {
                // Try to read the install log for diagnostics
                var logPath = Path.Combine(tempDir, "install.log");
                if (File.Exists(logPath))
                {
                    try
                    {
                        var logTail = ReadLastLines(logPath, 20);
                        Trace.TraceError("[SqlServerBackup] MSI install failed. Last log lines:\n{0}", logTail);
                    }
                    catch { /* best effort log reading */ }
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError("[SqlServerBackup] Exception during LocalDB installation: {0}", ex.Message);
            return false;
        }
        finally
        {
            // Clean up temp files
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extracts an embedded resource from the current assembly to a file on disk.
    /// </summary>
    /// <param name="resourceName">The fully qualified embedded resource name.</param>
    /// <param name="destinationPath">The file path to write the resource to.</param>
    /// <returns>True if extraction succeeded, false otherwise.</returns>
    private static bool ExtractEmbeddedResource(string resourceName, string destinationPath)
    {
        try
        {
            using var resourceStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);

            if (resourceStream is null)
                return false;

            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            resourceStream.CopyTo(fileStream);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the last N lines of a text file. Used for reading MSI install logs on failure.
    /// </summary>
    private static string ReadLastLines(string filePath, int lineCount)
    {
        var lines = File.ReadAllLines(filePath);
        var start = Math.Max(0, lines.Length - lineCount);
        return string.Join(Environment.NewLine, lines.Skip(start));
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

    /// <summary>
    /// Runs an external process with the given arguments and returns stdout.
    /// Returns null if the process fails or times out.
    /// </summary>
    /// <param name="fileName">The executable to run.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="elevated">If true, runs with <c>runas</c> verb (triggers UAC prompt).</param>
    /// <param name="timeoutMs">Maximum time to wait for the process to exit.</param>
    private static string? RunProcess(string fileName, string arguments,
        bool elevated = false, int timeoutMs = 120_000)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = elevated,
                CreateNoWindow = !elevated
            };

            if (elevated)
            {
                process.StartInfo.Verb = "runas";
            }
            else
            {
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
            }

            process.Start();

            string? output = null;
            if (!elevated)
            {
                output = process.StandardOutput.ReadToEnd();
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { /* best effort */ }
                return null;
            }

            return process.ExitCode == 0 ? (output ?? string.Empty) : null;
        }
        catch
        {
            return null;
        }
    }
}
