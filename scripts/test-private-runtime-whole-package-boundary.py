from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "PrivateRuntimePackageUpdater.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
verifier = (root / "launcher" / "ModulePackageVerifier.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

for token in [
    'ModuleId = "private-runtime"',
    'ModulePackageDownloadCache',
    'ModulePackageVerifier.Verify',
    'ModuleStagingInstaller.Stage',
    'ModuleStagingSelfTest.RunForTest',
    'VerifyRuntimeLock',
    'RequireReleaseBundle',
    'RuntimeModuleSetHash',
    '{ "runtimeBundleRevision", state.RuntimeBundleRevision }',
    '{ "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 }',
    '{ "runtimeModuleSetHash", state.RuntimeModuleSetHash }',
    'PrivateRuntimeProcessPlanBuilder',
    'KINOJO.Meter.EngineHost.exe',
]:
    assert token in updater, f"missing private runtime boundary: {token}"

for token in ["PrivateRuntimeReleaseManifest", "ActivePrivateRuntimeState", "PrivateRuntimeUpdateAuthorization"]:
    assert token in models, f"missing model: {token}"

for token in ["privateRuntimeUpdateAuthorization", "currentPrivateRuntime", 'Dict(value, "privateRuntime")']:
    assert token in api, f"missing API boundary: {token}"

for token in ["ModuleActivePrivateRuntimeFile", "ModulePrivateRuntimeUpdateLockFile"]:
    assert token in paths, f"missing deterministic path: {token}"

for token in ['"private-runtime"', '"KINOJO.Meter.EngineHost.exe"', '"ENGINE_HOST_PROCESS"']:
    assert token in verifier, f"missing package verifier topology: {token}"

for source in [form, handoff]:
    assert "AuthorizePrivateRuntimeUpdateAsync" in source
    assert "PrivateRuntimeUpdateCoordinator.ApplyAsync" in source

assert '<Compile Include="PrivateRuntimePackageUpdater.cs" />' in project

for source in [updater, api]:
    assert "Process.Start(" not in source, "6-4 updater must not launch an unseeded operating package"

for forbidden in ["CaptureUpdateAuthorization", "ProtocolUpdateAuthorization", "CombatUpdateAuthorization", "SyncUpdateAuthorization"]:
    assert forbidden not in updater + api, f"6-4 crossed a later individual module boundary: {forbidden}"

print("PRIVATE_RUNTIME_WHOLE_PACKAGE_BOUNDARY_OK authority=server module=private-runtime bundle=exact staged=true operating-pointer=unseeded process-plan=exact")
