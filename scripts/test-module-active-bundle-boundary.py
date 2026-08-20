from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "launcher" / "ModuleBundleActivator.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

required = [
    'public const string ActiveStatus = "ACTIVE_BUNDLE"',
    'public const string RequiredActivationMode = "ATOMIC_BUNDLE"',
    'public const string StaleBundleBaseCode = "STALE_BUNDLE_BASE"',
    'SELF_TEST_PASSED',
    'ComputeModuleSetHash',
    'ComputeDependencyFingerprint',
    'File.Replace(temporary, activeFile, null)',
    'File.Move(temporary, activeFile)',
    'ActivationAtomic = true',
    'ValidateWholeBundle',
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

print("module active bundle atomic boundary: PASS")
