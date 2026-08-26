[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GitHubOwner,
    [Parameter(Mandatory=$true)][string]$GitHubRepository,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit,
    [ValidateSet('stable','staging')][string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Launcher publication must run on Windows.' }
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedCommit must be a full Git commit SHA.' }
if ([String]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestName = if ($Channel -eq 'staging') { 'launcher-staging-version.json' } else { 'launcher-version.json' }
$manifest = Get-Content -LiteralPath (Join-Path $root "release\$manifestName") -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$artifactName = [string]$manifest.artifactName
$artifactPath = Join-Path $root "build\$artifactName"
$checksumName = if ($Channel -eq 'staging') { "checksums_launcher_staging_${version}.txt" } else { "checksums_launcher_${version}.txt" }
$checksumPath = Join-Path $root "build\$checksumName"
$repository = "$GitHubOwner/$GitHubRepository"
$tag = if ($Channel -eq 'staging') { "launcher-staging-v$version" } else { "launcher-v$version" }

function Assert-EmbeddedLauncher([string]$SetupPath, [string]$ExpectedFileVersion) {
    $assembly = [Reflection.Assembly]::LoadFile($SetupPath)
    $resourceName = 'KINOJO.Meter.Launcher.Payload'
    if ($assembly.GetManifestResourceNames() -notcontains $resourceName) { throw 'Launcher setup payload resource is missing.' }
    $temporary = Join-Path $env:RUNNER_TEMP ("kinojo-embedded-launcher-" + [Guid]::NewGuid().ToString('N') + '.exe')
    $stream = $assembly.GetManifestResourceStream($resourceName)
    try {
        $output = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.CopyTo($output) }
        finally { $output.Dispose() }
        $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($temporary).FileVersion
        if ($actualVersion -ne $ExpectedFileVersion) { throw "Embedded Launcher file version mismatch: $actualVersion" }
    }
    finally {
        if ($stream) { $stream.Dispose() }
        Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
    }
}

$expectedState = if ($Channel -eq 'staging') { 'STAGING_E2E' } else { 'ACTIVE' }
$expectedPublicDistribution = $Channel -eq 'stable'
if ([string]$manifest.channel -cne $Channel -or [string]$manifest.cutoverState -cne $expectedState -or
    [bool]$manifest.publicDistribution -ne $expectedPublicDistribution -or
    $manifest.codeSignatureRequired -ne $false -or [string]$manifest.publisherSubject -cne '' -or
    [string]$manifest.trustMode -cne 'WINDOWS_UNSIGNED_HOBBY' -or $manifest.smartScreenWarningExpected -ne $true) {
    throw 'Launcher publication channel, cutover state, or unsigned hobby trust contract is invalid.'
}
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "Launcher artifact is missing: $artifactPath" }
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw "Launcher checksum is missing: $checksumPath" }
Assert-EmbeddedLauncher $artifactPath ([string]$manifest.fileVersion)

$size = (Get-Item -LiteralPath $artifactPath).Length
$sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumLine = "$artifactName`t$size`t$sha256"
$localChecksum = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
if ($localChecksum -cne $checksumLine) { throw 'Local Launcher checksum does not match the executable.' }

$immutableSettingsRaw = & gh api "repos/$repository/immutable-releases" -H 'X-GitHub-Api-Version: 2026-03-10'
if ($LASTEXITCODE -ne 0) { throw 'GitHub immutable release setting readback failed.' }
$immutableSettings = ($immutableSettingsRaw -join "`n") | ConvertFrom-Json
if ($immutableSettings.enabled -ne $true) { throw 'GitHub immutable releases must be enabled before Launcher publication.' }

function Resolve-TagCommit([string]$Repository, [string]$Tag) {
    $raw = & gh api "repos/$Repository/git/ref/tags/$Tag" 2>$null
    if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace(($raw -join "`n"))) { return '' }
    $ref = ($raw -join "`n") | ConvertFrom-Json
    if ([string]$ref.object.type -eq 'commit') { return [string]$ref.object.sha }
    if ([string]$ref.object.type -ne 'tag') { throw 'Launcher tag has an unsupported object type.' }
    $tagSha = [string]$ref.object.sha
    $tagObject = ((& gh api "repos/$Repository/git/tags/$tagSha") -join "`n") | ConvertFrom-Json
    if ([string]$tagObject.object.type -ne 'commit') { throw 'Launcher tag does not resolve to a commit.' }
    return [string]$tagObject.object.sha
}

