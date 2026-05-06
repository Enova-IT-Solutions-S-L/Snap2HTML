using Microsoft.Extensions.Logging;

namespace Snap2HTML.Infrastructure.Logging;

/// <summary>
/// Manages the application-level Serilog logging configuration.
/// Exposes a standard <see cref="ILoggerFactory"/> so all services stay independent of Serilog.
/// </summary>
public interface IAppLoggerFactory : IDisposable
{
    /// <summary>
    /// Current <see cref="ILoggerFactory"/>.
    /// Always non-null — returns <see cref="NullLoggerFactory.Instance"/> when logging is disabled.
    /// </summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>Directory where rolling log files are written.</summary>
    string LogDirectory { get; }

    /// <summary>
    /// Whether logging is currently active.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Reconfigures the logger (called when the user toggles logging or changes log level).
    /// </summary>
    /// <param name="enabled">Enable or disable file logging.</param>
    /// <param name="minimumLevel">Serilog level name: Verbose, Debug, Information, Warning, Error.</param>
    void Configure(bool enabled, string minimumLevel = "Information");

    /// <summary>Flushes any buffered log entries to disk.</summary>
    void Flush();
}
