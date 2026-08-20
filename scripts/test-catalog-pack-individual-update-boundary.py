from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "CatalogPackUpdater.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

for token in [
    'DungeonBossPackId = "dungeon-boss-catalog"',
    'ClassSkillPackId = "class-skill-catalog"',
    'BossHpPackId = "boss-hp-fingerprint"',
    'VersionShaConflictCode = "CATALOG_VERSION_SHA_CONFLICT"',
    'KINOJO_DUNGEON_BOSS_CATALOG_RELEASE_V1',
    'KINOJO_CLASS_SKILL_CATALOG_RELEASE_V1',
    'KINOJO_BOSS_HP_FINGERPRINT_RELEASE_V1',
    'signature.Length != 384',
    'SetEquals(new[] { "catalog.json", "install-manifest.json" })',
    'File.Replace(temporary, path, null)',
    'CatalogPackUpdateCoordinator',
    'foreach (var packId in PackOrder)',
    'storage/v1/object/sign/meter-core-private/catalog-packs/',
]:
    assert token in updater, f"missing 6-1 Catalog Pack boundary token: {token}"

for token in [
    'catalogPackUpdateAuthorization',
    'currentCatalogPacks',
    'ParseCatalogPackAuthorization',
    'DictList(value, "catalogPacks")',
]:
    assert token in api, f"missing 6-1 API authorization token: {token}"

assert 'AuthorizeCatalogPackUpdatesAsync' in form
assert 'CatalogPackUpdateCoordinator.ApplyAsync' in form
assert 'AuthorizeCatalogPackUpdatesAsync' in handoff
assert 'CatalogPackUpdateCoordinator.ApplyAsync' in handoff
assert 'CatalogPackRoot = Path.Combine(Root, "catalog-packs")' in paths
assert '<Compile Include="CatalogPackUpdater.cs" />' in project

for source in [updater, api, form, handoff]:
    for forbidden in [
        "UiAssetPackInstaller.InstallPackage",
        "ModuleBundleActivator.Activate",
        "meter_core_release_master",
        "meter_launcher_release_master",
    ]:
        assert forbidden not in source, f"6-1 crossed deferred/release boundary: {forbidden}"

assert "File.Delete(LauncherPaths.ActiveCoreFile)" not in updater
assert "Directory.Delete(LauncherPaths.CoreRoot" not in updater
print("Catalog Pack individual update boundary: PASS")
