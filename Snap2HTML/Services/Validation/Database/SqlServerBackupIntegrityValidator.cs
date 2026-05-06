using System.Diagnostics;
using System.Net.Http;
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
    /// We read the first 0xE0 (224) bytes — enough to cover the MTF TAPE DBLK
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
    /// If LocalDB is not available, falls back to returning Valid (header-only validation).
    /// </remarks>
    protected override async ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct)
    {
        if (!LocalDbAvailable.Value)
            return IntegrityStatus.Valid;

        var connectionString = $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=30";

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            // RESTORE VERIFYONLY reads the backup and validates its integrity
            // WITH CHECKSUM also verifies page-level checksums when present
            const string sql = "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM";

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@path", filePath);
            command.CommandTimeout = 3600; // Large backups can take a long time

            await command.ExecuteNonQueryAsync(ct);

            return IntegrityStatus.Valid;
        }
        catch (SqlException ex) when (ex.Number is 3013 or 3180 or 3183 or 3201)
        {
            // 3013 = RESTORE VERIFYONLY is terminating abnormally
            // 3180 = Backup set cannot be restored (corrupt or incompatible)
            // 3183 = RESTORE detected an error on page
            // 3201 = Cannot open backup device (permissions / path)
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
    /// Stable Microsoft redirect URL for the SQL Server 2022 Express bootstrapper (SSEI).
    /// The bootstrapper is ~6 MB and supports downloading just the LocalDB MSI via
    /// <c>/ACTION=Download /MEDIATYPE=LocalDB</c>.
    /// </summary>
    private const string SseiDownloadUrl = "https://go.microsoft.com/fwlink/?linkid=2215160";

    /// <summary>
    /// Checks whether SqlLocalDB is installed, and ensures the dedicated
    /// Snap2HTML instance exists and is running.
    /// If LocalDB is not installed, downloads and installs it silently.
    /// Returns true if LocalDB is ready for use, false otherwise.
    /// </summary>
    /// <remarks>
    /// Installation flow when LocalDB is absent:
    ///   1. Download the SQL Server 2022 Express bootstrapper (SSEI) from Microsoft.
    ///   2. Use SSEI to download only the LocalDB MSI (~50 MB).
    ///   3. Install the MSI silently with msiexec (triggers a UAC prompt).
    ///   4. Create and start the Snap2HTML dedicated instance.
    ///   5. Verify connectivity with a test connection.
    /// </remarks>
    private static bool EnsureLocalDbInstance()
    {
        try
        {
            // Check if LocalDB is already installed
            if (!IsLocalDbInstalled())
            {
                if (!DownloadAndInstallLocalDb())
                    return false;
            }

            // Create instance (idempotent — ignore failure if it already exists)
            RunProcess("sqllocaldb.exe", $"create \"{LocalDbInstanceName}\"");

            // Start instance (idempotent — ignore failure if already running)
            RunProcess("sqllocaldb.exe", $"start \"{LocalDbInstanceName}\"");

            // Verify connectivity with a real test connection
            var connectionString = $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=30";
            using var connection = new SqlConnection(connectionString);
            connection.Open();

            return true;
        }
        catch
        {
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
    /// Downloads the SQL Server 2022 Express bootstrapper (SSEI), uses it to
    /// download the LocalDB MSI, and installs it silently.
    /// </summary>
    private static bool DownloadAndInstallLocalDb()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Snap2HTML_LocalDB_Setup");

        try
        {
            Directory.CreateDirectory(tempDir);

            var sseiPath = Path.Combine(tempDir, "SQL2022-SSEI-Expr.exe");
            var mediaDir = Path.Combine(tempDir, "Media");

            // Step 1: Download the SSEI bootstrapper (~6 MB)
            if (!DownloadFile(SseiDownloadUrl, sseiPath))
                return false;

            // Step 2: Use SSEI to download just the LocalDB MSI (~50 MB)
            var downloadResult = RunProcess(sseiPath,
                $"/ACTION=Download /MEDIAPATH=\"{mediaDir}\" /MEDIATYPE=LocalDB /QUIET");

            if (downloadResult is null)
                return false;

            // Step 3: Find the downloaded MSI
            var msiPath = Path.Combine(mediaDir, "SqlLocalDB.msi");
            if (!File.Exists(msiPath))
                return false;

            // Step 4: Install silently via msiexec (triggers UAC elevation)
            var installResult = RunProcess("msiexec.exe",
                $"/i \"{msiPath}\" /qn IACCEPTSQLLOCALDBLICENSETERMS=YES",
                elevated: true,
                timeoutMs: 300_000); // 5 min for install

            return installResult is not null;
        }
        catch
        {
            return false;
        }
        finally
        {
            // Clean up temp files
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Downloads a file from the given URL to a local path.
    /// </summary>
    private static bool DownloadFile(string url, string destinationPath)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = httpClient.GetAsync(url).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            response.Content.CopyToAsync(fs).GetAwaiter().GetResult();

            return true;
        }
        catch
        {
            return false;
        }
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
