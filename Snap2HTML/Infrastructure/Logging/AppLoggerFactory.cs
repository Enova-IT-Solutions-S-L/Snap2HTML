using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Snap2HTML.Infrastructure.Logging;

/// <summary>
/// Serilog-backed <see cref="IAppLoggerFactory"/> that writes compressed rolling daily log files
/// to <c>&lt;AppDir&gt;/logs/</c>.
/// <para>
/// A single <see cref="SerilogLoggerFactory"/> is created once and kept for the lifetime of the
/// process. Because it is constructed without an explicit <see cref="Serilog.Core.Logger"/>, every
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> it vends routes through
/// <see cref="Log.Logger"/> (Serilog's global) at call time. Calling <see cref="Configure"/> only
/// needs to replace <see cref="Log.Logger"/> — all existing <c>ILogger</c> instances immediately
/// pick up the change without requiring a restart.
/// </para>
/// </summary>
public sealed class AppLoggerFactory : IAppLoggerFactory
{
    // Permanent factory — survives Configure() calls. Uses Log.Logger (global) because
    // no explicit Serilog logger is passed to the constructor.
    private readonly SerilogLoggerFactory _loggerFactory;
    private bool _disposed;
    private string _currentLevel = "Information";

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

        // Start silent; Configure() will set Log.Logger when logging is enabled.
        Log.Logger = Serilog.Core.Logger.None;

        // The factory is created once. It uses Log.Logger via Serilog's global pipeline.
        _loggerFactory = new SerilogLoggerFactory(dispose: false);
    }

    /// <inheritdoc />
    public void Configure(bool enabled, string minimumLevel = "Information")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IsEnabled = enabled;
        _currentLevel = minimumLevel;

        // Close the previous global logger (flushes buffered entries to disk).
        Log.CloseAndFlush();

        if (!enabled)
        {
            Log.Logger = Serilog.Core.Logger.None;
            return;
        }

        Directory.CreateDirectory(LogDirectory);

        var level = ParseLevel(minimumLevel);

        // Replacing Log.Logger is enough — all existing ILogger instances route through it.
        Log.Logger = new LoggerConfiguration()
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
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (!IsEnabled) return;

        // CloseAndFlush writes remaining buffered entries, then Configure reopens the sink.
        Log.CloseAndFlush();
        Configure(true, _currentLevel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Log.CloseAndFlush();
        _loggerFactory.Dispose();
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
