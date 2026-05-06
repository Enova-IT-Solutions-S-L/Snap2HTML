using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Infrastructure.Prerequisites.SqlLocalDb;

/// <summary>
/// Detects and installs SQL Server Express LocalDB.
///
/// Detection checks the Windows Registry for any installed LocalDB version and
/// verifies connectivity to the dedicated "Snap2HTMLValidator" instance.
///
/// Installation extracts the bundled SqlLocalDB.msi embedded resource and runs
/// a silent msiexec install (UAC elevation required), then creates and starts
/// the dedicated LocalDB instance.
/// </summary>
public sealed class SqlLocalDbPrerequisite : PrerequisiteBase, ISqlLocalDbPrerequisite
{
    private const string LocalDbInstanceName = "Snap2HTMLValidator";
    private const string LocalDbMsiResourceName = "Snap2HTML.Resources.Installers.SqlLocalDB.msi";

    private const string RegistryKey =
        @"SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions";

    public SqlLocalDbPrerequisite(ILoggerFactory? loggerFactory = null) : base(loggerFactory) { }

    /// <inheritdoc />
    public override string Id => "SqlLocalDB";

    /// <inheritdoc />
    public override string Name => "SQL Server LocalDB";

    /// <inheritdoc />
    public override string Description =>
        "Required for full integrity validation of SQL Server backup files (.bak). " +
        "Enables RESTORE VERIFYONLY without a full SQL Server installation.";

    /// <inheritdoc />
    public override bool IsRequired => false;

    /// <inheritdoc />
    public override bool CanInstall => true;

    // ─────────────────────────────────────────────────────────────────────────
    // Check
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task CheckAsync(CancellationToken ct = default)
    {
        Status = PrerequisiteStatus.Checking;

        Logger.LogInformation("Checking SqlLocalDB prerequisite");

        await Task.Run(() =>
        {
            try
            {
                if (!IsLocalDbInstalled())
                {
                    Logger.LogInformation("SqlLocalDB not found in registry");
                    Status = PrerequisiteStatus.NotInstalled;
                    return;
                }

                Logger.LogDebug("SqlLocalDB registry entry found. Verifying instance connectivity");

                // LocalDB binaries present; verify the instance is reachable.
                EnsureInstanceRunning();

                var connectionString =
                    $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=10";

                using var connection = new SqlConnection(connectionString);
                connection.Open();

                Logger.LogInformation("SqlLocalDB check passed. Instance={Instance}", LocalDbInstanceName);
                Status = PrerequisiteStatus.Installed;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SqlLocalDB check failed; treating as NotInstalled");
                Status = PrerequisiteStatus.NotInstalled;
            }
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Install
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        Status = PrerequisiteStatus.Installing;

        Logger.LogInformation("Starting SqlLocalDB installation");

        await Task.Run(() =>
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Snap2HTML_LocalDB_Setup");

            try
            {
                progress?.Report("Extracting SQL Server LocalDB installer...");
                Directory.CreateDirectory(tempDir);
                var msiPath = Path.Combine(tempDir, "SqlLocalDB.msi");

                progress?.Report(
                    "Running silent installation (a UAC prompt may appear)...");

                if (!ExtractEmbeddedResource(LocalDbMsiResourceName, msiPath))
                {
                    Logger.LogError("Failed to extract SqlLocalDB MSI from embedded resources");
                    progress?.Report("ERROR: Could not extract the installer from resources.");
                    Status = PrerequisiteStatus.InstallFailed;
                    return;
                }

                Logger.LogInformation("Running msiexec for SqlLocalDB. MsiPath={MsiPath}", msiPath);
                var logPath = Path.Combine(tempDir, "install.log");
                var installResult = RunProcess(
                    "msiexec.exe",
                    $"/i \"{msiPath}\" /qn IACCEPTSQLLOCALDBLICENSETERMS=YES /l*v \"{logPath}\"",
                    elevated: true,
                    timeoutMs: 300_000); // 5 min

                if (installResult is null)
                {
                    var tail = ReadLastLines(logPath, 20);
                    Logger.LogError("SqlLocalDB msiexec failed. Log tail:\n{LogTail}", tail);
                    progress?.Report("ERROR: Installation failed. Check the log files for details.");
                    Status = PrerequisiteStatus.InstallFailed;
                    return;
                }

                Logger.LogInformation("msiexec succeeded. Creating LocalDB instance={Instance}", LocalDbInstanceName);
                progress?.Report("Installation complete. Creating database instance...");

                EnsureInstanceRunning();

                // Verify connectivity
                var connectionString =
                    $@"Server=(localdb)\{LocalDbInstanceName};Integrated Security=true;Connection Timeout=30";

                using var connection = new SqlConnection(connectionString);
                connection.Open();

                Logger.LogInformation("SqlLocalDB installation and connectivity verified successfully");
                progress?.Report("SQL Server LocalDB is ready.");
                Status = PrerequisiteStatus.Installed;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unexpected error during SqlLocalDB installation");
                progress?.Report($"ERROR: {ex.Message}");
                Status = PrerequisiteStatus.InstallFailed;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static bool IsLocalDbInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.SubKeyCount > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates (if absent) and starts the dedicated LocalDB instance.
    /// Both operations are idempotent — errors are silently ignored.
    /// </summary>
    private void EnsureInstanceRunning()
    {
        RunProcess("sqllocaldb.exe", $"create \"{LocalDbInstanceName}\"");
        RunProcess("sqllocaldb.exe", $"start \"{LocalDbInstanceName}\"");
    }
}
