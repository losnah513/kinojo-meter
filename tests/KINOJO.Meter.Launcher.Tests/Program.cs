using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal static class LauncherVersion
    {
        public const string Channel = LauncherBuildProfile.Channel;
        public const string Current = "1.1.1";

        public static bool IsStaging
        {
            get { return String.Equals(Channel, "staging", StringComparison.Ordinal); }
        }
    }

    internal static class LauncherPackageTests
    {
        private static int _passed;

        [STAThread]
        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "kinojo-launcher-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("channel profile is compile-time bound", VerifyChannelProfile);
                Run("parse Server Meter launch operation", VerifyMeterLaunchOperationParsing);
                Run("parse Catalog Pack update authorization", VerifyCatalogPackAuthorizationParsing);
                Run("parse UI Asset Pack update authorization", VerifyUiAssetPackAuthorizationParsing);
                Run("parse Meter Shell update authorization", VerifyShellModuleAuthorizationParsing);
                Run("parse private runtime update authorization", VerifyPrivateRuntimeAuthorizationParsing);
                Run("parse Capture Engine update authorization", VerifyCaptureAuthorizationParsing);
                Run("parse hidden Core update handoff arguments", VerifyCoreUpdateHandoffArguments);
                Run("keep handoff secrets out of command line", VerifyCoreUpdateHandoffCommandLineBoundary);
                Run("parse redirected Core update handoff envelope", VerifyCoreUpdateHandoffEnvelope);
                Run("reject mismatched Core update request id", VerifyCoreUpdateHandoffRequestMismatch);
                Run("format takeover READY signal", VerifyCoreUpdateHandoffReadySignal);
                Run("compare Core handoff semantic versions", VerifyCoreUpdateHandoffVersionComparison);
                Run("accept six PASS KEY text elements", VerifyPassKeyLength);
                Run("reject incomplete PASS KEY", VerifyIncompletePassKey);
                Run("render compact Launcher UI layout contracts", VerifyLauncherUiLayoutContracts);
                Run("valid package", () => VerifyPackage(root, false, false, false));
                Run("reject unmanaged file", () => ExpectFailure(() => VerifyPackage(root, true, false, false)));
                Run("reject duplicate archive path", () => ExpectFailure(() => VerifyPackage(root, false, true, false)));
                Run("reject tampered install manifest hash", () => ExpectFailure(() => VerifyPackage(root, false, false, true)));
                Run("reject traversal path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("../outside.txt", false)));
                Run("reject Windows ADS path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("KINOJO.Meter.exe:payload", false)));
                Run("reject rooted path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("C:\\Windows\\system32.dll", false)));
                Run("reject reserved device path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("NUL.txt", false)));
                Run("accept vendor-signed WinDivert driver", VerifyBundledDriverSignature);
                Run("reject unsigned executable as vendor driver", () => ExpectFailure(() => AuthenticodeVerifier.Verify(typeof(LauncherPackageTests).Assembly.Location, "")));
                Run("reject tampered vendor driver", () => VerifyTamperedDriverRejected(root));
                Run("accept Launcher content feed", VerifyLauncherContentFeed);
                Run("filter cross-channel Launcher content", VerifyLauncherContentChannelFilter);
                Run("reject Launcher content wrong host", () => ExpectFailure(() => LauncherContentClient.ParseForTest(ContentFeedJson(rows => rows[0]["url"] = "https://example.com/notice"))));
                Run("reject duplicate Launcher content id", () => ExpectFailure(() => LauncherContentClient.ParseForTest(ContentFeedJson(rows => rows.Add(new Dictionary<string, object>(rows[0]))))));
                Run("reject unsupported Launcher content schema", () => ExpectFailure(() => LauncherContentClient.ParseForTest(ContentFeedJson(null, 2))));
                Run("accept Launcher self-update contract", VerifyLauncherUpdateContract);
                Run("parse Launcher self-update manifest", VerifyLauncherUpdateParsing);
                Run("compare Launcher semantic versions", () =>
                {
                    if (LauncherUpdateService.CompareVersionsForTest("1.2.0", "1.1.9") <= 0)
                        throw new InvalidOperationException("Launcher semantic version comparison failed.");
                });
                Run("reject Launcher update wrong host", () => ExpectFailure(() =>
                {
                    var release = LauncherUpdateRelease();
                    release.DownloadUrl = release.DownloadUrl.Replace("github.com", "example.com");
                    LauncherUpdateService.ValidateManifestForTest(release);
                }));
                Run("reject Launcher update wrong channel", () => ExpectFailure(() =>
                {
                    var release = LauncherUpdateRelease();
                    release.Channel = LauncherVersion.Channel == "staging" ? "stable" : "staging";
                    LauncherUpdateService.ValidateManifestForTest(release);
                }));
                Run("reject Launcher update cross-channel tag", () => ExpectFailure(() =>
                {
                    var release = LauncherUpdateRelease();
                    release.DownloadUrl = LauncherVersion.IsStaging
                        ? release.DownloadUrl.Replace("launcher-staging-v", "launcher-v")
                        : release.DownloadUrl.Replace("launcher-v", "launcher-staging-v");
                    LauncherUpdateService.ValidateManifestForTest(release);
                }));
                using (var signingKey = new RSACryptoServiceProvider(3072))
                {
                    signingKey.PersistKeyInCsp = false;
                    Run("accept RSA-signed hobby release", () => VerifyReleaseContract(signingKey, null));
                    Run("reject tampered package hash", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.Sha256 = new String('b', 64))));
                    Run("reject tampered install manifest hash", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.InstallManifestSha256 = new String('c', 64))));
                    Run("reject missing manifest signature", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.ManifestSignature = "")));
                    Run("reject wrong signing key id", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.SigningKeyId = "wrong-key")));
                    Run("reject Authenticode-required hobby release", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.CodeSignatureRequired = true)));
                    Run("reject cross-channel signed URL", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.DownloadUrl = value.DownloadUrl.Replace("/" + LauncherVersion.Channel + "/", "/" + (LauncherVersion.Channel == "staging" ? "stable" : "staging") + "/"))));
                    Run("download and activate UI Asset Pack independently", () => VerifyUiAssetIndividualUpdate(root, signingKey));
                    Run("revalidate UI Asset Pack without redownload", () => VerifyUiAssetIdempotent(root, signingKey));
                    Run("reject same UI Asset version with different SHA", () => VerifyUiAssetVersionShaConflict(root, signingKey));
                    Run("accept Server-authorized Meter Shell release", VerifyShellReleaseContract);
                    Run("reject malformed Meter Shell RSA-3072 signature", VerifyShellReleaseMalformedSignature);
                    Run("reject Meter Shell signed URL outside exact path", VerifyShellReleaseWrongPath);
                    Run("reject same Meter Shell version with different SHA", VerifyShellVersionShaConflict);
                    Run("activate only self-tested Meter Shell against exact runtime Bundle", () => VerifyShellActivationBoundary(root));
                    Run("enforce exact Meter Shell download host and path", VerifyShellDownloadBoundary);
                    Run("accept Server-authorized private runtime release", VerifyPrivateRuntimeReleaseContract);
                    Run("reject private runtime release for another Bundle", () => VerifyPrivateRuntimeBundleBoundary(root));
                    Run("activate private runtime whole package against exact Bundle", () => VerifyPrivateRuntimeActivationBoundary(root));
                    Run("build exact Shell and EngineHost process plan", () => VerifyPrivateRuntimeProcessPlan(root));
                    Run("accept Server-authorized Capture Engine release", VerifyCaptureReleaseContract);
                    Run("reject Capture Engine release outside exact signed path", VerifyCaptureReleaseWrongPath);
                    Run("reject same Capture Engine version with different SHA", VerifyCaptureVersionShaConflict);
                    Run("reject Capture Engine for another parent private runtime", () => VerifyCaptureParentBoundary(root));
                    Run("activate Capture Engine against exact parent and Bundle", () => VerifyCaptureActivationBoundary(root));
                    Run("build exact Capture override process plan", () => VerifyCaptureProcessPlan(root));
                }
                Console.WriteLine("Launcher package tests passed: " + _passed);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { }
            }
        }

        private static void VerifyCatalogPackAuthorizationParsing()
        {
            var response = new Dictionary<string, object>
            {
                { "ok", true }, { "authorized", true },
                { "catalogPacks", new object[] { new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "channel", LauncherVersion.Channel },
                    { "packId", "class-skill-catalog" }, { "catalogVersion", "CLASS_SKILL_CATALOG_20260820_01" },
                    { "minimumLauncherVersion", "1.0.0" }, { "packageId", "fixture" },
                    { "fileName", "fixture.zip" }, { "fileSize", 123L }, { "sha256", new String('a', 64) },
                    { "installManifestSha256", new String('b', 64) }, { "catalogSha256", new String('c', 64) },
                    { "downloadUrl", "https://example.invalid/fixture" }, { "expiresAt", DateTimeOffset.UtcNow.AddMinutes(5).ToString("o") },
                    { "integrityMode", "RSA_SHA256_MANIFEST_V1" }, { "signingKeyId", "fixture" }, { "manifestSignature", "fixture" }
                } } }
            };
            var parsed = LauncherApiClient.ParseCatalogPackAuthorizationForTest(response);
            if (parsed == null || !parsed.Authorized || parsed.Releases == null || parsed.Releases.Count != 1 ||
                parsed.Releases[0].PackId != "class-skill-catalog" || parsed.Releases[0].CatalogSha256 != new String('c', 64))
                throw new InvalidOperationException("Catalog Pack authorization parsing failed.");
            response["authorized"] = false;
            var denied = LauncherApiClient.ParseCatalogPackAuthorizationForTest(response);
            if (denied == null || denied.Authorized)
                throw new InvalidOperationException("Catalog Pack authorization ignored an explicit Server denial.");
        }

        private static void VerifyUiAssetPackAuthorizationParsing()
        {
            var response = new Dictionary<string, object>
            {
                { "ok", true }, { "authorized", true },
                { "uiAssetPack", new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "channel", LauncherVersion.Channel }, { "packId", "ui-assets" },
                    { "version", "1.0.1" }, { "minimumLauncherVersion", "1.0.0" }, { "packageId", "fixture" },
                    { "fileName", "KinojoUiAssets_1.0.1.zip" }, { "fileSize", 123L }, { "sha256", new String('a', 64) },
                    { "installManifestSha256", new String('b', 64) }, { "themeSha256", new String('c', 64) },
                    { "downloadUrl", "https://example.invalid/fixture?token=test" }, { "expiresAt", DateTimeOffset.UtcNow.AddMinutes(1).ToString("o") },
                    { "integrityMode", "RSA_SHA256_MANIFEST_V1" }, { "signingKeyId", "fixture" }, { "manifestSignature", "fixture" }
                } }
            };
            var parsed = LauncherApiClient.ParseUiAssetPackAuthorizationForTest(response);
            if (parsed == null || !parsed.Authorized || parsed.Release == null || parsed.Release.PackId != "ui-assets" ||
                parsed.Release.ThemeSha256 != new String('c', 64) || parsed.Release.ExpiresAt == DateTimeOffset.MinValue)
                throw new InvalidOperationException("UI Asset Pack authorization parsing failed.");
            response["authorized"] = false;
            if (LauncherApiClient.ParseUiAssetPackAuthorizationForTest(response).Authorized)
                throw new InvalidOperationException("UI Asset Pack authorization ignored an explicit Server denial.");
        }

        private static void VerifyShellModuleAuthorizationParsing()
        {
            var response = new Dictionary<string, object>
            {
                { "ok", true }, { "authorized", true },
                { "shellModule", new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "channel", LauncherVersion.Channel }, { "moduleId", "shell" },
                    { "version", "0.3.0" }, { "minimumLauncherVersion", "1.1.1" },
                    { "packageId", LauncherVersion.Channel + ":shell:0.3.0:" + new String('a', 16) },
                    { "packagePath", "modules/shell/0.3.0/KinojoMeterShell_0.3.0_x64.zip" },
                    { "fileName", "KinojoMeterShell_0.3.0_x64.zip" }, { "fileSize", 123L },
                    { "sha256", new String('a', 64) }, { "packageManifestSha256", new String('b', 64) },
                    { "contractSetVersion", 1 }, { "stateSchemaVersion", 1 },
                    { "primaryArtifact", "KINOJO.Meter.Shell.exe" },
                    { "downloadUrl", "https://example.invalid/fixture?token=test" },
                    { "expiresAt", DateTimeOffset.UtcNow.AddMinutes(1).ToString("o") },
                    { "integrityMode", "RSA_SHA256" }, { "signingKeyId", "fixture" },
                    { "manifestSignature", Convert.ToBase64String(new byte[384]) }, { "pointerGeneration", 4L }
                } }
            };
            var parsed = LauncherApiClient.ParseShellModuleAuthorizationForTest(response);
            if (parsed == null || !parsed.Authorized || parsed.Release == null ||
                parsed.Release.ModuleId != "shell" || parsed.Release.ContractSetVersion != 1 ||
                parsed.Release.PointerGeneration != 4 || parsed.Release.PackageManifestSha256 != new String('b', 64))
                throw new InvalidOperationException("Meter Shell authorization parsing failed.");
            response["authorized"] = false;
            if (LauncherApiClient.ParseShellModuleAuthorizationForTest(response).Authorized)
                throw new InvalidOperationException("Meter Shell authorization ignored an explicit Server denial.");
        }

        private static void VerifyPrivateRuntimeAuthorizationParsing()
        {
            var release = PrivateRuntimeRelease();
            var response = new Dictionary<string, object>
            {
                { "ok", true }, { "authorized", true },
                { "privateRuntime", new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "channel", release.Channel }, { "moduleId", release.ModuleId },
                    { "version", release.Version }, { "minimumLauncherVersion", release.MinimumLauncherVersion },
                    { "packageId", release.PackageId }, { "packagePath", release.PackagePath },
                    { "fileName", release.FileName }, { "fileSize", release.FileSize }, { "sha256", release.Sha256 },
                    { "packageManifestSha256", release.PackageManifestSha256 }, { "contractSetVersion", 1 },
                    { "stateSchemaVersion", 1 }, { "primaryArtifact", release.PrimaryArtifact },
                    { "runtimeBundleRevision", release.RuntimeBundleRevision },
                    { "runtimeBundleLockSha256", release.RuntimeBundleLockSha256 },
                    { "runtimeModuleSetHash", release.RuntimeModuleSetHash },
                    { "downloadUrl", release.DownloadUrl }, { "expiresAt", release.ExpiresAt.ToString("o") },
                    { "integrityMode", release.IntegrityMode }, { "signingKeyId", release.SigningKeyId },
                    { "manifestSignature", release.ManifestSignature }, { "pointerGeneration", 3L }
                } }
            };
            var parsed = LauncherApiClient.ParsePrivateRuntimeAuthorizationForTest(response);
            if (parsed == null || !parsed.Authorized || parsed.Release == null ||
                parsed.Release.ModuleId != "private-runtime" || parsed.Release.RuntimeBundleRevision != "B000100" ||
                parsed.Release.RuntimeModuleSetHash != new String('e', 64) || parsed.Release.PointerGeneration != 3)
                throw new InvalidOperationException("private runtime authorization parsing failed.");
        }

        private static void VerifyPrivateRuntimeReleaseContract()
        {
            PrivateRuntimePackageUpdater.ValidateReleaseForTest(PrivateRuntimeRelease(), "josvoltpktvwysrasffq.supabase.co");
        }

        private static void VerifyCaptureAuthorizationParsing()
        {
            var release = CaptureRelease();
            var response = new Dictionary<string, object>
            {
                { "ok", true }, { "authorized", true },
                { "captureModule", new Dictionary<string, object>
                {
                    { "schemaVersion", 1 }, { "channel", release.Channel }, { "moduleId", release.ModuleId },
                    { "version", release.Version }, { "minimumLauncherVersion", release.MinimumLauncherVersion },
                    { "packageId", release.PackageId }, { "packagePath", release.PackagePath },
                    { "fileName", release.FileName }, { "fileSize", release.FileSize }, { "sha256", release.Sha256 },
                    { "packageManifestSha256", release.PackageManifestSha256 }, { "contractSetVersion", 1 },
                    { "stateSchemaVersion", 0 }, { "primaryArtifact", release.PrimaryArtifact },
                    { "runtimeBundleRevision", release.RuntimeBundleRevision },
                    { "runtimeBundleLockSha256", release.RuntimeBundleLockSha256 },
                    { "runtimeModuleSetHash", release.RuntimeModuleSetHash },
                    { "parentPrivateRuntimeVersion", release.ParentPrivateRuntimeVersion },
                    { "parentPrivateRuntimeSha256", release.ParentPrivateRuntimeSha256 },
                    { "parentPrivateRuntimePointerGeneration", release.ParentPrivateRuntimePointerGeneration },
                    { "downloadUrl", release.DownloadUrl }, { "expiresAt", release.ExpiresAt.ToString("o") },
                    { "integrityMode", release.IntegrityMode }, { "signingKeyId", release.SigningKeyId },
                    { "manifestSignature", release.ManifestSignature }, { "pointerGeneration", 5L }
                } }
            };
            var parsed = LauncherApiClient.ParseCaptureModuleAuthorizationForTest(response);
            if (parsed == null || !parsed.Authorized || parsed.Release == null ||
                parsed.Release.ModuleId != "capture" || parsed.Release.StateSchemaVersion != 0 ||
                parsed.Release.ParentPrivateRuntimeVersion != "0.3.0" ||
                parsed.Release.ParentPrivateRuntimePointerGeneration != 7 || parsed.Release.PointerGeneration != 5)
                throw new InvalidOperationException("Capture Engine authorization parsing failed.");
        }

        private static void VerifyCaptureReleaseContract()
        {
            CaptureModuleUpdater.ValidateReleaseForTest(CaptureRelease(), "josvoltpktvwysrasffq.supabase.co");
        }

        private static void VerifyCaptureReleaseWrongPath()
        {
            var release = CaptureRelease();
            release.DownloadUrl = release.DownloadUrl.Replace("/modules/capture/", "/modules/capture-lookalike/");
            ExpectFailure(() => CaptureModuleUpdater.ValidateReleaseForTest(release, "josvoltpktvwysrasffq.supabase.co"));
        }

        private static void VerifyCaptureVersionShaConflict()
        {
            var release = CaptureRelease();
            var current = new ActiveCaptureModuleState { ModuleVersion = release.Version, PackageSha256 = new String('f', 64) };
            ExpectFailure(() => CaptureModuleUpdater.RejectVersionConflictForTest(current, release));
        }

        private static void VerifyCaptureParentBoundary(string root)
        {
            var release = CaptureRelease();
            var runtime = CapturePrivateRuntime();
            runtime.PointerGeneration++;
            ExpectFailure(() => CaptureModuleUpdater.ActivateForTest(
                release, CaptureStaged(root, release), CaptureSelfTest(root, release), PrivateRuntimeBundle(), runtime));
        }

        private static void VerifyCaptureActivationBoundary(string root)
        {
            var release = CaptureRelease();
            var active = CaptureModuleUpdater.ActivateForTest(
                release, CaptureStaged(root, release), CaptureSelfTest(root, release), PrivateRuntimeBundle(), CapturePrivateRuntime());
            if (active == null || active.ModuleId != "capture" || active.StateSchemaVersion != 0 ||
                active.RuntimeModuleSetHash != release.RuntimeModuleSetHash ||
                active.ParentPrivateRuntimeVersion != release.ParentPrivateRuntimeVersion ||
                active.ParentPrivateRuntimeSha256 != release.ParentPrivateRuntimeSha256 ||
                active.ParentPrivateRuntimePointerGeneration != release.ParentPrivateRuntimePointerGeneration)
                throw new InvalidOperationException("Capture Engine activation lost exact parent/Bundle identity.");
        }

        private static void VerifyCaptureProcessPlan(string root)
        {
            var shellRoot = Path.Combine(root, "capture-plan-shell");
            var runtimeRoot = Path.Combine(root, "capture-plan-runtime");
            var captureRoot = Path.Combine(root, "capture-plan-capture");
            Directory.CreateDirectory(shellRoot); Directory.CreateDirectory(runtimeRoot); Directory.CreateDirectory(captureRoot);
            File.WriteAllBytes(Path.Combine(shellRoot, "KINOJO.Meter.Shell.exe"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(runtimeRoot, "KINOJO.Meter.EngineHost.exe"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(captureRoot, "KINOJO.Meter.Capture.dll"), new byte[] { 3 });
            var shell = new ActiveShellModuleState
            {
                Channel = LauncherVersion.Channel, PrimaryArtifact = "KINOJO.Meter.Shell.exe", StagedDirectory = shellRoot,
                RuntimeBundleRevision = "B000100", RuntimeBundleLockSha256 = new String('d', 64)
            };
            var runtime = CapturePrivateRuntime();
            runtime.StagedDirectory = runtimeRoot;
            runtime.PrimaryArtifact = "KINOJO.Meter.EngineHost.exe";
            var capture = new ActiveCaptureModuleState
            {
                Channel = LauncherVersion.Channel, PrimaryArtifact = "KINOJO.Meter.Capture.dll", StagedDirectory = captureRoot,
                RuntimeBundleRevision = "B000100", RuntimeBundleLockSha256 = new String('d', 64),
                RuntimeModuleSetHash = new String('e', 64), ParentPrivateRuntimeVersion = runtime.ModuleVersion,
                ParentPrivateRuntimeSha256 = runtime.PackageSha256,
                ParentPrivateRuntimePointerGeneration = runtime.PointerGeneration
            };
            var plan = PrivateRuntimeProcessPlanBuilder.Build(shell, runtime, capture);
            if (!plan.CaptureOverrideActive || !plan.CaptureAssembly.EndsWith("KINOJO.Meter.Capture.dll", StringComparison.Ordinal) ||
                plan.RuntimeModuleSetHash != runtime.RuntimeModuleSetHash)
                throw new InvalidOperationException("Capture override process plan did not preserve exact identity.");
            capture.ParentPrivateRuntimeSha256 = new String('f', 64);
            ExpectFailure(() => PrivateRuntimeProcessPlanBuilder.Build(shell, runtime, capture));
        }

        private static CaptureModuleReleaseManifest CaptureRelease()
        {
            const string version = "0.3.1";
            var sha = new String('c', 64);
            var fileName = "KinojoCapture_" + version + "_x64.zip";
            return new CaptureModuleReleaseManifest
            {
                SchemaVersion = 1, Channel = LauncherVersion.Channel, ModuleId = "capture", Version = version,
                MinimumLauncherVersion = "1.1.1", PackageId = LauncherVersion.Channel + ":capture:" + version + ":" + sha.Substring(0, 16),
                PackagePath = "modules/capture/" + version + "/" + fileName, FileName = fileName, FileSize = 123,
                Sha256 = sha, PackageManifestSha256 = new String('b', 64), ContractSetVersion = 1, StateSchemaVersion = 0,
                PrimaryArtifact = "KINOJO.Meter.Capture.dll", RuntimeBundleRevision = "B000100",
                RuntimeBundleLockSha256 = new String('d', 64), RuntimeModuleSetHash = new String('e', 64),
                ParentPrivateRuntimeVersion = "0.3.0", ParentPrivateRuntimeSha256 = new String('a', 64),
                ParentPrivateRuntimePointerGeneration = 7,
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/modules/capture/" +
                    LauncherVersion.Channel + "/" + version + "/" + fileName + "?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2), IntegrityMode = "RSA_SHA256", SigningKeyId = "capture-test-key",
                ManifestSignature = Convert.ToBase64String(new byte[384]), PointerGeneration = 5
            };
        }

        private static ActivePrivateRuntimeState CapturePrivateRuntime()
        {
            return new ActivePrivateRuntimeState
            {
                Channel = LauncherVersion.Channel, ModuleVersion = "0.3.0", PackageSha256 = new String('a', 64),
                PointerGeneration = 7, RuntimeBundleRevision = "B000100",
                RuntimeBundleLockSha256 = new String('d', 64), RuntimeModuleSetHash = new String('e', 64)
            };
        }

        private static ModuleStagingInstallResult CaptureStaged(string root, CaptureModuleReleaseManifest release)
        {
            return new ModuleStagingInstallResult
            {
                ModuleId = "capture", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                StagedDirectory = Path.Combine(root, "capture-stage"), InstallStatus = ModuleStagingInstaller.StagedStatus
            };
        }

        private static ModuleSelfTestResult CaptureSelfTest(string root, CaptureModuleReleaseManifest release)
        {
            var receipt = Path.Combine(root, "capture-self-test.json");
            File.WriteAllText(receipt, "{\"status\":\"SELF_TEST_PASSED\"}", new UTF8Encoding(false));
            return new ModuleSelfTestResult
            {
                ModuleId = "capture", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                ReceiptFile = receipt, Status = ModuleStagingSelfTest.PassedStatus
            };
        }

        private static void VerifyPrivateRuntimeBundleBoundary(string root)
        {
            var release = PrivateRuntimeRelease();
            var staged = PrivateRuntimeStaged(root, release);
            var selfTest = PrivateRuntimeSelfTest(root, release);
            var bundle = PrivateRuntimeBundle();
            release.RuntimeBundleRevision = "B000101";
            ExpectFailure(() => PrivateRuntimePackageUpdater.ActivateForTest(release, staged, selfTest, bundle));
        }

        private static void VerifyPrivateRuntimeActivationBoundary(string root)
        {
            var release = PrivateRuntimeRelease();
            var active = PrivateRuntimePackageUpdater.ActivateForTest(
                release, PrivateRuntimeStaged(root, release), PrivateRuntimeSelfTest(root, release), PrivateRuntimeBundle());
            if (active == null || active.ModuleId != "private-runtime" ||
                active.RuntimeBundleRevision != release.RuntimeBundleRevision ||
                active.RuntimeBundleLockSha256 != release.RuntimeBundleLockSha256 ||
                active.RuntimeModuleSetHash != release.RuntimeModuleSetHash)
                throw new InvalidOperationException("private runtime activation lost exact Bundle identity.");
        }

        private static void VerifyPrivateRuntimeProcessPlan(string root)
        {
            var shellRoot = Path.Combine(root, "plan-shell");
            var runtimeRoot = Path.Combine(root, "plan-runtime");
            Directory.CreateDirectory(shellRoot); Directory.CreateDirectory(runtimeRoot);
            File.WriteAllBytes(Path.Combine(shellRoot, "KINOJO.Meter.Shell.exe"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(runtimeRoot, "KINOJO.Meter.EngineHost.exe"), new byte[] { 2 });
            var shell = new ActiveShellModuleState
            {
                Channel = LauncherVersion.Channel, PrimaryArtifact = "KINOJO.Meter.Shell.exe", StagedDirectory = shellRoot,
                RuntimeBundleRevision = "B000100", RuntimeBundleLockSha256 = new String('d', 64)
            };
            var runtime = new ActivePrivateRuntimeState
            {
                Channel = LauncherVersion.Channel, PrimaryArtifact = "KINOJO.Meter.EngineHost.exe", StagedDirectory = runtimeRoot,
                RuntimeBundleRevision = "B000100", RuntimeBundleLockSha256 = new String('d', 64)
            };
            var plan = PrivateRuntimeProcessPlanBuilder.Build(shell, runtime);
            if (!plan.ShellExecutable.EndsWith("KINOJO.Meter.Shell.exe", StringComparison.Ordinal) ||
                !plan.EngineHostExecutable.EndsWith("KINOJO.Meter.EngineHost.exe", StringComparison.Ordinal))
                throw new InvalidOperationException("split process plan did not preserve exact executable paths.");
            runtime.RuntimeBundleLockSha256 = new String('f', 64);
            ExpectFailure(() => PrivateRuntimeProcessPlanBuilder.Build(shell, runtime));
        }

        private static PrivateRuntimeReleaseManifest PrivateRuntimeRelease()
        {
            const string version = "0.3.0";
            var sha = new String('a', 64);
            var fileName = "KinojoPrivateRuntime_" + version + "_x64.zip";
            return new PrivateRuntimeReleaseManifest
            {
                SchemaVersion = 1, Channel = LauncherVersion.Channel, ModuleId = "private-runtime", Version = version,
                MinimumLauncherVersion = "1.1.1", PackageId = LauncherVersion.Channel + ":private-runtime:" + version + ":" + sha.Substring(0, 16),
                PackagePath = "modules/private-runtime/" + version + "/" + fileName, FileName = fileName, FileSize = 123,
                Sha256 = sha, PackageManifestSha256 = new String('b', 64), ContractSetVersion = 1, StateSchemaVersion = 1,
                PrimaryArtifact = "KINOJO.Meter.EngineHost.exe", RuntimeBundleRevision = "B000100",
                RuntimeBundleLockSha256 = new String('d', 64), RuntimeModuleSetHash = new String('e', 64),
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/modules/private-runtime/" +
                    LauncherVersion.Channel + "/" + version + "/" + fileName + "?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2), IntegrityMode = "RSA_SHA256", SigningKeyId = "runtime-test-key",
                ManifestSignature = Convert.ToBase64String(new byte[384]), PointerGeneration = 1
            };
        }

        private static ActiveModuleBundleState PrivateRuntimeBundle()
        {
            return new ActiveModuleBundleState
            {
                Channel = LauncherVersion.Channel, BundleRevision = "B000100", BundleLockSha256 = new String('d', 64),
                ModuleSetHash = new String('e', 64), ContractSetVersion = 1
            };
        }

        private static ModuleStagingInstallResult PrivateRuntimeStaged(string root, PrivateRuntimeReleaseManifest release)
        {
            return new ModuleStagingInstallResult
            {
                ModuleId = "private-runtime", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                StagedDirectory = Path.Combine(root, "private-runtime-stage"), InstallStatus = ModuleStagingInstaller.StagedStatus
            };
        }

        private static ModuleSelfTestResult PrivateRuntimeSelfTest(string root, PrivateRuntimeReleaseManifest release)
        {
            var receipt = Path.Combine(root, "private-runtime-self-test.json");
            File.WriteAllText(receipt, "{\"status\":\"SELF_TEST_PASSED\"}", new UTF8Encoding(false));
            return new ModuleSelfTestResult
            {
                ModuleId = "private-runtime", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                ReceiptFile = receipt, Status = ModuleStagingSelfTest.PassedStatus
            };
        }

        private static void VerifyShellReleaseContract()
        {
            ShellModuleUpdater.ValidateReleaseForTest(ShellRelease(), "josvoltpktvwysrasffq.supabase.co");
        }

        private static void VerifyShellReleaseWrongPath()
        {
            var release = ShellRelease();
            release.DownloadUrl = release.DownloadUrl.Replace("/modules/shell/", "/modules/shell-lookalike/");
            ExpectFailure(() => ShellModuleUpdater.ValidateReleaseForTest(release, "josvoltpktvwysrasffq.supabase.co"));
        }

        private static void VerifyShellReleaseMalformedSignature()
        {
            var release = ShellRelease();
            release.ManifestSignature = Convert.ToBase64String(new byte[383]);
            ExpectFailure(() => ShellModuleUpdater.ValidateReleaseForTest(release, "josvoltpktvwysrasffq.supabase.co"));
        }

        private static void VerifyShellVersionShaConflict()
        {
            var release = ShellRelease();
            var current = new ActiveShellModuleState { ModuleVersion = release.Version, PackageSha256 = new String('f', 64) };
            ExpectFailure(() => ShellModuleUpdater.RejectVersionConflictForTest(current, release));
        }

        private static void VerifyShellActivationBoundary(string root)
        {
            var receipt = Path.Combine(root, "shell-self-test.json");
            File.WriteAllText(receipt, "{\"status\":\"SELF_TEST_PASSED\"}", new UTF8Encoding(false));
            var release = ShellRelease();
            var staged = new ModuleStagingInstallResult
            {
                ModuleId = "shell", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                StagedDirectory = Path.Combine(root, "shell-stage"),
                InstallStatus = ModuleStagingInstaller.StagedStatus
            };
            var selfTest = new ModuleSelfTestResult
            {
                ModuleId = "shell", ModuleVersion = release.Version, ArchiveSha256 = release.Sha256,
                ReceiptFile = receipt, Status = ModuleStagingSelfTest.PassedStatus
            };
            var bundle = new ActiveModuleBundleState
            {
                Channel = LauncherVersion.Channel, BundleRevision = "B000100",
                BundleLockSha256 = new String('d', 64), ContractSetVersion = 1
            };
            var active = ShellModuleUpdater.ActivateForTest(release, staged, selfTest, bundle);
            if (active == null || active.ModuleId != "shell" || active.RuntimeBundleRevision != "B000100" ||
                active.RuntimeBundleLockSha256 != new String('d', 64) ||
                active.SelfTestReceiptSha256 != Hash(File.ReadAllBytes(receipt)))
                throw new InvalidOperationException("Meter Shell activation did not preserve exact runtime/self-test identity.");

            selfTest.Status = "FAILED";
            ExpectFailure(() => ShellModuleUpdater.ActivateForTest(release, staged, selfTest, bundle));
        }

        private static void VerifyShellDownloadBoundary()
        {
            var release = ShellRelease();
            var uri = new Uri(release.DownloadUrl);
            ModulePackageDownloadCache.ValidateRequestForTest(new ModulePackageDownloadRequest
            {
                ModuleId = "shell", ModuleVersion = release.Version, PackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256, DownloadUri = uri,
                ExpectedDownloadHost = uri.Host, ExpectedDownloadPath = uri.AbsolutePath,
                ExpectedFileSize = release.FileSize
            });
            ExpectFailure(() => ModulePackageDownloadCache.ValidateRequestForTest(new ModulePackageDownloadRequest
            {
                ModuleId = "shell", ModuleVersion = release.Version, PackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256,
                DownloadUri = new Uri(release.DownloadUrl.Replace("josvoltpktvwysrasffq.supabase.co", "example.com")),
                ExpectedDownloadHost = uri.Host, ExpectedDownloadPath = uri.AbsolutePath,
                ExpectedFileSize = release.FileSize
            }));
        }

        private static ShellModuleReleaseManifest ShellRelease()
        {
            var sha = new String('a', 64);
            var version = "0.3.0";
            var fileName = "KinojoMeterShell_" + version + "_x64.zip";
            return new ShellModuleReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                ModuleId = "shell",
                Version = version,
                MinimumLauncherVersion = "1.1.1",
                PackageId = LauncherVersion.Channel + ":shell:" + version + ":" + sha.Substring(0, 16),
                PackagePath = "modules/shell/" + version + "/" + fileName,
                FileName = fileName,
                FileSize = 123,
                Sha256 = sha,
                PackageManifestSha256 = new String('b', 64),
                ContractSetVersion = 1,
                StateSchemaVersion = 1,
                PrimaryArtifact = "KINOJO.Meter.Shell.exe",
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/modules/shell/" +
                    LauncherVersion.Channel + "/" + version + "/" + fileName + "?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
                IntegrityMode = "RSA_SHA256",
                SigningKeyId = "shell-test-key",
                ManifestSignature = Convert.ToBase64String(new byte[384]),
                PointerGeneration = 1
            };
        }

        private static void VerifyUiAssetIndividualUpdate(string root, RSACryptoServiceProvider key)
        {
            var updateRoot = Path.Combine(root, "ui-changed");
            var fixture = CreateUiAssetFixture("1.0.1", "one", key);
            var handler = new UiAssetFixtureHandler(fixture.PackageBytes);
            using (var installer = new UiAssetPackInstaller(handler, updateRoot, key.ExportParameters(false), "ui-test-key"))
            {
                var result = installer.EnsureInstalledAsync(fixture.Release, "josvoltpktvwysrasffq.supabase.co", CancellationToken.None).GetAwaiter().GetResult();
                if (!result.Changed || !result.Downloaded || handler.RequestCount != 1 || installer.ReadVerifiedActiveState() == null)
                    throw new InvalidOperationException("UI Asset Pack was not independently activated.");
            }
        }

        private static void VerifyUiAssetIdempotent(string root, RSACryptoServiceProvider key)
        {
            var updateRoot = Path.Combine(root, "ui-idempotent");
            var fixture = CreateUiAssetFixture("1.0.2", "one", key);
            using (var installer = new UiAssetPackInstaller(new UiAssetFixtureHandler(fixture.PackageBytes), updateRoot, key.ExportParameters(false), "ui-test-key"))
                installer.EnsureInstalledAsync(fixture.Release, "josvoltpktvwysrasffq.supabase.co", CancellationToken.None).GetAwaiter().GetResult();
            var noDownload = new UiAssetFixtureHandler(null);
            using (var installer = new UiAssetPackInstaller(noDownload, updateRoot, key.ExportParameters(false), "ui-test-key"))
            {
                var result = installer.EnsureInstalledAsync(fixture.Release, "josvoltpktvwysrasffq.supabase.co", CancellationToken.None).GetAwaiter().GetResult();
                if (result.Changed || result.Downloaded || noDownload.RequestCount != 0)
                    throw new InvalidOperationException("Exact UI Asset Pack was downloaded again.");
            }
        }

        private static void VerifyUiAssetVersionShaConflict(string root, RSACryptoServiceProvider key)
        {
            var updateRoot = Path.Combine(root, "ui-conflict");
            var first = CreateUiAssetFixture("1.0.3", "one", key);
            var conflicting = CreateUiAssetFixture("1.0.3", "two", key);
            using (var installer = new UiAssetPackInstaller(new UiAssetFixtureHandler(first.PackageBytes), updateRoot, key.ExportParameters(false), "ui-test-key"))
                installer.EnsureInstalledAsync(first.Release, "josvoltpktvwysrasffq.supabase.co", CancellationToken.None).GetAwaiter().GetResult();
            var pointer = Path.Combine(updateRoot, "active.json");
            var before = File.ReadAllBytes(pointer);
            var handler = new UiAssetFixtureHandler(conflicting.PackageBytes);
            try
            {
                using (var installer = new UiAssetPackInstaller(handler, updateRoot, key.ExportParameters(false), "ui-test-key"))
                    installer.EnsureInstalledAsync(conflicting.Release, "josvoltpktvwysrasffq.supabase.co", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                if (error.ToString().IndexOf(UiAssetPackInstaller.VersionShaConflictCode, StringComparison.Ordinal) >= 0 &&
                    handler.RequestCount == 0 && before.SequenceEqual(File.ReadAllBytes(pointer))) return;
                throw;
            }
            throw new InvalidOperationException("UI Asset version/SHA conflict did not fail closed.");
        }

        private static UiAssetFixture CreateUiAssetFixture(string version, string marker, RSACryptoServiceProvider key)
        {
            var json = new JavaScriptSerializer();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { "fonts/regular.ttf", Encoding.UTF8.GetBytes("regular-" + marker) },
                { "fonts/bold.ttf", Encoding.UTF8.GetBytes("bold-" + marker) },
                { "icons/classes/normal/test.png", Encoding.UTF8.GetBytes("class-" + marker) },
                { "icons/status/ok.png", Encoding.UTF8.GetBytes("status-" + marker) },
                { "icons/boss/test.png", Encoding.UTF8.GetBytes("boss-" + marker) }
            };
            files["theme.json"] = Encoding.UTF8.GetBytes(json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "packId", "ui-assets" }, { "version", version }, { "themeId", "fixture-theme" },
                { "fallback", "EMBEDDED_CORE" },
                { "fonts", new object[] { new Dictionary<string, object> { { "regular", "fonts/regular.ttf" }, { "bold", "fonts/bold.ttf" } } } },
                { "classIcons", new Dictionary<string, object> { { "variants", new object[] { "normal" } }, { "keys", new object[] { "test" } }, { "pathTemplate", "icons/classes/{variant}/{key}.png" } } },
                { "statusIcons", new Dictionary<string, object> { { "ok", "icons/status/ok.png" } } },
                { "bossIcons", new Dictionary<string, object> { { "test", "icons/boss/test.png" } } }
            }));
            var manifest = new UiAssetInstallManifest
            {
                SchemaVersion = 1,
                PackId = "ui-assets",
                Version = version,
                ThemeId = "fixture-theme",
                Files = files.Select(pair => new UiAssetInstallFile { Path = pair.Key, Size = pair.Value.Length, Sha256 = Hash(pair.Value) }).ToList()
            };
            var manifestBytes = Encoding.UTF8.GetBytes(json.Serialize(manifest));
            byte[] packageBytes;
            using (var stream = new MemoryStream())
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                {
                    foreach (var pair in files) WriteEntry(archive, pair.Key, pair.Value);
                    WriteEntry(archive, "install-manifest.json", manifestBytes);
                }
                packageBytes = stream.ToArray();
            }
            var release = new UiAssetReleaseManifest
            {
                SchemaVersion = 1, Channel = LauncherVersion.Channel, PackId = "ui-assets", Version = version,
                MinimumLauncherVersion = "1.0.0", PackageId = LauncherVersion.Channel + ":ui-assets:" + version,
                FileName = "KinojoUiAssets_" + version + ".zip", FileSize = packageBytes.Length, Sha256 = Hash(packageBytes),
                InstallManifestSha256 = Hash(manifestBytes), ThemeSha256 = Hash(files["theme.json"]),
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/ui-assets/" + LauncherVersion.Channel + "/" + version + "/KinojoUiAssets_" + version + ".zip?token=fixture",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2), IntegrityMode = UiAssetReleaseIntegrityVerifier.IntegrityMode,
                SigningKeyId = "ui-test-key", ReleaseNote = "fixture"
            };
            release.ManifestSignature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(UiAssetReleaseIntegrityVerifier.Canonicalize(release)), CryptoConfig.MapNameToOID("SHA256")));
            return new UiAssetFixture { Release = release, PackageBytes = packageBytes };
        }

        private static void VerifyMeterLaunchOperationParsing()
        {
            var allowed = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", new Dictionary<string, object>
                    {
                        { "channel", LauncherVersion.Channel },
                        { "launchEnabled", true },
                        { "launchMessage", "테스트 실행 허용" }
                    }
                }
            });
            if (allowed == null || !allowed.Enabled || allowed.Channel != LauncherVersion.Channel || allowed.Message != "테스트 실행 허용")
                throw new InvalidOperationException("Server Meter launch operation was not parsed.");

            var blocked = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object>
            {
                { "ok", true },
                { "operation", new Dictionary<string, object>
                    {
                        { "channel", LauncherVersion.Channel },
                        { "launchEnabled", false },
                        { "launchMessage", "점검 중" }
                    }
                }
            });
            if (blocked == null || blocked.Enabled || blocked.Message != "점검 중")
                throw new InvalidOperationException("Server launch-disabled operation was not fail-closed.");

            var missing = LauncherApiClient.ParseLaunchOperationForTest(new Dictionary<string, object> { { "ok", true } });
            if (missing == null || missing.Enabled)
                throw new InvalidOperationException("Missing launch operation did not fail closed.");
        }

        private static void VerifyCoreUpdateHandoffArguments()
        {
            var requestId = Guid.NewGuid().ToString("N");
            CoreUpdateHandoffRequest request;
            string error;
            if (!CoreUpdateHandoffProtocol.TryParseArguments(new[]
                {
                    CoreUpdateHandoffProtocol.ModeArgument,
                    CoreUpdateHandoffProtocol.RequestArgument,
                    requestId,
                    CoreUpdateHandoffProtocol.ProcessArgument,
                    "321"
                }, out request, out error) || request == null || request.CoreProcessId != 321 ||
                !String.Equals(request.RequestId, requestId, StringComparison.Ordinal))
                throw new InvalidOperationException("Hidden handoff arguments were not parsed.");
        }

        private static void VerifyCoreUpdateHandoffCommandLineBoundary()
        {
            var requestId = Guid.NewGuid().ToString("N");
            var args = new[]
            {
                CoreUpdateHandoffProtocol.ModeArgument,
                CoreUpdateHandoffProtocol.RequestArgument,
                requestId,
                CoreUpdateHandoffProtocol.ProcessArgument,
                "321"
            };
            var serialized = String.Join(" ", args);
            if (serialized.IndexOf("session", StringComparison.OrdinalIgnoreCase) >= 0 ||
                serialized.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                serialized.IndexOf("installation", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Secret-bearing fields leaked into Launcher arguments.");

            CoreUpdateHandoffRequest ignored;
            string error;
            if (CoreUpdateHandoffProtocol.TryParseArguments(args.Concat(new[] { "--session-token", "secret" }).ToArray(), out ignored, out error))
                throw new InvalidOperationException("Unexpected secret-bearing Launcher arguments were accepted.");
        }

        private static void VerifyCoreUpdateHandoffEnvelope()
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new CoreUpdateHandoffRequest { RequestId = requestId, CoreProcessId = 321 };
            var token = new String('T', 32);
            var installationId = Guid.NewGuid().ToString("N");
            var raw = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "requestId", requestId },
                { "coreProcessId", 321 },
                { "sessionToken", token },
                { "installationId", installationId },
                { "currentCoreVersion", "0.2.49" },
                { "issuedAtUtc", DateTime.UtcNow.ToString("o") },
                { "account", new Dictionary<string, object> { { "mainCharacterName", "청소기" } } },
                { "characters", new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { { "characterKey", "self" }, { "characterName", "청소기" } }
                    }
                }
            };
            var line = CoreUpdateHandoffProtocol.EnvelopePrefix + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(raw)));
            var parsed = CoreUpdateHandoffProtocol.ParseEnvelopeLineForTest(line, request, DateTime.UtcNow);
            if (parsed == null || parsed.SessionToken != token || parsed.InstallationId != installationId ||
                parsed.Characters == null || parsed.Characters.Count != 1)
                throw new InvalidOperationException("Redirected handoff envelope was not parsed.");
        }

        private static void VerifyCoreUpdateHandoffRequestMismatch()
        {
            var requestId = Guid.NewGuid().ToString("N");
            var request = new CoreUpdateHandoffRequest { RequestId = requestId, CoreProcessId = 321 };
            var raw = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "requestId", Guid.NewGuid().ToString("N") },
                { "coreProcessId", 321 },
                { "sessionToken", new String('T', 32) },
                { "installationId", Guid.NewGuid().ToString("N") },
                { "currentCoreVersion", "0.2.49" },
                { "issuedAtUtc", DateTime.UtcNow.ToString("o") },
                { "account", new Dictionary<string, object>() },
                { "characters", new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { { "characterKey", "self" } }
                    }
                }
            };
            var line = CoreUpdateHandoffProtocol.EnvelopePrefix + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(raw)));
            ExpectFailure(() => CoreUpdateHandoffProtocol.ParseEnvelopeLineForTest(line, request, DateTime.UtcNow));
        }

        private static void VerifyCoreUpdateHandoffReadySignal()
        {
            var requestId = Guid.NewGuid().ToString("N");
            var expected = CoreUpdateHandoffProtocol.ReadyPrefix + requestId + " 0.2.50";
            if (!String.Equals(CoreUpdateHandoffProtocol.ReadyLine(requestId, "0.2.50"), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher takeover READY signal changed.");
        }

        private static void VerifyCoreUpdateHandoffVersionComparison()
        {
            if (CoreUpdateHandoffProtocol.CompareVersions("0.2.50", "0.2.49") <= 0 ||
                CoreUpdateHandoffProtocol.CompareVersions("0.2.49", "0.2.49") != 0 ||
                CoreUpdateHandoffProtocol.CompareVersions("0.2.48", "0.2.49") >= 0)
                throw new InvalidOperationException("Core handoff semantic version comparison failed.");
        }

        private static void VerifyChannelProfile()
        {
            var expectedFunction = LauncherVersion.Channel == "staging" ? "meter-staging-ingest" : "meter-ingest";
            var expectedFolder = LauncherVersion.Channel == "staging" ? "KINOJO Meter Staging" : "KINOJO Meter";
            if (!String.Equals(LauncherBuildProfile.FunctionName, expectedFunction, StringComparison.Ordinal) ||
                !String.Equals(LauncherBuildProfile.DataFolderName, expectedFolder, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher channel profile is not compile-time bound.");
        }

        private static void VerifyPassKeyLength()
        {
            const string passKey = "kinojo";
            const string expected = "KINOJO";
            var normalized = LauncherPassKeyContract.Normalize(passKey);
            if (!LauncherPassKeyContract.IsValid(normalized) ||
                LauncherPassKeyContract.TextElements(normalized).Length != 6 ||
                !String.Equals(normalized, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Six-character PASS KEY was not normalized to uppercase.");
        }

        private static void VerifyIncompletePassKey()
        {
            if (LauncherPassKeyContract.IsValid("KINOJ"))
                throw new InvalidOperationException("Incomplete PASS KEY was accepted.");
        }

        private static void VerifyLauncherUiLayoutContracts()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var previewDirectory = Environment.GetEnvironmentVariable("KINOJO_LAUNCHER_UI_PREVIEW_DIR");
            using (var login = new LauncherLoginForm(true))
            {
                login.CreateControl();
                login.PerformLayout();
                if (!login.VisualContractForTesting)
                    throw new InvalidOperationException("Compact PASS KEY login layout contract failed.");
                if (!String.IsNullOrWhiteSpace(previewDirectory))
                    SaveFormPreview(login, Path.Combine(previewDirectory, "launcher-login-compact.png"));
            }
            using (var launcher = new LauncherForm(new LauncherLoginResult
            {
                SessionToken = new String('T', 32),
                DisplayName = "테스트 사용자"
            }, true))
            {
                launcher.CreateControl();
                launcher.PerformLayout();
                if (!launcher.SidebarBrandContractForTesting)
                    throw new InvalidOperationException("Launcher brand header clipping contract failed.");
                if (!String.IsNullOrWhiteSpace(previewDirectory))
                    SaveFormPreview(launcher, Path.Combine(previewDirectory, "launcher-main-header.png"));
            }
        }

        private static void SaveFormPreview(Form form, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.Show();
            Application.DoEvents();
            using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.ClientSize));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            form.Hide();
        }

        private static void VerifyBundledDriverSignature()
        {
            AuthenticodeVerifier.Verify(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinDivert64.sys"), "");
        }

        private static void VerifyTamperedDriverRejected(string root)
        {
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinDivert64.sys");
            var target = Path.Combine(root, "WinDivert64-tampered.sys");
            var bytes = File.ReadAllBytes(source);
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(target, bytes);
            ExpectFailure(() => AuthenticodeVerifier.Verify(target, ""));
        }

        private static void VerifyLauncherContentFeed()
        {
            var result = LauncherContentClient.ParseForTest(ContentFeedJson(null));
            if (result == null || result.Items == null || result.Items.Count != 1 || result.Items[0].Id != "test-update")
                throw new InvalidOperationException("Launcher content feed was not parsed.");
        }

        private static void VerifyLauncherContentChannelFilter()
        {
            var other = LauncherVersion.Channel == "staging" ? "stable" : "staging";
            var result = LauncherContentClient.ParseForTest(ContentFeedJson(rows => rows.Add(new Dictionary<string, object>
            {
                { "id", "other-channel" },
                { "type", "notice" },
                { "channel", other },
                { "pinned", false },
                { "title", "Other channel" },
                { "summary", "Must be filtered" },
                { "publishedAt", "2026-08-06T14:00:00+09:00" },
                { "version", "" },
                { "url", "https://kinojo.info/meter/" }
            })));
            if (result.Items.Count != 1 || result.Items.Any(item => item.Id == "other-channel"))
                throw new InvalidOperationException("Cross-channel Launcher content was not filtered.");
        }

        private static void VerifyLauncherUpdateContract()
        {
            LauncherUpdateService.ValidateManifestForTest(LauncherUpdateRelease());
        }

        private static void VerifyLauncherUpdateParsing()
        {
            var release = LauncherUpdateRelease();
            var raw = new Dictionary<string, object>
            {
                { "ok", true },
                { "launcherRelease", new Dictionary<string, object>
                    {
                        { "releaseAvailable", true },
                        { "updateAvailable", true },
                        { "launcherUpdate", new Dictionary<string, object>
                            {
                                { "version", release.Version },
                                { "fileVersion", release.FileVersion },
                                { "minimumVersion", release.MinimumVersion },
                                { "fileName", release.FileName },
                                { "fileSize", release.FileSize },
                                { "sha256", release.Sha256 },
                                { "downloadUrl", release.DownloadUrl },
                                { "mandatory", true },
                                { "releaseNote", "test" },
                                { "codeSignatureRequired", false },
                                { "publisherSubject", "" },
                                { "trustMode", "WINDOWS_UNSIGNED_HOBBY" },
                                { "smartScreenWarningExpected", true },
                                { "channel", release.Channel }
                            }
                        }
                    }
                }
            };
            var parsed = LauncherApiClient.ParseLauncherUpdateForTest(raw);
            if (parsed == null || !parsed.ReleaseAvailable || !parsed.UpdateAvailable || parsed.Release == null ||
                !String.Equals(parsed.Release.Version, release.Version, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher update manifest was not parsed.");
        }

        private static LauncherUpdateManifest LauncherUpdateRelease()
        {
            const string version = "1.2.0";
            var fileName = LauncherVersion.IsStaging
                ? "KINOJO_Meter_Launcher_Staging_" + version + ".exe"
                : "KINOJO_Meter_Launcher_" + version + ".exe";
            var tag = LauncherVersion.IsStaging ? "launcher-staging-v" + version : "launcher-v" + version;
            return new LauncherUpdateManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                Version = version,
                FileVersion = version + ".0",
                MinimumVersion = "1.1.0",
                FileName = fileName,
                FileSize = 1024,
                Sha256 = new String('a', 64),
                DownloadUrl = "https://github.com/losnah513/kinojo-meter/releases/download/" + tag + "/" + fileName,
                Mandatory = true,
                ReleaseNote = "test",
                CodeSignatureRequired = false,
                PublisherSubject = "",
                TrustMode = "WINDOWS_UNSIGNED_HOBBY",
                SmartScreenWarningExpected = true
            };
        }

        private static string ContentFeedJson(Action<List<Dictionary<string, object>>> mutate, int schemaVersion = 1)
        {
            var rows = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    { "id", "test-update" },
                    { "type", "update" },
                    { "channel", "all" },
                    { "pinned", true },
                    { "title", "Test update" },
                    { "summary", "Validated Launcher content" },
                    { "publishedAt", "2026-08-06T14:00:00+09:00" },
                    { "version", "1.0.0" },
                    { "url", "https://kinojo.info/meter/" }
                }
            };
            if (mutate != null) mutate(rows);
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", schemaVersion },
                { "updatedAt", "2026-08-06T14:00:00+09:00" },
                { "items", rows }
            });
        }

        private static void VerifyPackage(string root, bool unmanaged, bool duplicate, bool wrongManifestHash)
        {
            var id = Guid.NewGuid().ToString("N");
            var package = Path.Combine(root, id + ".zip");
            var destination = Path.Combine(root, id);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                { LauncherBuildProfile.CoreEntryPoint, Encoding.UTF8.GetBytes("test-core") },
                { "version.json", Encoding.UTF8.GetBytes("{\"version\":\"0.2.38\"}") }
            };
            var managed = files.Select(pair => new CoreInstallFile
            {
                Path = pair.Key,
                Size = pair.Value.Length,
                Sha256 = Hash(pair.Value)
            }).ToList();
            var installManifest = new CoreInstallManifest
            {
                SchemaVersion = 1,
                CoreVersion = "0.2.38",
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                Files = managed
            };
            var installManifestBytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(installManifest));
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (var pair in files) WriteEntry(archive, pair.Key, pair.Value);
                WriteEntry(archive, "install-manifest.json", installManifestBytes);
                if (unmanaged) WriteEntry(archive, "unmanaged.dll", new byte[] { 1, 2, 3 });
                if (duplicate) WriteEntry(archive, "version.json", Encoding.UTF8.GetBytes("duplicate"));
            }
            var release = new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                CoreVersion = "0.2.38",
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                InstallManifestSha256 = wrongManifestHash ? new String('0', 64) : Hash(installManifestBytes),
                CodeSignatureRequired = false,
                PublisherSubject = ""
            };
            using (var installer = new CorePackageInstaller()) installer.ExtractAndVerifyForTest(package, destination, release);
        }

        private static void VerifyReleaseContract(RSACryptoServiceProvider signingKey, Action<CoreReleaseManifest> mutateAfterSigning)
        {
            const string keyId = "launcher-test-key";
            var release = new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                CoreVersion = "0.2.38",
                MinimumCoreVersion = "0.2.38",
                MinimumLauncherVersion = "1.0.0",
                PackageId = LauncherVersion.Channel + ":0.2.38:" + new String('a', 16),
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                FileSize = 1,
                Sha256 = new String('a', 64),
                InstallManifestSha256 = new String('d', 64),
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/" + LauncherVersion.Channel + "/0.2.38/package?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                Mandatory = true,
                CodeSignatureRequired = false,
                PublisherSubject = "",
                IntegrityMode = CoreReleaseIntegrityVerifier.IntegrityMode,
                SigningKeyId = keyId
            };
            release.ManifestSignature = Convert.ToBase64String(signingKey.SignData(
                Encoding.UTF8.GetBytes(CoreReleaseIntegrityVerifier.Canonicalize(release)),
                CryptoConfig.MapNameToOID("SHA256")));
            if (mutateAfterSigning != null) mutateAfterSigning(release);
            CorePackageInstaller.ValidateReleaseForTest(
                release,
                "josvoltpktvwysrasffq.supabase.co",
                signingKey.ExportParameters(false),
                keyId);
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var output = entry.Open()) output.Write(content, 0, content.Length);
        }

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(value).Select(item => item.ToString("x2")));
        }

        private static void ExpectFailure(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Expected package validation failure did not occur.");
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed += 1;
            Console.WriteLine("PASS " + name);
        }

        private sealed class UiAssetFixture
        {
            public UiAssetReleaseManifest Release { get; set; }
            public byte[] PackageBytes { get; set; }
        }

        private sealed class UiAssetFixtureHandler : HttpMessageHandler
        {
            private readonly byte[] _content;
            public int RequestCount { get; private set; }

            public UiAssetFixtureHandler(byte[] content) { _content = content; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount += 1;
                if (_content == null) throw new InvalidOperationException("Unexpected UI Asset network request.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(_content)
                });
            }
        }
    }
}
