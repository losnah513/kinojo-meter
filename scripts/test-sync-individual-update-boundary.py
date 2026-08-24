from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "SyncModuleUpdater.cs").read_text(encoding="utf-8")
runtime = (root / "launcher" / "PrivateRuntimePackageUpdater.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    'ModuleId = "sync"',
    'VersionShaConflictCode = "SYNC_VERSION_SHA_CONFLICT"',
    'PrivateRuntimeRequiredCode = "SYNC_PRIVATE_RUNTIME_REQUIRED"',
    'PrivateRuntimeChangedCode = "SYNC_PRIVATE_RUNTIME_CHANGED"',
    'CaptureRequiredCode = "SYNC_CAPTURE_REQUIRED"',
    'CaptureChangedCode = "SYNC_CAPTURE_CHANGED"',
    'ProtocolRequiredCode = "SYNC_PROTOCOL_REQUIRED"',
    'ProtocolChangedCode = "SYNC_PROTOCOL_CHANGED"',
    'ModulePackageDownloadCache',
    'ModulePackageVerifier.Verify',
    'ModuleStagingInstaller.Stage',
    'ModuleStagingSelfTest.RunForTest',
    'ModuleBundleActivator.ReadVerifiedActiveBundle',
    'ReadDefaultPrivateRuntime',
    'ReadDefaultCapture',
    'ReadDefaultProtocol',
    'ParentPrivateRuntimeVersion',
    'ParentPrivateRuntimeSha256',
    'ParentPrivateRuntimePointerGeneration',
    'ParentCaptureVersion',
    'ParentCaptureSha256',
    'ParentCapturePointerGeneration',
    'ParentProtocolVersion',
    'ParentProtocolSha256',
    'ParentProtocolPointerGeneration',
    'RuntimeBundleRevision',
    'RuntimeBundleLockSha256',
    'RuntimeModuleSetHash',
    'ModuleActiveSyncFile',
    'ModuleSyncUpdateLockFile',
    'ModuleProtocolUpdateLockFile',
    'WriteActiveState',
    'SyncModuleUpdateCoordinator',
    'dependencies.Count != 4',
]:
    assert token in updater, f"missing Sync updater boundary: {token}"

for token in [
    "SyncModuleReleaseManifest",
    "ActiveSyncModuleState",
    "SyncModuleUpdateAuthorization",
    "ParentPrivateRuntimePointerGeneration",
    "ParentCapturePointerGeneration",
    "ParentProtocolPointerGeneration",
]:
    assert token in models, f"missing Sync model: {token}"

for token in [
    "syncUpdateAuthorization",
    "currentSyncModule",
    "currentCaptureModule",
    "currentProtocolModule",
    "currentPrivateRuntime",
    "ParseSyncModuleAuthorization",
    'Dict(value, "syncModule")',
]:
    assert token in api, f"missing Sync API boundary: {token}"

for token in ["ModuleActiveSyncFile", ".sync-update.lock"]:
    assert token in paths, f"missing deterministic Sync path: {token}"

for source in [form, handoff]:
    assert "AuthorizeSyncModuleUpdateAsync" in source
    assert "SyncModuleUpdateCoordinator.ApplyAsync" in source

for token in ["SyncAssembly", "SyncOverrideActive", "ParentPrivateRuntimeSha256", "ParentCaptureSha256", "ParentProtocolSha256"]:
    assert token in runtime, f"missing exact Sync process-plan boundary: {token}"
assert '{ "pointerGeneration", state.PointerGeneration }' in runtime

assert '<Compile Include="SyncModuleUpdater.cs" />' in project

for token in [
    "parse Sync Engine update authorization",
    "accept Server-authorized Sync Engine release",
    "reject Sync Engine release outside exact signed path",
    "reject same Sync Engine version with different SHA",
    "reject Sync Engine for another parent private runtime",
    "reject Sync Engine for another active Capture",
    "reject Sync Engine for another active Protocol",
    "activate Sync Engine against exact Protocol parent and Bundle",
    "build exact Sync override process plan",
]:
    assert token in tests, f"missing Sync regression test: {token}"

for source in [updater, api, runtime]:
    assert "Process.Start(" not in source, "6-7 must not cut over process launch"

for forbidden in [
    "CombatUpdateAuthorization",
    "EncounterUpdateAuthorization",
]:
    assert forbidden not in updater + api, f"6-7 crossed a later individual module boundary: {forbidden}"

for forbidden in ["ModuleActivePrivateRuntimeFile", "ModuleActiveBundleFile", "ModuleActiveCaptureFile", "ModuleActiveProtocolFile"]:
    assert forbidden not in updater, f"Sync updater must not overwrite parent state: {forbidden}"

print("SYNC_INDIVIDUAL_UPDATE_BOUNDARY_OK authority=server module=sync parents=private-runtime+capture+protocol direct-deps=contracts+capture+protocol+combat bundle=exact active-state=separate process-cutover=false")
