from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "ShellModuleUpdater.cs").read_text(encoding="utf-8")
download = (root / "launcher" / "ModulePackageDownloadCache.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    'VersionShaConflictCode = "SHELL_VERSION_SHA_CONFLICT"',
    'RuntimeBundleRequiredCode = "SHELL_RUNTIME_BUNDLE_REQUIRED"',
    "ModulePackageDownloadCache",
    "ModulePackageVerifier.Verify",
    "ModuleStagingInstaller.Stage",
    "ModuleStagingSelfTest.RunForTest",
    "ModuleBundleActivator.ReadVerifiedActiveBundle",
    "ModuleActiveShellFile",
    "ModuleShellUpdateLockFile",
    "RuntimeBundleRevision",
    "RuntimeBundleLockSha256",
    "WriteActiveState",
    "ShellModuleUpdateCoordinator",
]:
    assert token in updater, f"missing Shell updater boundary: {token}"

for token in [
    "ExpectedDownloadHost",
    "ExpectedDownloadPath",
    "ExpectedFileSize",
    "ValidateApprovedDownloadUri",
    'Uri.UnescapeDataString(parts[0]), "token"',
]:
    assert token in download, f"missing strict module download boundary: {token}"

for token in [
    "ShellModuleReleaseManifest",
    "ActiveShellModuleState",
    "ShellModuleUpdateAuthorization",
    "PointerGeneration",
]:
    assert token in models, f"missing Shell model: {token}"

for token in [
    "shellUpdateAuthorization",
    "currentShellModule",
    "ParseShellModuleAuthorization",
    'Dict(value, "shellModule")',
]:
    assert token in api, f"missing Shell API boundary: {token}"

for token in ["ModuleActiveShellFile", ".shell-update.lock"]:
    assert token in paths, f"missing deterministic Shell path: {token}"

for source in [form, handoff]:
    assert "AuthorizeShellModuleUpdateAsync" in source
    assert "ShellModuleUpdateCoordinator.ApplyAsync" in source

assert '<Compile Include="ShellModuleUpdater.cs" />' in project

for token in [
    "parse Meter Shell update authorization",
    "accept Server-authorized Meter Shell release",
    "reject Meter Shell signed URL outside exact path",
    "reject same Meter Shell version with different SHA",
    "activate only self-tested Meter Shell against exact runtime Bundle",
]:
    assert token in tests, f"missing Shell regression test: {token}"

for source in [updater, api, form, handoff]:
    for forbidden in [
        "ModuleBundleActivator.Activate(",
        "File.Delete(LauncherPaths.ActiveCoreFile)",
    ]:
        assert forbidden not in source, f"Stage 6-3 crossed runtime/release boundary: {forbidden}"

for source in [updater, api]:
    assert "Process.Start(" not in source, "Stage 6-3 must not cut over process launch"

print("Meter Shell individual update boundary: PASS")
