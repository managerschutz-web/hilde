using Hilde.Core;
using Serilog;
using Serilog.Events;

namespace Hilde.Desktop;

/// <summary>Serilog in %LOCALAPPDATA%\HILDE\Logs – niemals Transkriptinhalte loggen.</summary>
internal static class Logging
{
    public static void Configure(HildePaths paths)
    {
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("App", "HILDE")
            .WriteTo.File(Path.Combine(paths.Logs, "hilde-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("HILDE {Version} startet ({Os})",
            typeof(Logging).Assembly.GetName().Version, Environment.OSVersion);
    }
}
