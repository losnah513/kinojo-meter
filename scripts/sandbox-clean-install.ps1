$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = 'C:\KINOJO_TEST'
$ExpectedPath = Join-Path $Root 'expected.json'
$ResultsDir = Join-Path $Root 'results'
$ResultJson = Join-Path $ResultsDir 'clean-install-result.json'
$ResultText = Join-Path $ResultsDir 'clean-install-result.txt'
$Transcript = Join-Path $ResultsDir 'sandbox-transcript.txt'

New-Item -Path $ResultsDir -ItemType Directory -Force | Out-Null
Start-Transcript -Path $Transcript -Force | Out-Null

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Content
    )
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Get-FileSha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-ShortcutFile {
    param([Parameter(Mandatory=$true)][string[]]$Paths)
    foreach ($path in $Paths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) { return $true }
    }
    return $false
}

$result = [ordered]@{
    schemaVersion = 1
    testType = 'FRESH_INSTALL_WINDOWS_SANDBOX'
    success = $false
    message = ''
    testedAtUtc = [DateTime]::UtcNow.ToString('o')
    installedVersion = ''
    installedFileVersion = ''
    managedFileCount = 0
    desktopShortcut = $false
    startMenuShortcut = $false
    uninstallEntry = $false
    applicationRunning = $false
}

