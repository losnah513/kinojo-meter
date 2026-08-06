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
Assert-EmbeddedLauncher $artifactPath ([string]$manifest.fileVersion)

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

$tagCommit = Resolve-TagCommit $repository $tag
if ($tagCommit -and $tagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) {
    throw "$tag is immutable and already points to $tagCommit. Bump launcher-version.json."
}
if (-not $tagCommit) {
    $releaseArguments = @('release', 'create', $tag, $artifactPath, '--repo', $repository, '--target', $ExpectedCommit,
        '--title', $(if ($Channel -eq 'staging') { "KINOJO Meter Launcher STAGING $version" } else { "KINOJO Meter Launcher $version" }),
        '--notes', [string]$manifest.releaseNote)
    if ($Channel -eq 'staging') { $releaseArguments += '--prerelease' }
    & gh @releaseArguments
    if ($LASTEXITCODE -ne 0) { throw 'Launcher GitHub Release creation failed.' }
    $tagCommit = Resolve-TagCommit $repository $tag
}
if (-not $tagCommit -or $tagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) { throw 'Launcher tag readback failed.' }

$downloadRoot = Join-Path $env:RUNNER_TEMP ("kinojo-launcher-verify-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloadRoot | Out-Null
try {
    $release = ((& gh release view $tag --repo $repository --json isDraft,isPrerelease,assets) -join "`n") | ConvertFrom-Json
    $expectedPrerelease = $Channel -eq 'staging'
    if ($LASTEXITCODE -ne 0 -or $release.isDraft -eq $true -or [bool]$release.isPrerelease -ne $expectedPrerelease) {
        throw 'Launcher Release channel readback failed.'
    }
    $assetNames = @($release.assets | ForEach-Object { [string]$_.name })
    if ($assetNames -notcontains $artifactName) {
        & gh release upload $tag $artifactPath --repo $repository
        if ($LASTEXITCODE -ne 0) { throw 'Launcher executable asset recovery failed.' }
    }
    & gh release download $tag --repo $repository --dir $downloadRoot --pattern $artifactName
    if ($LASTEXITCODE -ne 0) { throw 'Launcher remote executable download failed.' }
    $remoteArtifact = Join-Path $downloadRoot $artifactName
    Assert-EmbeddedLauncher $remoteArtifact ([string]$manifest.fileVersion)
    $size = (Get-Item -LiteralPath $remoteArtifact).Length
    $sha256 = (Get-FileHash -LiteralPath $remoteArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$artifactName`t$size`t$sha256"
    @($checksumLine) | Set-Content -LiteralPath $checksumPath -Encoding ascii

    if ($assetNames -contains $checksumName) {
        & gh release download $tag --repo $repository --dir $downloadRoot --pattern $checksumName
        if ($LASTEXITCODE -ne 0) { throw 'Launcher remote checksum download failed.' }
        $remoteChecksum = (Get-Content -LiteralPath (Join-Path $downloadRoot $checksumName) -Raw).Trim()
        if ($remoteChecksum -ne $checksumLine) { throw 'Existing Launcher checksum is immutable and does not match the published executable.' }
    } else {
        & gh release upload $tag $checksumPath --repo $repository
        if ($LASTEXITCODE -ne 0) { throw 'Launcher checksum asset recovery failed.' }
    }
} finally {
    Remove-Item -LiteralPath $downloadRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Launcher release verified: channel=$Channel $repository $tag @ $ExpectedCommit size=$size sha256=$sha256"
