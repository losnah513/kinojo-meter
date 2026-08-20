from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "launcher" / "ModuleStagingSelfTest.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

required = [
    'public const string PassedStatus = "SELF_TEST_PASSED"',
    'public const string ReceiptName = "self-test.json"',
    'ValidateStagedFiles',
    'ValidatePrimaryArtifact',
    'AssemblyName.GetAssemblyName',
    'ValidateDependencies',
    'DependencyFingerprint',
    '"activationAllowed", false',
    '"activeBundleChanged", false',
]
for token in required:
    assert token in source, f"missing 5-6 self-test boundary token: {token}"

for forbidden in [
    "ActiveCoreFile",
    "ActiveUiAssetFile",
    "Process.Start",
    "active-bundle",
    "BeginNewAttempt",
    "File.Replace(temporary, LauncherPaths",
]:
    assert forbidden not in source, f"5-6 crossed deferred activation/reset boundary: {forbidden}"

assert 'ModuleSelfTests = Path.Combine(ModuleRoot, "self-tests")' in paths
assert 'Directory.CreateDirectory(ModuleSelfTests)' in paths
assert '<Compile Include="ModuleStagingSelfTest.cs" />' in project

print("module staging self-test boundary: PASS")
