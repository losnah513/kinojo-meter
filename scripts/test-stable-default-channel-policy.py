#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW_ROOT = ROOT / ".github/workflows"

MATRIX_WORKFLOWS = (
    "capture-individual-update-boundary.yml",
    "catalog-pack-individual-update-boundary.yml",
    "combat-encounter-compatibility-group-boundary.yml",
    "private-runtime-whole-package-boundary.yml",
    "protocol-individual-update-boundary.yml",
    "shell-module-individual-update-boundary.yml",
    "sync-individual-update-boundary.yml",
)

SPLIT_STEP_WORKFLOWS = (
    "combat-encounter-individual-update-boundary.yml",
    "module-active-bundle-boundary.yml",
    "module-bundle-rollback-boundary.yml",
    "module-damaged-redownload-boundary.yml",
    "module-package-verifier-boundary.yml",
    "module-staging-installer-boundary.yml",
    "module-staging-self-test-boundary.yml",
)

SPLIT_STEP_COUNTS = {
    name: (2 if name == "combat-encounter-individual-update-boundary.yml" else 1)
    for name in SPLIT_STEP_WORKFLOWS
}

STABLE_MATRIX = (
    "channel: ${{ fromJSON(github.event_name == 'workflow_dispatch' "
    "&& inputs.target_channel == 'staging' && '[\"staging\"]' || '[\"stable\"]') }}"
)
STABLE_STEP_CONDITION = (
    "if: github.event_name != 'workflow_dispatch' || inputs.target_channel != 'staging'"
)
STAGING_STEP_CONDITION = (
    "if: github.event_name == 'workflow_dispatch' && inputs.target_channel == 'staging'"
)


def require(condition, code):
    if not condition:
        raise SystemExit("STABLE_DEFAULT_CHANNEL_POLICY_FAIL " + code)


launcher_workflow = (WORKFLOW_ROOT / "launcher-build.yml").read_text(encoding="utf-8")
require("default: stable" in launcher_workflow, "launcher_dispatch_default")
require("options: [stable, staging]" in launcher_workflow, "launcher_dispatch_order")
require(STABLE_MATRIX in launcher_workflow, "launcher_stable_fallback_matrix")

for name in MATRIX_WORKFLOWS:
    workflow = (WORKFLOW_ROOT / name).read_text(encoding="utf-8")
    require("workflow_dispatch:" in workflow, name + ":dispatch")
    require("default: stable" in workflow, name + ":dispatch_default")
    require("options: [stable, staging]" in workflow, name + ":dispatch_order")
    require(STABLE_MATRIX in workflow, name + ":stable_fallback_matrix")

for name in SPLIT_STEP_WORKFLOWS:
    workflow = (WORKFLOW_ROOT / name).read_text(encoding="utf-8")
    require("workflow_dispatch:" in workflow, name + ":dispatch")
    require("default: stable" in workflow, name + ":dispatch_default")
    require("options: [stable, staging]" in workflow, name + ":dispatch_order")
    require("/p:LauncherChannel=staging" in workflow, name + ":staging_build")
    expected_count = SPLIT_STEP_COUNTS[name]
    require(
        workflow.count(STABLE_STEP_CONDITION) == expected_count,
        name + ":stable_step_condition",
    )
    require(
        workflow.count(STAGING_STEP_CONDITION) == workflow.count("/p:LauncherChannel=staging")
        == expected_count,
        name + ":staging_step_condition",
    )

all_workflows = "\n".join(
    path.read_text(encoding="utf-8") for path in WORKFLOW_ROOT.glob("*.yml")
)
require("channel: [stable, staging]" not in all_workflows, "implicit_dual_build")

print(
    "STABLE_DEFAULT_CHANNEL_POLICY_OK default=stable staging=explicit-only "
    f"matrix_workflows={len(MATRIX_WORKFLOWS)} split_step_workflows={len(SPLIT_STEP_WORKFLOWS)}"
)
