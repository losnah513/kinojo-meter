[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [switch]$PreflightOnly,
    [string]$ProjectRoot = ''
)


$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest


if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This build script must run on Windows 10/11 x64.'
}


function Resolve-MeterRoot {
    param([string]$RequestedRoot)


    if ([String]::IsNullOrWhiteSpace($RequestedRoot)) {
        return (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
    }


    $candidate = [Environment]::ExpandEnvironmentVariables($RequestedRoot.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Project root does not exist: $candidate"
    }


    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    $meterChild = Join-Path $resolved '05_METER_DESKTOP'
    if (Test-Path -LiteralPath (Join-Path $meterChild 'release\version.json') -PathType Leaf) {
        return $meterChild
    }
    if (Test-Path -LiteralPath (Join-Path $resolved 'release\version.json') -PathType Leaf) {
        return $resolved
    }


    throw "KINOJO Meter root was not found under: $resolved`nExpected either <project root>\05_METER_DESKTOP or the 05_METER_DESKTOP folder itself."
}


$Root = Resolve-MeterRoot -RequestedRoot $ProjectRoot
$ResolvedProjectRoot = Split-Path $Root -Parent
$VersionManifest = Join-Path $Root 'release\version.json'
$BuildDir = Join-Path $Root 'build'
$ArtifactsDir = Join-Path $Root 'artifacts'
$AppOut = Join-Path $ArtifactsDir 'app'
$SetupOut = Join-Path $ArtifactsDir 'setup'
$PayloadStage = Join-Path $ArtifactsDir 'payload'
$RuntimeAssetsDir = Join-Path $Root 'assets\runtime'
$AppProject = Join-Path $Root 'src\KINOJO.Meter.csproj'
$SetupProject = Join-Path $Root 'setup\KINOJO.Meter.Setup.csproj'
$AppManifest = Join-Path $Root 'src\app.manifest'
$SetupManifest = Join-Path $Root 'setup\app.manifest'
$SetupProgram = Join-Path $Root 'setup\SetupProgram.cs'
$SetupEngine = Join-Path $Root 'setup\SetupEngine.cs'
$ReleasePreparationScript = Join-Path $Root 'scripts\prepare-github-release.ps1'


$ExpectedWinDivertHashes = @{
    'WinDivert.dll'   = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
}


function Assert-RequiredFile {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path"
    }
}


