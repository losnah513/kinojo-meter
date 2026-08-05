[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GitHubOwner,
    [Parameter(Mandatory=$true)][string]$GitHubRepository,
    [string]$MinimumVersion = '',
    [string]$ReleaseNote = '',
    [switch]$Mandatory,
    [switch]$VerifyRemote
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This release preparation script must run on Windows.'
}
if ($GitHubOwner -notmatch '^[A-Za-z0-9_.-]+$' -or $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'GitHub owner and repository may contain only letters, digits, underscore, dot, and hyphen.'
}

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$VersionFile = Join-Path $Root 'release\version.json'
$BuildDir = Join-Path $Root 'build'
$OutputDir = Join-Path $BuildDir 'release'

function Assert-File {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
}

function Read-Json {
    param([Parameter(Mandatory=$true)][string]$Path)
    Assert-File $Path
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Assert-Version {
    param([Parameter(Mandatory=$true)][string]$Value, [Parameter(Mandatory=$true)][string]$Name)
    if ($Value -notmatch '^\d+\.\d+\.\d+$') { throw "$Name must use major.minor.patch: $Value" }
}

function Assert-BinaryVersion {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Expected)
    Assert-File $Path
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ([String]::IsNullOrWhiteSpace($actual) -or $actual.Trim() -ne $Expected) {
        throw "Binary file version mismatch: $Path`nExpected: $Expected`nActual: $actual"
    }
}

$release = Read-Json $VersionFile
$version = [string]$release.version
$fileVersion = [string]$release.fileVersion
$channel = [string]$release.channel
Assert-Version $version 'version'
if ($fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or -not $fileVersion.StartsWith($version + '.', [StringComparison]::Ordinal)) {
    throw "fileVersion does not match version: $fileVersion"
}
if ([String]::IsNullOrWhiteSpace($MinimumVersion)) { $MinimumVersion = [string]$release.minimumVersion }
if ([String]::IsNullOrWhiteSpace($ReleaseNote)) { $ReleaseNote = [string]$release.releaseNote }
if (-not $PSBoundParameters.ContainsKey('Mandatory')) { $Mandatory = [bool]$release.mandatory }
Assert-Version $MinimumVersion 'minimumVersion'
if ([version]$MinimumVersion -gt [version]$version) { throw 'minimumVersion cannot be greater than version.' }
if ($channel -ne 'stable') { throw "Only the stable channel is currently publishable: $channel" }
if ($release.serverUpdateManifestReady -ne $true) { throw 'serverUpdateManifestReady must be true before publication.' }
if ($null -eq $release.releaseAutomation -or $release.releaseAutomation.enabled -ne $true -or $release.releaseAutomation.serverSync -ne $true) {
    throw 'Release automation is not enabled in release/version.json.'
}

