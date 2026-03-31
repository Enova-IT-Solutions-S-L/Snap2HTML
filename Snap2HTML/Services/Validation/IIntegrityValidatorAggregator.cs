using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation;

/// <summary>
/// Interface for the aggregator that dispatches integrity validation
/// to the appropriate format-specific validator.
/// </summary>
public interface IIntegrityValidatorAggregator
{
    /// <summary>
    /// Returns support information for all registered format validators,
    /// describing which validation levels each extension supports.
    /// </summary>
    IReadOnlyList<FormatSupportInfo> GetSupportedFormats();

    /// <summary>
    /// Validates the integrity of a single file by dispatching to the appropriate validator.
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
    /// Validates the integrity of multiple files in batch by dispatching to appropriate validators.
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
