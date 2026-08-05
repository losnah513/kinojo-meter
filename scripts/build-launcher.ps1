[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'KINOJO Meter Launcher must be built on Windows.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root 'release\launcher-version.json'
$projectPath = Join-Path $root 'launcher\KINOJO.Meter.Launcher.csproj'
$buildDirectory = Join-Path $root 'build'
$outputDirectory = Join-Path $root 'artifacts\launcher'

foreach ($path in @($manifestPath, $projectPath, (Join-Path $root 'launcher\app.manifest'))) {
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
    throw 'Launcher artifactName must contain the exact Launcher version.'
}

$msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) { throw 'MSBuild was not found.' }

Remove-Item $outputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $outputDirectory -ItemType Directory -Force | Out-Null
New-Item $buildDirectory -ItemType Directory -Force | Out-Null

& $msbuild $projectPath /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 `
    /p:LauncherVersion=$version /p:LauncherFileVersion=$fileVersion /p:OutDir="$outputDirectory\" /nologo
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed with exit code $LASTEXITCODE." }

$built = Join-Path $outputDirectory 'KINOJO.Meter.Launcher.exe'
$published = Join-Path $buildDirectory $artifactName
if (-not (Test-Path -LiteralPath $built -PathType Leaf)) { throw 'Launcher executable was not produced.' }
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($built).FileVersion
if ($actualVersion -ne $fileVersion) { throw "Launcher file version mismatch: $actualVersion" }
Copy-Item -LiteralPath $built -Destination $published -Force

$size = (Get-Item -LiteralPath $published).Length
$sha256 = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $buildDirectory "checksums_launcher_${version}.txt"
@("$artifactName`t$size`t$sha256") | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Launcher : $published"
Write-Host "Size     : $size"
Write-Host "SHA-256  : $sha256"
