using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Snap2HTML.Core.Models;
using Snap2HTML.Services.Generation;
using Snap2HTML.Services.Scanning;

namespace Snap2HTML.Presenters;

/// <summary>
/// Progress information for the main form.
/// </summary>
public class MainFormProgress
{
    /// <summary>
    /// The current status message.
    /// </summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// The percentage complete (0-100), or -1 if indeterminate.
    /// </summary>
    public int PercentComplete { get; set; } = -1;
}

/// <summary>
/// Result of the snapshot operation.
/// </summary>
public class SnapshotResult
{
    /// <summary>
    /// Whether the operation was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Whether the operation was cancelled.
    /// </summary>
    public bool WasCancelled { get; set; }

    /// <summary>
    /// Any error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The path to the generated file.
    /// </summary>
    public string? OutputPath { get; set; }
}

/// <summary>
/// Interface for the main form view.
/// </summary>
public interface IMainFormView
{
    /// <summary>
    /// Updates the progress display.
    /// </summary>
    void UpdateProgress(MainFormProgress progress);

    /// <summary>
    /// Shows an error message to the user.
    /// </summary>
    void ShowError(string title, string message);

    /// <summary>
    /// Sets the form to busy state (processing) or idle state.
    /// </summary>
    void SetBusyState(bool isBusy);
}

/// <summary>
/// Presenter for the main form that orchestrates folder scanning and HTML generation.
/// </summary>
public class MainFormPresenter
{
    private readonly IFolderScanner _folderScanner;
    private readonly IHtmlGenerator _htmlGenerator;
    private readonly IMainFormView _view;
    private readonly ILogger<MainFormPresenter> _logger;
    private CancellationTokenSource? _cancellationTokenSource;

    public MainFormPresenter(
        IFolderScanner folderScanner,
        IHtmlGenerator htmlGenerator,
        IMainFormView view,
        ILoggerFactory? loggerFactory = null)
    {
        _folderScanner = folderScanner;
        _htmlGenerator = htmlGenerator;
        _view = view;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<MainFormPresenter>();
    }

    /// <summary>
    /// Gets whether a snapshot operation is currently in progress.
    /// </summary>
    public bool IsProcessing { get; private set; }

    /// <summary>
    /// Creates a snapshot asynchronously.
    /// </summary>
    public async Task<SnapshotResult> CreateSnapshotAsync(
        SnapSettings settings,
        string appName,
        string appVersion)
    {
        if (IsProcessing)
        {
            return new SnapshotResult
            {
                Success = false,
                ErrorMessage = "A snapshot operation is already in progress."
            };
        }

        IsProcessing = true;
        _cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _cancellationTokenSource.Token;

        _logger.LogInformation(
            "Snapshot started. Root={RootFolder}, IntegrityLevel={IntegrityLevel}, Hash={Hash}",
            settings.RootFolder, settings.IntegrityLevel, settings.EnableHashing);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _view.SetBusyState(true);

            // Create scan options from settings
            var scanOptions = new ScanOptions
            {
                RootFolder = settings.RootFolder,
                SkipHiddenItems = settings.SkipHiddenItems,
                SkipSystemItems = settings.SkipSystemItems,
                EnableHashing = settings.EnableHashing,
                IntegrityLevel = settings.IntegrityLevel
            };

            // Create progress reporter for scanning
            var scanProgress = new Progress<ScanProgress>(p =>
            {
                _view.UpdateProgress(new MainFormProgress
                {
                    StatusMessage = $"{p.StatusMessage} ({p.CurrentItem})",
                    PercentComplete = -1
                });
            });

            // Scan folders
            var scanResult = await _folderScanner.ScanAsync(scanOptions, scanProgress, cancellationToken);

            if (scanResult.WasCancelled)
            {
                _logger.LogInformation("Scan was cancelled");
                return new SnapshotResult
                {
                    Success = false,
                    WasCancelled = true
                };
            }

            if (!string.IsNullOrEmpty(scanResult.ErrorMessage))
            {
                _logger.LogError("Scan returned an error: {ErrorMessage}", scanResult.ErrorMessage);
                return new SnapshotResult
                {
                    Success = false,
                    ErrorMessage = scanResult.ErrorMessage
                };
            }

            _logger.LogInformation(
                "Scan complete. Dirs={Dirs}, Files={Files}, Size={Size} bytes, IntegrityCorrupt={Corrupt}/{Checked}",
                scanResult.TotalDirectories, scanResult.TotalFiles, scanResult.TotalSize,
                scanResult.IntegrityCorruptCount, scanResult.IntegrityCheckedCount);

            // Create HTML generation options
            var htmlOptions = new HtmlGenerationOptions
            {
                Title = settings.Title,
                OutputFile = settings.OutputFile,
                RootFolder = settings.RootFolder,
                LinkFiles = settings.LinkFiles,
                LinkRoot = settings.LinkRoot,
                OpenInBrowser = settings.OpenInBrowser,
                AppName = appName,
                AppVersion = appVersion
            };

            // Create progress reporter for HTML generation
            var htmlProgress = new Progress<HtmlGenerationProgress>(p =>
            {
                _view.UpdateProgress(new MainFormProgress
                {
                    StatusMessage = p.StatusMessage,
                    PercentComplete = p.PercentComplete
                });
            });

            // Generate HTML
            var htmlResult = await _htmlGenerator.GenerateAsync(scanResult, htmlOptions, htmlProgress, cancellationToken);

            if (htmlResult.WasCancelled)
            {
                _logger.LogInformation("HTML generation was cancelled");
                return new SnapshotResult
                {
                    Success = false,
                    WasCancelled = true
                };
            }

            if (!htmlResult.Success)
            {
                _logger.LogError("HTML generation failed: {ErrorMessage}", htmlResult.ErrorMessage);
                _view.ShowError("Error", htmlResult.ErrorMessage ?? "An unknown error occurred.");
                return new SnapshotResult
                {
                    Success = false,
                    ErrorMessage = htmlResult.ErrorMessage
                };
            }

            _logger.LogInformation(
                "Snapshot complete in {Elapsed} ms. Output={OutputPath}",
                sw.ElapsedMilliseconds, htmlResult.OutputPath);

            _view.UpdateProgress(new MainFormProgress
            {
                StatusMessage = "Ready!",
                PercentComplete = 100
            });

            return new SnapshotResult
            {
                Success = true,
                OutputPath = htmlResult.OutputPath
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Snapshot cancelled by user after {Elapsed} ms", sw.ElapsedMilliseconds);
            _view.UpdateProgress(new MainFormProgress
            {
                StatusMessage = "User cancelled"
            });

            return new SnapshotResult
            {
                Success = false,
                WasCancelled = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot failed after {Elapsed} ms", sw.ElapsedMilliseconds);
            _view.ShowError("Error", ex.Message);
            return new SnapshotResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            IsProcessing = false;
            _view.SetBusyState(false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    /// <summary>
    /// Cancels the current snapshot operation.
    /// </summary>
    public void CancelOperation()
    {
        _cancellationTokenSource?.Cancel();
    }
}
