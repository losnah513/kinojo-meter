[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$GitHubOwner,
    [Parameter(Mandatory=$true)][string]$GitHubRepository,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Launcher publication must run on Windows.' }
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedCommit must be a full Git commit SHA.' }
if ([String]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'GH_TOKEN is required.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -LiteralPath (Join-Path $root 'release\launcher-version.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$artifactName = [string]$manifest.artifactName
$artifactPath = Join-Path $root "build\$artifactName"
$checksumPath = Join-Path $root "build\checksums_launcher_${version}.txt"
$repository = "$GitHubOwner/$GitHubRepository"
$tag = "launcher-v$version"

if ($manifest.cutoverState -ne 'ACTIVE' -or $manifest.publicDistribution -ne $true -or $manifest.codeSignatureRequired -ne $true) {
    throw 'Launcher publication requires ACTIVE, publicDistribution=true and codeSignatureRequired=true.'
}
if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) { throw "Launcher artifact is missing: $artifactPath" }
$signature = Get-AuthenticodeSignature -LiteralPath $artifactPath
$publisher = [string]$manifest.publisherSubject
if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject.IndexOf($publisher, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Launcher must have a valid Authenticode signature from '$publisher'."
}

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
    & gh release create $tag $artifactPath --repo $repository --target $ExpectedCommit `
        --title "KINOJO Meter Launcher $version" --notes ([string]$manifest.releaseNote)
    if ($LASTEXITCODE -ne 0) { throw 'Launcher GitHub Release creation failed.' }
    $tagCommit = Resolve-TagCommit $repository $tag
}
if (-not $tagCommit -or $tagCommit.ToLowerInvariant() -ne $ExpectedCommit.ToLowerInvariant()) { throw 'Launcher tag readback failed.' }

$downloadRoot = Join-Path $env:RUNNER_TEMP ("kinojo-launcher-verify-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloadRoot | Out-Null
try {
    $release = ((& gh release view $tag --repo $repository --json isDraft,isPrerelease,assets) -join "`n") | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $release.isDraft -eq $true -or $release.isPrerelease -eq $true) { throw 'Launcher stable Release readback failed.' }
    $assetNames = @($release.assets | ForEach-Object { [string]$_.name })
    if ($assetNames -notcontains $artifactName) {
        & gh release upload $tag $artifactPath --repo $repository
        if ($LASTEXITCODE -ne 0) { throw 'Launcher executable asset recovery failed.' }
    }
    & gh release download $tag --repo $repository --dir $downloadRoot --pattern $artifactName
    if ($LASTEXITCODE -ne 0) { throw 'Launcher remote executable download failed.' }
    $remoteArtifact = Join-Path $downloadRoot $artifactName
    $remoteSignature = Get-AuthenticodeSignature -LiteralPath $remoteArtifact
    if ($remoteSignature.Status -ne 'Valid' -or -not $remoteSignature.SignerCertificate -or
        $remoteSignature.SignerCertificate.Subject.IndexOf($publisher, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw 'Published Launcher signature readback failed.'
    }
    $size = (Get-Item -LiteralPath $remoteArtifact).Length
    $sha256 = (Get-FileHash -LiteralPath $remoteArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$artifactName`t$size`t$sha256"
    @($checksumLine) | Set-Content -LiteralPath $checksumPath -Encoding ascii

    $checksumName = Split-Path -Leaf $checksumPath
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

Write-Host "Launcher release verified: $repository $tag @ $ExpectedCommit size=$size sha256=$sha256"
