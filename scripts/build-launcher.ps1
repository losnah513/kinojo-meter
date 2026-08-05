[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('stable','staging')][string]$Channel = 'stable',
    [switch]$AppOnly,
    [switch]$SetupOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'KINOJO Meter Launcher must be built on Windows.'
}
if ($AppOnly -and $SetupOnly) { throw 'AppOnly and SetupOnly cannot be used together.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root (if ($Channel -eq 'staging') { 'release\launcher-staging-version.json' } else { 'release\launcher-version.json' })
$launcherProject = Join-Path $root 'launcher\KINOJO.Meter.Launcher.csproj'
$setupProject = Join-Path $root 'launcher-setup\KINOJO.Meter.Launcher.Setup.csproj'
$buildDirectory = Join-Path $root 'build'
$appBuildDirectory = Join-Path $buildDirectory "launcher-app-$Channel"
$artifactDirectory = Join-Path $root "artifacts\launcher-$Channel"
$appOutput = Join-Path $artifactDirectory 'app'
$setupOutput = Join-Path $artifactDirectory 'setup'
$launcherAssemblyName = if ($Channel -eq 'staging') { 'KINOJO.Meter.Launcher.Staging.exe' } else { 'KINOJO.Meter.Launcher.exe' }
$setupAssemblyName = if ($Channel -eq 'staging') { 'KINOJO.Meter.Launcher.Staging.Setup.exe' } else { 'KINOJO.Meter.Launcher.Setup.exe' }
$stagedApp = Join-Path $appBuildDirectory $launcherAssemblyName

foreach ($path in @(
    $manifestPath, $launcherProject, $setupProject,
    (Join-Path $root 'launcher\app.manifest'),
    (Join-Path $root 'launcher-setup\app.manifest')
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required Launcher input is missing: $path" }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$fileVersion = [string]$manifest.fileVersion
$artifactName = [string]$manifest.artifactName
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Launcher version manifest is invalid.'
}
if ($artifactName -ne "KINOJO_Meter_Launcher_${version}.exe") {
    if ($Channel -ne 'staging' -or $artifactName -ne "KINOJO_Meter_Launcher_Staging_${version}.exe") {
        throw 'Launcher artifactName must contain the exact Launcher version and channel.'
    }
}
if ([string]$manifest.channel -ne $Channel) { throw 'Launcher manifest channel does not match the requested build channel.' }

$msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
$msbuild = if ($msbuildCommand) { $msbuildCommand.Source } else { $null }
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) { throw 'MSBuild was not found.' }

function Assert-FileVersion([string]$Path, [string]$Expected) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Build output is missing: $Path" }
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ($actual -ne $Expected) { throw "File version mismatch: $Path expected=$Expected actual=$actual" }
}

New-Item $buildDirectory -ItemType Directory -Force | Out-Null
New-Item $appBuildDirectory -ItemType Directory -Force | Out-Null

if (-not $SetupOnly) {
    Remove-Item $appOutput -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $appOutput -ItemType Directory -Force | Out-Null
    & $msbuild $launcherProject /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 `
        /p:LauncherVersion=$version /p:LauncherFileVersion=$fileVersion /p:OutDir="$appOutput\" `
        /p:LauncherChannel=$Channel /nologo
    if ($LASTEXITCODE -ne 0) { throw "Launcher application build failed with exit code $LASTEXITCODE." }

    $builtApp = Join-Path $appOutput $launcherAssemblyName
    Assert-FileVersion $builtApp $fileVersion
    Copy-Item -LiteralPath $builtApp -Destination $stagedApp -Force
}

Assert-FileVersion $stagedApp $fileVersion
if ($AppOnly) {
    Write-Host "Launcher application: $stagedApp"
    return
}

Remove-Item $setupOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item $setupOutput -ItemType Directory -Force | Out-Null
& $msbuild $setupProject /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 `
    /p:LauncherVersion=$version /p:LauncherFileVersion=$fileVersion /p:LauncherPayloadPath="$stagedApp" `
    /p:OutDir="$setupOutput\" /p:LauncherChannel=$Channel /nologo
if ($LASTEXITCODE -ne 0) { throw "Launcher setup build failed with exit code $LASTEXITCODE." }

$builtSetup = Join-Path $setupOutput $setupAssemblyName
$publishedSetup = Join-Path $buildDirectory $artifactName
Assert-FileVersion $builtSetup $fileVersion
Copy-Item -LiteralPath $builtSetup -Destination $publishedSetup -Force
Assert-FileVersion $publishedSetup $fileVersion

$assembly = [Reflection.Assembly]::LoadFile($publishedSetup)
$resourceName = 'KINOJO.Meter.Launcher.Payload'
if ($assembly.GetManifestResourceNames() -notcontains $resourceName) { throw 'Launcher setup does not contain the Launcher payload.' }
$stream = $assembly.GetManifestResourceStream($resourceName)
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $embeddedHash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
finally { $stream.Dispose() }
$appHash = (Get-FileHash -LiteralPath $stagedApp -Algorithm SHA256).Hash.ToLowerInvariant()
if ($embeddedHash -ne $appHash) { throw 'Embedded Launcher payload does not match the staged application.' }

$size = (Get-Item -LiteralPath $publishedSetup).Length
$sha256 = (Get-FileHash -LiteralPath $publishedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $buildDirectory (if ($Channel -eq 'staging') { "checksums_launcher_staging_${version}.txt" } else { "checksums_launcher_${version}.txt" })
@("$artifactName`t$size`t$sha256") | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Launcher app   : $stagedApp"
Write-Host "Launcher setup : $publishedSetup"
Write-Host "Channel        : $Channel"
Write-Host "Size           : $size"
Write-Host "SHA-256        : $sha256"
