[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GitHubOwner,
    [Parameter(Mandatory=$true)][string]$GitHubRepository,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This publication script must run on Windows.'
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "ExpectedCommit must be a full Git commit SHA: $ExpectedCommit"
}
if ([String]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
    throw 'GH_TOKEN is required to publish a GitHub Release.'
}

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -LiteralPath (Join-Path $Root 'release\version.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$tag = "v$version"
$repository = "$GitHubOwner/$GitHubRepository"
$setupPath = Join-Path $Root "build\KINOJO_Meter_${version}_Setup.exe"
$checksumPath = Join-Path $Root "build\checksums_${version}.txt"
foreach ($path in @($setupPath, $checksumPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release asset is missing: $path" }
}
if ($manifest.channel -ne 'stable' -or $manifest.serverUpdateManifestReady -ne $true) {
    throw 'Only a stable, Server-ready release can be published.'
}
if ($null -eq $manifest.releaseAutomation -or $manifest.releaseAutomation.enabled -ne $true -or $manifest.releaseAutomation.serverSync -ne $true) {
    throw 'Release automation is not enabled in release/version.json.'
}

function Resolve-RemoteTagCommit {
    param([string]$Repository, [string]$Tag)
    $refJson = & gh api "repos/$Repository/git/ref/tags/$Tag" 2>$null
    if ($LASTEXITCODE -ne 0 -or [String]::IsNullOrWhiteSpace(($refJson -join "`n"))) { return '' }
    $ref = ($refJson -join "`n") | ConvertFrom-Json
    $objectType = [string]$ref.object.type
    $objectSha = [string]$ref.object.sha
    if ($objectType -eq 'commit') { return $objectSha }
    if ($objectType -eq 'tag') {
        $tagJson = & gh api "repos/$Repository/git/tags/$objectSha"
        if ($LASTEXITCODE -ne 0) { throw "Could not resolve annotated tag: $Tag" }
        $tagObject = ($tagJson -join "`n") | ConvertFrom-Json
        if ([string]$tagObject.object.type -ne 'commit') { throw "Tag does not point to a commit: $Tag" }
        return [string]$tagObject.object.sha
    }
    throw "Unsupported Git tag object type: $objectType"
}

$existingTagCommit = Resolve-RemoteTagCommit -Repository $repository -Tag $tag
if ($existingTagCommit -and $existingTagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) {
    throw "Release version is immutable. $tag already points to $existingTagCommit, not $ExpectedCommit. Bump release/version.json."
}

$releaseJson = & gh release view $tag --repo $repository --json tagName,isDraft,isPrerelease,url 2>$null
$releaseExists = $LASTEXITCODE -eq 0 -and -not [String]::IsNullOrWhiteSpace(($releaseJson -join "`n"))
if (-not $releaseExists) {
    $arguments = @('release','create',$tag,$setupPath,$checksumPath,'--repo',$repository,'--title',"KINOJO Meter $tag",'--notes',[string]$manifest.releaseNote)
    if ($existingTagCommit) { $arguments += '--verify-tag' }
    else { $arguments += @('--target',$ExpectedCommit) }
    & gh @arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub Release creation failed: $tag" }
    $existingTagCommit = Resolve-RemoteTagCommit -Repository $repository -Tag $tag
}

if (-not $existingTagCommit -or $existingTagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) {
    throw "Published tag commit does not match the workflow commit: tag=$existingTagCommit workflow=$ExpectedCommit"
}
$releaseJson = & gh release view $tag --repo $repository --json tagName,isDraft,isPrerelease,url,assets
if ($LASTEXITCODE -ne 0) { throw "GitHub Release readback failed: $tag" }
$publishedRelease = ($releaseJson -join "`n") | ConvertFrom-Json
if ($publishedRelease.isDraft -eq $true -or $publishedRelease.isPrerelease -eq $true) {
    throw 'Stable publication must not be draft or prerelease.'
}
$publishedAssetNames = @($publishedRelease.assets | ForEach-Object { [string]$_.name })
foreach ($assetPath in @($setupPath, $checksumPath)) {
    $assetName = Split-Path -Leaf $assetPath
    if ($publishedAssetNames -notcontains $assetName) {
        & gh release upload $tag $assetPath --repo $repository
        if ($LASTEXITCODE -ne 0) { throw "Missing GitHub Release asset recovery failed: $assetName" }
    }
}

& (Join-Path $PSScriptRoot 'prepare-github-release.ps1') -GitHubOwner $GitHubOwner -GitHubRepository $GitHubRepository -VerifyRemote
if ($LASTEXITCODE -ne 0) { throw 'Remote GitHub Release verification failed.' }

$registrationPath = Join-Path $Root "build\release\KINOJO_Meter_${version}_release-registration.json"
if (-not (Test-Path -LiteralPath $registrationPath -PathType Leaf)) { throw "Release registration JSON is missing: $registrationPath" }
$registration = Get-Content -LiteralPath $registrationPath -Raw | ConvertFrom-Json
if ($registration.remoteVerified -ne $true -or [string]$registration.version -ne $version) {
    throw 'Release registration JSON did not pass remote verification.'
}
& gh release upload $tag $registrationPath --repo $repository --clobber
if ($LASTEXITCODE -ne 0) { throw 'Release registration JSON upload failed.' }

if (-not [String]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "tag=$tag" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    "registrationPath=$registrationPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
}
Write-Host "Published and remotely verified: $repository $tag @ $ExpectedCommit"
