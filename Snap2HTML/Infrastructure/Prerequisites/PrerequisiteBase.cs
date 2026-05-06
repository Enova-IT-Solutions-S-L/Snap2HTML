using System.Diagnostics;
using System.Reflection;
using Snap2HTML.Core.Models;

namespace Snap2HTML.Infrastructure.Prerequisites;

/// <summary>
/// Base class for prerequisite implementations.
/// Provides shared helpers: <see cref="RunProcess"/> and <see cref="ExtractEmbeddedResource"/>.
/// </summary>
public abstract class PrerequisiteBase : IPrerequisite
{
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
    protected static bool ExtractEmbeddedResource(string resourceName, string destinationPath)
    {
        try
        {
            using var resourceStream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resourceName);

            if (resourceStream is null)
            {
                Trace.TraceError(
                    "[Prerequisites] Embedded resource '{0}' not found in assembly.", resourceName);
                return false;
            }

            using var fileStream = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            resourceStream.CopyTo(fileStream);

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "[Prerequisites] Failed to extract resource '{0}' to '{1}': {2}",
                resourceName, destinationPath, ex.Message);
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
    protected static string? RunProcess(
        string fileName,
        string arguments,
        bool elevated = false,
        int timeoutMs = 120_000)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
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

            string? output = null;
            if (!elevated)
            {
                output = process.StandardOutput.ReadToEnd();
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch { /* best effort */ }
                Trace.TraceWarning(
                    "[Prerequisites] Process '{0} {1}' timed out after {2} ms.",
                    fileName, arguments, timeoutMs);
                return null;
            }

            return process.ExitCode == 0 ? (output ?? string.Empty) : null;
        }
        catch (Exception ex)
        {
            Trace.TraceError(
                "[Prerequisites] Failed to run '{0} {1}': {2}", fileName, arguments, ex.Message);
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
