using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Scanning;

/// <summary>
/// Options for folder scanning operations.
/// </summary>
public class ScanOptions
{
    /// <summary>
    /// The root folder to scan.
    /// </summary>
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Whether to skip hidden files and folders.
    /// </summary>
    public bool SkipHiddenItems { get; set; } = true;

    /// <summary>
    /// Whether to skip system files and folders.
    /// </summary>
    public bool SkipSystemItems { get; set; } = true;

    /// <summary>
    /// Whether to compute file hashes during scanning.
    /// </summary>
    public bool EnableHashing { get; set; }

    /// <summary>
    /// The level of file integrity validation to perform.
    /// </summary>
    public IntegrityValidationLevel IntegrityLevel { get; set; } = IntegrityValidationLevel.None;

    /// <summary>
    /// Maximum degree of parallelism for scanning operations.
    /// Default is the minimum of processor count and 4.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = Math.Min(Environment.ProcessorCount, 4);
}

/// <summary>
/// Progress information for folder scanning operations.
/// </summary>
public class ScanProgress
{
    /// <summary>
    /// The current status message.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// The number of folders processed.
    /// </summary>
    public int FoldersProcessed { get; set; }

    /// <summary>
    /// The number of files processed.
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// The current item being processed.
    /// </summary>
    public string? CurrentItem { get; set; }
}

/// <summary>
/// Result of a folder scan operation.
/// </summary>
public class ScanResult
{
    /// <summary>
    /// The list of scanned folders with their files.
    /// </summary>
    public List<SnappedFolder> Folders { get; set; } = [];

    /// <summary>
    /// Whether the scan was cancelled.
    /// </summary>
    public bool WasCancelled { get; set; }

    /// <summary>
    /// Any error message if the scan failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total number of directories scanned.
    /// </summary>
    public int TotalDirectories { get; set; }

    /// <summary>
    /// Total number of files scanned.
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// Total size of all files in bytes.
    /// </summary>
    public long TotalSize { get; set; }
}

/// <summary>
/// Interface for scanning folder contents.
/// </summary>
public interface IFolderScanner
{
    /// <summary>
    /// Scans a folder and returns its contents asynchronously.
    /// </summary>
    /// <param name="options">The scan options.</param>
    /// <param name="progress">Progress reporter for scan status updates.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>The scan result containing folder and file information.</returns>
    Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
