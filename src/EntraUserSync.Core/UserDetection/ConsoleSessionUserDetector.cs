using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using EntraUserSync.Core.Abstractions;
using EntraUserSync.Core.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace EntraUserSync.Core.UserDetection;

[SupportedOSPlatform("windows")]
public sealed class ConsoleSessionUserDetector(
    IRegistry registry,
    ILogger<ConsoleSessionUserDetector>? logger = null) : IUserDetector
{
    public UserDetectionResult Detect(IReadOnlyList<string> allowedUpnSuffixes)
    {
        try
        {
            if (!TryGetSessionId(out var sessionId, out var ambiguous))
            {
                if (ambiguous)
                {
                    logger?.LogWarning("Multiple active sessions detected (RDS). Skipping sync.");
                    return new UserDetectionResult
                    {
                        Outcome = UserDetectionOutcome.AmbiguousSessions,
                        Message = "Multiple active sessions detected; cannot determine a single signed-in user."
                    };
                }

                logger?.LogInformation("No interactive user session found.");
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.NoUser,
                    Message = "No interactive user session found."
                };
            }

            if (!TryGetUserSid(sessionId, out var sid, out var sidError))
            {
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.Failed,
                    Message = sidError ?? "Failed to resolve session user SID."
                };
            }

            var upn = TryGetUpnFromIdentityStore(sid) ?? TryGetUpnFromActiveDirectory(sid);
            if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@', StringComparison.Ordinal))
            {
                logger?.LogInformation("User SID {Sid} appears to be a local account or has no UPN.", sid);
                return new UserDetectionResult
                {
                    Outcome = UserDetectionOutcome.LocalAccount,
                    Message = "Signed-in account has no UPN (likely a local account)."
                };
            }

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
                User = new DetectedUser
                {
                    Sid = sid,
                    Upn = upn,
                    Source = "detected"
                }
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

    private bool TryGetSessionId(out uint sessionId, out bool ambiguous)
    {
        ambiguous = false;
        sessionId = NativeMethods.WTSGetActiveConsoleSessionId();
        if (sessionId is not 0xFFFFFFFF and not 0)
            return true;

        if (!NativeMethods.WTSEnumerateSessionsW(
                NativeMethods.WTS_CURRENT_SERVER_HANDLE,
                0,
                1,
                out var sessionInfoPtr,
                out var count))
        {
            sessionId = 0;
            return false;
        }

        try
        {
            var active = new List<uint>();
            var structSize = Marshal.SizeOf<NativeMethods.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var current = IntPtr.Add(sessionInfoPtr, i * structSize);
                var info = Marshal.PtrToStructure<NativeMethods.WTS_SESSION_INFO>(current);
                if (info.State == NativeMethods.WTS_CONNECTSTATE_CLASS.WTSActive && info.SessionId > 0)
                    active.Add((uint)info.SessionId);
            }

            if (active.Count == 1)
            {
                sessionId = active[0];
                return true;
            }

            if (active.Count > 1)
            {
                ambiguous = true;
                sessionId = 0;
                return false;
            }

            sessionId = 0;
            return false;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionInfoPtr);
        }
    }

    private static bool TryGetUserSid(uint sessionId, out string sid, out string? error)
    {
        sid = string.Empty;
        error = null;

        if (!NativeMethods.WTSQueryUserToken(sessionId, out var token))
        {
            error = $"WTSQueryUserToken failed for session {sessionId}. Win32={Marshal.GetLastWin32Error()}";
            return false;
        }

        try
        {
            NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenUser, buffer, length, out _))
                {
                    error = $"GetTokenInformation failed. Win32={Marshal.GetLastWin32Error()}";
                    return false;
                }

                var tokenUser = Marshal.PtrToStructure<NativeMethods.TOKEN_USER>(buffer);
                if (!NativeMethods.ConvertSidToStringSidW(tokenUser.User.Sid, out var sidPtr))
                {
                    error = $"ConvertSidToStringSid failed. Win32={Marshal.GetLastWin32Error()}";
                    return false;
                }

                try
                {
                    sid = Marshal.PtrToStringUni(sidPtr) ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(sid);
                }
                finally
                {
                    NativeMethods.LocalFree(sidPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private string? TryGetUpnFromIdentityStore(string sid)
    {
        // HKLM\SOFTWARE\Microsoft\IdentityStore\Cache\<SID>\IdentityCache\<SID> value UserName
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

    private string? TryGetUpnFromActiveDirectory(string sid)
    {
        try
        {
            var account = new SecurityIdentifier(sid);
            using var searcher = new DirectorySearcher
            {
                Filter = $"(objectSid={account.Value})",
                PropertiesToLoad = { "userPrincipalName" },
                SearchScope = SearchScope.Subtree
            };

            var binary = new byte[account.BinaryLength];
            account.GetBinaryForm(binary, 0);
            searcher.Filter = "(objectSid=" + EscapeSidBytes(binary) + ")";
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

    private static string EscapeSidBytes(byte[] sidBytes)
    {
        // LDAP binary filter: \xx\xx...
        return string.Concat(sidBytes.Select(b => $"\\{b:X2}"));
    }
}
