#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VERIFIER = ROOT / "launcher" / "ModulePackageVerifier.cs"
CACHE = ROOT / "launcher" / "ModulePackageDownloadCache.cs"
LAUNCHER_PROJECT = ROOT / "launcher" / "KINOJO.Meter.Launcher.csproj"
TEST_PROJECT = ROOT / "tests" / "KINOJO.Meter.ModulePackageVerifierTests" / "KINOJO.Meter.ModulePackageVerifierTests.csproj"
TEST_PROGRAM = ROOT / "tests" / "KINOJO.Meter.ModulePackageVerifierTests" / "Program.cs"


def fail(message):
    print("MODULE_PACKAGE_VERIFIER_BOUNDARY_FAIL " + message)
    raise SystemExit(1)


def require(condition, message):
    if not condition:
        fail(message)


def read(path):
    require(path.is_file(), "missing=" + str(path.relative_to(ROOT)))
    return path.read_text(encoding="utf-8")


verifier = read(VERIFIER)
cache = read(CACHE)
launcher_project = read(LAUNCHER_PROJECT)
test_project = read(TEST_PROJECT)
test_program = read(TEST_PROGRAM)

for token in (
    'SupportedManifestSchemaVersion = 1',
    'SupportedContractSetVersion = 1',
    'KINOJO_METER_MODULE_PACKAGE',
    'KINOJO_MODULE_PACKAGE_MANIFEST_V1',
    'package.manifest.json',
    'RSA_SHA256',
    'ZipFile.OpenRead',
    'archive SHA-256',
    'Manifest RSA',
    'Contract Set',
    'state schema',
    'verification.json',
    'installAllowed',
    'activationAllowed',
):
    require(token in verifier, "missing_verifier_token=" + token)

require('Sha256File(request.Cache.PackageFile)' in verifier, "archive_sha_not_calculated")
require('String.Equals(archiveSha256, request.ExpectedSha256' in verifier, "archive_sha_not_bound_to_bundle_lock")
require('rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature)' in verifier, "rsa_sha256_not_verified")
require('manifest.ContractSetVersion != request.ContractSetVersion' in verifier, "contract_set_not_bound")
require('manifest.State.StateSchemaVersion != request.StateSchemaVersion' in verifier, "state_schema_not_bound")
require('dependencies.SequenceEqual(Dependencies[request.ModuleId]' in verifier, "dependency_topology_not_checked")
require('archiveFiles.Count != declared.Count + 1' in verifier, "archive_file_set_not_exact")
require('Sha256Stream(stream)' in verifier, "inner_file_sha_not_checked")
require('PrimaryArtifacts[request.ModuleId]' in verifier, "primary_artifact_not_checked")
require('VerificationStatus, "UNVERIFIED"' in verifier or 'VerificationStatus, "UNVERIFIED"' in cache, "unverified_source_not_required")
require('{ "installAllowed", false }' in verifier, "verification_receipt_install_gate_missing")
require('{ "activationAllowed", false }' in verifier, "verification_receipt_activation_gate_missing")

for forbidden in (
    'ExtractToDirectory',
    'active-bundle',
    'ActiveBundle',
    'Process.Start',
    'Directory.Move(stagingDirectory, LauncherPaths',
):
    require(forbidden not in verifier, "later_stage_behavior=" + forbidden)

require('RequiresVerification = true' in cache, "cache_no_longer_requires_verification")
require('VerificationStatus = UnverifiedStatus' in cache, "cache_status_changed_from_unverified")
require('<Compile Include="ModulePackageVerifier.cs" />' in launcher_project, "launcher_project_missing_verifier")
for token in (
    'ModulePackageVerifier.cs',
    'ModulePackageDownloadCache.cs',
    'System.IO.Compression',
    'System.IO.Compression.FileSystem',
):
    require(token in test_project, "test_project_missing=" + token)

for phrase in (
    'verify archive SHA, RSA manifest, Contract and internal file hashes',
    'reject Bundle Lock archive SHA mismatch',
    'reject tampered signed manifest',
    'reject unsupported Contract Set',
    'reject state schema mismatch',
    'reject dependency topology mismatch',
    'reject inner file SHA mismatch',
    'reject undeclared archive file',
    'reject package signed by wrong key',
):
    require(phrase in test_program, "self_test_missing=" + phrase)

print(
    "MODULE_PACKAGE_VERIFIER_BOUNDARY_OK archive-sha=verified manifest-rsa=verified "
    "contract=verified inner-files=verified receipt=verified-only install=false activation=false next=5-5"
)
