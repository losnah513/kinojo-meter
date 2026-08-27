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
launcher_project = (ROOT / "launcher" / "KINOJO.Meter.Launcher.csproj").read_text(encoding="utf-8")
launcher_form = (ROOT / "launcher" / "LauncherForm.cs").read_text(encoding="utf-8")
runtime_coordinator = (ROOT / "launcher" / "RuntimeLaunchCoordinator.cs").read_text(encoding="utf-8")
launcher_tests = (ROOT / "tests" / "KINOJO.Meter.Launcher.Tests" / "Program.cs").read_text(encoding="utf-8")

assert stable["version"] == "1.1.5"
assert stable["artifactName"] == "KINOJO_Meter_Launcher_1.1.5.exe"
assert stable["channel"] == "stable"
assert stable["cutoverState"] == "ACTIVE"
assert stable["publicDistribution"] is True

assert staging["version"] == "1.1.7"
assert staging["fileVersion"] == "1.1.7.0"
assert staging["artifactName"] == "KINOJO_Meter_Launcher_Staging_1.1.7.exe"
assert staging["channel"] == "staging"
assert staging["cutoverState"] == "STAGING_E2E"
assert staging["publicDistribution"] is False
assert version_tuple(staging["version"]) > version_tuple(stable["version"])
assert version_tuple(staging["version"]) >= version_tuple("1.1.7")  # B000051 minimum Launcher

assert '<Compile Include="RuntimeLaunchCoordinator.cs" />' in launcher_project
assert "RuntimeLaunchCoordinator.TryLaunchAsync(" in launcher_form
assert 'Status = "PUBLIC_RUNTIME_COORDINATOR_JOINT_CUTOVER_VERIFIED"' in runtime_coordinator
assert "JointCutoverEvidenceComplete = true" in runtime_coordinator
assert "LegacyCoreFallbackWithActiveBundle = false" in runtime_coordinator
for regression in (
    "build paired split Runtime Launcher sessions",
    "reject split Runtime plan for another Bundle",
    "handle Shell exit 20 as Launcher update takeover",
    "handle Shell exit 21 as character reconnect takeover",
):
    assert regression in launcher_tests, f"missing joint Runtime regression: {regression}"

required_publisher_fragments = (
    "$artifactPath, $checksumPath",
    "--draft",
    "isDraft,isPrerelease,isImmutable,assets",
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

print("Stage 8-5 fresh-runtime STAGING Launcher 1.1.7 publication boundary verified.")
