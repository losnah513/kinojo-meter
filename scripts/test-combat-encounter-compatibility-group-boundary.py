from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "CombatEncounterCompatibilityGroupUpdater.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    "CombatEncounterCompatibilityGroupUpdater",
    'ModuleId != "combat"',
    'ModuleId != "encounter"',
    "CompatibilityGroupId",
    "CompatibilityGroupId(release)",
    "ModulePackageDownloadCache",
    "ModulePackageVerifier.Verify",
    "ModuleStagingInstaller.Stage",
    "ModuleStagingSelfTest.RunForTest",
    "CombatDependencies(bundle, protocol)",
    "EncounterDependencies(bundle)",
    "ParentPrivateRuntimePointerGeneration",
    "ParentCapturePointerGeneration",
    "ParentProtocolPointerGeneration",
    "RuntimeBundleRevision",
    "RuntimeBundleLockSha256",
    "RuntimeModuleSetHash",
    "ModuleSyncUpdateLockFile",
    "ModuleCombatEncounterUpdateLockFile",
    "WriteActiveState(active)",
]:
    assert token in updater, f"missing Combat·Encounter group boundary: {token}"

assert updater.index("ExclusiveFile(_activationLock)") < updater.index("ExclusiveFile(_privateRuntimeLock)")
assert updater.index("ExclusiveFile(_privateRuntimeLock)") < updater.index("ExclusiveFile(_captureLock)")
assert updater.index("ExclusiveFile(_captureLock)") < updater.index("ExclusiveFile(_protocolLock)")
assert updater.index("ExclusiveFile(_protocolLock)") < updater.index("ExclusiveFile(_syncLock)")
assert updater.index("ExclusiveFile(_syncLock)") < updater.index("ExclusiveFile(_groupLock)")

for token in [
    "combatEncounterCompatibilityGroupAuthorization",
    "currentCombatEncounterGroup",
    "currentProtocolModule",
    "currentCaptureModule",
    "currentPrivateRuntime",
    "ParseCombatEncounterCompatibilityGroupAuthorization",
    'Dict(value, "combatEncounterGroup")',
    'Dict(release, "combatModule")',
    'Dict(release, "encounterModule")',
]:
    assert token in api, f"missing Combat·Encounter API boundary: {token}"

for token in ["active-combat-encounter.json", ".combat-encounter-update.lock"]:
    assert token in paths, f"missing deterministic group state path: {token}"

for source in [form, handoff]:
    assert "AuthorizeCombatEncounterCompatibilityGroupUpdateAsync" in source
    assert "CombatEncounterCompatibilityGroupUpdateCoordinator.ApplyAsync" in source
    assert source.index("AuthorizeProtocolModuleUpdateAsync") < source.index("AuthorizeCombatEncounterCompatibilityGroupUpdateAsync")
    assert source.index("AuthorizeCombatEncounterCompatibilityGroupUpdateAsync") < source.index("AuthorizeSyncModuleUpdateAsync")

assert '<Compile Include="CombatEncounterCompatibilityGroupUpdater.cs" />' in project

for token in [
    "parse Combat Encounter compatibility group authorization",
    "accept Server-authorized Combat Encounter compatibility group",
    "reject Combat Encounter package outside exact signed path",
    "reject mismatched Combat Encounter compatibility identity",
    "reject same Combat version with different SHA in compatibility group",
    "reject Combat Encounter group for another active Protocol",
    "activate Combat and Encounter through one compatibility pointer",
]:
    assert token in tests, f"missing Combat·Encounter regression: {token}"

for source in [updater, api]:
    assert "Process.Start(" not in source, "Stage 6-8 must not cut over the operating process"

for forbidden in ["active-combat.json", "active-encounter.json", "CombatModuleUpdater", "EncounterModuleUpdater"]:
    assert forbidden not in updater, f"Stage 6-8 group updater must keep its bootstrap pair atomic: {forbidden}"

for forbidden in ["ModuleActiveBundleFile", "ModuleActivePrivateRuntimeFile", "ModuleActiveCaptureFile", "ModuleActiveProtocolFile", "ModuleActiveSyncFile"]:
    assert forbidden not in updater, f"group updater must not overwrite parent or Sync state: {forbidden}"

print("COMBAT_ENCOUNTER_COMPATIBILITY_GROUP_BOUNDARY_OK packages=2 active-pointer=1 parents=runtime+capture+protocol bundle=exact process-cutover=false")
