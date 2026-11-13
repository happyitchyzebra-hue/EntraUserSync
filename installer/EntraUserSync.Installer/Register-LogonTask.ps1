# Registers EntraUserSync to start at user logon in the interactive user context (NOT LocalSystem).
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [string] $TaskName = 'EntraUserSync'
)

$ErrorActionPreference = 'Stop'
$exe = $ExePath.TrimEnd('\')
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Executable not found: $exe"
}

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts EntraUserSync in the logged-on user session (not LocalSystem).</Description>
    <URI>\$TaskName</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-4</GroupId>
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
      <Command>$([System.Security.SecurityElement]::Escape($exe))</Command>
    </Exec>
  </Actions>
</Task>
"@

$temp = Join-Path $env:TEMP "EntraUserSync.task.xml"
# Task Scheduler XML must be Unicode
[System.IO.File]::WriteAllText($temp, $xml, [System.Text.Encoding]::Unicode)

schtasks.exe /Delete /TN $TaskName /F 2>$null | Out-Null
& schtasks.exe /Create /TN $TaskName /XML $temp /F
if ($LASTEXITCODE -ne 0) {
    throw "schtasks /Create failed with exit code $LASTEXITCODE"
}
Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
Write-Host "Registered scheduled task '$TaskName' (InteractiveToken / logon)."
