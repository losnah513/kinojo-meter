from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "launcher" / "ModuleStagingInstaller.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

required = [
    'public const string StagedStatus = "STAGED"',
    'public const string InstallReceiptName = "staging-install.json"',
    'ModulePackageVerifier.Verify(',
    'ModulePackageVerifier.VerifyForTest(',
    'EnsureNoDifferentShaSibling',
    'ExtractSafely',
    'Directory.Move(temporaryDirectory, finalDirectory)',
    '"activationAllowed", false',
    '"activeBundleChanged", false',
]
for token in required:
    assert token in source, f"missing staging boundary token: {token}"

for forbidden in [
    "ActiveCoreFile",
    "ActiveUiAssetFile",
    "Process.Start",
    "active-bundle",
    "BeginNewAttempt",
]:
    assert forbidden not in source, f"5-5 crossed deferred boundary: {forbidden}"

assert 'ModuleStaging = Path.Combine(ModuleRoot, "staging")' in paths
assert 'Directory.CreateDirectory(ModuleStaging)' in paths
assert '<Compile Include="ModuleStagingInstaller.cs" />' in project

print("module staging installer boundary: PASS")
