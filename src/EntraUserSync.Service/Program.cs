using EntraUserSync.Core;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Auth;
using EntraUserSync.Core.Configuration;
using EntraUserSync.Core.Graph;
using EntraUserSync.Core.Registry;
using EntraUserSync.Core.Sync;
using EntraUserSync.Core.UserDetection;
using EntraUserSync.Service;
using EntraUserSync.Service.Cli;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;

var isCli = args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
var isWindows = OperatingSystem.IsWindows();

var bootstrapLogDir = isWindows
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EntraUserSync",
        "logs")
    : Path.Combine(Path.GetTempPath(), "EntraUserSync", "logs");
Directory.CreateDirectory(bootstrapLogDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.File(
        path: Path.Combine(bootstrapLogDir, "entrausersync-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true)
    .WriteTo.Console()
    .CreateLogger();

try
{
    if (isWindows && IsRunningAsSystem())
    {
        Log.Fatal(
            "EntraUserSync must run as the logged-on user, not as LocalSystem. " +
            "Use the logon scheduled task installed by the MSI, or launch the exe in the user session.");
        return (int)SyncExitCode.Config;
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();

    // User-session host (scheduled task). Do NOT register as a LocalSystem Windows Service.
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddSingleton<IRegistry, WindowsRegistry>();
    builder.Services.AddSingleton<IConfigValidator, ConfigValidator>();
    builder.Services.AddSingleton<IAppSettingsProvider>(sp =>
        new AppSettingsProvider(
            sp.GetRequiredService<IRegistry>(),
            logger: sp.GetService<ILogger<AppSettingsProvider>>(),
            enableWatchers: !isCli));
    builder.Services.AddSingleton<CertificateHolder>();
    builder.Services.AddSingleton<ICertificateLoader, CertificateLoader>();
    builder.Services.AddSingleton<ITokenProvider>(sp =>
        new MsalTokenProvider(
            () => sp.GetRequiredService<IAppSettingsProvider>().GetEffective().Settings,
            () => sp.GetRequiredService<CertificateHolder>().Current,
            sp.GetService<ILogger<MsalTokenProvider>>()));
    builder.Services.AddHttpClient<IGraphUserClient, GraphUserClient>();
    builder.Services.AddSingleton<IUdfWriter, DattoUdfWriter>();
    builder.Services.AddSingleton<IStatusReporter>(sp =>
    {
        var settings = sp.GetRequiredService<IAppSettingsProvider>().GetEffective().Settings;
        return new StatusReporter(sp.GetRequiredService<IRegistry>(), settings.RegistryView);
    });

    if (isWindows)
        builder.Services.AddSingleton<IUserDetector, CurrentUserDetector>();
    else
        builder.Services.AddSingleton<IUserDetector, UnsupportedUserDetector>();

    builder.Services.AddSingleton<ISyncOrchestrator, SyncOrchestrator>();

    if (!isCli)
        builder.Services.AddHostedService<Worker>();

    using var host = builder.Build();
    ConfigureLoggingFromSettings(host.Services);

    if (isCli)
        return await CliRunner.RunAsync(args, host.Services).ConfigureAwait(false);

    Log.Information("EntraUserSync {Version} user-session host starting", VersionInfo.Current);
    await host.RunAsync().ConfigureAwait(false);
    Log.Information("EntraUserSync host stopped");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "EntraUserSync terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}

static bool IsRunningAsSystem()
{
    try
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return id.User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true;
    }
    catch
    {
        return false;
    }
}

static void ConfigureLoggingFromSettings(IServiceProvider services)
{
    try
    {
        var settings = services.GetRequiredService<IAppSettingsProvider>().GetEffective().Settings;
        Directory.CreateDirectory(settings.LogPath);

        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(settings.LogPath, "entrausersync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, settings.LogRetentionDays),
                shared: true)
            .WriteTo.Console();

        if (OperatingSystem.IsWindows())
        {
            config = config.WriteTo.EventLog(
                source: "EntraUserSync",
                logName: "Application",
                restrictedToMinimumLevel: LogEventLevel.Warning,
                manageEventSource: false);
        }

        Log.Logger = config.CreateLogger();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not reconfigure logging from settings; using bootstrap logger.");
    }
}

file sealed class UnsupportedUserDetector : IUserDetector
{
    public UserDetectionResult Detect(IReadOnlyList<string> allowedUpnSuffixes) =>
        new()
        {
            Outcome = UserDetectionOutcome.Failed,
            Message = "User detection is only supported on Windows."
        };
}
