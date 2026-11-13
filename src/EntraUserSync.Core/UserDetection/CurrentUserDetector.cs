using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Principal;
using EntraUserSync.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EntraUserSync.Core.UserDetection;

/// <summary>
/// Resolves the UPN of the process identity (the signed-in user).
/// The agent is intended to run as the logged-on user, not as LocalSystem.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CurrentUserDetector(
    IRegistry? registry = null,
    ILogger<CurrentUserDetector>? logger = null) : IUserDetector
{
    public UserDetectionResult Detect(IReadOnlyList<string> allowedUpnSuffixes)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is null)
            {
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.NoUser,
                    Message = "No Windows identity for the current process."
                };
            }

            var sid = identity.User.Value;
            if (IsLocalSystem(identity) || IsServiceAccount(identity))
            {
                logger?.LogError(
                    "EntraUserSync is running as {Name} ({Sid}). It must run as the logged-on user, not as SYSTEM/service.",
                    identity.Name,
                    sid);
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.Failed,
                    Message = "Process is running as SYSTEM or a service account. Install/run EntraUserSync as the logged-on user (scheduled task at logon)."
                };
            }

            var upn = TryGetUpnFromIdentityStore(sid)
                      ?? TryGetUpnFromWindowsIdentity(identity)
                      ?? TryGetUpnFromAccountManagement()
                      ?? TryGetUpnFromActiveDirectory(sid);

            if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@', StringComparison.Ordinal))
            {
                logger?.LogInformation("Current user {Name} has no UPN (likely a local account).", identity.Name);
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.LocalAccount,
                    Message = $"Signed-in account '{identity.Name}' has no UPN (likely a local account)."
                };
            }

            upn = upn.Trim();
            var suffix = upn[(upn.IndexOf('@') + 1)..];
            if (allowedUpnSuffixes.Count > 0 &&
                !allowedUpnSuffixes.Any(s => string.Equals(s, suffix, StringComparison.OrdinalIgnoreCase)))
            {
                logger?.LogInformation("UPN suffix {Suffix} is not in AllowedUpnSuffixes. Skipping.", suffix);
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.SuffixNotAllowed,
                    User = new DetectedUser { Sid = sid, Upn = upn, Source = "filtered" },
                    Message = $"UPN suffix '{suffix}' is not allowed."
                };
            }

            return new UserDetectionResult
            {
                Outcome = UserDetectionOutcome.Found,
                User = new DetectedUser { Sid = sid, Upn = upn, Source = "current-user" }
            };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "User detection failed");
            return new UserDetectionResult
            {
                Outcome = UserDetectionOutcome.Failed,
                Message = ex.Message
            };
        }
    }

    private static bool IsLocalSystem(WindowsIdentity identity)
    {
        if (identity.User is null)
            return false;
        // SID S-1-5-18 = LocalSystem
        return identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid)
               || identity.Name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsServiceAccount(WindowsIdentity identity)
    {
        if (identity.User is null)
            return false;
        return identity.User.IsWellKnown(WellKnownSidType.LocalServiceSid)
               || identity.User.IsWellKnown(WellKnownSidType.NetworkServiceSid)
               || identity.Name.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase);
    }

    private string? TryGetUpnFromIdentityStore(string sid)
    {
        if (registry is null)
            return null;

        var path = $@"SOFTWARE\Microsoft\IdentityStore\Cache\{sid}\IdentityCache\{sid}";
        using var key = registry.OpenSubKey(RegistryHive.LocalMachine, path, writable: false, RegistryView.Registry64)
                     ?? registry.OpenSubKey(RegistryHive.LocalMachine, path, writable: false, RegistryView.Registry32);
        var value = key?.GetValue("UserName") as string;
        if (!string.IsNullOrWhiteSpace(value) && value.Contains('@', StringComparison.Ordinal))
        {
            logger?.LogDebug("Resolved UPN from IdentityStore for {Sid}", sid);
            return value.Trim();
        }

        return null;
    }

    private static string? TryGetUpnFromWindowsIdentity(WindowsIdentity identity)
    {
        // Claims may include UPN on Entra-joined devices.
        foreach (var claim in identity.Claims)
        {
            if (claim.Type is "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn"
                or "upn"
                && claim.Value.Contains('@', StringComparison.Ordinal))
            {
                return claim.Value;
            }
        }

        if (identity.Name.Contains('@', StringComparison.Ordinal))
            return identity.Name;

        return null;
    }

    private string? TryGetUpnFromAccountManagement()
    {
        try
        {
            using var ctx = new PrincipalContext(ContextType.Domain);
            var principal = UserPrincipal.Current;
            if (!string.IsNullOrWhiteSpace(principal?.UserPrincipalName))
            {
                logger?.LogDebug("Resolved UPN from AccountManagement");
                return principal.UserPrincipalName;
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AccountManagement UPN lookup failed");
        }

        return null;
    }

    private string? TryGetUpnFromActiveDirectory(string sid)
    {
        try
        {
            var account = new SecurityIdentifier(sid);
            var binary = new byte[account.BinaryLength];
            account.GetBinaryForm(binary, 0);

            using var searcher = new System.DirectoryServices.DirectorySearcher
            {
                Filter = "(objectSid=" + EscapeSidBytes(binary) + ")",
                SearchScope = System.DirectoryServices.SearchScope.Subtree
            };
            searcher.PropertiesToLoad.Add("userPrincipalName");
            var result = searcher.FindOne();
            var upn = result?.Properties["userPrincipalName"]?.Count > 0
                ? result.Properties["userPrincipalName"][0]?.ToString()
                : null;
            if (!string.IsNullOrWhiteSpace(upn))
            {
                logger?.LogDebug("Resolved UPN from Active Directory for {Sid}", sid);
                return upn.Trim();
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "AD UPN lookup failed for {Sid}", sid);
        }

        return null;
    }

    private static string EscapeSidBytes(byte[] sidBytes) =>
        string.Concat(sidBytes.Select(b => $"\\{b:X2}"));
}
