using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Snap2HTML.Infrastructure.Logging;

/// <summary>
/// Serilog-backed <see cref="IAppLoggerFactory"/> that writes compressed rolling daily log files
/// to <c>&lt;AppDir&gt;/logs/</c>.
/// When logging is disabled a <see cref="NullLoggerFactory"/> is returned so call-sites
/// generate zero overhead.
/// </summary>
public sealed class AppLoggerFactory : IAppLoggerFactory
{
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private Serilog.Core.Logger? _serilogLogger;
    private bool _disposed;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory => _loggerFactory;

    /// <inheritdoc />
    public string LogDirectory { get; }

    /// <inheritdoc />
    public bool IsEnabled { get; private set; }

    public AppLoggerFactory()
    {
        // Place logs next to the executable so the app stays portable
        var appDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppDomain.CurrentDomain.BaseDirectory;
        LogDirectory = Path.Combine(appDir, "logs");
    }

    /// <inheritdoc />
    public void Configure(bool enabled, string minimumLevel = "Information")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Tear down the previous logger
        if (_loggerFactory is not NullLoggerFactory)
        {
            _loggerFactory.Dispose();
            _loggerFactory = NullLoggerFactory.Instance;
        }

        _serilogLogger?.Dispose();
        _serilogLogger = null;

        IsEnabled = enabled;

        if (!enabled)
            return;

        Directory.CreateDirectory(LogDirectory);

        var level = ParseLevel(minimumLevel);

        _serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.WithProperty("AppVersion", GetAppVersion())
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "snap2html-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 5,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _loggerFactory = new SerilogLoggerFactory(_serilogLogger, dispose: false);
    }

    /// <inheritdoc />
    public void Flush()
    {
        // Serilog flushes synchronously when the underlying logger is closed
        _serilogLogger?.Dispose();

        if (IsEnabled)
        {
            // Recreate the same sink so logging continues after a flush
            var level = _serilogLogger is not null
                ? LogEventLevel.Information   // fallback; real level is already baked in
                : LogEventLevel.Information;

            Directory.CreateDirectory(LogDirectory);

            _serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .Enrich.WithProperty("AppVersion", GetAppVersion())
                .WriteTo.File(
                    path: Path.Combine(LogDirectory, "snap2html-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 5,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            if (_loggerFactory is not NullLoggerFactory)
                _loggerFactory.Dispose();

            _loggerFactory = new SerilogLoggerFactory(_serilogLogger, dispose: false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_loggerFactory is not NullLoggerFactory)
            _loggerFactory.Dispose();

        _serilogLogger?.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static LogEventLevel ParseLevel(string level) => level.ToUpperInvariant() switch
    {
        "VERBOSE" or "TRACE"   => LogEventLevel.Verbose,
        "DEBUG"                => LogEventLevel.Debug,
        "WARNING" or "WARN"    => LogEventLevel.Warning,
        "ERROR"                => LogEventLevel.Error,
        "FATAL" or "CRITICAL"  => LogEventLevel.Fatal,
        _                      => LogEventLevel.Information,
    };

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var attr = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
        return attr?.InformationalVersion ?? "unknown";
    }
}
