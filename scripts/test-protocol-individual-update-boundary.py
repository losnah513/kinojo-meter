from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "ProtocolModuleUpdater.cs").read_text(encoding="utf-8")
runtime = (root / "launcher" / "PrivateRuntimePackageUpdater.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    'ModuleId = "protocol"',
    'VersionShaConflictCode = "PROTOCOL_VERSION_SHA_CONFLICT"',
    'PrivateRuntimeRequiredCode = "PROTOCOL_PRIVATE_RUNTIME_REQUIRED"',
    'PrivateRuntimeChangedCode = "PROTOCOL_PRIVATE_RUNTIME_CHANGED"',
    'CaptureRequiredCode = "PROTOCOL_CAPTURE_REQUIRED"',
    'CaptureChangedCode = "PROTOCOL_CAPTURE_CHANGED"',
    'ModulePackageDownloadCache',
    'ModulePackageVerifier.Verify',
    'ModuleStagingInstaller.Stage',
    'ModuleStagingSelfTest.RunForTest',
    'ModuleBundleActivator.ReadVerifiedActiveBundle',
    'ReadDefaultPrivateRuntime',
    'ReadDefaultCapture',
    'ParentPrivateRuntimeVersion',
    'ParentPrivateRuntimeSha256',
    'ParentPrivateRuntimePointerGeneration',
    'ParentCaptureVersion',
    'ParentCaptureSha256',
    'ParentCapturePointerGeneration',
    'RuntimeBundleRevision',
    'RuntimeBundleLockSha256',
    'RuntimeModuleSetHash',
    'ModuleActiveProtocolFile',
    'ModuleProtocolUpdateLockFile',
    'WriteActiveState',
    'ProtocolModuleUpdateCoordinator',
    'dependencies.Count != 2',
]:
    assert token in updater, f"missing Protocol updater boundary: {token}"

for token in [
    "ProtocolModuleReleaseManifest",
    "ActiveProtocolModuleState",
    "ProtocolModuleUpdateAuthorization",
    "ParentPrivateRuntimePointerGeneration",
    "ParentCapturePointerGeneration",
]:
    assert token in models, f"missing Protocol model: {token}"

for token in [
    "protocolUpdateAuthorization",
    "currentProtocolModule",
    "currentCaptureModule",
    "currentPrivateRuntime",
    "ParseProtocolModuleAuthorization",
    'Dict(value, "protocolModule")',
]:
    assert token in api, f"missing Protocol API boundary: {token}"

for token in ["ModuleActiveProtocolFile", ".protocol-update.lock"]:
    assert token in paths, f"missing deterministic Protocol path: {token}"

for source in [form, handoff]:
    assert "AuthorizeProtocolModuleUpdateAsync" in source
    assert "ProtocolModuleUpdateCoordinator.ApplyAsync" in source

for token in ["ProtocolAssembly", "ProtocolOverrideActive", "ParentPrivateRuntimeSha256", "ParentCaptureSha256"]:
    assert token in runtime, f"missing exact Protocol process-plan boundary: {token}"
assert '{ "pointerGeneration", state.PointerGeneration }' in runtime

assert '<Compile Include="ProtocolModuleUpdater.cs" />' in project

for token in [
    "parse Protocol Engine update authorization",
    "accept Server-authorized Protocol Engine release",
    "reject Protocol Engine release outside exact signed path",
    "reject same Protocol Engine version with different SHA",
    "reject Protocol Engine for another parent private runtime",
    "reject Protocol Engine for another active Capture",
    "activate Protocol Engine against exact Capture parent and Bundle",
    "build exact Protocol override process plan",
]:
    assert token in tests, f"missing Protocol regression test: {token}"

for source in [updater, api, runtime]:
    assert "Process.Start(" not in source, "6-6 must not cut over process launch"

for forbidden in [
    "SyncModuleUpdater",
    "CombatUpdateAuthorization",
    "SyncUpdateAuthorization",
]:
    assert forbidden not in updater + api, f"6-6 crossed a later individual module boundary: {forbidden}"

for forbidden in ["ModuleActivePrivateRuntimeFile", "ModuleActiveBundleFile", "ModuleActiveCaptureFile"]:
    assert forbidden not in updater, f"Protocol updater must not overwrite parent state: {forbidden}"

print("PROTOCOL_INDIVIDUAL_UPDATE_BOUNDARY_OK authority=server module=protocol parents=private-runtime+capture bundle=exact active-state=separate process-cutover=false")
