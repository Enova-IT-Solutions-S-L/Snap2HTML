using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Services.Validation;

/// <summary>
/// Abstract base class for file integrity validators.
/// Provides the Channel-based producer/consumer pipeline for efficient batch processing
/// and the template method pattern for validation (magic bytes → full validation).
/// Subclasses only need to implement format-specific magic bytes and full validation logic.
/// </summary>
public abstract class FileIntegrityValidatorBase : IFileIntegrityValidator
{
    /// <summary>
    /// Maximum number of bytes to read for magic bytes validation.
    /// Subclasses can override if they need more header bytes.
    /// </summary>
    protected virtual int MagicBytesBufferSize => 16;

    /// <inheritdoc />
    public abstract string CategoryName { get; }

    /// <inheritdoc />
    public virtual bool SupportsFullValidation => false;

    /// <inheritdoc />
    public abstract IReadOnlySet<string> SupportedExtensions { get; }

    /// <inheritdoc />
    public bool CanValidate(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return SupportedExtensions.Contains(extension);
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

        if (!CanValidate(filePath))
        {
            return new ValueTask<IntegrityStatus>(IntegrityStatus.NotSupported);
        }

        return ValidateInternalAsync(filePath, level, ct);
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

        // Create a bounded channel for the producer/consumer pattern
        // Capacity of 2000 provides a good balance between memory usage and throughput
        // for typical directory scanning scenarios with 100K+ files
        const int channelCapacity = 2000;
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(channelCapacity)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Use 2x processor count as I/O-bound operations benefit from more parallelism
        // than CPU-bound tasks, since threads spend most time waiting for disk I/O
        var workerCount = Environment.ProcessorCount * 2;
        var results = Channel.CreateUnbounded<(string Path, IntegrityStatus Status)>();

        // Producer: push files into channel
        var producerTask = Task.Run(async () =>
        {
            try
            {
                foreach (var file in files)
                {
                    if (ct.IsCancellationRequested) break;

                    if (CanValidate(file))
                    {
                        await channel.Writer.WriteAsync(file, ct);
                    }
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        // Consumers: process files from channel
        var consumerTasks = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            await foreach (var filePath in channel.Reader.ReadAllAsync(ct))
            {
                var status = await ValidateInternalAsync(filePath, level, ct);
                await results.Writer.WriteAsync((filePath, status), ct);
            }
        }).ToArray();

        // Wait for all consumers to complete and close results channel
        _ = Task.WhenAll(consumerTasks).ContinueWith(_ =>
        {
            results.Writer.Complete();
        }, ct);

        // Yield results as they become available
        await foreach (var result in results.Reader.ReadAllAsync(ct))
        {
            yield return result;
        }

        await producerTask;
        await Task.WhenAll(consumerTasks);
    }

    /// <summary>
    /// Internal validation logic using the template method pattern:
    /// 1. Validate magic bytes
    /// 2. If level is FullDecode, perform full format-specific validation
    /// </summary>
    private async ValueTask<IntegrityStatus> ValidateInternalAsync(
        string filePath,
        IntegrityValidationLevel level,
        CancellationToken ct)
    {
        try
        {
            // Level 1: Magic bytes validation
            var magicBytesValid = await ValidateMagicBytesAsync(filePath, ct);
            if (!magicBytesValid)
            {
                return IntegrityStatus.InvalidMagicBytes;
            }

            if (level == IntegrityValidationLevel.MagicBytesOnly)
            {
                return IntegrityStatus.Valid;
            }

            // Level 2: Full validation using format-specific logic
            return await ValidateFullAsync(filePath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return IntegrityStatus.DecodingFailed;
        }
    }

    /// <summary>
    /// Validates the magic bytes (file signature) of the file.
    /// Uses ArrayPool for efficient buffer management.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if magic bytes match the expected format signature.</returns>
    protected async ValueTask<bool> ReadAndValidateMagicBytesAsync(string filePath, CancellationToken ct)
    {
        var bufferSize = MagicBytesBufferSize;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
            if (bytesRead == 0) return false;

            return CheckMagicBytes(buffer.AsSpan(0, bytesRead));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Checks the magic bytes buffer against known format signatures.
    /// Subclasses must implement this to define their format-specific signatures.
    /// </summary>
    /// <param name="header">The file header bytes that were read.</param>
    /// <returns>True if the header matches a known signature for this format.</returns>
    protected abstract bool CheckMagicBytes(ReadOnlySpan<byte> header);

    /// <summary>
    /// Validates the magic bytes of the file.
    /// Default implementation uses <see cref="ReadAndValidateMagicBytesAsync"/>.
    /// Subclasses can override for custom magic bytes validation logic.
    /// </summary>
    protected virtual ValueTask<bool> ValidateMagicBytesAsync(string filePath, CancellationToken ct)
    {
        return ReadAndValidateMagicBytesAsync(filePath, ct);
    }

    /// <summary>
    /// Performs full format-specific validation beyond magic bytes.
    /// Subclasses must implement this with their library-specific validation logic.
    /// </summary>
    /// <param name="filePath">The path to the file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The integrity status after full validation.</returns>
    protected abstract ValueTask<IntegrityStatus> ValidateFullAsync(string filePath, CancellationToken ct);
}
