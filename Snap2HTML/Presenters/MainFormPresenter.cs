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
    private CancellationTokenSource? _cancellationTokenSource;

    public MainFormPresenter(
        IFolderScanner folderScanner,
        IHtmlGenerator htmlGenerator,
        IMainFormView view)
    {
        _folderScanner = folderScanner;
        _htmlGenerator = htmlGenerator;
        _view = view;
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
                return new SnapshotResult
                {
                    Success = false,
                    WasCancelled = true
                };
            }

            if (!string.IsNullOrEmpty(scanResult.ErrorMessage))
            {
                return new SnapshotResult
                {
                    Success = false,
                    ErrorMessage = scanResult.ErrorMessage
                };
            }

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
                return new SnapshotResult
                {
                    Success = false,
                    WasCancelled = true
                };
            }

            if (!htmlResult.Success)
            {
                _view.ShowError("Error", htmlResult.ErrorMessage ?? "An unknown error occurred.");
                return new SnapshotResult
                {
                    Success = false,
                    ErrorMessage = htmlResult.ErrorMessage
                };
            }

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
