[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Require-Text([string]$Path, [string]$Pattern, [string]$Message) {
    $content = Get-Content -LiteralPath (Join-Path $root $Path) -Raw
    if ($content -notmatch $Pattern) { throw "$Message ($Path)" }
}

$launcher = Get-Content -LiteralPath (Join-Path $root 'release\launcher-version.json') -Raw | ConvertFrom-Json
$core = Get-Content -LiteralPath (Join-Path $root 'release\core-version.json') -Raw | ConvertFrom-Json
if ($launcher.publicDistribution -ne $true -or $launcher.coreDelivery -ne 'SERVER_AUTHORIZED_PRIVATE_STORAGE') {
    throw 'Launcher manifest must describe public Launcher/private Core delivery.'
}
if ($launcher.codeSignatureRequired -ne $true -or [string]$launcher.publisherSubject -cne 'KINOJO INFO') {
    throw 'Launcher Authenticode requirement is missing.'
}
if ($core.publicDistribution -ne $false -or $core.storageBucket -ne 'meter-core-private' -or $core.codeSignatureRequired -ne $true -or
    [string]$core.publisherSubject -cne 'KINOJO INFO') {
    throw 'Core manifest must remain private and signature-required.'
}
if ([string]$launcher.cutoverState -ne [string]$core.cutoverState) { throw 'Launcher/Core cutover states must move together.' }

Require-Text '.github\workflows\launcher-build.yml' 'build-launcher[.]ps1' 'Public workflow does not build the Launcher.'
Require-Text '.github\workflows\launcher-build.yml' 'Rebuild setup with the signed Launcher application' 'Signed Launcher is not embedded before setup publication.'
Require-Text 'launcher-setup\KINOJO.Meter.Launcher.Setup.csproj' 'KINOJO[.]Meter[.]Launcher[.]Payload' 'Public Launcher setup payload resource is missing.'
Require-Text 'launcher-setup\SetupEngine.cs' 'LocalApplicationData' 'Launcher setup must use a per-user install path.'
Require-Text 'launcher-setup\SetupEngine.cs' 'UninstallString' 'Launcher setup uninstall registration is missing.'
$publicWorkflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\launcher-build.yml') -Raw
if ($publicWorkflow -match 'run:\s*[^\r\n]*build-core-private' -or $publicWorkflow -match 'path:\s*[^\r\n]*KinojoMeterCore_') {
    throw 'Public Launcher workflow must never build or publish a Core package.'
}
Require-Text 'launcher\CorePackageInstaller.cs' 'RedirectStandardInput\s*=\s*true' 'Launcher must transfer the session through redirected stdin.'
Require-Text 'launcher\CorePackageInstaller.cs' 'KINOJO_CORE_READY_V1' 'Launcher must verify the Core ready handshake.'
Require-Text 'launcher\CorePackageInstaller.cs' 'StartCoreAndWaitForReadyAsync\(install[.]Previous' 'Launcher must automatically run the previous Core after a failed update.'
Require-Text 'launcher\CorePackageInstaller.cs' 'WinVerifyTrust' 'Core Authenticode validation is missing.'
Require-Text 'launcher\CorePackageInstaller.cs' 'storage/v1/object/sign/meter-core-private' 'Private Storage URL allow-list is missing.'
Require-Text 'launcher\CorePackageInstaller.cs' 'ValidatePackageRelativePath' 'Core ZIP path hardening is missing.'
Require-Text 'launcher\CorePackageInstaller.cs' 'maximumArchiveEntries' 'Core ZIP entry count boundary is missing.'
Require-Text 'src\Program.cs' 'LauncherSessionEnvelope[.]TryRead' 'Core direct execution gate is missing.'
Require-Text 'src\Program.cs' 'KINOJO_CORE_READY_V1' 'Core ready handshake is missing.'
Require-Text 'src\GameCapture.cs' 'KINOJO-Realtime-Decoder' 'Dedicated Decoder worker is missing.'
Require-Text 'src\OverlayWindow.cs' 'KINOJO-Realtime-DPS' 'Dedicated DPS worker is missing.'
Require-Text 'src\OverlayWindow.cs' 'TimeSpan[.]FromMilliseconds\(50\)' 'Snapshot render throttle is missing.'
Require-Text 'src\DiagnosticLog.cs' 'ThreadPriority[.]BelowNormal' 'Background diagnostic writer priority is missing.'

Write-Host "Distribution boundary verified: launcher=$($launcher.version) core=$($core.version) state=$($core.cutoverState)"
