using System.Runtime.CompilerServices;
using Snap2HTML.Core.Models;
using Snap2HTML.Services.Validation.Archive;
using Snap2HTML.Services.Validation.Audio;
using Snap2HTML.Services.Validation.Document;
using Snap2HTML.Services.Validation.Image;
using Snap2HTML.Services.Validation.Pdf;
using Snap2HTML.Services.Validation.Video;

namespace Snap2HTML.Services.Validation;

/// <summary>
/// Aggregator that dispatches file integrity validation to the appropriate
/// format-specific validator based on file extension.
/// New validators can be added by registering them in the constructor.
/// </summary>
public class IntegrityValidatorAggregator : IIntegrityValidatorAggregator
{
    private readonly IFileIntegrityValidator[] _validators;

    /// <summary>
    /// Creates a default aggregator with all built-in validators.
    /// </summary>
    public static IntegrityValidatorAggregator CreateDefault() => new(
        new ImageIntegrityValidator(),
        new PdfIntegrityValidator(),
        new VideoIntegrityValidator(),
        new AudioIntegrityValidator(),
        new ArchiveIntegrityValidator(),
        new DocumentIntegrityValidator());

    /// <summary>
    /// Creates a new aggregator with the specified validators.
    /// Validators are checked in order; the first one that supports a file's extension handles it.
    /// </summary>
    /// <param name="validators">The format-specific validators to register.</param>
    public IntegrityValidatorAggregator(params IFileIntegrityValidator[] validators)
    {
        _validators = validators;
    }

    /// <inheritdoc />
    public IReadOnlyList<FormatSupportInfo> GetSupportedFormats()
    {
        var formats = new List<FormatSupportInfo>();
        foreach (var validator in _validators)
        {
            foreach (var ext in validator.SupportedExtensions.Order(StringComparer.OrdinalIgnoreCase))
            {
                formats.Add(new FormatSupportInfo(
                    validator.CategoryName,
                    ext.TrimStart('.'),
                    SupportsHeaderValidation: true,
                    validator.SupportsFullValidation));
            }
        }
        return formats;
    }

    /// <inheritdoc />
    public ValueTask<IntegrityStatus> ValidateAsync(
        string filePath,
        IntegrityValidationLevel level,
        CancellationToken ct)
    {
        if (level == IntegrityValidationLevel.None)
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.Unknown);
        }

        var validator = FindValidator(filePath);
        if (validator == null)
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.NotSupported);
        }

        return validator.ValidateAsync(filePath, level, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Path, IntegrityStatus Status)> ValidateBatchAsync(
        IEnumerable<string> files,
        IntegrityValidationLevel level,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (level == IntegrityValidationLevel.None)
        {
            yield break;
        }

        // Group files by their validator, then delegate each batch
        var groups = new Dictionary<IFileIntegrityValidator, List<string>>();
        var unsupported = new List<string>();

        foreach (var file in files)
        {
            var validator = FindValidator(file);
            if (validator == null)
            {
                unsupported.Add(file);
                continue;
            }

            if (!groups.TryGetValue(validator, out var list))
            {
                list = [];
                groups[validator] = list;
            }
            list.Add(file);
        }

        // Yield NotSupported for files without a validator
        foreach (var file in unsupported)
        {
            yield return (file, IntegrityStatus.NotSupported);
        }

        // Delegate each group to its validator's batch pipeline
        foreach (var (validator, fileList) in groups)
        {
            await foreach (var result in validator.ValidateBatchAsync(fileList, level, ct))
            {
                yield return result;
            }
        }
    }

    /// <summary>
    /// Finds the first validator that can handle the given file.
    /// </summary>
    private IFileIntegrityValidator? FindValidator(string filePath)
    {
        foreach (var validator in _validators)
        {
            if (validator.CanValidate(filePath))
            {
                return validator;
            }
        }
        return null;
    }
}
