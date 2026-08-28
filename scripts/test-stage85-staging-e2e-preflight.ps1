$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$inspector = Join-Path $root 'scripts\inspect-stage85-staging-e2e.ps1'
$testRoot = Join-Path $env:TEMP ('kinojo-stage85-e2e-preflight-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $testRoot 'data'
$installRoot = Join-Path $testRoot 'install'
$outputRoot = Join-Path $testRoot 'output'

try {
    New-Item -Path $dataRoot,$installRoot -ItemType Directory -Force | Out-Null
    & $inspector -DataRoot $dataRoot -InstallRoot $installRoot -OutputDirectory $outputRoot

    $jsonPath = Join-Path $outputRoot 'stage85-staging-e2e-preflight.json'
    $shaPath = Join-Path $outputRoot 'stage85-staging-e2e-preflight.sha256'
    if (-not (Test-Path -LiteralPath $jsonPath -PathType Leaf) -or -not (Test-Path -LiteralPath $shaPath -PathType Leaf)) {
        throw 'Preflight evidence and checksum files were not created.'
    }
    $raw = Get-Content -LiteralPath $jsonPath -Raw
    $evidence = $raw | ConvertFrom-Json
    if ([int]$evidence.schemaVersion -ne 1 -or [string]$evidence.evidenceType -cne 'KINOJO_METER_STAGE85_STAGING_E2E_PREFLIGHT' -or
        [string]$evidence.evidenceScope -cne 'PREFLIGHT_ONLY_NOT_STAGING_VERIFICATION' -or
        [string]$evidence.status -cne 'INCOMPLETE_WITH_FAILURES' -or $evidence.receiptEligible -ne $false) {
        throw 'Empty-machine preflight did not fail closed.'
    }
    if (@($evidence.checks).Count -ne 14 -or @($evidence.blockers).Count -ne 14 -or
        @($evidence.checks | Where-Object { $_.id -eq 'existing-install-update' -and $_.status -eq 'FAIL' }).Count -ne 1 -or
        @($evidence.checks | Where-Object { $_.id -eq 'active-bundle-atomic-replace' -and $_.status -eq 'PENDING' }).Count -ne 1) {
        throw 'Preflight checklist or blocker aggregation is incomplete.'
    }
    foreach ($secretMarker in @('sessionToken','passKey','manifestSignature','device.dat',$testRoot)) {
        if ($raw.Contains($secretMarker)) { throw "Preflight evidence leaked a prohibited value or local path: $secretMarker" }
    }

    $actualSha = (Get-FileHash -LiteralPath $jsonPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedLine = "stage85-staging-e2e-preflight.json`t$((Get-Item -LiteralPath $jsonPath).Length)`t$actualSha"
    if ((Get-Content -LiteralPath $shaPath -Raw).Trim() -cne $expectedLine) {
        throw 'Preflight SHA-256 sidecar does not bind the exact evidence bytes.'
    }
    $tampered = [Text.Encoding]::UTF8.GetBytes($raw + 'tampered')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $tamperedSha = ([BitConverter]::ToString($sha.ComputeHash($tampered))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($tamperedSha -ceq $actualSha) { throw 'Tampered preflight evidence retained the original SHA-256.' }

    $fakeActiveBundle = Join-Path $dataRoot 'modules\active-bundle.json'
    New-Item -Path (Split-Path -Parent $fakeActiveBundle) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($fakeActiveBundle, '{"status":"ACTIVE_BUNDLE"}', [Text.UTF8Encoding]::new($false))
    $isolatedOutput = Join-Path $testRoot 'isolated'
    & $inspector -DataRoot $dataRoot -InstallRoot $installRoot -OutputDirectory $isolatedOutput
    $isolated = Get-Content -LiteralPath (Join-Path $isolatedOutput 'stage85-staging-e2e-preflight.json') -Raw | ConvertFrom-Json
    if ($isolated.activeBundle.present -ne $true -or $isolated.activeBundle.launcherValidationPassed -ne $false -or
        $isolated.activeBundle.exactIdentity -ne $false -or
        @($isolated.checks | Where-Object { $_.evidenceCode -eq 'ACTIVE_BUNDLE_LAUNCHER_VALIDATION_FAILED' -and $_.status -eq 'FAIL' }).Count -ne 3) {
        throw 'Custom data root active Bundle was not isolated from the installed Launcher default data root.'
    }

    $hostExe = (Get-Process -Id $PID).Path
    $requiredOutput = Join-Path $testRoot 'required'
    $arguments = '-NoProfile -NonInteractive -File "' + $inspector + '" -DataRoot "' + $dataRoot +
        '" -InstallRoot "' + $installRoot + '" -OutputDirectory "' + $requiredOutput + '" -RequireReady'
    $required = Start-Process -FilePath $hostExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($required.ExitCode -eq 0) { throw 'RequireReady accepted incomplete Stage 8-5 evidence.' }

    Write-Host 'STAGE85_STAGING_E2E_PREFLIGHT_BOUNDARY_OK checks=14 incompleteReadable=true requireReadyRejected=true customRootIsolated=true secrets=absent sha256Bound=true'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
