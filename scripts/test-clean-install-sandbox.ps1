[CmdletBinding()]
param(
    [ValidateRange(3,60)]
    [int]$WaitMinutes = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This test must run on Windows 10/11.'
}

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$VersionManifest = Join-Path $Root 'release\version.json'
$SandboxWorker = Join-Path $Root 'scripts\sandbox-clean-install.ps1'
$BuildDir = Join-Path $Root 'build'
$SandboxExe = Join-Path $env:WINDIR 'System32\WindowsSandbox.exe'

function Assert-RequiredFile {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Content
    )
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

Assert-RequiredFile -Path $VersionManifest
Assert-RequiredFile -Path $SandboxWorker
if (-not (Test-Path -LiteralPath $SandboxExe -PathType Leaf)) {
    throw @'
Windows Sandbox is not installed or enabled.
Use Windows 11 Pro/Enterprise/Education, enable "Windows Sandbox" in "Turn Windows features on or off", restart Windows, and run this test again.
The production KINOJO Meter installation was not changed.
'@
}

try {
    $release = Get-Content -LiteralPath $VersionManifest -Raw | ConvertFrom-Json
}
catch {
    throw "Release version manifest is not valid JSON: $VersionManifest"
}

$Version = [string]$release.version
$FileVersion = [string]$release.fileVersion
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Release version.json contains an invalid version.'
}

$SetupName = "KINOJO_Meter_${Version}_Setup.exe"
$SetupPath = Join-Path $BuildDir $SetupName
Assert-RequiredFile -Path $SetupPath
$SetupInfo = Get-Item -LiteralPath $SetupPath
$SetupHash = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$SetupFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($SetupPath).FileVersion
if ($SetupFileVersion -ne $FileVersion) {
    throw "Setup file version mismatch. Expected=$FileVersion Actual=$SetupFileVersion"
}

$Workspace = Join-Path $BuildDir ("sandbox-clean-install-" + $Version)
$ResultsDir = Join-Path $Workspace 'results'
$ResultJson = Join-Path $ResultsDir 'clean-install-result.json'
$ResultText = Join-Path $ResultsDir 'clean-install-result.txt'
$WsbPath = Join-Path $Workspace "KINOJO_Meter_${Version}_CleanInstall.wsb"

Remove-Item -LiteralPath $Workspace -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $Workspace -ItemType Directory -Force | Out-Null
New-Item -Path $ResultsDir -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath $SetupPath -Destination (Join-Path $Workspace $SetupName) -Force
Copy-Item -LiteralPath $SandboxWorker -Destination (Join-Path $Workspace 'sandbox-clean-install.ps1') -Force

$expected = [ordered]@{
    schemaVersion = 1
    testType = 'FRESH_INSTALL_WINDOWS_SANDBOX'
    product = 'KINOJO Meter'
    version = $Version
    fileVersion = $FileVersion
    setupFileName = $SetupName
    setupSha256 = $SetupHash
    setupSize = [int64]$SetupInfo.Length
    installPath = 'C:\Program Files\KINOJO Meter'
}
Write-Utf8NoBom -Path (Join-Path $Workspace 'expected.json') -Content ($expected | ConvertTo-Json -Depth 5)

$escapedWorkspace = [Security.SecurityElement]::Escape($Workspace)
$wsb = @"
<Configuration>
  <Networking>Enable</Networking>
  <ClipboardRedirection>Enable</ClipboardRedirection>
  <PrinterRedirection>Disable</PrinterRedirection>
  <AudioInput>Disable</AudioInput>
  <VideoInput>Disable</VideoInput>
  <MemoryInMB>4096</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$escapedWorkspace</HostFolder>
      <SandboxFolder>C:\KINOJO_TEST</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\KINOJO_TEST\sandbox-clean-install.ps1</Command>
  </LogonCommand>
</Configuration>
"@
Write-Utf8NoBom -Path $WsbPath -Content $wsb

Write-Host '[1/3] Release artifact validated'
Write-Host "Version : $Version ($FileVersion)"
Write-Host "Setup   : $SetupPath"
Write-Host "SHA-256 : $SetupHash"
Write-Host '[2/3] Starting Windows Sandbox'
Write-Host 'Approve the Windows elevation prompt inside Sandbox if it appears.'
Start-Process -FilePath $SandboxExe -ArgumentList ('"' + $WsbPath + '"') | Out-Null

Write-Host "[3/3] Waiting up to $WaitMinutes minutes for the clean-install report"
$deadline = (Get-Date).AddMinutes($WaitMinutes)
while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $ResultJson -PathType Leaf)) {
    Start-Sleep -Seconds 2
}

if (-not (Test-Path -LiteralPath $ResultJson -PathType Leaf)) {
    throw "The Sandbox report was not created within $WaitMinutes minutes. Keep the Sandbox open, review any UAC or installer message, and run this test again. Workspace: $Workspace"
}

$result = Get-Content -LiteralPath $ResultJson -Raw | ConvertFrom-Json
if ($result.success -ne $true) {
    $message = [string]$result.message
    throw "Clean-install Sandbox validation failed: $message`nReport: $ResultText"
}

Write-Host 'Clean-install Sandbox validation completed.'
Write-Host "Installed version : $($result.installedVersion)"
Write-Host "Installed files   : $($result.managedFileCount)"
Write-Host "Desktop shortcut  : $($result.desktopShortcut)"
Write-Host "Start menu        : $($result.startMenuShortcut)"
Write-Host "Uninstall entry   : $($result.uninstallEntry)"
Write-Host "Report            : $ResultText"
Write-Host 'Visually confirm that the PASS KEY screen is open inside Sandbox, then close the Sandbox window.'