$fileName = "KINOJO_Meter_${version}_Setup.exe"
$setupPath = Join-Path $BuildDir $fileName
$payloadPath = Join-Path $BuildDir "KinojoMeterPayload_${version}.zip"
$checksumPath = Join-Path $BuildDir "checksums_${version}.txt"
Assert-File $setupPath
Assert-File $payloadPath
Assert-File $checksumPath
Assert-BinaryVersion $setupPath $fileVersion
$payloadSha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToUpperInvariant()
$setupSha256 = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToUpperInvariant()
$checksumRows = @{}
foreach ($line in @(Get-Content -LiteralPath $checksumPath)) {
    $parts = @($line.Trim([char]0xFEFF).Split("`t"))
    if ($parts.Count -eq 3 -and -not [String]::IsNullOrWhiteSpace($parts[0])) {
        $checksumRows[$parts[0]] = $parts
    }
}
$expectedChecksumRows = @(
    [pscustomobject]@{ FileName = "KinojoMeterPayload_${version}.zip"; FileSize = (Get-Item -LiteralPath $payloadPath).Length; Sha256 = $payloadSha256 },
    [pscustomobject]@{ FileName = $fileName; FileSize = (Get-Item -LiteralPath $setupPath).Length; Sha256 = $setupSha256 }
)
foreach ($expected in $expectedChecksumRows) {
    if (-not $checksumRows.ContainsKey($expected.FileName)) { throw "Checksum row is missing: $($expected.FileName)" }
    $actual = $checksumRows[$expected.FileName]
    if ([int64]$actual[1] -ne [int64]$expected.FileSize -or $actual[2].ToUpperInvariant() -ne $expected.Sha256) {
        throw "Checksum row does not match the release artifact: $($expected.FileName)"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($payloadPath)
try {
    foreach ($name in @('KINOJO.Meter.exe','version.json')) {
        if ($null -eq $archive.GetEntry($name)) { throw "Payload entry is missing: $name" }
    }
    $manifestEntry = $archive.GetEntry('version.json')
    $stream = $manifestEntry.Open()
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { $payloadVersion = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
    if ([string]$payloadVersion.version -ne $version -or [string]$payloadVersion.fileVersion -ne $fileVersion) {
        throw 'Payload version.json does not match release/version.json.'
    }
}
finally { $archive.Dispose() }

$temp = Join-Path $env:TEMP ("kinojo-release-check-" + [Guid]::NewGuid().ToString('N'))
New-Item $temp -ItemType Directory -Force | Out-Null
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($payloadPath, $temp)
    Assert-BinaryVersion (Join-Path $temp 'KINOJO.Meter.exe') $fileVersion
}
finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }

$sha256 = $setupSha256.ToLowerInvariant()
$fileSize = (Get-Item -LiteralPath $setupPath).Length
if ($fileSize -le 0 -or $fileSize -gt 536870912) { throw "Installer size is outside the allowed range: $fileSize" }
$tag = "v$version"
$downloadUrl = "https://github.com/$GitHubOwner/$GitHubRepository/releases/download/$tag/$fileName"
$remoteVerified = $false

if ($VerifyRemote) {
    Write-Host 'Downloading the GitHub Release asset for remote verification...'
    $remotePath = Join-Path $env:TEMP ("kinojo-remote-" + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        $downloaded = $false
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            try {
                Invoke-WebRequest -Uri $downloadUrl -OutFile $remotePath -UseBasicParsing -MaximumRedirection 5
                $downloaded = $true
                break
            }
            catch {
                if ($attempt -ge 2) { throw }
                Start-Sleep -Seconds 5
            }
        }
        if (-not $downloaded) { throw 'GitHub Release asset download did not complete.' }
        $remoteSize = (Get-Item -LiteralPath $remotePath).Length
        $remoteHash = (Get-FileHash -LiteralPath $remotePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteSize -ne $fileSize) { throw "Remote file size mismatch. Expected $fileSize, actual $remoteSize" }
        if ($remoteHash -ne $sha256) { throw "Remote SHA-256 mismatch. Expected $sha256, actual $remoteHash" }
        Assert-BinaryVersion $remotePath $fileVersion
        $remoteVerified = $true
    }
    finally { Remove-Item $remotePath -Force -ErrorAction SilentlyContinue }
}

New-Item $OutputDir -ItemType Directory -Force | Out-Null
$registrationPath = Join-Path $OutputDir "KINOJO_Meter_${version}_release-registration.json"
$registration = [ordered]@{
    schemaVersion = 1
    product = 'KINOJO Meter'
    channel = $channel
    version = $version
    fileVersion = $fileVersion
    minimumVersion = $MinimumVersion
    fileName = $fileName
    tag = $tag
    downloadUrl = $downloadUrl
    sha256 = $sha256
    fileSize = $fileSize
    mandatory = [bool]$Mandatory
    releaseNote = $ReleaseNote
    remoteVerified = $remoteVerified
    preparedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$registration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $registrationPath -Encoding UTF8

Write-Host 'Release preparation completed.'
Write-Host "Tag      : $tag"
Write-Host "Setup    : $setupPath"
Write-Host "Checksum : $sha256"
Write-Host "Size     : $fileSize"
Write-Host "URL      : $downloadUrl"
Write-Host "Manifest : $registrationPath"
if (-not $VerifyRemote) {
    Write-Host 'Upload the Setup EXE and checksum text file to the GitHub Release, then run this script again with -VerifyRemote.'
}
