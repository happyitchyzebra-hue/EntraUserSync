using System.Text;
using EntraUserSync.Core;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Auth;
using EntraUserSync.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntraUserSync.Service.Cli;

public static class CliRunner
{
    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        if (HasFlag(args, "--show-config"))
            return ShowConfig(services);

        if (HasFlag(args, "--validate"))
            return await ValidateAsync(services).ConfigureAwait(false);

        if (HasFlag(args, "--run-once"))
            return await RunOnceAsync(args, services).ConfigureAwait(false);

        PrintHelp();
        return (int)SyncExitCode.Config;
    }

    private static int ShowConfig(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IAppSettingsProvider>();
        var effective = provider.GetEffective();
        Console.WriteLine($"EntraUserSync {VersionInfo.Current} — effective configuration");
        Console.WriteLine(new string('-', 72));
        foreach (var item in effective.Provenance.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{item.Name,-28} = {item.Value}");
            Console.WriteLine($"{string.Empty,28}   source: {item.Source}");
        }

        var validation = services.GetRequiredService<IConfigValidator>().Validate(effective.Settings);
        Console.WriteLine(new string('-', 72));
        if (validation.IsValid)
            Console.WriteLine("Validation: OK");
        else
        {
            Console.WriteLine("Validation: FAILED");
            foreach (var error in validation.Errors)
                Console.WriteLine($"  - {error}");
        }

        return validation.IsValid ? 0 : (int)SyncExitCode.Config;
    }

    private static async Task<int> ValidateAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Cli");
        var provider = services.GetRequiredService<IAppSettingsProvider>();
        var settings = provider.GetEffective().Settings;
        var validation = services.GetRequiredService<IConfigValidator>().Validate(settings);
        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
                Console.Error.WriteLine(error);
            return (int)SyncExitCode.Config;
        }

        var store = settings.CertificateStore == CertificateStoreLocation.CurrentUser
            ? System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser
            : System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine;
        var certLoader = services.GetRequiredService<ICertificateLoader>();
        var cert = certLoader.Load(settings.CertificateThumbprint, store, settings.CertExpiryWarningDays);
        if (!cert.Success || cert.Certificate is null)
        {
            Console.Error.WriteLine(cert.Error);
            return (int)SyncExitCode.CertAuth;
        }

        if (cert.IsNearExpiry)
            Console.WriteLine($"WARNING: certificate expires {cert.NotAfterUtc:u}");

        var holder = services.GetRequiredService<CertificateHolder>();
        holder.Set(cert.Certificate);
        var tokens = services.GetRequiredService<ITokenProvider>();
        tokens.Reset();

        try
        {
            var token = await tokens.GetAccessTokenAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("Token acquisition returned an empty token.");
                return (int)SyncExitCode.CertAuth;
            }

            Console.WriteLine("Configuration, certificate, and token acquisition: OK");
            logger.LogInformation("Validate succeeded. CertExpires={Expires:u}", cert.NotAfterUtc);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Token acquisition failed: {ex.Message}");
            return (int)SyncExitCode.CertAuth;
        }
    }

    private static async Task<int> RunOnceAsync(string[] args, IServiceProvider services)
    {
        var whatIf = HasFlag(args, "--whatif") || HasFlag(args, "--what-if");
        var upn = GetOption(args, "--upn");
        var orchestrator = services.GetRequiredService<ISyncOrchestrator>();
        var result = await orchestrator.RunAsync(new SyncRunOptions
        {
            WhatIf = whatIf,
            OverrideUpn = upn
        }).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append($"Result={result.Kind} Exit={(int)result.ExitCode} Stage={result.Stage}");
        if (!string.IsNullOrWhiteSpace(result.User))
            sb.Append($" User={result.User}");
        if (!string.IsNullOrWhiteSpace(result.Message))
            sb.Append($" Message={result.Message}");
        Console.WriteLine(sb.ToString());
        return (int)result.ExitCode;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            EntraUserSync — user-session agent for Entra ID → Datto RMM UDFs
            (Runs as the logged-on user, not LocalSystem.)

            Usage:
              EntraUserSync.exe                         Run user-session host (logon task)
              EntraUserSync.exe --run-once [--whatif] [--upn user@domain]
              EntraUserSync.exe --validate
              EntraUserSync.exe --show-config
              EntraUserSync.exe --help

            Exit codes:
              0 OK / skipped
              1 configuration
              2 user detection
              3 certificate / auth
              4 Graph
              5 writing UDFs
            """);
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }
}
