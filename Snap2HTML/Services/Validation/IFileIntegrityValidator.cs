using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation;

/// <summary>
/// Base interface for file integrity validators.
/// Each implementation handles a specific set of file formats (images, PDFs, documents, etc.).
/// </summary>
public interface IFileIntegrityValidator
{
    /// <summary>
    /// Display name for the format category (e.g., "Images", "Video", "Archives").
    /// </summary>
    string CategoryName { get; }

    /// <summary>
    /// Whether this validator implements full (deep) validation beyond magic bytes.
    /// </summary>
    bool SupportsFullValidation { get; }

    /// <summary>
    /// The set of file extensions this validator supports (e.g., ".jpg", ".pdf").
    /// Extensions must include the leading dot and be lowercase.
    /// </summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Determines whether this validator can handle the given file based on its extension.
    /// </summary>
    /// <param name="filePath">The path to the file to check.</param>
    /// <returns>True if this validator supports the file's extension.</returns>
    bool CanValidate(string filePath);

    /// <summary>
    /// Validates the integrity of a single file.
    /// </summary>
    /// <param name="filePath">The path to the file to validate.</param>
    /// <param name="level">The validation level to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The integrity status of the file.</returns>
    ValueTask<IntegrityStatus> ValidateAsync(
        string filePath,
        IntegrityValidationLevel level,
        CancellationToken ct);

    /// <summary>
    /// Validates the integrity of multiple files in batch using a Channel-based pipeline.
    /// </summary>
    /// <param name="files">The file paths to validate.</param>
    /// <param name="level">The validation level to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An async enumerable of path and status tuples.</returns>
    IAsyncEnumerable<(string Path, IntegrityStatus Status)> ValidateBatchAsync(
        IEnumerable<string> files,
        IntegrityValidationLevel level,
        CancellationToken ct);
}
