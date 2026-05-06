using Microsoft.Extensions.Logging;
using Snap2HTML.Infrastructure.Logging;
using Snap2HTML.Views;

namespace Snap2HTML;

static class Program
{
    /// <summary>Application-wide logger factory — configured from user settings at startup.</summary>
    internal static readonly AppLoggerFactory AppLoggerFactory = new();

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Initialise logging before anything else so early exceptions are captured.
        var settings = Properties.Settings.Default;
        AppLoggerFactory.Configure(settings.LoggingEnabled, settings.LogLevel);

        var logger = AppLoggerFactory.LoggerFactory.CreateLogger("Snap2HTML.Program");

        // Catch unhandled exceptions on the UI thread
        Application.ThreadException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "Unhandled UI-thread exception");
            AppLoggerFactory.Flush();
        };

        // Catch unhandled exceptions on background threads
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                logger.LogCritical(ex,
                    "Unhandled non-UI exception (IsTerminating={IsTerminating})", e.IsTerminating);
            AppLoggerFactory.Flush();
        };

        logger.LogInformation(
            "Application starting. Version={Version}",
            typeof(Program).Assembly.GetName().Version);

        try
        {
            Application.Run(new frmMain());
        }
        finally
        {
            logger.LogInformation("Application shutting down");
            AppLoggerFactory.Dispose();
        }
    }
}

