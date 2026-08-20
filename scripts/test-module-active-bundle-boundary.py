from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "launcher" / "ModuleBundleActivator.cs").read_text(encoding="utf-8")
pointer = (root / "launcher" / "ModuleBundleServerPointer.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

required = [
    'public const string ActiveStatus = "ACTIVE_BUNDLE"',
    'public const string RequiredActivationMode = "ATOMIC_BUNDLE"',
    'public const string StaleBundleBaseCode = "STALE_BUNDLE_BASE"',
    'ModuleStagingSelfTest.PassedStatus',
    'ComputeModuleSetHash',
    'ComputeDependencyFingerprint',
    'File.Replace(temporary, activeFile, null)',
    'File.Move(temporary, activeFile)',
    'ActivationAtomic = true',
    'ValidateWholeBundle',
    'ModuleBundleServerPointer.ValidateForActivation',
    'Channel = request.ExpectedChannel',
]
for token in required:
    assert token in source, f"missing 5-7 atomic bundle token: {token}"

for forbidden in [
    "Process.Start",
    "BeginNewAttempt",
    "ActiveCoreFile",
    "ActiveUiAssetFile",
    "meter_core_release_master",
    "meter_launcher_release_master",
    "supabase",
    "storageObjectPath",
    "Rollback(",
]:
    assert forbidden not in source, f"5-7 crossed deferred runtime/release/rollback boundary: {forbidden}"

assert 'ModuleActiveBundleFile = Path.Combine(ModuleRoot, "active-bundle.json")' in paths
assert 'ModuleActivationLockFile = Path.Combine(ModuleRoot, ".activation.lock")' in paths
assert '<Compile Include="ModuleBundleActivator.cs" />' in project
assert '<Compile Include="ModuleBundleServerPointer.cs" />' in project

for token in [
    'StablePromotionRequiredCode = "STABLE_PROMOTION_REQUIRED"',
    'StablePromotionMismatchCode = "STABLE_PROMOTION_MISMATCH"',
    'BundleOriginChannel',
    'PointerGeneration',
    'StagingVerificationId',
    'PreviousStableBundleLockSha256',
    'StablePointerGeneration',
    'String.Equals(value.BundleLockSha256, context.BundleLockSha256',
]:
    assert token in pointer, f"missing 5-10 Server pointer promotion token: {token}"

for forbidden in [
    "HttpClient",
    "Process.Start",
    "meter_core_release_master",
    "meter_launcher_release_master",
    "storageObjectPath",
]:
    assert forbidden not in pointer, f"5-10 pointer validator crossed network/release boundary: {forbidden}"

print("module active bundle atomic and Stable promotion boundary: PASS")
