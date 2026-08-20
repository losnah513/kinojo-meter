from pathlib import Path

root = Path(__file__).resolve().parents[1]
source = (root / "launcher" / "ModuleBundleRollback.cs").read_text(encoding="utf-8")
paths = (root / "launcher" / "LauncherPaths.cs").read_text(encoding="utf-8")
project = (root / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")

required = [
    "ActivateAndVerify",
    "RollbackCurrentToPrevious",
    "previous-bundle.json",
    "rollback-plan.json",
    "last-rollback.json",
    "READINESS_FAILURE",
    "ROLLED_BACK",
    "ROLLBACK_UNAVAILABLE",
    "ValidateRollbackTarget",
    "SelfTestReceiptSha256",
    "ManifestSha256",
    "ReleasePointerChanged = false",
    "File.Replace",
]
for token in required:
    if token not in source and token not in paths:
        raise SystemExit(f"missing Stage 5-8 rollback boundary token: {token}")

for token in ["Process.Start", "HttpClient", "BeginNewAttempt", "ActiveCoreFile", "ActiveUiAssetFile"]:
    if token in source:
        raise SystemExit(f"Stage 5-8 rollback boundary must not own runtime/download/reset/legacy pointer work: {token}")

for token in ["ModuleRollback", "ModulePreviousBundleFile", "ModuleRollbackPlanFile", "ModuleRollbackReceiptFile"]:
    if token not in paths:
        raise SystemExit(f"LauncherPaths missing rollback path: {token}")

if '<Compile Include="ModuleBundleRollback.cs" />' not in project:
    raise SystemExit("Launcher project does not compile ModuleBundleRollback.cs")

if ".active.json" in source or "active-" + "module" in source:
    raise SystemExit("Stage 5-8 must not create per-module active pointers")

print("Stage 5-8 module Bundle rollback boundary checks passed")
