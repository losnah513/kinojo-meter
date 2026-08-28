[CmdletBinding()]
param(
    [string]$DataRoot = '',
    [string]$InstallRoot = '',
    [string]$OutputDirectory = '',
    [switch]$RequireReady
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Stage 8-5 local E2E preflight must run on Windows.'
}

$Expected = [ordered]@{
    BundleRevision = 'B000051'
    BundleLockSha256 = '7532c3631a3e1de36218d14a486f31c3b2d3ad9e58f18eef1e371dc6dcc30e96'
    ModuleSetHash = '971159ada58afcac0a67c15bad1e9a3ed8ad23e160f71fbabcd02b68aa3a1178'
    BundleProductVersion = '0.3.1'
    LauncherFileVersion = '1.1.8.0'
    LauncherProductVersion = '1.1.8+a18241d17a31956a535756aa4ded5ed78bf3b8bf'
    LauncherSha256 = '414823dc67c286315263e35d836297e9b1ce9c848929fa4e63a7b1d2ad6d68dd'
    LauncherSetupSha256 = '1a716874278188b18912d5d071624f0718f66b8b634a2925c79ceabf1c67cc15'
    CoreVersion = '0.2.80'
    CorePackageSha256 = 'fe27e3ab999383f529222cd267eec4715347d1138f76cabb52f62d510bf3100e'
    CoreInstallManifestSha256 = '4f09e38fedd7b8d43cfd044f4a47a6189676d4d5382d35593aa74b51688e0c0c'
}

$RequiredModuleIds = @('contracts','capture','protocol','combat','encounter','sync','shell')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$defaultDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'KINOJO Meter Staging'
if ([String]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = $defaultDataRoot
}
if ([String]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\KINOJO Meter Staging'
}
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\stage85-e2e-preflight'
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

function Get-Sha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Content
    )
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-Check {
    param(
        [Parameter(Mandatory=$true)][string]$Id,
        [Parameter(Mandatory=$true)][ValidateSet('PASS','PENDING','FAIL')][string]$Status,
        [Parameter(Mandatory=$true)][string]$EvidenceCode
    )
    return [ordered]@{ id=$Id; status=$Status; evidenceCode=$EvidenceCode }
}

