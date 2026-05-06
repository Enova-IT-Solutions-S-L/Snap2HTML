using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Infrastructure.Prerequisites;

/// <summary>
/// Base class for prerequisite implementations.
/// Provides shared helpers: <see cref="RunProcess"/> and <see cref="ExtractEmbeddedResource"/>.
/// </summary>
public abstract class PrerequisiteBase : IPrerequisite
{
    private readonly ILogger _logger;

    /// <summary>Logger available to subclasses.</summary>
    protected ILogger Logger => _logger;

    protected PrerequisiteBase(ILoggerFactory? loggerFactory = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger(GetType().FullName ?? GetType().Name);
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public virtual bool IsRequired => false;

    /// <inheritdoc />
    public virtual bool CanInstall => false;

    /// <inheritdoc />
    public PrerequisiteStatus Status { get; protected set; } = PrerequisiteStatus.Unknown;

    /// <inheritdoc />
    public abstract Task CheckAsync(CancellationToken ct = default);

    /// <inheritdoc />
    public virtual Task InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.CompletedTask;

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts an embedded resource from the executing assembly to a file on disk.
    /// </summary>
    /// <param name="resourceName">Fully qualified embedded resource name.</param>
    /// <param name="destinationPath">Target file path to write the resource to.</param>
    /// <returns><see langword="true"/> if extraction succeeded.</returns>
    protected bool ExtractEmbeddedResource(string resourceName, string destinationPath)
    {
        try
        {
            using var resourceStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);

            if (resourceStream is null)
            {
                _logger.LogError(
                    "Embedded resource '{ResourceName}' not found in assembly.", resourceName);
                return false;
            }

            using var fileStream = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            resourceStream.CopyTo(fileStream);

            _logger.LogDebug(
                "Extracted resource '{ResourceName}' to '{DestinationPath}'.",
                resourceName, destinationPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to extract resource '{ResourceName}' to '{DestinationPath}'.",
                resourceName, destinationPath);
            return false;
        }
    }

    /// <summary>
    /// Runs an external process and waits for it to finish.
    /// </summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="elevated">
    /// When <see langword="true"/>, uses the <c>runas</c> verb (triggers a UAC prompt).
    /// </param>
    /// <param name="timeoutMs">Maximum milliseconds to wait before killing the process.</param>
    /// <returns>
    /// Standard output of the process, or <see langword="null"/> if the process failed,
    /// timed out, or was run elevated (stdout is unavailable when elevated).
    /// </returns>
    protected string? RunProcess(
        string fileName,
        string arguments,
        bool elevated = false,
        int timeoutMs = 120_000)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = elevated,
                CreateNoWindow = !elevated,
            };

            if (elevated)
            {
                process.StartInfo.Verb = "runas";
            }
            else
            {
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
            }

            process.Start();

            _logger.LogDebug("Started process '{FileName}' with args: {Arguments}", fileName, arguments);

            string? output = null;
            if (!elevated)
            {
                output = process.StandardOutput.ReadToEnd();
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { /* best effort */ }
                _logger.LogWarning(
                    "Process '{FileName} {Arguments}' timed out after {TimeoutMs} ms.",
                    fileName, arguments, timeoutMs);
                return null;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Process '{FileName} {Arguments}' exited with code {ExitCode}.",
                    fileName, arguments, process.ExitCode);
                return null;
            }

            return output ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to run '{FileName} {Arguments}'.", fileName, arguments);
            return null;
        }
    }

    /// <summary>
    /// Reads the last <paramref name="lineCount"/> lines of a text file.
    /// Returns an empty string if the file cannot be read.
    /// </summary>
    protected static string ReadLastLines(string filePath, int lineCount)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var start = Math.Max(0, lines.Length - lineCount);
            return string.Join(Environment.NewLine, lines.Skip(start));
        }
        catch
        {
            return string.Empty;
        }
    }
}
