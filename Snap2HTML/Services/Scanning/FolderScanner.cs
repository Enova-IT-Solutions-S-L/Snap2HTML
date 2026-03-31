using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Snap2HTML.Core.Models;
using Snap2HTML.Core.Utilities;
using Snap2HTML.Infrastructure.FileSystem;
using Snap2HTML.Services.Validation;

namespace Snap2HTML.Services.Scanning;

/// <summary>
/// Implementation of IFolderScanner that scans folders for files and metadata.
/// Uses single-pass enumeration with parallel processing via Channels for improved performance.
/// </summary>
public class FolderScanner : IFolderScanner
{
    private readonly IFileSystemAbstraction _fileSystem;
    private readonly IIntegrityValidatorAggregator _integrityValidator;

    public FolderScanner(IFileSystemAbstraction fileSystem)
        : this(fileSystem, IntegrityValidatorAggregator.CreateDefault())
    {
    }

    public FolderScanner(IFileSystemAbstraction fileSystem, IIntegrityValidatorAggregator integrityValidator)
    {
        _fileSystem = fileSystem;
        _integrityValidator = integrityValidator;
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ScanResult();

        try
        {
            var stopwatch = Stopwatch.StartNew();

            // Collect all directories first (single pass with enumeration)
            var dirs = await CollectDirectoriesAsync(
                options.RootFolder,
                options,
                stopwatch,
                progress,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                return result;
            }

            dirs = StringUtils.SortDirList(dirs);

            // Process directories
            stopwatch.Restart();

            // Use ConcurrentDictionary for lock-free parallel writes
            var folders = new ConcurrentDictionary<string, SnappedFolder>();

            // Use parallel processing for large directory sets
            int totFiles;
            if (dirs.Count > 10)
            {
                totFiles = await ProcessDirectoriesParallelAsync(
                    dirs, folders, options, stopwatch, progress, cancellationToken);
            }
            else
            {
                // Sequential processing for small sets
                totFiles = await ProcessDirectoriesSequentialAsync(
                    dirs, folders, options, stopwatch, progress, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.WasCancelled = true;
                return result;
            }

            // Convert to sorted list maintaining order
            result.Folders = dirs
                .Where(d => folders.ContainsKey(d))
                .Select(d => folders[d])
                .ToList();

            // Calculate stats
            CalculateStats(result);
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<List<string>> CollectDirectoriesAsync(
        string rootFolder,
        ScanOptions options,
        Stopwatch stopwatch,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var dirs = new List<string> { rootFolder };
        var queue = new Queue<string>();
        queue.Enqueue(rootFolder);

        await Task.Run(() =>
        {
            while (queue.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                var currentDir = queue.Dequeue();

                try
                {
                    // Use EnumerateDirectories for lazy enumeration (memory efficient)
                    foreach (var d in Directory.EnumerateDirectories(currentDir))
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        var includeThisFolder = ShouldIncludeDirectory(d, options);

                        if (includeThisFolder)
                        {
                            dirs.Add(d);
                            queue.Enqueue(d);

                            if (stopwatch.ElapsedMilliseconds >= 50)
                            {
                                progress?.Report(new ScanProgress
                                {
                                    StatusMessage = $"Getting folders... {dirs.Count}",
                                    FoldersProcessed = dirs.Count,
                                    CurrentItem = d
                                });
                                stopwatch.Restart();
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in CollectDirectoriesAsync(): {ex.Message}");
                }
            }
        }, cancellationToken);

        return dirs;
    }

    private bool ShouldIncludeDirectory(string path, ScanOptions options)
    {
        if (!options.SkipHiddenItems && !options.SkipSystemItems)
            return true;

        try
        {
            var attr = File.GetAttributes(path);

            if (options.SkipHiddenItems && (attr & FileAttributes.Hidden) == FileAttributes.Hidden)
                return false;

            if (options.SkipSystemItems && (attr & FileAttributes.System) == FileAttributes.System)
                return false;
        }
        catch
        {
            // If we can't get attributes, include the directory
        }

        return true;
    }

    private async Task<int> ProcessDirectoriesParallelAsync(
        List<string> dirs,
        ConcurrentDictionary<string, SnappedFolder> folders,
        ScanOptions options,
        Stopwatch stopwatch,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var localTotFiles = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(dirs, parallelOptions, async (dirName, ct) =>
        {
            var folder = await ProcessDirectoryAsync(dirName, options, ct);

            folders[dirName] = folder;
            var newTotal = Interlocked.Add(ref localTotFiles, folder.Files.Count);

            if (stopwatch.ElapsedMilliseconds >= 50)
            {
                progress?.Report(new ScanProgress
                {
                    StatusMessage = $"Reading files... {newTotal}",
                    FilesProcessed = newTotal,
                    FoldersProcessed = folders.Count,
                    CurrentItem = dirName
                });
                stopwatch.Restart();
            }
        });

        return localTotFiles;
    }

    private async Task<int> ProcessDirectoriesSequentialAsync(
        List<string> dirs,
        ConcurrentDictionary<string, SnappedFolder> folders,
        ScanOptions options,
        Stopwatch stopwatch,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totFiles = 0;

        foreach (var dirName in dirs)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var folder = await ProcessDirectoryAsync(dirName, options, cancellationToken);
            folders[dirName] = folder;
            totFiles += folder.Files.Count;

            if (stopwatch.ElapsedMilliseconds >= 50)
            {
                progress?.Report(new ScanProgress
                {
                    StatusMessage = $"Reading files... {totFiles}",
                    FilesProcessed = totFiles,
                    FoldersProcessed = folders.Count,
                    CurrentItem = dirName
                });
                stopwatch.Restart();
            }
        }

        return totFiles;
    }

    private async Task<SnappedFolder> ProcessDirectoryAsync(string dirName, ScanOptions options, CancellationToken ct)
    {
        var folder = CreateSnappedFolder(dirName);
        SetFolderMetadata(folder, dirName);

        var files = await GetFilesInFolderAsync(dirName, options, ct);
        foreach (var file in files)
        {
            folder.Files.Add(file);
        }

        return folder;
    }

    private SnappedFolder CreateSnappedFolder(string dirName)
    {
        if (dirName == Path.GetPathRoot(dirName))
        {
            return new SnappedFolder("", dirName);
        }

        return new SnappedFolder(
            Path.GetFileName(dirName),
            Path.GetDirectoryName(dirName) ?? string.Empty);
    }

    private void SetFolderMetadata(SnappedFolder folder, string dirName)
    {
        try
        {
            folder.ModifiedTimestamp = StringUtils.ToUnixTimestamp(_fileSystem.GetLastWriteTime(dirName).ToLocalTime());
            folder.CreatedTimestamp = StringUtils.ToUnixTimestamp(_fileSystem.GetCreationTime(dirName).ToLocalTime());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex} Exception caught.");
        }
    }

    private async Task<List<SnappedFile>> GetFilesInFolderAsync(string dirName, ScanOptions options, CancellationToken ct)
    {
        var result = new List<SnappedFile>();

        try
        {
            // Use EnumerateFiles for lazy enumeration (memory efficient)
            foreach (var filePath in Directory.EnumerateFiles(dirName))
            {
                var snappedFile = await CreateSnappedFileAsync(filePath, options, ct);
                if (snappedFile.HasValue)
                {
                    result.Add(snappedFile.Value);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex} Exception caught.");
        }

        // Sort files by name
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        return result;
    }

    private async ValueTask<SnappedFile?> CreateSnappedFileAsync(string filePath, ScanOptions options, CancellationToken ct)
    {
        try
        {
            var fi = _fileSystem.GetFileInfo(filePath);
            var isHidden = (fi.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
            var isSystem = (fi.Attributes & FileAttributes.System) == FileAttributes.System;

            if ((isHidden && options.SkipHiddenItems) || (isSystem && options.SkipSystemItems))
            {
                return null;
            }

            var modifiedTimestamp = StringUtils.ToUnixTimestamp(fi.LastWriteTime.ToLocalTime());
            var createdTimestamp = StringUtils.ToUnixTimestamp(fi.CreationTime.ToLocalTime());

            // Compute hash if enabled — opens file once for SHA256
            var hash = string.Empty;
            if (options.EnableHashing)
            {
                hash = ComputeFileHash(filePath);
            }

            // Validate integrity if enabled — truly async, no thread blocking
            var integrityStatus = IntegrityStatus.Unknown;
            if (options.IntegrityLevel != IntegrityValidationLevel.None)
            {
                integrityStatus = await ValidateIntegrityAsync(filePath, options.IntegrityLevel, ct);
            }

            return new SnappedFile(
                Path.GetFileName(filePath),
                fi.Length,
                modifiedTimestamp,
                createdTimestamp,
                hash,
                integrityStatus);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex} Exception caught.");
            return null;
        }
    }

    /// <summary>
    /// Validates file integrity asynchronously without blocking thread pool threads.
    /// </summary>
    private async ValueTask<IntegrityStatus> ValidateIntegrityAsync(
        string filePath,
        IntegrityValidationLevel level,
        CancellationToken ct)
    {
        try
        {
            return await _integrityValidator.ValidateAsync(filePath, level, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error validating integrity for {filePath}: {ex.Message}");
            return IntegrityStatus.Unknown;
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = SHA256.HashData(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error computing hash for {filePath}: {ex.Message}");
            return string.Empty;
        }
    }

    private static void CalculateStats(ScanResult result)
    {
        result.TotalDirectories = result.Folders.Count;
        result.TotalFiles = 0;
        result.TotalSize = 0;

        foreach (var folder in result.Folders)
        {
            foreach (var file in folder.Files)
            {
                result.TotalFiles++;
                result.TotalSize += file.Size;
            }
        }
    }
}