function Assert-ForbiddenPath {
    param([Parameter(Mandatory=$true)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        throw "Legacy or generated path must not exist in the active source tree: $Path"
    }
}


function Assert-ApplicationManifest {
    param([Parameter(Mandatory=$true)][string]$Path)


    Assert-RequiredFile -Path $Path
    try {
        [xml]$manifestXml = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        throw "Application manifest is not valid XML: $Path"
    }


    if ($null -eq $manifestXml.DocumentElement -or $manifestXml.DocumentElement.LocalName -ne 'assembly') {
        throw "Application manifest root must be assembly: $Path"
    }


    $requestedLevel = $manifestXml.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='trustInfo']/*[local-name()='security']/*[local-name()='requestedPrivileges']/*[local-name()='requestedExecutionLevel']")
    if ($null -eq $requestedLevel) {
        throw "Application manifest requestedExecutionLevel is missing: $Path"
    }


    $level = [string]$requestedLevel.GetAttribute('level')
    if ($level -ne 'requireAdministrator') {
        throw "Application manifest must require administrator privileges: $Path"
    }
}


function Resolve-ApplicationManifest {
    param([Parameter(Mandatory=$true)][string]$CanonicalPath)


    $downloadedPath = $CanonicalPath + '.xml'
    $canonicalExists = Test-Path -LiteralPath $CanonicalPath -PathType Leaf
    $downloadedExists = Test-Path -LiteralPath $downloadedPath -PathType Leaf


    if (-not $canonicalExists -and -not $downloadedExists) {
        throw "Required application manifest is missing: $CanonicalPath"
    }


    if ($canonicalExists -and $downloadedExists) {
        Assert-ApplicationManifest -Path $CanonicalPath
        Assert-ApplicationManifest -Path $downloadedPath


        $canonicalHash = (Get-FileHash -LiteralPath $CanonicalPath -Algorithm SHA256).Hash
        $downloadedHash = (Get-FileHash -LiteralPath $downloadedPath -Algorithm SHA256).Hash
        if ($canonicalHash -ne $downloadedHash) {
            throw "Both manifest files exist but their contents differ: $CanonicalPath and $downloadedPath"
        }


        Write-Host "Verified matching manifest download alias: $downloadedPath"
        return $CanonicalPath
    }


    if (-not $canonicalExists) {
        Assert-ApplicationManifest -Path $downloadedPath
        Copy-Item -LiteralPath $downloadedPath -Destination $CanonicalPath -Force
        Write-Host "Recovered application manifest from download alias: $downloadedPath"
    }


    Assert-ApplicationManifest -Path $CanonicalPath
    return $CanonicalPath
}


function Assert-ProjectCompileInputs {
    param([Parameter(Mandatory=$true)][string]$ProjectPath)


    Assert-RequiredFile -Path $ProjectPath
    try {
        [xml]$projectXml = Get-Content -LiteralPath $ProjectPath -Raw
    }
    catch {
        throw "Project file is not valid XML: $ProjectPath"
    }


    $projectDirectory = Split-Path $ProjectPath -Parent
    foreach ($node in @($projectXml.SelectNodes('//Compile[@Include]'))) {
        $include = [string]$node.Include
        if ([String]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) { continue }
        if ($include.IndexOfAny([char[]]'*?') -ge 0) {
            $matches = @(Get-ChildItem -Path (Join-Path $projectDirectory $include) -File -ErrorAction SilentlyContinue)
            if ($matches.Count -eq 0) {
                throw "Project compile wildcard matched no files: $ProjectPath -> $include"
            }
            continue
        }
        $sourcePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $include))
        Assert-RequiredFile -Path $sourcePath
    }
}


function Assert-FileSha256 {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$ExpectedHash
    )
    Assert-RequiredFile -Path $Path
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $ExpectedHash.ToUpperInvariant()) {
        throw "File SHA-256 mismatch: $Path`nExpected: $ExpectedHash`nActual: $actual"
    }
}


function Read-VersionManifest {
    param([Parameter(Mandatory=$true)][string]$Path)
    Assert-RequiredFile -Path $Path
    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Release version manifest is not valid JSON: $Path"
    }


    $required = @('product','version','fileVersion','channel','minimumVersion','releaseNote','databaseContract','edgeApiVersion')
    foreach ($name in $required) {
        $value = $manifest.$name
        if ($null -eq $value -or [String]::IsNullOrWhiteSpace([string]$value)) {
            throw "Release version manifest field is missing: $name"
        }
    }


    $version = [string]$manifest.version
    $fileVersion = [string]$manifest.fileVersion
    if ($version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Release version must use major.minor.patch: $version"
    }
    if ($fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "File version must use major.minor.patch.revision: $fileVersion"
    }
    if (-not $fileVersion.StartsWith($version + '.', [StringComparison]::Ordinal)) {
        throw "File version must start with release version: version=$version fileVersion=$fileVersion"
    }
    if (([string]$manifest.product) -ne 'KINOJO Meter') {
        throw "Unexpected product in release version manifest: $($manifest.product)"
    }
    $minimumVersion = [string]$manifest.minimumVersion
    if ($minimumVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "Minimum version must use major.minor.patch: $minimumVersion"
    }
    if ([version]$minimumVersion -gt [version]$version) {
        throw "Minimum version cannot be greater than release version: minimum=$minimumVersion version=$version"
    }
    if ($manifest.mandatory -isnot [bool]) {
        throw 'Release version manifest field must be boolean: mandatory'
    }
    if ($null -eq $manifest.releaseAutomation -or $manifest.releaseAutomation.enabled -ne $true -or $manifest.releaseAutomation.serverSync -ne $true) {
        throw 'Release automation must explicitly enable GitHub Release and Server sync.'
    }


    return $manifest
}


function Assert-BinaryVersion {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$ExpectedFileVersion
    )
    Assert-RequiredFile -Path $Path
    $actual = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    if ([String]::IsNullOrWhiteSpace($actual) -or $actual.Trim() -ne $ExpectedFileVersion) {
        throw "Binary file version mismatch: $Path`nExpected: $ExpectedFileVersion`nActual: $actual"
    }
}


Write-Host '[0/7] Preflight validation'
foreach ($path in @(
    (Join-Path $Root 'projects'),
    (Join-Path $Root 'build\payload'),
    (Join-Path $Root 'KinojoMeter.ServerBridge'),
    (Join-Path $Root 'PREPARE_GITHUB_RELEASE.cmd'),
    (Join-Path $Root 'VERIFY_GITHUB_RELEASE.cmd'),
    (Join-Path $Root 'TEST_CLEAN_INSTALL_SANDBOX.cmd')
)) {
    Assert-ForbiddenPath -Path $path
}
foreach ($path in @($VersionManifest, $AppProject, $SetupProject, $SetupProgram, $SetupEngine, $ReleasePreparationScript)) {
    Assert-RequiredFile -Path $path
}
Resolve-ApplicationManifest -CanonicalPath $AppManifest | Out-Null
Resolve-ApplicationManifest -CanonicalPath $SetupManifest | Out-Null
Assert-ProjectCompileInputs -ProjectPath $AppProject
Assert-ProjectCompileInputs -ProjectPath $SetupProject


$Release = Read-VersionManifest -Path $VersionManifest
$Version = [string]$Release.version
$FileVersion = [string]$Release.fileVersion
$PayloadZip = Join-Path $BuildDir "KinojoMeterPayload_$Version.zip"
$SetupFinal = Join-Path $BuildDir "KINOJO_Meter_${Version}_Setup.exe"
$ChecksumFile = Join-Path $BuildDir "checksums_$Version.txt"
foreach ($name in @('README.txt','third-party-checksums.txt')) {
    Assert-RequiredFile -Path (Join-Path $RuntimeAssetsDir $name)
}
foreach ($entry in $ExpectedWinDivertHashes.GetEnumerator()) {
    Assert-FileSha256 -Path (Join-Path $RuntimeAssetsDir $entry.Key) -ExpectedHash $entry.Value
}


$MsBuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
$MsBuildPath = if ($MsBuildCommand) { $MsBuildCommand.Source } else { $null }
if (-not $MsBuildPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found) { $MsBuildPath = $found }
    }
}
if (-not $MsBuildPath) {
    throw 'MSBuild was not found. Install Visual Studio with the .NET desktop development workload and .NET Framework 4.8 SDK/Targeting Pack.'
}


Write-Host "Project : $ResolvedProjectRoot"
Write-Host "Meter   : $Root"
Write-Host "Version : $Version ($FileVersion)"
Write-Host "Channel : $($Release.channel)"
Write-Host "MSBuild : $MsBuildPath"
Write-Host 'NuGet   : MSBuild /restore will restore SharpPcap 6.3.1 and PacketDotNet 1.4.8.'
if ($PreflightOnly) {
    Write-Host 'Preflight completed. Compilation was skipped because -PreflightOnly was specified.'
    exit 0
}


Remove-Item $ArtifactsDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $AppOut -ItemType Directory -Force | Out-Null
New-Item $SetupOut -ItemType Directory -Force | Out-Null
New-Item $PayloadStage -ItemType Directory -Force | Out-Null
New-Item $BuildDir -ItemType Directory -Force | Out-Null
Get-ChildItem $BuildDir -File -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '^KinojoMeterPayload_\d+\.\d+\.\d+\.zip$' -or
    $_.Name -match '^KINOJO_Meter_\d+\.\d+\.\d+_Setup\.exe$' -or
    $_.Name -match '^checksums_\d+\.\d+\.\d+\.txt$'
} | Remove-Item -Force


Write-Host '[1/7] Build KINOJO Meter application'
& $MsBuildPath $AppProject /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 /p:KinojoVersion=$Version /p:KinojoFileVersion=$FileVersion /p:OutDir="$AppOut\" /nologo
if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }


$AppExe = Join-Path $AppOut 'KINOJO.Meter.exe'
Assert-BinaryVersion -Path $AppExe -ExpectedFileVersion $FileVersion


Write-Host '[2/7] Validate capture runtime dependencies'
foreach ($name in @('SharpPcap.dll','PacketDotNet.dll')) {
    Assert-RequiredFile -Path (Join-Path $AppOut $name)
}


Write-Host '[3/7] Assemble payload'
Copy-Item $AppExe (Join-Path $PayloadStage 'KINOJO.Meter.exe') -Force
Get-ChildItem $AppOut -File | Where-Object { $_.Extension -in @('.dll', '.config') } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $PayloadStage $_.Name) -Force
}
foreach ($name in @('WinDivert.dll','WinDivert64.sys','README.txt','third-party-checksums.txt')) {
    $source = Join-Path $RuntimeAssetsDir $name
    Assert-RequiredFile -Path $source
    Copy-Item $source (Join-Path $PayloadStage $name) -Force
}
Copy-Item $VersionManifest (Join-Path $PayloadStage 'version.json') -Force
Remove-Item $PayloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PayloadStage '*') -DestinationPath $PayloadZip -CompressionLevel Optimal
Assert-RequiredFile -Path $PayloadZip


Write-Host '[4/7] Build installer'
& $MsBuildPath $SetupProject /restore /m /t:Build /p:Configuration=$Configuration /p:Platform=x64 /p:KinojoVersion=$Version /p:KinojoFileVersion=$FileVersion /p:PayloadZipPath="$PayloadZip" /p:OutDir="$SetupOut\" /nologo
if ($LASTEXITCODE -ne 0) { throw "Installer build failed with exit code $LASTEXITCODE." }
$SetupBuilt = Join-Path $SetupOut "KINOJO_Meter_${Version}_Setup.exe"
Assert-BinaryVersion -Path $SetupBuilt -ExpectedFileVersion $FileVersion
Copy-Item $SetupBuilt $SetupFinal -Force


Write-Host '[5/7] Validate final artifacts'
Assert-BinaryVersion -Path $SetupFinal -ExpectedFileVersion $FileVersion
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadArchive = [System.IO.Compression.ZipFile]::OpenRead($PayloadZip)
try {
    $payloadNames = @($payloadArchive.Entries | ForEach-Object { $_.FullName })
    foreach ($name in @('KINOJO.Meter.exe','KINOJO.Meter.exe.config','SharpPcap.dll','PacketDotNet.dll','WinDivert.dll','WinDivert64.sys','README.txt','version.json')) {
        if ($payloadNames -notcontains $name) { throw "Required payload entry is missing: $name" }
    }


    $versionEntry = $payloadArchive.GetEntry('version.json')
    if ($null -eq $versionEntry) { throw 'Payload version.json entry is missing.' }
    $stream = $versionEntry.Open()
    try {
        $reader = New-Object IO.StreamReader($stream)
        try { $payloadRelease = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
    if (([string]$payloadRelease.version) -ne $Version -or ([string]$payloadRelease.fileVersion) -ne $FileVersion) {
        throw 'Payload version.json does not match the release version contract.'
    }
}
finally {
    $payloadArchive.Dispose()
}


Write-Host '[6/7] Validate payload application version'
$payloadExtract = Join-Path $ArtifactsDir 'payload-version-check'
Remove-Item $payloadExtract -Recurse -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::ExtractToDirectory($PayloadZip, $payloadExtract)
try {
    Assert-BinaryVersion -Path (Join-Path $payloadExtract 'KINOJO.Meter.exe') -ExpectedFileVersion $FileVersion
}
finally {
    Remove-Item $payloadExtract -Recurse -Force -ErrorAction SilentlyContinue
}


Write-Host '[7/7] Write SHA-256 checksums'
$payloadHash = (Get-FileHash $PayloadZip -Algorithm SHA256).Hash
$setupHash = (Get-FileHash $SetupFinal -Algorithm SHA256).Hash
@(
    "KinojoMeterPayload_$Version.zip`t$((Get-Item $PayloadZip).Length)`t$payloadHash",
    "KINOJO_Meter_${Version}_Setup.exe`t$((Get-Item $SetupFinal).Length)`t$setupHash"
) | Set-Content $ChecksumFile -Encoding UTF8


Write-Host 'Build completed successfully.'
Write-Host "Payload : $PayloadZip"
Write-Host "Setup   : $SetupFinal"
Write-Host "SHA-256 : $ChecksumFile"
Write-Host 'Next     : Merge the verified pull request to main for automated GitHub Release publication.'
