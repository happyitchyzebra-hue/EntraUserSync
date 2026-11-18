#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Restore, test, publish (self-contained win-x64), and build the EntraUserSync MSI.
  The published agent runs as the logged-on user (logon scheduled task), not LocalSystem.

.PARAMETER Configuration
  Debug or Release (default Release).

.PARAMETER Version
  Override product version (default from Directory.Build.props).

.PARAMETER SkipTests
  Skip unit tests.

.PARAMETER SkipMsi
  Publish the service only; do not build the WiX MSI.

.PARAMETER Sign
  After publish/MSI, Authenticode-sign outputs with signtool.

.PARAMETER SignCertPath
  Path to the .pfx / cert used by signtool (required when -Sign is set).

.PARAMETER SignCertPassword
  Optional password for the signing certificate PFX.

.PARAMETER SignTimestampUrl
  Timestamp server URL (default DigiCert).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $Version,

    [switch] $SkipTests,
    [switch] $SkipMsi,
    [switch] $Sign,
    [string] $SignCertPath,
    [string] $SignCertPassword,
    [string] $SignTimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1]
    }
    else {
        $Version = '1.0.0'
    }
}

$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish\service'
$msiOutDir = Join-Path $artifacts 'msi'
New-Item -ItemType Directory -Force -Path $artifacts, $publishDir, $msiOutDir | Out-Null

Write-Host "==> EntraUserSync build $Version ($Configuration)" -ForegroundColor Cyan

Write-Host '==> dotnet restore' -ForegroundColor Cyan
dotnet restore (Join-Path $root 'EntraUserSync.sln')
if ($LASTEXITCODE -ne 0) { throw "restore failed ($LASTEXITCODE)" }

if (-not $SkipTests) {
    Write-Host '==> dotnet test' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\EntraUserSync.Core.Tests\EntraUserSync.Core.Tests.csproj') `
        -c $Configuration `
        --no-restore `
        --logger 'trx;LogFileName=test-results.trx' `
        --results-directory (Join-Path $artifacts 'test-results')
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }
}

Write-Host '==> publish self-contained win-x64' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish (Join-Path $root 'src\EntraUserSync.Service\EntraUserSync.Service.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version.0 `
    -p:FileVersion=$Version.0 `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

$exePath = Join-Path $publishDir 'EntraUserSync.exe'
if (-not (Test-Path $exePath)) { throw "Published exe not found: $exePath" }

# ---------------------------------------------------------------------------
# AUTHENTICODE SIGNING (optional)
# Uncomment / pass -Sign with -SignCertPath to sign the published exe (and MSI).
# Requires Windows SDK signtool.exe on PATH.
# Example:
#   .\build.ps1 -Sign -SignCertPath C:\certs\codesign.pfx -SignCertPassword '***'
# ---------------------------------------------------------------------------
function Invoke-SignFile {
    param([Parameter(Mandatory)] [string] $Path)
    if (-not $Sign) { return }
    if (-not $SignCertPath) { throw '-Sign requires -SignCertPath' }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { throw 'signtool.exe not found on PATH. Install the Windows SDK.' }

    $signArgs = @(
        'sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $SignTimestampUrl,
        '/f', $SignCertPath
    )
    if ($SignCertPassword) {
        $signArgs += @('/p', $SignCertPassword)
    }
    $signArgs += $Path

    Write-Host "==> signtool $($Path)" -ForegroundColor Yellow
    & signtool.exe @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path ($LASTEXITCODE)" }
}

Invoke-SignFile -Path $exePath

if ($SkipMsi) {
    Write-Host "Skipped MSI. Published: $exePath" -ForegroundColor Green
    return
}

# Windows PowerShell 5.1 does not define $IsWindows (it's $null → -not $null is $true).
$isWindowsHost = ($env:OS -eq 'Windows_NT') -or ($IsWindows -eq $true)
if (-not $isWindowsHost) {
    Write-Host 'WiX MSI build requires Windows. Published exe only.' -ForegroundColor Yellow
    Write-Host "Published: $exePath"
    return
}

Write-Host '==> build MSI (WiX v5)' -ForegroundColor Cyan
$msiProject = Join-Path $root 'installer\EntraUserSync.Installer\EntraUserSync.Installer.wixproj'
# Avoid trailing backslash escaping PowerShell's closing quote (e.g. -p:ServicePublishDir="...\service\").
$publishDirForWix = ($publishDir.TrimEnd('\', '/') -replace '\\', '/') + '/'
dotnet build $msiProject `
    -c $Configuration `
    -p:Version=$Version `
    -p:ServicePublishDir=$publishDirForWix
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }

$builtMsi = Get-ChildItem -Path (Join-Path $root 'installer\EntraUserSync.Installer\bin') -Filter '*.msi' -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $builtMsi) { throw 'MSI output not found under installer/EntraUserSync.Installer/bin' }

$destMsi = Join-Path $msiOutDir "EntraUserSync-$Version.msi"
Copy-Item $builtMsi.FullName $destMsi -Force
# Also place a copy at artifacts\EntraUserSync-<version>.msi as specified.
$flatMsi = Join-Path $artifacts "EntraUserSync-$Version.msi"
Copy-Item $builtMsi.FullName $flatMsi -Force

Invoke-SignFile -Path $flatMsi

Write-Host ''
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  EXE: $exePath"
Write-Host "  MSI: $flatMsi"
