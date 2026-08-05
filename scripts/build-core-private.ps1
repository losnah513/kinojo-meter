[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$PrepareOnly,
    [switch]$PackageOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_REPOSITORY_VISIBILITY -eq 'public') {
    throw 'Private Core build is blocked in a public GitHub repository.'
}
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'KINOJO Meter Core must be built on Windows.'
}
if ($PrepareOnly -and $PackageOnly) { throw 'PrepareOnly and PackageOnly cannot be used together.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root 'release\core-version.json'
$projectPath = Join-Path $root 'src\KINOJO.Meter.csproj'
$runtime = Join-Path $root 'assets\runtime'
$build = Join-Path $root 'build-private'
$output = Join-Path $root 'artifacts\core-private'
$stage = Join-Path $output 'package'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$fileVersion = [string]$manifest.fileVersion
$packageName = [string]$manifest.packageName
if ($manifest.publicDistribution -ne $false -or $packageName -ne "KinojoMeterCore_${version}_x64.zip") {
    throw 'Core private release manifest is invalid.'
}

$msbuild = (Get-Command msbuild.exe -ErrorAction SilentlyContinue).Source
if (-not $msbuild) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) { throw 'MSBuild was not found.' }

if (-not $PackageOnly) {
    Remove-Item $output -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $stage -ItemType Directory -Force | Out-Null
    New-Item $build -ItemType Directory -Force | Out-Null
    & $msbuild $projectPath /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 `
        /p:KinojoVersion=$version /p:KinojoFileVersion=$fileVersion /p:OutDir="$output\app\" /nologo
    if ($LASTEXITCODE -ne 0) { throw "Core build failed with exit code $LASTEXITCODE." }

    Get-ChildItem -LiteralPath (Join-Path $output 'app') -File | Where-Object { $_.Extension -in @('.exe','.dll','.config') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage $_.Name) -Force
    }
    foreach ($name in @('WinDivert.dll','WinDivert64.sys','README.txt','third-party-checksums.txt')) {
        Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $stage $name) -Force
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'version.json') -Force
    if ($PrepareOnly) {
        Write-Host "Unsigned Core staging directory: $stage"
        return
    }
}

if (-not (Test-Path -LiteralPath $stage -PathType Container)) {
    throw 'Prepared Core staging directory is missing. Run with -PrepareOnly before -PackageOnly.'
}

if ($manifest.codeSignatureRequired -eq $true) {
    $publisher = [string]$manifest.publisherSubject
    if ([string]::IsNullOrWhiteSpace($publisher)) { throw 'publisherSubject is required for a signed Core release.' }
    foreach ($binary in @(Get-ChildItem -LiteralPath $stage -File | Where-Object { $_.Extension -in @('.exe','.dll') })) {
        $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject.IndexOf($publisher, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Core binary must be signed by '$publisher' before packaging: $($binary.Name)"
        }
    }
}

Remove-Item -LiteralPath (Join-Path $stage 'install-manifest.json') -Force -ErrorAction SilentlyContinue

$files = @(Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
    [ordered]@{ path=$_.Name; size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$installManifest = [ordered]@{
    schemaVersion=1
    coreVersion=$version
    entryPoint='KINOJO.Meter.exe'
    files=$files
}
$installManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'install-manifest.json') -Encoding utf8

$package = Join-Path $build $packageName
Remove-Item $package -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $package -CompressionLevel Optimal
$size = (Get-Item -LiteralPath $package).Length
$sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
@("$packageName`t$size`t$sha256") | Set-Content -LiteralPath (Join-Path $build "checksums_core_${version}.txt") -Encoding ascii
Write-Host "Private Core package: $package"
Write-Host "Size                : $size"
Write-Host "SHA-256             : $sha256"
