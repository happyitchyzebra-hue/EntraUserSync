# EntraUserSync

User-session agent that reads the **logged-on user’s** attributes from **Microsoft Entra ID** (Microsoft Graph, app-only certificate auth) and writes them into **Datto RMM** user-defined fields (`HKLM\SOFTWARE\CentraStage\CustomN`).

This replaces the older PowerShell RMM component with a proper installable agent: self-contained **.NET 10** background host + **MSI** (WiX v5).

**Important:** the agent runs as the **logged-on user**, not as LocalSystem. The MSI registers a Task Scheduler logon task (`InteractiveToken` / Interactive users group). It refuses to start if launched as SYSTEM.

## What it does

1. Resolves the current process user’s UPN (IdentityStore / Windows identity / AD) — same idea as `whoami /upn`.
2. Authenticates to Graph with MSAL client-credentials + certificate (token cached in memory). Default cert store: **CurrentUser\My**.
3. Reads configured attributes (default `department`, `jobTitle`).
4. Writes only changed values to Datto `CustomN` REG_SZ values (MSI grants Users write on `HKLM\SOFTWARE\CentraStage`).
5. Publishes run status under `HKLM\SOFTWARE\EntraUserSync\Status` for RMM monitoring.

Triggers: host start at logon (short delay), every `SyncIntervalMinutes` (default 240), and on session unlock / logon / console connect via `SystemEvents` (debounced 30s).

## Requirements

- Windows x64 endpoint (Datto-managed)
- Entra ID app registration with **Application** permission `User.Read.All` + admin consent
- Client certificate in `CurrentUser\My` for each user (or `LocalMachine\My` if you set `CERTSTORE=LocalMachine` and grant the user read on the private key)
- Build machine: .NET 10 SDK; WiX via `WixToolset.Sdk` (MSI build is Windows-only)

## Build

```powershell
# Restore, test, publish win-x64 self-contained, build MSI
.\build.ps1

# Optional Authenticode signing
.\build.ps1 -Sign -SignCertPath C:\certs\codesign.pfx -SignCertPassword '***'
```

Outputs:

- `artifacts\publish\service\EntraUserSync.exe`
- `artifacts\EntraUserSync-<version>.msi`

## Entra app registration

1. Entra ID → App registrations → New registration (accounts in this org).
2. **Certificates & secrets** → Upload the public `.cer` (keep the `.pfx` for endpoints).
3. **API permissions** → Microsoft Graph → Application permissions → `User.Read.All` → **Grant admin consent**.
4. Note **Application (client) ID** and **Directory (tenant) ID** (or your tenant domain).

## Deploy the certificate to endpoints

Because the agent runs **as the user**, install the `.pfx` into **CurrentUser\My** for each interactive user (Datto component “run as logged-in user”, Active Setup, or logon script).

```powershell
# Example in the user session
Import-PfxCertificate -FilePath .\app.pfx -CertStoreLocation Cert:\CurrentUser\My -Password (Read-Host -AsSecureString)
```

If you prefer a machine cert, import to `LocalMachine\My` and pass `CERTSTORE=LocalMachine`, then grant the user (or `Users`) read on the private key.

Copy the certificate **thumbprint** (hex) for MSI properties.

## Silent MSI install

```powershell
msiexec /i EntraUserSync-1.0.0.msi /qn `
  TENANTID=contoso.onmicrosoft.com `
  CLIENTID=34d26ebe-da2c-4155-81f7-7b7776b78b07 `
  CERTTHUMBPRINT=9BF693EFABFDD1261D566856F349BDB1AEC689FE `
  CERTSTORE=CurrentUser `
  CLOUD=Global `
  FIELDMAPPINGS="department=2;jobTitle=3" `
  ALLOWEDUPNSUFFIXES="contoso.com;contoso.onmicrosoft.com" `
  SYNCINTERVALMINUTES=240 `
  CLEARWHENEMPTY=0 `
  /l*v install.log
```

Public properties (also written to `HKLM\SOFTWARE\EntraUserSync`):

| Property | Registry value | Notes |
|---|---|---|
| `TENANTID` | `TenantId` | GUID or domain |
| `CLIENTID` | `ClientId` | App registration GUID |
| `CERTTHUMBPRINT` | `CertificateThumbprint` | Non-hex chars stripped at runtime |
| `CERTSTORE` | `CertificateStore` | `CurrentUser` (default) or `LocalMachine` |
| `CLOUD` | `Cloud` | `Global`, `USGov`, `USGovDoD`, `China` |
| `FIELDMAPPINGS` | `FieldMappings` | `attr=N;attr=N` (N = 1–30) |
| `ALLOWEDUPNSUFFIXES` | `AllowedUpnSuffixes` | `;`-separated domains; empty = allow any UPN |
| `SYNCINTERVALMINUTES` | `SyncIntervalMinutes` | Default 240 |
| `CLEARWHENEMPTY` | `ClearFieldWhenEmpty` | `0`/`1` |
| `REMOVECONFIG` | (uninstall only) | `1` deletes `C:\ProgramData\EntraUserSync` |

