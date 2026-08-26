#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
workflow = (ROOT / ".github/workflows/launcher-build.yml").read_text(encoding="utf-8")


def require(condition, code):
    if not condition:
        raise SystemExit("STABLE_DEFAULT_CHANNEL_POLICY_FAIL " + code)


require("default: stable" in workflow, "dispatch_default")
require("options: [stable, staging]" in workflow, "dispatch_order")
require("inputs.target_channel == 'staging'" in workflow, "explicit_staging_selection")
require("'[\"staging\"]' || '[\"stable\"]'" in workflow, "stable_fallback_matrix")
require("channel: [stable, staging]" not in workflow, "implicit_dual_build")

print("STABLE_DEFAULT_CHANNEL_POLICY_OK default=stable staging=explicit-only")
