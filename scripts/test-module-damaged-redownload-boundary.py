from pathlib import Path

root = Path(__file__).resolve().parents[1]
repair = root / "launcher" / "ModuleDamagedPackageRepair.cs"
launcher_project = root / "launcher" / "KINOJO.Meter.Launcher.csproj"
test_project = root / "tests" / "KINOJO.Meter.ModuleDamagedPackageRepairTests" / "KINOJO.Meter.ModuleDamagedPackageRepairTests.csproj"
test_program = root / "tests" / "KINOJO.Meter.ModuleDamagedPackageRepairTests" / "Program.cs"

for path in (repair, launcher_project, test_project, test_program):
    if not path.exists():
        raise SystemExit(f"missing Stage 5-9 file: {path}")

text = repair.read_text(encoding="utf-8")
required = [
    'RepairedStatus = "REPAIRED"',
    'ActiveModuleRepairBlockedCode = "ACTIVE_MODULE_REPAIR_REQUIRES_ROLLBACK"',
    'MODULE_REPAIR_PURGED',
    'MODULE_REPAIR_VERIFIED',
    'MODULE_REPAIR_STAGED',
    'MODULE_REPAIR_COMPLETED',
    'ModulePackageVerifier.Verify(',
    'ModuleStagingInstaller.Stage(',
    'ModuleStagingSelfTest.Run(',
    'cache.CacheDirectoryForTest(download)',
    'DownloadedFresh = true',
    'ActiveBundleChanged = false',
    'ReleasePointerChanged = false',
]
for needle in required:
    if needle not in text:
        raise SystemExit(f"missing Stage 5-9 boundary: {needle}")

for forbidden in (
    "Process.Start",
    "Process.Kill",
    "ActiveCoreFile",
    "ActiveUiAssetFile",
    "ModuleBundleRollback.Rollback",
    "meter_core_release_master",
    "meter_launcher_release_master",
    "storage.objects",
):
    if forbidden in text:
        raise SystemExit(f"Stage 5-9 crossed deferred boundary: {forbidden}")

project_text = launcher_project.read_text(encoding="utf-8")
if '<Compile Include="ModuleDamagedPackageRepair.cs" />' not in project_text:
    raise SystemExit("Launcher project does not compile ModuleDamagedPackageRepair.cs")

test_text = test_program.read_text(encoding="utf-8")
for scenario in (
    "same-length damaged cache is purged and downloaded fresh",
    "damaged staging and stale self-test are replaced",
    "active exact module requires Stage 5-8 rollback first",
    "unrelated active bundle pointer stays byte-identical",
    "different SHA sibling slot is never deleted",
    "bad redownload fails closed without partial repaired slot",
    "repair receipt stays inactive and never changes release pointer",
):
    if scenario not in test_text:
        raise SystemExit(f"missing Stage 5-9 test scenario: {scenario}")

print("MODULE_DAMAGED_REDOWNLOAD_BOUNDARY_OK exact-target=fresh-download verify=5-4 stage=5-5 self-test=5-6 active=unchanged release-pointer=unchanged rollback=5-8-prerequisite")
