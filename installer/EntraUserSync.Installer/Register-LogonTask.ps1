# Registers EntraUserSync to start at user logon for the Users group (NOT LocalSystem).
# Deferred MSI custom actions run as SYSTEM; INTERACTIVE (S-1-5-4) fails in that context.
# Prefer schtasks /RU Users; fall back to XML with GroupId S-1-5-32-545 + InteractiveToken.
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [string] $TaskName = 'EntraUserSync'
)

$ErrorActionPreference = 'Continue'
$logDir = 'C:\ProgramData\EntraUserSync'
$logPath = Join-Path $logDir 'task-register.log'

function Write-Log {
    param([string] $Message)
    $line = '[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message
    try {
        if (-not (Test-Path -LiteralPath $logDir)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    } catch {
        # Best-effort logging; never block registration on log I/O.
    }
    Write-Host $line
}

try {
    $exe = $ExePath.Trim().TrimEnd('\')
    Write-Log "Register-LogonTask starting. TaskName=$TaskName ExePath=$exe"

    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Log "ERROR: Executable not found: $exe"
        exit 1
    }

    Write-Log "Deleting existing task (if any)..."
    & schtasks.exe /Delete /TN $TaskName /F 2>&1 | ForEach-Object { Write-Log "  delete: $_" }
    Write-Log "Delete exit code: $LASTEXITCODE"

    # Primary: create ONLOGON task for the Users group at LIMITED privilege.
    $tr = "`"$exe`""
    Write-Log "Trying schtasks /Create /SC ONLOGON /RU Users /RL LIMITED ..."
    $createOut = & schtasks.exe /Create /TN $TaskName /TR $tr /SC ONLOGON /RU Users /RL LIMITED /F 2>&1
    $createOut | ForEach-Object { Write-Log "  create: $_" }
    $createCode = $LASTEXITCODE
    Write-Log "schtasks /Create exit code: $createCode"

    if ($createCode -eq 0) {
        Write-Log "SUCCESS: Registered scheduled task '$TaskName' via schtasks /RU Users."
        exit 0
    }

    # Fallback: XML with Builtin\Users (S-1-5-32-545) + InteractiveToken + LogonTrigger.
    Write-Log "Primary create failed; falling back to XML registration (GroupId S-1-5-32-545)..."
    $escapedExe = [System.Security.SecurityElement]::Escape($exe)
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts EntraUserSync in the logged-on user session (not LocalSystem).</Description>
    <URI>\$TaskName</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
      <LogonType>InteractiveToken</LogonType>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>$escapedExe</Command>
    </Exec>
  </Actions>
</Task>
"@

    $temp = Join-Path $env:TEMP "EntraUserSync.task.xml"
    [System.IO.File]::WriteAllText($temp, $xml, [System.Text.Encoding]::Unicode)
    Write-Log "Wrote task XML to $temp"

    & schtasks.exe /Delete /TN $TaskName /F 2>&1 | ForEach-Object { Write-Log "  delete-before-xml: $_" }

    $xmlOut = & schtasks.exe /Create /TN $TaskName /XML $temp /F 2>&1
    $xmlOut | ForEach-Object { Write-Log "  xml-create: $_" }
    $xmlCode = $LASTEXITCODE
    Write-Log "schtasks /Create /XML exit code: $xmlCode"
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue

    if ($xmlCode -eq 0) {
        Write-Log "SUCCESS: Registered scheduled task '$TaskName' via XML (Users / InteractiveToken)."
        exit 0
    }

    Write-Log "ERROR: Both schtasks /RU Users and XML fallback failed (codes $createCode / $xmlCode)."
    exit 1
}
catch {
    Write-Log "ERROR: $($_.Exception.Message)"
    Write-Log $_.ScriptStackTrace
    exit 1
}
