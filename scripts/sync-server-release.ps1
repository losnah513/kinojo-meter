[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Endpoint,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedCommit must be a full Git commit SHA.' }
if ([String]::IsNullOrWhiteSpace($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) -or [String]::IsNullOrWhiteSpace($env:ACTIONS_ID_TOKEN_REQUEST_URL)) {
    throw 'GitHub Actions OIDC environment is not available. The job requires id-token: write.'
}

$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifest = Get-Content -LiteralPath (Join-Path $Root 'release\version.json') -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$registrationPath = Join-Path $Root "build\release\KINOJO_Meter_${version}_release-registration.json"
if (-not (Test-Path -LiteralPath $registrationPath -PathType Leaf)) { throw "Verified release registration JSON is missing: $registrationPath" }
$registration = Get-Content -LiteralPath $registrationPath -Raw | ConvertFrom-Json
if ($registration.remoteVerified -ne $true -or [string]$registration.version -ne $version) {
    throw 'Only a remotely verified registration manifest can be synchronized.'
}

$audience = [Uri]::EscapeDataString('kinojo-meter-release-sync')
$separator = if ($env:ACTIONS_ID_TOKEN_REQUEST_URL.Contains('?')) { '&' } else { '?' }
$tokenUrl = "$($env:ACTIONS_ID_TOKEN_REQUEST_URL)$separator" + "audience=$audience"
$tokenResponse = Invoke-RestMethod -Uri $tokenUrl -Headers @{ Authorization = "Bearer $($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN)"; Accept = 'application/json' } -Method Get
$idToken = [string]$tokenResponse.value
if ([String]::IsNullOrWhiteSpace($idToken)) { throw 'GitHub OIDC provider did not return an ID token.' }

$body = @{ version = $version; commitSha = $ExpectedCommit } | ConvertTo-Json -Compress
$result = Invoke-RestMethod -Uri $Endpoint -Method Post -Headers @{ Authorization = "Bearer $idToken"; Accept = 'application/json' } -ContentType 'application/json' -Body $body
if ($result.ok -ne $true -or [string]$result.activatedVersion -ne $version) {
    throw "Server release synchronization failed: $($result | ConvertTo-Json -Depth 8 -Compress)"
}
Write-Host "Server Release Master synchronized: version=$version active=$($result.active) operation=$($result.operationStatus)"
