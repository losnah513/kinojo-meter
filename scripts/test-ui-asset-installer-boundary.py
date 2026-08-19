#!/usr/bin/env python3
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def fail(message):
    print("UI_ASSET_INSTALLER_FAIL:", message)
    return False


def main():
    ok = True
    paths = read("launcher/LauncherPaths.cs")
    models = read("launcher/LauncherModels.cs")
    installer = read("launcher/UiAssetPackInstaller.cs")
    validator = read("launcher/UiAssetPackageValidator.cs")
    state = read("launcher/UiAssetPackState.cs")
    installer_all = installer + validator + state
    verifier = read("launcher/UiAssetReleaseIntegrityVerifier.cs")
    launcher_project = read("launcher/KINOJO.Meter.Launcher.csproj")
    test_project = read("tests/KINOJO.Meter.Launcher.Tests/KINOJO.Meter.Launcher.Tests.csproj")

    for marker in ("UiAssetRoot", "UiAssetVersions", "UiAssetStaging", "ActiveUiAssetFile", "UiAssetVersionDirectory"):
        if marker not in paths:
            ok = fail("Launcher path contract missing: " + marker) and ok
    for marker in ("UiAssetReleaseManifest", "UiAssetInstallManifest", "ActiveUiAssetState", "UiAssetInstallResult"):
        if marker not in models:
            ok = fail("Launcher model missing: " + marker) and ok
    for marker in ("KINOJO_UI_ASSET_RELEASE_V1", "RSA_SHA256_MANIFEST_V1", "CoreSigningPublicModulusBase64", "CoreSigningKeyId"):
        if marker not in verifier:
            ok = fail("UI Asset signature contract missing: " + marker) and ok
    for marker in ("InstallPackage", "ReadVerifiedActiveState", "Rollback", "InstallManifestSha256", "ThemeSha256", "EMBEDDED_CORE", "폐기된 Area4", "UiAssetVersionDirectory", "ExpectedManagedPathsFromTheme", "SetEquals"):
        if marker not in installer_all:
            ok = fail("UI Asset installer contract missing: " + marker) and ok
    if "EndSWith" in installer_all:
        ok = fail("invalid EndsWith call spelling detected") and ok
    if 'IndexOf("area4"' not in validator or "UI Asset ZIP" not in validator:
        ok = fail("Area4 file/directory ZIP rejection contract missing") and ok
    if "HttpClient" in installer_all or "DownloadAsync" in installer_all:
        ok = fail("Stage 3-2 must not pull Stage 5/6 remote acquisition forward") and ok
    if "CorePackageInstaller.cs" not in launcher_project:
        ok = fail("existing Core installer was unexpectedly removed") and ok
    for name in ("UiAssetPackInstaller.cs", "UiAssetPackageValidator.cs", "UiAssetPackState.cs", "UiAssetReleaseIntegrityVerifier.cs"):
        if name not in launcher_project or name not in test_project:
            ok = fail("new installer source not compiled in launcher/test project: " + name) and ok
    if "UiAssetPackInstaller" not in installer_all or "UiAssetReleaseIntegrityVerifier.PackId" not in installer_all:
        ok = fail("separate Asset Pack identity missing") and ok

    if ok:
        print("UI_ASSET_INSTALLER_OK local-install=true signature=RSA3072 sha=true rollback=true remote-acquisition=deferred area4=forbidden")
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