Upgrades/repairs without properties keep existing registry values (remember-property pattern).

Install layout:

- Binaries: `C:\Program Files\EntraUserSync\`
- Data/logs: `C:\ProgramData\EntraUserSync\` (Users read/write; kept on uninstall unless `REMOVECONFIG=1`)
- Logon task: `EntraUserSync` (Task Scheduler, **InteractiveToken** — runs as the logged-on user, not SYSTEM)
- Optional file overrides: `C:\ProgramData\EntraUserSync\config.json` (see `config.example.json`)

Config precedence (later wins): built-in defaults → `config.json` → `HKLM\SOFTWARE\EntraUserSync`.

## Datto RMM component example

Create a component that deploys the MSI using site variables:

```bat
msiexec /i "%~dp0EntraUserSync-1.0.0.msi" /qn ^
  TENANTID=%EntraTenantId% ^
  CLIENTID=%EntraClientId% ^
  CERTTHUMBPRINT=%EntraCertThumbprint% ^
  FIELDMAPPINGS="department=2;jobTitle=3" ^
  ALLOWEDUPNSUFFIXES=%EntraAllowedUpnSuffixes% ^
  /l*v "%TEMP%\EntraUserSync-install.log"

IF %ERRORLEVEL% NEQ 0 EXIT /B %ERRORLEVEL%
```

Attach the MSI as a component file. Deploy the `.pfx` with a separate elevated component before installing the agent.

## CLI (same exe)

```powershell
& "C:\Program Files\EntraUserSync\EntraUserSync.exe" --show-config
& "C:\Program Files\EntraUserSync\EntraUserSync.exe" --validate
& "C:\Program Files\EntraUserSync\EntraUserSync.exe" --run-once
& "C:\Program Files\EntraUserSync\EntraUserSync.exe" --run-once --whatif --upn user@contoso.com
```

Exit codes: `0` OK/skipped, `1` config, `2` user detection, `3` cert/auth, `4` Graph, `5` UDF write.

## Status registry (monitoring)

`HKLM\SOFTWARE\EntraUserSync\Status`:

| Value | Meaning |
|---|---|
| `LastRunUtc` | ISO-8601 UTC timestamp |
| `LastResult` | `Success` / `Skipped` / `Failed` |
| `LastError` | Error text when failed |
| `LastUser` | UPN synced |
| `LastStage` | `config` / `user` / `certificate` / `auth` / `graph` / `udf` |
| `CertExpiresUtc` | Certificate not-after (UTC) |
| `Version` | Agent version |

Datto monitor example: alert when `LastResult=Failed` or when `CertExpiresUtc` is within 30 days.

## Logging

- Rolling files: `C:\ProgramData\EntraUserSync\logs\entrausersync-YYYYMMDD.log`
- Windows Event Log source `EntraUserSync` (Warning/Error; start/stop Information)
- Tokens and certificates are never logged

## Troubleshooting

| Symptom | Check |
|---|---|
| Agent won’t start / “must run as logged-on user” | Do not run as SYSTEM; ensure the `EntraUserSync` logon task exists (`schtasks /Query /TN EntraUserSync`) |
| Host won’t start | Event Log / file log; `--show-config` / `--validate` in the user session |
| Skipped: no user | No interactive session; wait for logon |
| Skipped: local account | Device not Entra/hybrid joined, or UPN missing |
| Skipped: suffix | `AllowedUpnSuffixes` |
| Cert errors | Thumbprint, store, private key ACL for SYSTEM, expiry |
| Graph 403 | `User.Read.All` application permission + admin consent |
| Graph 404 | User not in tenant / wrong cloud |
| UDFs not updating in Datto | Confirm `CustomN` under `HKLM\SOFTWARE\CentraStage`; Registry64 vs 32 (see TODO in code) |

## Solution layout

```
EntraUserSync.sln
src/EntraUserSync.Core/       # config, auth, Graph, user detection, UDF writer
src/EntraUserSync.Service/    # User-session host + CLI (not LocalSystem)
installer/EntraUserSync.Installer/  # WiX v5 MSI (logon scheduled task)
tests/EntraUserSync.Core.Tests/
build.ps1
```

Target framework: **.NET 10** (`net10.0`), published self-contained `win-x64` (no runtime install on endpoints).
