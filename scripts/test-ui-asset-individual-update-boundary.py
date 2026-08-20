from pathlib import Path

root = Path(__file__).resolve().parents[1]
installer = (root / "launcher" / "UiAssetPackInstaller.cs").read_text(encoding="utf-8")
state = (root / "launcher" / "UiAssetPackState.cs").read_text(encoding="utf-8")
validator = (root / "launcher" / "UiAssetPackageValidator.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
models = (root / "launcher" / "LauncherModels.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    'VersionShaConflictCode = "UI_ASSET_VERSION_SHA_CONFLICT"',
    "EnsureInstalledAsync",
    "RequireApprovedDownloadUri",
    'storage/v1/object/sign/meter-core-private/ui-assets/',
    'String.Equals(Uri.UnescapeDataString(parts[0]), "token"',
    "RejectVersionShaConflict",
    "SameRelease",
    "Downloaded = false",
    "UiAssetPackUpdateCoordinator",
]:
    assert token in installer, f"missing 6-2 UI Asset updater token: {token}"

for token in [
    "UiAssetReleaseIntegrityVerifier.VerifyForTest(release, _publicKey, _expectedKeyId)",
    "_activeFile",
]:
    assert token in state + validator, f"missing injected UI Asset trust/path token: {token}"

for token in [
    "UiAssetPackUpdateAuthorization",
    "DownloadUrl",
    "ExpiresAt",
    "Downloaded",
]:
    assert token in models, f"missing UI Asset model token: {token}"

for token in [
    "uiAssetPackUpdateAuthorization",
    "currentUiAssetPack",
    "ParseUiAssetPackAuthorization",
    'Dict(value, "uiAssetPack")',
]:
    assert token in api, f"missing 6-2 API authorization token: {token}"

for source in [form, handoff]:
    assert "AuthorizeUiAssetPackUpdateAsync" in source
    assert "UiAssetPackUpdateCoordinator.ApplyAsync" in source

for token in [
    "download and activate UI Asset Pack independently",
    "revalidate UI Asset Pack without redownload",
    "reject same UI Asset version with different SHA",
    "handler.RequestCount == 0",
    "before.SequenceEqual(File.ReadAllBytes(pointer))",
]:
    assert token in tests, f"missing 6-2 regression test token: {token}"

for source in [installer, api, form, handoff]:
    for forbidden in [
        "ModuleBundleActivator.Activate",
        "File.Delete(LauncherPaths.ActiveCoreFile)",
        "meter_core_release_master",
        "meter_launcher_release_master",
    ]:
        assert forbidden not in source, f"6-2 crossed release/bundle boundary: {forbidden}"

print("UI Asset Pack individual update boundary: PASS")
