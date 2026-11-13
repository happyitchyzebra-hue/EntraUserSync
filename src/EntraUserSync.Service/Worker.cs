using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Configuration;
using Microsoft.Win32;

namespace EntraUserSync.Service;

/// <summary>
/// Background sync loop for a user-session host (scheduled task / logon run).
/// Not LocalSystem — session unlock is observed via SystemEvents.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ISyncOrchestrator _orchestrator;
    private readonly IAppSettingsProvider _settingsProvider;
    private readonly ILogger<Worker> _logger;
    private CancellationTokenSource? _debounceCts;

    public Worker(
        ISyncOrchestrator orchestrator,
        IAppSettingsProvider settingsProvider,
        ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _settingsProvider = settingsProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _settingsProvider.Changed += OnSettingsChanged;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not subscribe to SessionSwitch events.");
            }
        }

        var settings = _settingsProvider.GetEffective().Settings;
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, settings.StartupDelaySeconds));
        _logger.LogInformation(
            "EntraUserSync worker started as user host. Initial delay {Delay}.",
            startupDelay);

        try
        {
            if (startupDelay > TimeSpan.Zero)
                await Task.Delay(startupDelay, stoppingToken).ConfigureAwait(false);

            ScheduleSync("startup", stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                settings = _settingsProvider.GetEffective().Settings;
                var interval = TimeSpan.FromMinutes(Math.Max(1, settings.SyncIntervalMinutes));
                try
                {
                    await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                    ScheduleSync("interval", stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                try { SystemEvents.SessionSwitch -= OnSessionSwitch; }
                catch { /* ignore */ }
            }

            _settingsProvider.Changed -= OnSettingsChanged;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLogon or SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
        {
            _logger.LogInformation("Session change: {Reason}", e.Reason);
            ScheduleSync($"session:{e.Reason}", CancellationToken.None);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        _logger.LogInformation("Configuration changed; next sync will use the new settings.");

    private void ScheduleSync(string reason, CancellationToken stoppingToken)
    {
        var settings = _settingsProvider.GetEffective().Settings;
        var debounce = TimeSpan.FromSeconds(Math.Max(1, settings.DebounceSeconds));

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = _debounceCts.Token;
        _logger.LogDebug("Sync scheduled ({Reason}), debouncing {Debounce}.", reason, debounce);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounce, token).ConfigureAwait(false);
                _logger.LogInformation("Running sync ({Reason}).", reason);
                var result = await _orchestrator.RunAsync(new SyncRunOptions(), token).ConfigureAwait(false);
                _logger.LogInformation(
                    "Sync finished: {Kind} exit={ExitCode} stage={Stage} message={Message}",
                    result.Kind,
                    (int)result.ExitCode,
                    result.Stage,
                    result.Message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // debounced / shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled sync failed ({Reason}).", reason);
            }
        }, token);
    }
}