try {
    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) {
        throw 'expected.json is missing from the mapped Sandbox folder.'
    }
    $expected = Get-Content -LiteralPath $ExpectedPath -Raw | ConvertFrom-Json
    $setupPath = Join-Path $Root ([string]$expected.setupFileName)
    $installPath = [string]$expected.installPath
    $uninstallKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KINOJO Meter'

    if (Test-Path -LiteralPath $installPath) {
        throw "The Sandbox is not clean. Install path already exists: $installPath"
    }
    if (Test-Path -LiteralPath $uninstallKey) {
        throw 'The Sandbox is not clean. KINOJO Meter uninstall registry entry already exists.'
    }
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Setup file is missing: $setupPath"
    }

    $setupInfo = Get-Item -LiteralPath $setupPath
    if ([int64]$setupInfo.Length -ne [int64]$expected.setupSize) {
        throw 'Setup file size does not match expected.json.'
    }
    if ((Get-FileSha256 -Path $setupPath) -ne ([string]$expected.setupSha256).ToLowerInvariant()) {
        throw 'Setup SHA-256 does not match expected.json.'
    }
    $setupFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath).FileVersion
    if ($setupFileVersion -ne [string]$expected.fileVersion) {
        throw "Setup file version mismatch. Expected=$($expected.fileVersion) Actual=$setupFileVersion"
    }

    Write-Host 'Starting the unified installer in clean-install mode...'
    $arguments = '/silent /launch /path "' + $installPath + '"'
    $setupProcess = Start-Process -FilePath $setupPath -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    if ($setupProcess.ExitCode -ne 0) {
        throw "Setup exited with code $($setupProcess.ExitCode)."
    }
    Start-Sleep -Seconds 2

    foreach ($required in @(
        'KINOJO.Meter.exe',
        'KINOJO.Meter.exe.config',
        'KINOJO.Meter.Setup.exe',
        'version.json',
        'install-manifest.json',
        'SharpPcap.dll',
        'PacketDotNet.dll',
        'WinDivert.dll',
        'WinDivert64.sys'
    )) {
        $requiredPath = Join-Path $installPath $required
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Installed file is missing: $required"
        }
    }

    $installedRelease = Get-Content -LiteralPath (Join-Path $installPath 'version.json') -Raw | ConvertFrom-Json
    if ([string]$installedRelease.version -ne [string]$expected.version) {
        throw 'Installed version.json release version does not match expected.json.'
    }
    if ([string]$installedRelease.fileVersion -ne [string]$expected.fileVersion) {
        throw 'Installed version.json file version does not match expected.json.'
    }

    $appPath = Join-Path $installPath 'KINOJO.Meter.exe'
    $installedFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($appPath).FileVersion
    if ($installedFileVersion -ne [string]$expected.fileVersion) {
        throw "Installed EXE file version mismatch. Expected=$($expected.fileVersion) Actual=$installedFileVersion"
    }

    $manifest = Get-Content -LiteralPath (Join-Path $installPath 'install-manifest.json') -Raw | ConvertFrom-Json
    $managedFiles = @($manifest.ManagedFiles)
    if ($managedFiles.Count -lt 7) {
        throw 'install-manifest.json does not contain the expected managed file list.'
    }
    foreach ($record in $managedFiles) {
        $relative = ([string]$record.Path).Replace('/', '\')
        $installed = Join-Path $installPath $relative
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) {
            throw "Managed file is missing: $relative"
        }
        $info = Get-Item -LiteralPath $installed
        if ([int64]$info.Length -ne [int64]$record.Size) {
            throw "Managed file size mismatch: $relative"
        }
        if ((Get-FileSha256 -Path $installed) -ne ([string]$record.Sha256).ToLowerInvariant()) {
            throw "Managed file SHA-256 mismatch: $relative"
        }
    }

    if (-not (Test-Path -LiteralPath $uninstallKey)) {
        throw 'The uninstall registry entry was not created.'
    }
    $registry = Get-ItemProperty -LiteralPath $uninstallKey
    if ([string]$registry.DisplayVersion -ne [string]$expected.version) {
        throw 'The uninstall registry version does not match the installed version.'
    }
    if ([string]$registry.InstallLocation -ne $installPath) {
        throw 'The uninstall registry install location does not match the expected path.'
    }

    $desktopShortcut = Test-ShortcutFile -Paths @(
        'C:\Users\Public\Desktop\KINOJO Meter.lnk',
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'KINOJO Meter.lnk')
    )
    $startMenuShortcut = Test-ShortcutFile -Paths @(
        'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\KINOJO Meter\KINOJO Meter.lnk',
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\KINOJO Meter\KINOJO Meter.lnk')
    )
    if (-not $desktopShortcut) { throw 'The desktop shortcut was not created.' }
    if (-not $startMenuShortcut) { throw 'The Start menu shortcut was not created.' }

    $running = @(Get-Process -Name 'KINOJO.Meter' -ErrorAction SilentlyContinue).Count -gt 0
    if (-not $running) {
        throw 'KINOJO Meter is not running after installation.'
    }

    $result.success = $true
    $result.message = 'Clean installation completed and all automated validations passed.'
    $result.installedVersion = [string]$installedRelease.version
    $result.installedFileVersion = $installedFileVersion
    $result.managedFileCount = $managedFiles.Count
    $result.desktopShortcut = $desktopShortcut
    $result.startMenuShortcut = $startMenuShortcut
    $result.uninstallEntry = $true
    $result.applicationRunning = $running
}
catch {
    $result.success = $false
    $result.message = $_.Exception.Message
    Write-Error $_.Exception.Message
}
finally {
    $json = $result | ConvertTo-Json -Depth 6
    Write-Utf8NoBom -Path $ResultJson -Content $json
    $lines = @(
        'KINOJO Meter Clean Install Sandbox Test',
        'Success: ' + $result.success,
        'Message: ' + $result.message,
        'Version: ' + $result.installedVersion,
        'FileVersion: ' + $result.installedFileVersion,
        'ManagedFiles: ' + $result.managedFileCount,
        'DesktopShortcut: ' + $result.desktopShortcut,
        'StartMenuShortcut: ' + $result.startMenuShortcut,
        'UninstallEntry: ' + $result.uninstallEntry,
        'ApplicationRunning: ' + $result.applicationRunning,
        'TestedAtUtc: ' + $result.testedAtUtc
    )
    Write-Utf8NoBom -Path $ResultText -Content ($lines -join [Environment]::NewLine)
    try { Stop-Transcript | Out-Null } catch { }
}

if ($result.success -ne $true) { exit 1 }
