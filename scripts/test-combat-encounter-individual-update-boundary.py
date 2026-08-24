from pathlib import Path

root = Path(__file__).resolve().parents[1]
updater = (root / "launcher" / "CombatEncounterIndividualModuleUpdater.cs").read_text(encoding="utf-8")
group = (root / "launcher" / "CombatEncounterCompatibilityGroupUpdater.cs").read_text(encoding="utf-8")
api = (root / "launcher" / "LauncherApiClient.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
form = (root / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
handoff = (root / "launcher" / "CoreUpdateHandoffMode.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
test_project = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "KINOJO.Meter.Launcher.Tests.csproj").read_text(encoding="utf-8")
tests = (root / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

for token in [
    "CombatEncounterIndividualModuleUpdater",
    "CombatEncounterIndividualModuleAuthorization",
    "ActiveCombatEncounterIndividualModuleState",
    "CompatibilityGroupId",
    "CounterpartModuleId",
    "ModulePackageDownloadCache",
    "ModulePackageVerifier.Verify",
    "ModuleStagingInstaller.Stage",
    "ModuleStagingSelfTest.RunForTest",
    "RequireTransition",
    "MultipleModuleChangeCode",
    "WriteJson(_activeFile",
    "WriteJson(_groupActiveFile",
    'new CombatEncounterIndividualModuleUpdater("combat")',
    'new CombatEncounterIndividualModuleUpdater("encounter")',
]:
    assert token in updater + form + handoff, f"missing Stage 6-9 individual boundary: {token}"

assert updater.index("ExclusiveFile(_activationLock)") < updater.index("ExclusiveFile(_privateRuntimeLock)")
assert updater.index("ExclusiveFile(_privateRuntimeLock)") < updater.index("ExclusiveFile(_captureLock)")
assert updater.index("ExclusiveFile(_captureLock)") < updater.index("ExclusiveFile(_protocolLock)")
assert updater.index("ExclusiveFile(_protocolLock)") < updater.index("ExclusiveFile(_syncLock)")
assert updater.index("ExclusiveFile(_syncLock)") < updater.index("ExclusiveFile(_groupLock)")
assert updater.index("ExclusiveFile(_groupLock)") < updater.index("ExclusiveFile(_combatLock)")
assert updater.index("ExclusiveFile(_combatLock)") < updater.index("ExclusiveFile(_encounterLock)")

for token in [
    "combatEncounterIndividualUpdateAuthorization",
    "currentModule",
    "currentCounterpart",
    "currentCombatEncounterGroup",
    "ParseCombatEncounterIndividualModuleAuthorization",
    'Dict(value, "combatEncounterIndividualUpdate")',
    'Dict(update, "compatibilityGroup")',
]:
    assert token in api, f"missing Stage 6-9 API boundary: {token}"

for token in [
    "active-combat.json", "active-encounter.json",
    ".combat-update.lock", ".encounter-update.lock",
]:
    assert token in paths, f"missing Stage 6-9 deterministic path: {token}"

for source in [form, handoff]:
    assert source.index("AuthorizeCombatEncounterCompatibilityGroupUpdateAsync") < source.index("AuthorizeCombatEncounterIndividualModuleUpdateAsync")
    assert source.index('AuthorizeCombatEncounterIndividualModuleUpdateAsync(\n                        "combat"') < source.index('AuthorizeCombatEncounterIndividualModuleUpdateAsync(\n                        "encounter"')
    assert source.index('AuthorizeCombatEncounterIndividualModuleUpdateAsync(\n                        "encounter"') < source.index("AuthorizeSyncModuleUpdateAsync")

assert '<Compile Include="CombatEncounterIndividualModuleUpdater.cs" />' in project
assert "CombatEncounterIndividualModuleUpdater.cs" in test_project

for token in [
    "parse Combat Encounter individual module authorization",
    "accept Server-authorized Combat individual update",
    "accept Server-authorized Encounter individual update",
    "reject Combat individual transition that also changes Encounter",
    "activate only Combat and preserve Encounter compatibility",
    "activate only Encounter and preserve Combat compatibility",
]:
    assert token in tests, f"missing Stage 6-9 launcher regression: {token}"

for source in [updater, api]:
    assert "Process.Start(" not in source, "Stage 6-9 must not cut over the operating process"

for forbidden in [
    "ModuleActiveBundleFile", "ModuleActivePrivateRuntimeFile",
    "ModuleActiveCaptureFile", "ModuleActiveProtocolFile", "ModuleActiveSyncFile",
]:
    assert forbidden not in updater, f"individual updater must not overwrite parent/Sync state: {forbidden}"

assert "CombatPointerGeneration" in group and "EncounterPointerGeneration" in group
print("COMBAT_ENCOUNTER_INDIVIDUAL_UPDATE_BOUNDARY_OK modules=combat+encounter active-pointers=2 compatibility-witness=1 one-at-a-time=true process-cutover=false")
