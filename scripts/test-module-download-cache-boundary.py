#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / "launcher" / "ModulePackageDownloadCache.cs"
PATHS = ROOT / "launcher" / "LauncherPaths.cs"
LAUNCHER_CSPROJ = ROOT / "launcher" / "KINOJO.Meter.Launcher.csproj"
TEST_CSPROJ = ROOT / "tests" / "KINOJO.Meter.ModuleDownloadCacheTests" / "KINOJO.Meter.ModuleDownloadCacheTests.csproj"
TEST_PROGRAM = ROOT / "tests" / "KINOJO.Meter.ModuleDownloadCacheTests" / "Program.cs"


def fail(message):
    print("MODULE_DOWNLOAD_CACHE_BOUNDARY_FAIL " + message)
    raise SystemExit(1)


def read(path):
    if not path.is_file():
        fail("missing=" + str(path.relative_to(ROOT)))
    return path.read_text(encoding="utf-8")


cache = read(CACHE)
paths = read(PATHS)
launcher_project = read(LAUNCHER_CSPROJ)
test_project = read(TEST_CSPROJ)
test_program = read(TEST_PROGRAM)

required_cache_tokens = (
    'MaximumPackageBytes = 64L * 1024L * 1024L',
    'UnverifiedStatus = "UNVERIFIED"',
    'HttpCompletionOption.ResponseHeadersRead',
    'Path.Combine(_cacheRoot, ".incoming")',
    'Directory.Move(stagingDirectory, finalDirectory)',
    'RequiresVerification = true',
    'VerificationStatus = UnverifiedStatus',
    'expectedPrefix = "modules/" + request.ModuleId + "/" + request.ModuleVersion + "/"',
    'MODULE_CACHE_HIT_UNVERIFIED',
    'MODULE_CACHE_COMMITTED_UNVERIFIED',
)
for token in required_cache_tokens:
    if token not in cache:
        fail("cache_token=" + token)

for forbidden in (
    "SHA256.Create",
    "ComputeHash(",
    "ZipFile.ExtractToDirectory",
    "ZipArchive",
    "ActiveBundle",
    "active-bundle",
    "Process.Start",
):
    if forbidden in cache:
        fail("5-4_or_later_behavior=" + forbidden)

if 'public static readonly string ModuleRoot = Path.Combine(Root, "modules")' not in paths:
    fail("module_root")
if 'public static readonly string ModulePackageCache = Path.Combine(ModuleRoot, "cache")' not in paths:
    fail("module_cache_root")
if "Directory.CreateDirectory(ModulePackageCache);" not in paths:
    fail("module_cache_directory")

if '<Compile Include="ModulePackageDownloadCache.cs" />' not in launcher_project:
    fail("launcher_compile")
if 'ModulePackageDownloadCache.cs' not in test_project or 'LauncherPaths.cs' not in test_project:
    fail("test_compile_links")

for expected_test in (
    "download into unverified quarantine cache",
    "reuse exact module/version/SHA cache candidate",
    "different expected SHA uses a different cache slot",
    "reject module/version packagePath mismatch",
    "reject packagePath traversal",
    "reject non-HTTPS package URL",
    "reject announced oversized package",
):
    if expected_test not in test_program:
        fail("self_test=" + expected_test)

if 'metadata.IndexOf("https://"' not in test_program:
    fail("signed_url_not_persisted_test")
if 'second.RequiresVerification' not in test_program:
    fail("cache_hit_verification_gate")

print(
    "MODULE_DOWNLOAD_CACHE_BOUNDARY_OK cache=module/version/sha quarantine=UNVERIFIED "
    "cache-hit=exact-sha download=https-only verification=deferred-to-5-4 activation=false next=5-4"
)
