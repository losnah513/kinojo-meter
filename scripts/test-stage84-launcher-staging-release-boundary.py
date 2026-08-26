#!/usr/bin/env python3
"""Fail closed when the Stage 8-4 Launcher publication boundary drifts."""

from __future__ import annotations

import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def version_tuple(value: str) -> tuple[int, ...]:
    if not re.fullmatch(r"\d+(?:\.\d+)+", value):
        raise AssertionError(f"invalid numeric version: {value}")
    return tuple(int(part) for part in value.split("."))


stable = json.loads((ROOT / "release" / "launcher-version.json").read_text(encoding="utf-8"))
staging = json.loads((ROOT / "release" / "launcher-staging-version.json").read_text(encoding="utf-8"))
publisher = (ROOT / "scripts" / "publish-launcher-release.ps1").read_text(encoding="utf-8")
workflow = (ROOT / ".github" / "workflows" / "launcher-build.yml").read_text(encoding="utf-8")

assert stable["version"] == "1.1.5"
assert stable["artifactName"] == "KINOJO_Meter_Launcher_1.1.5.exe"
assert stable["channel"] == "stable"
assert stable["cutoverState"] == "ACTIVE"
assert stable["publicDistribution"] is True

assert staging["version"] == "1.1.6"
assert staging["fileVersion"] == "1.1.6.0"
assert staging["artifactName"] == "KINOJO_Meter_Launcher_Staging_1.1.6.exe"
assert staging["channel"] == "staging"
assert staging["cutoverState"] == "STAGING_E2E"
assert staging["publicDistribution"] is False
assert version_tuple(staging["version"]) > version_tuple(stable["version"])
assert version_tuple(staging["version"]) >= version_tuple("1.1.3")  # B000050 minimum Launcher

required_publisher_fragments = (
    "repos/$repository/immutable-releases",
    "X-GitHub-Api-Version: 2026-03-10",
    "if ($immutableSettings.enabled -ne $true)",
    "$artifactPath, $checksumPath",
    "--draft",
    "Assert-RemoteAssets $repository $tag",
    "gh release edit $tag --repo $repository --draft=false",
    "Assert-ReleaseContract $release $false $true",
)
for fragment in required_publisher_fragments:
    assert fragment in publisher, f"missing immutable publisher contract: {fragment}"

assert "gh release upload" not in publisher
assert publisher.index("Assert-RemoteAssets $repository $tag") < publisher.index(
    "gh release edit $tag --repo $repository --draft=false"
)
assert publisher.rindex("Assert-RemoteAssets $repository $tag") > publisher.index(
    "Assert-ReleaseContract $release $false $true"
)

assert "PUBLISH_STAGING_LAUNCHER_${version}" in workflow
assert "environment='meter-launcher-staging'" in workflow
assert "id-token: write" in workflow
assert "./scripts/verify-distribution-boundary.ps1" in workflow
assert "./scripts/test-stage84-launcher-staging-release-boundary.py" in workflow
assert "./scripts/sync-launcher-release.ps1" in workflow

print("Stage 8-4 immutable STAGING Launcher publication boundary verified.")
