from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "CaptureModuleUpdater.cs").read_text(encoding="utf-8")
runtime = (root / "launcher" / "PrivateRuntimePackageUpdater.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    'ModuleId = "capture"',
    'VersionShaConflictCode = "CAPTURE_VERSION_SHA_CONFLICT"',
    'PrivateRuntimeRequiredCode = "CAPTURE_PRIVATE_RUNTIME_REQUIRED"',
    'PrivateRuntimeChangedCode = "CAPTURE_PRIVATE_RUNTIME_CHANGED"',
    'ModulePackageDownloadCache',
    'ModulePackageVerifier.Verify',
    'ModuleStagingInstaller.Stage',
    'ModuleStagingSelfTest.RunForTest',
    'ModuleBundleActivator.ReadVerifiedActiveBundle',
    'ReadDefaultPrivateRuntime',
    'ParentPrivateRuntimeVersion',
    'ParentPrivateRuntimeSha256',
    'ParentPrivateRuntimePointerGeneration',
    'RuntimeBundleRevision',
    'RuntimeBundleLockSha256',
    'RuntimeModuleSetHash',
    'ModuleActiveCaptureFile',
    'ModuleCaptureUpdateLockFile',
    'WriteActiveState',
    'CaptureModuleUpdateCoordinator',
    'dependencies.Count != 1',
]:
    assert token in updater, f"missing Capture updater boundary: {token}"

for token in [
    "CaptureModuleReleaseManifest",
    "ActiveCaptureModuleState",
    "CaptureModuleUpdateAuthorization",
    "ParentPrivateRuntimePointerGeneration",
]:
    assert token in models, f"missing Capture model: {token}"

for token in [
    "captureUpdateAuthorization",
    "currentCaptureModule",
    "currentPrivateRuntime",
    "ParseCaptureModuleAuthorization",
    'Dict(value, "captureModule")',
]:
    assert token in api, f"missing Capture API boundary: {token}"

for token in ["ModuleActiveCaptureFile", ".capture-update.lock"]:
    assert token in paths, f"missing deterministic Capture path: {token}"

for source in [form, handoff]:
    assert "AuthorizeCaptureModuleUpdateAsync" in source
    assert "CaptureModuleUpdateCoordinator.ApplyAsync" in source

for token in ["CaptureAssembly", "CaptureOverrideActive", "ParentPrivateRuntimeSha256"]:
    assert token in runtime, f"missing exact Capture process-plan boundary: {token}"
assert '{ "pointerGeneration", state.PointerGeneration }' in runtime

assert '<Compile Include="CaptureModuleUpdater.cs" />' in project

for token in [
    "parse Capture Engine update authorization",
    "accept Server-authorized Capture Engine release",
    "reject Capture Engine release outside exact signed path",
    "reject same Capture Engine version with different SHA",
    "reject Capture Engine for another parent private runtime",
    "activate Capture Engine against exact parent and Bundle",
    "build exact Capture override process plan",
]:
    assert token in tests, f"missing Capture regression test: {token}"

for source in [updater, api, runtime]:
    assert "Process.Start(" not in source, "6-5 must not cut over process launch"

for forbidden in [
    "ProtocolModuleUpdater",
    "ProtocolUpdateAuthorization",
    "CombatUpdateAuthorization",
    "SyncUpdateAuthorization",
]:
    assert forbidden not in updater + api, f"6-5 crossed a later individual module boundary: {forbidden}"

for forbidden in ["ModuleActivePrivateRuntimeFile", "ModuleActiveBundleFile"]:
    assert forbidden not in updater, f"Capture updater must not overwrite parent state: {forbidden}"

print("CAPTURE_INDIVIDUAL_UPDATE_BOUNDARY_OK authority=server module=capture parent=private-runtime bundle=exact active-state=separate process-cutover=false")
