[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Endpoint,
    [Parameter(Mandatory=$true)][string]$ExpectedCommit,
    [ValidateSet('stable','staging')][string]$Channel = 'stable'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'ExpectedCommit must be a full Git commit SHA.' }
if ([String]::IsNullOrWhiteSpace($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN) -or [String]::IsNullOrWhiteSpace($env:ACTIONS_ID_TOKEN_REQUEST_URL)) {
    throw 'GitHub Actions OIDC is unavailable. The job requires id-token: write.'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestName = if ($Channel -eq 'staging') { 'launcher-staging-version.json' } else { 'launcher-version.json' }
$manifest = Get-Content -LiteralPath (Join-Path $root "release\$manifestName") -Raw | ConvertFrom-Json
$audience = [Uri]::EscapeDataString('kinojo-meter-release-sync')
$separator = if ($env:ACTIONS_ID_TOKEN_REQUEST_URL.Contains('?')) { '&' } else { '?' }
$tokenUrl = "$($env:ACTIONS_ID_TOKEN_REQUEST_URL)$separator" + "audience=$audience"
$token = Invoke-RestMethod -Method Get -Uri $tokenUrl `
    -Headers @{ Authorization = "Bearer $($env:ACTIONS_ID_TOKEN_REQUEST_TOKEN)"; Accept = 'application/json' }
$idToken = [string]$token.value
if ([String]::IsNullOrWhiteSpace($idToken)) { throw 'GitHub OIDC provider returned no token.' }
$body = @{ releaseType='launcher'; channel=$Channel; version=[string]$manifest.version; commitSha=$ExpectedCommit } | ConvertTo-Json -Compress
$result = Invoke-RestMethod -Method Post -Uri $Endpoint -Headers @{ Authorization="Bearer $idToken"; Accept='application/json' } `
    -ContentType 'application/json' -Body $body
if ($result.ok -ne $true -or [string]$result.channel -cne $Channel -or
    [string]$result.activatedVersion -ne [string]$manifest.version -or $result.remoteVerified -ne $true) {
    throw "Launcher Server synchronization failed: $($result | ConvertTo-Json -Depth 8 -Compress)"
}
Write-Host "Launcher Server Master synchronized: channel=$Channel version=$($result.activatedVersion) operation=$($result.operationStatus)"