function Exact-CoreState {
    param([Parameter(Mandatory=$true)][object]$State)
    $expectedInstalled = [IO.Path]::GetFullPath((Join-Path $DataRoot ('core\versions\' + $Expected.CoreVersion)))
    $actualInstalled = if ([String]::IsNullOrWhiteSpace([string]$State.InstalledPath)) { '' } else { [IO.Path]::GetFullPath([string]$State.InstalledPath) }
    return [string]$State.Channel -ceq 'staging' -and
        [string]$State.CoreVersion -ceq $Expected.CoreVersion -and
        [string]$State.PackageSha256 -ceq $Expected.CorePackageSha256 -and
        [string]$State.InstallManifestSha256 -ceq $Expected.CoreInstallManifestSha256 -and
        $actualInstalled -ceq $expectedInstalled -and
        (Test-Path -LiteralPath $actualInstalled -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $actualInstalled ([string]$State.EntryPoint)) -PathType Leaf)
}

$launcherPath = Join-Path $InstallRoot 'KINOJO.Meter.Launcher.Staging.exe'
$setupPath = Join-Path $InstallRoot 'KINOJO.Meter.Launcher.Staging.Setup.exe'
$activeCorePath = Join-Path $DataRoot 'core\active.json'
$activeBundlePath = Join-Path $DataRoot 'modules\active-bundle.json'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KINOJO Meter Launcher Staging'

$launcher = [ordered]@{
    present = $false
    exactReleaseBytes = $false
    fileVersion = ''
    productVersion = ''
    sha256 = ''
    setupSha256 = ''
    registryExact = $false
}
$launcherStatus = 'FAIL'
$launcherCode = 'LAUNCHER_MISSING'
if ((Test-Path -LiteralPath $launcherPath -PathType Leaf) -and (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    $launcherInfo = Get-Item -LiteralPath $launcherPath
    $launcher.present = $true
    $launcher.fileVersion = [string]$launcherInfo.VersionInfo.FileVersion
    $launcher.productVersion = [string]$launcherInfo.VersionInfo.ProductVersion
    $launcher.sha256 = Get-Sha256 -Path $launcherPath
    $launcher.setupSha256 = Get-Sha256 -Path $setupPath
    $registryExact = $false
    if (Test-Path -LiteralPath $uninstallKey) {
        $registry = Get-ItemProperty -LiteralPath $uninstallKey
        $registryExact = [string]$registry.DisplayVersion -ceq $Expected.LauncherProductVersion -and
            [IO.Path]::GetFullPath([string]$registry.InstallLocation) -ceq $InstallRoot
    }
    $launcher.registryExact = $registryExact
    $launcher.exactReleaseBytes = $launcher.fileVersion -ceq $Expected.LauncherFileVersion -and
        $launcher.productVersion -ceq $Expected.LauncherProductVersion -and
        $launcher.sha256 -ceq $Expected.LauncherSha256 -and
        $launcher.setupSha256 -ceq $Expected.LauncherSetupSha256 -and
        $registryExact
    if ($launcher.exactReleaseBytes) { $launcherStatus='PASS'; $launcherCode='LAUNCHER_1_1_8_EXACT' }
    else { $launcherCode='LAUNCHER_RELEASE_IDENTITY_MISMATCH' }
}

$core = [ordered]@{
    present = $false
    exactRelease = $false
    version = ''
    activeStateSha256 = ''
    packageSha256 = ''
    installManifestSha256 = ''
}
$coreStatus = 'FAIL'
$coreCode = 'CORE_ACTIVE_STATE_MISSING'
if (Test-Path -LiteralPath $activeCorePath -PathType Leaf) {
    try {
        $coreState = Get-Content -LiteralPath $activeCorePath -Raw | ConvertFrom-Json
        $core.present = $true
        $core.version = [string]$coreState.CoreVersion
        $core.activeStateSha256 = Get-Sha256 -Path $activeCorePath
        $core.packageSha256 = [string]$coreState.PackageSha256
        $core.installManifestSha256 = [string]$coreState.InstallManifestSha256
        $core.exactRelease = Exact-CoreState -State $coreState
        if ($core.exactRelease) { $coreStatus='PASS'; $coreCode='CORE_0_2_80_EXACT' }
        else { $coreCode='CORE_RELEASE_IDENTITY_MISMATCH' }
    }
    catch { $coreCode='CORE_ACTIVE_STATE_INVALID' }
}

$bundle = [ordered]@{
    present = $false
    launcherValidationPassed = $false
    exactIdentity = $false
    bundleRevision = ''
    bundleLockSha256 = ''
    moduleSetHash = ''
    productVersion = ''
    activeStateSha256 = ''
    moduleCount = 0
    modules = @()
}
$bundleStatus = 'PENDING'
$bundleCode = 'ACTIVE_BUNDLE_NOT_CREATED'
if (Test-Path -LiteralPath $activeBundlePath -PathType Leaf) {
    $bundle.present = $true
    try {
        if ($DataRoot -cne [IO.Path]::GetFullPath($defaultDataRoot)) {
            throw 'Installed Launcher active Bundle validation is restricted to its default Staging data root.'
        }
        if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) { throw 'Launcher is missing.' }
        $assembly = [Reflection.Assembly]::LoadFile($launcherPath)
        $type = $assembly.GetType('KinojoMeterLauncher.ModuleBundleActivator',$true)
        $method = $type.GetMethod('ReadVerifiedActiveBundle',[Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static)
        if ($null -eq $method) { throw 'Installed Launcher validator is unavailable.' }
        $state = $method.Invoke($null,@())
        if ($null -eq $state) { throw 'Installed Launcher returned no active Bundle.' }
        $modules = @($state.Modules | Sort-Object ModuleId | ForEach-Object {
            [ordered]@{
                moduleId = [string]$_.ModuleId
                moduleVersion = [string]$_.ModuleVersion
                archiveSha256 = [string]$_.ArchiveSha256
                stateSchemaVersion = [int]$_.StateSchemaVersion
                manifestSha256 = [string]$_.ManifestSha256
                selfTestReceiptSha256 = [string]$_.SelfTestReceiptSha256
            }
        })
        $moduleIds = @($modules | ForEach-Object { [string]$_.moduleId })
        $exactModules = $moduleIds.Count -eq $RequiredModuleIds.Count -and
            @($moduleIds | Where-Object { $_ -notin $RequiredModuleIds }).Count -eq 0
        $bundle.launcherValidationPassed = $true
        $bundle.bundleRevision = [string]$state.BundleRevision
        $bundle.bundleLockSha256 = [string]$state.BundleLockSha256
        $bundle.moduleSetHash = [string]$state.ModuleSetHash
        $bundle.productVersion = [string]$state.ProductVersion
        $bundle.activeStateSha256 = Get-Sha256 -Path $activeBundlePath
        $bundle.moduleCount = $modules.Count
        $bundle.modules = $modules
        $bundle.exactIdentity = [string]$state.Status -ceq 'ACTIVE_BUNDLE' -and
            [string]$state.Channel -ceq 'staging' -and [bool]$state.ActivationAtomic -eq $true -and
            $bundle.bundleRevision -ceq $Expected.BundleRevision -and
            $bundle.bundleLockSha256 -ceq $Expected.BundleLockSha256 -and
            $bundle.moduleSetHash -ceq $Expected.ModuleSetHash -and
            $bundle.productVersion -ceq $Expected.BundleProductVersion -and $exactModules
        if ($bundle.exactIdentity) { $bundleStatus='PASS'; $bundleCode='ACTIVE_BUNDLE_EXACT_AND_SELF_TESTED' }
        else { $bundleStatus='FAIL'; $bundleCode='ACTIVE_BUNDLE_IDENTITY_MISMATCH' }
    }
    catch {
        $bundleStatus='FAIL'
        $bundleCode='ACTIVE_BUNDLE_LAUNCHER_VALIDATION_FAILED'
    }
}

$existingUpdateStatus = if ($launcherStatus -eq 'PASS' -and $coreStatus -eq 'PASS') { 'PENDING' } else { 'FAIL' }
$checks = @(
    (New-Check 'clean-install' 'PENDING' 'WINDOWS_CLEAN_MACHINE_RECEIPT_REQUIRED'),
    (New-Check 'existing-install-update' $existingUpdateStatus $(if($existingUpdateStatus -eq 'PENDING'){'EXACT_BASELINE_PRESENT_UPDATE_RECEIPT_REQUIRED'}else{'INSTALLED_RELEASE_NOT_EXACT'})),
    (New-Check 'changed-modules-only-download' 'PENDING' 'RUNTIME_DOWNLOAD_COUNTER_RECEIPT_REQUIRED'),
    (New-Check 'staging-slot-install' $bundleStatus $bundleCode),
    (New-Check 'module-self-test' $bundleStatus $bundleCode),
    (New-Check 'shell-engine-ready-health' 'PENDING' 'RUNTIME_READY_HEALTH_RECEIPT_REQUIRED'),
    (New-Check 'active-bundle-atomic-replace' $bundleStatus $bundleCode),
    (New-Check 'meter-on-off' 'PENDING' 'OPERATOR_RUNTIME_RECEIPT_REQUIRED'),
    (New-Check 'game-packet-protocol-combat-encounter-sync' 'PENDING' 'REAL_GAME_EVIDENCE_REQUIRED'),
    (New-Check 'damaged-module-redownload' 'PENDING' 'FAULT_INJECTION_RECEIPT_REQUIRED'),
    (New-Check 'startup-failure-rollback' 'PENDING' 'ROLLBACK_RECEIPT_REQUIRED'),
    (New-Check 'next-launch-incident-upload' 'PENDING' 'INCIDENT_UPLOAD_RECEIPT_REQUIRED'),
    (New-Check 'state-migration-rollback-readability' 'PENDING' 'STATE_COMPATIBILITY_RECEIPT_REQUIRED'),
    (New-Check 'dpi-80-90-100-readability' 'PENDING' 'VISUAL_EVIDENCE_REQUIRED')
)
$passed = @($checks | Where-Object { $_.status -eq 'PASS' }).Count
$failed = @($checks | Where-Object { $_.status -eq 'FAIL' }).Count
$pending = @($checks | Where-Object { $_.status -eq 'PENDING' }).Count
$status = if ($passed -eq $checks.Count) { 'READY_TO_VERIFY' } elseif ($failed -gt 0) { 'INCOMPLETE_WITH_FAILURES' } else { 'INCOMPLETE' }
$blockers = @($checks | Where-Object { $_.status -ne 'PASS' } | ForEach-Object { [string]$_.id })

$document = [ordered]@{
    schemaVersion = 1
    evidenceType = 'KINOJO_METER_STAGE85_STAGING_E2E_PREFLIGHT'
    evidenceScope = 'PREFLIGHT_ONLY_NOT_STAGING_VERIFICATION'
    status = $status
    receiptEligible = ($status -eq 'READY_TO_VERIFY')
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    expected = $Expected
    launcher = $launcher
    core = $core
    activeBundle = $bundle
    checks = $checks
    summary = [ordered]@{ total=$checks.Count; passed=$passed; pending=$pending; failed=$failed }
    blockers = $blockers
    prohibitions = @('NO_REBUILD_DURING_VERIFICATION','NO_UNVERIFIED_BUNDLE_REPLACEMENT','NO_STABLE_POINTER_CHANGE')
}

New-Item -Path $OutputDirectory -ItemType Directory -Force | Out-Null
$jsonPath = Join-Path $OutputDirectory 'stage85-staging-e2e-preflight.json'
$shaPath = Join-Path $OutputDirectory 'stage85-staging-e2e-preflight.sha256'
$json = ($document | ConvertTo-Json -Depth 12) + "`n"
Write-Utf8NoBom -Path $jsonPath -Content $json
$jsonSize = (Get-Item -LiteralPath $jsonPath).Length
$jsonSha256 = Get-Sha256 -Path $jsonPath
Write-Utf8NoBom -Path $shaPath -Content ("stage85-staging-e2e-preflight.json`t$jsonSize`t$jsonSha256`n")

Write-Host "STAGE85_STAGING_E2E_PREFLIGHT status=$status passed=$passed pending=$pending failed=$failed receiptEligible=$($document.receiptEligible.ToString().ToLowerInvariant()) sha256=$jsonSha256"
Write-Host "Evidence: $jsonPath"
if ($RequireReady -and $status -ne 'READY_TO_VERIFY') {
    throw "Stage 8-5 Staging E2E is not ready for verification. blockers=$($blockers -join ',')"
}