function Read-Release([string]$Repository, [string]$Tag) {
    $raw = & gh release view $Tag --repo $Repository --json tagName,targetCommitish,isDraft,isPrerelease,isImmutable,assets 2>$null
    if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace(($raw -join "`n"))) { return $null }
    return (($raw -join "`n") | ConvertFrom-Json)
}

function Assert-ReleaseContract([object]$Release, [bool]$ExpectedDraft, [bool]$ExpectedImmutable) {
    $expectedPrerelease = $Channel -eq 'staging'
    if ($null -eq $Release -or [string]$Release.tagName -cne $tag -or
        [bool]$Release.isDraft -ne $ExpectedDraft -or
        [bool]$Release.isPrerelease -ne $expectedPrerelease -or
        [bool]$Release.isImmutable -ne $ExpectedImmutable) {
        throw 'Launcher Release state contract readback failed.'
    }
    $assetNames = @($Release.assets | ForEach-Object { [string]$_.name })
    if ($assetNames.Count -ne 2 -or $assetNames -notcontains $artifactName -or $assetNames -notcontains $checksumName) {
        throw 'Launcher Release must contain exactly the executable and checksum assets.'
    }
}

function Assert-RemoteAssets([string]$Repository, [string]$Tag) {
    $downloadRoot = Join-Path $env:RUNNER_TEMP ("kinojo-launcher-verify-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $downloadRoot | Out-Null
    try {
        & gh release download $Tag --repo $Repository --dir $downloadRoot --pattern $artifactName --pattern $checksumName
        if ($LASTEXITCODE -ne 0) { throw 'Launcher remote asset download failed.' }
        $remoteArtifact = Join-Path $downloadRoot $artifactName
        $remoteChecksumPath = Join-Path $downloadRoot $checksumName
        Assert-EmbeddedLauncher $remoteArtifact ([string]$manifest.fileVersion)
        $remoteSize = (Get-Item -LiteralPath $remoteArtifact).Length
        $remoteSha256 = (Get-FileHash -LiteralPath $remoteArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
        $remoteChecksum = (Get-Content -LiteralPath $remoteChecksumPath -Raw).Trim()
        if ($remoteSize -ne $size -or $remoteSha256 -cne $sha256 -or $remoteChecksum -cne $checksumLine) {
            throw 'Launcher remote assets do not exactly match the local publication candidate.'
        }
    } finally {
        Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$release = Read-Release $repository $tag
if ($null -eq $release) {
    $releaseArguments = @('release', 'create', $tag, $artifactPath, $checksumPath, '--repo', $repository,
        '--target', $ExpectedCommit, '--draft',
        '--title', $(if ($Channel -eq 'staging') { "KINOJO Meter Launcher STAGING $version" } else { "KINOJO Meter Launcher $version" }),
        '--notes', [string]$manifest.releaseNote)
    if ($Channel -eq 'staging') { $releaseArguments += @('--prerelease', '--latest=false') }
    & gh @releaseArguments
    if ($LASTEXITCODE -ne 0) { throw 'Launcher GitHub draft Release creation failed.' }
    $release = Read-Release $repository $tag
}

if ($release.isDraft -eq $true) {
    if ([string]$release.targetCommitish -cne $ExpectedCommit) {
        throw 'Existing Launcher draft targets another commit. Delete the draft or bump the version.'
    }
    Assert-ReleaseContract $release $true $false
    Assert-RemoteAssets $repository $tag
    & gh release edit $tag --repo $repository --draft=false
    if ($LASTEXITCODE -ne 0) { throw 'Launcher GitHub draft publication failed.' }
}

$tagCommit = Resolve-TagCommit $repository $tag
if (-not $tagCommit -or $tagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) {
    throw 'Launcher immutable tag readback failed.'
}
$release = Read-Release $repository $tag
Assert-ReleaseContract $release $false $true
Assert-RemoteAssets $repository $tag

Write-Host "Immutable Launcher release verified: channel=$Channel $repository $tag @ $ExpectedCommit size=$size sha256=$sha256"
