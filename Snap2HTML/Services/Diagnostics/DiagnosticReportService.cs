using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace Snap2HTML.Services.Diagnostics;

/// <summary>
/// Builds a ZIP diagnostic report containing recent log files and sanitized system information,
/// ready to send to the development team.
/// </summary>
public static class DiagnosticReportService
{
    /// <summary>
    /// Creates a diagnostic ZIP file at <paramref name="destinationPath"/>.
    /// Includes all <c>*.log</c> files from <paramref name="logDirectory"/> and a
    /// <c>system-info.txt</c> with sanitized environment details.
    /// </summary>
    /// <param name="logDirectory">Directory that contains the rolling log files.</param>
    /// <param name="destinationPath">Full path of the ZIP file to create.</param>
    /// <returns>Number of log files included in the report.</returns>
    public static int GenerateReport(string logDirectory, string destinationPath)
    {
        using var zip = ZipFile.Open(destinationPath, ZipArchiveMode.Create);

        // System information
        var sysInfo = BuildSystemInfo();
        var sysEntry = zip.CreateEntry("system-info.txt", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(sysEntry.Open(), Encoding.UTF8))
            writer.Write(sysInfo);

        // Log files
        var logCount = 0;
        if (Directory.Exists(logDirectory))
        {
            foreach (var logFile in Directory.GetFiles(logDirectory, "*.log")
                                             .OrderByDescending(f => f))
            {
                var entryName = Path.Combine("logs", Path.GetFileName(logFile));
                zip.CreateEntryFromFile(logFile, entryName, CompressionLevel.Optimal);
                logCount++;
            }
        }

        return logCount;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildSystemInfo()
    {
        var sb = new StringBuilder();
        var asm = typeof(DiagnosticReportService).Assembly;

        sb.AppendLine("=== Snap2HTML Diagnostic Report ===");
        sb.AppendLine($"Generated         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // ── Application ──────────────────────────────────────────────────────
        sb.AppendLine("--- Application ---");
        sb.AppendLine($"Version           : {asm.GetName().Version}");
        var infoVer = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)
            ?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVer))
            sb.AppendLine($"Informational ver : {infoVer}");
        sb.AppendLine($"Exe path          : {Environment.ProcessPath}");
        sb.AppendLine($"Working directory : {Environment.CurrentDirectory}");
        sb.AppendLine($"Command line      : {Environment.CommandLine}");
        sb.AppendLine();

        // ── Operating System ─────────────────────────────────────────────────
        sb.AppendLine("--- Operating System ---");
        sb.AppendLine($"Description       : {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture      : {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"OS version        : {Environment.OSVersion}");
        sb.AppendLine($"Machine name      : {Environment.MachineName}");
        sb.AppendLine($"User name         : {Environment.UserName}");
        sb.AppendLine($"User domain       : {Environment.UserDomainName}");
        sb.AppendLine($"System directory  : {Environment.SystemDirectory}");
        sb.AppendLine($"UI culture        : {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine($"Is 64-bit OS      : {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"Is 64-bit process : {Environment.Is64BitProcess}");
        sb.AppendLine();

        // ── .NET Runtime ─────────────────────────────────────────────────────
        sb.AppendLine("--- .NET Runtime ---");
        sb.AppendLine($"Framework         : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Architecture      : {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"Runtime ID        : {RuntimeInformation.RuntimeIdentifier}");
        sb.AppendLine();

        // ── Hardware ─────────────────────────────────────────────────────────
        sb.AppendLine("--- Hardware ---");
        sb.AppendLine($"CPU cores (logical): {Environment.ProcessorCount}");
        AppendCpuInfo(sb);
        AppendMemoryInfo(sb);
        AppendDiskInfo(sb);
        sb.AppendLine();

        // ── Process ──────────────────────────────────────────────────────────
        sb.AppendLine("--- Process ---");
        using var proc = Process.GetCurrentProcess();
        sb.AppendLine($"PID               : {proc.Id}");
        sb.AppendLine($"Start time        : {proc.StartTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Up time           : {(DateTime.Now - proc.StartTime):hh\\:mm\\:ss}");
        sb.AppendLine($"Working set       : {proc.WorkingSet64 / 1024 / 1024:N0} MB");
        sb.AppendLine($"Private memory    : {proc.PrivateMemorySize64 / 1024 / 1024:N0} MB");
        sb.AppendLine($"GC total memory   : {GC.GetTotalMemory(false) / 1024 / 1024:N0} MB");
        sb.AppendLine($"Thread count      : {proc.Threads.Count}");
        sb.AppendLine();

        // ── Settings (sanitized) ─────────────────────────────────────────────
        sb.AppendLine("--- Application Settings ---");
        var s = Properties.Settings.Default;
        sb.AppendLine($"LoggingEnabled    : {s.LoggingEnabled}");
        sb.AppendLine($"LogLevel          : {s.LogLevel}");

        return sb.ToString();
    }

    private static void AppendCpuInfo(StringBuilder sb)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                sb.AppendLine($"CPU name          : {obj["Name"]?.ToString()?.Trim()}");
                sb.AppendLine($"CPU physical cores: {obj["NumberOfCores"]}");
                sb.AppendLine($"CPU max clock     : {obj["MaxClockSpeed"]} MHz");
            }
        }
        catch
        {
            sb.AppendLine("CPU name          : (unavailable)");
        }
    }

    private static void AppendMemoryInfo(StringBuilder sb)
    {
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            sb.AppendLine($"Total RAM (GC)    : {gcInfo.TotalAvailableMemoryBytes / 1024 / 1024:N0} MB");

            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                var total = Convert.ToInt64(obj["TotalVisibleMemorySize"]) / 1024;
                var free  = Convert.ToInt64(obj["FreePhysicalMemory"]) / 1024;
                sb.AppendLine($"Total RAM         : {total:N0} MB");
                sb.AppendLine($"Free RAM          : {free:N0} MB");
                sb.AppendLine($"Used RAM          : {total - free:N0} MB");
            }
        }
        catch
        {
            sb.AppendLine("RAM               : (unavailable)");
        }
    }

    private static void AppendDiskInfo(StringBuilder sb)
    {
        try
        {
            var exeRoot = Path.GetPathRoot(Environment.ProcessPath ?? string.Empty);
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                // Only include the drive where the exe lives, plus any fixed drives
                if (drive.DriveType != DriveType.Fixed &&
                    !string.Equals(drive.RootDirectory.FullName, exeRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                var totalGb = drive.TotalSize / 1024.0 / 1024 / 1024;
                var freeGb  = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                sb.AppendLine($"Drive {drive.Name,-4}          : {freeGb:F1} GB free / {totalGb:F1} GB total ({drive.DriveFormat})");
            }
        }
        catch
        {
            sb.AppendLine("Disk              : (unavailable)");
        }
    }
}