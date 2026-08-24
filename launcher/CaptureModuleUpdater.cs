using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class CaptureModuleUpdater : IDisposable
    {
        internal const string VersionShaConflictCode = "CAPTURE_VERSION_SHA_CONFLICT";
        internal const string RuntimeBundleRequiredCode = "CAPTURE_RUNTIME_BUNDLE_REQUIRED";
        internal const string RuntimeBundleChangedCode = "CAPTURE_RUNTIME_BUNDLE_CHANGED";
        internal const string PrivateRuntimeRequiredCode = "CAPTURE_PRIVATE_RUNTIME_REQUIRED";
        internal const string PrivateRuntimeChangedCode = "CAPTURE_PRIVATE_RUNTIME_CHANGED";
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex BundlePattern = new Regex("^B[0-9]{6,}$", RegexOptions.CultureInvariant);

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly ModulePackageDownloadCache _cache;
        private readonly string _moduleRoot;
        private readonly string _stagingRoot;
        private readonly string _selfTestRoot;
        private readonly string _activeFile;
        private readonly string _activationLock;
        private readonly string _privateRuntimeLock;
        private readonly string _captureLock;
        private readonly Func<ActiveModuleBundleState> _readActiveBundle;
        private readonly Func<ActivePrivateRuntimeState> _readActivePrivateRuntime;

        public CaptureModuleUpdater()
            : this(
                new ModulePackageDownloadCache(),
                LauncherPaths.ModuleRoot,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                LauncherPaths.ModuleActiveCaptureFile,
                LauncherPaths.ModuleActivationLockFile,
                LauncherPaths.ModulePrivateRuntimeUpdateLockFile,
                LauncherPaths.ModuleCaptureUpdateLockFile,
                ModuleBundleActivator.ReadVerifiedActiveBundle,
                ReadDefaultPrivateRuntime)
        {
        }

        internal CaptureModuleUpdater(
            ModulePackageDownloadCache cache,
            string moduleRoot,
            string stagingRoot,
            string selfTestRoot,
            string activeFile,
            string activationLock,
            string privateRuntimeLock,
            string captureLock,
            Func<ActiveModuleBundleState> readActiveBundle,
            Func<ActivePrivateRuntimeState> readActivePrivateRuntime)
        {
            _cache = cache ?? throw new ArgumentNullException("cache");
            _moduleRoot = Path.GetFullPath(moduleRoot ?? throw new ArgumentNullException("moduleRoot"));
            _stagingRoot = Path.GetFullPath(stagingRoot ?? throw new ArgumentNullException("stagingRoot"));
            _selfTestRoot = Path.GetFullPath(selfTestRoot ?? throw new ArgumentNullException("selfTestRoot"));
            _activeFile = Path.GetFullPath(activeFile ?? throw new ArgumentNullException("activeFile"));
            _activationLock = Path.GetFullPath(activationLock ?? throw new ArgumentNullException("activationLock"));
            _privateRuntimeLock = Path.GetFullPath(privateRuntimeLock ?? throw new ArgumentNullException("privateRuntimeLock"));
            _captureLock = Path.GetFullPath(captureLock ?? throw new ArgumentNullException("captureLock"));
            _readActiveBundle = readActiveBundle ?? throw new ArgumentNullException("readActiveBundle");
            _readActivePrivateRuntime = readActivePrivateRuntime ?? throw new ArgumentNullException("readActivePrivateRuntime");
            EnsureUnderRoot(_moduleRoot, _stagingRoot);
            EnsureUnderRoot(_moduleRoot, _selfTestRoot);
            EnsureUnderRoot(_moduleRoot, _activeFile);
            EnsureUnderRoot(_moduleRoot, _activationLock);
            EnsureUnderRoot(_moduleRoot, _privateRuntimeLock);
            EnsureUnderRoot(_moduleRoot, _captureLock);
        }

        public ActiveCaptureModuleState ReadVerifiedActiveState()
        {
            var state = ReadAuthorizationState();
            if (state == null) return null;

            var bundle = RequireCompatibleBundle(state.Channel, state.ContractSetVersion);
            if (!String.Equals(bundle.BundleRevision, state.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleLockSha256, state.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(bundle.ModuleSetHash, state.RuntimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": Capture Engine이 검증된 runtime Bundle과 현재 Bundle이 다릅니다.");
            RequireCompatiblePrivateRuntime(
                state.Channel,
                state.RuntimeBundleRevision,
                state.RuntimeBundleLockSha256,
                state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion,
                state.ParentPrivateRuntimeSha256,
                state.ParentPrivateRuntimePointerGeneration);

            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage))
                throw new InvalidOperationException("Capture Engine staging 경로가 결정 경로와 일치하지 않습니다.");

            var target = new ModuleStagingInstallResult
            {
                ModuleId = "capture",
                ModuleVersion = state.ModuleVersion,
                ArchiveSha256 = state.PackageSha256,
                StagedDirectory = expectedStage,
                InstallReceiptFile = Path.Combine(expectedStage, ModuleStagingInstaller.InstallReceiptName),
                AlreadyStaged = true,
                InstallStatus = ModuleStagingInstaller.StagedStatus
            };
            var selfTest = ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = target, Dependencies = Dependencies(bundle) },
                _stagingRoot,
                _selfTestRoot);
            if (!String.Equals(Sha256File(selfTest.ReceiptFile), state.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath)), state.PackageManifestSha256, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, state.PrimaryArtifact)))
                throw new InvalidOperationException("Capture Engine active state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        internal ActiveCaptureModuleState ReadAuthorizationState()
        {
            if (!File.Exists(_activeFile)) return null;
            ActiveCaptureModuleState state;
            try { state = _json.Deserialize<ActiveCaptureModuleState>(File.ReadAllText(_activeFile, Encoding.UTF8)); }
            catch (Exception error) { throw new InvalidOperationException("Capture Engine active state를 신뢰할 수 없습니다.", error); }
            ValidateActiveStateShape(state);
            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            var expectedSelfTest = Path.Combine(_selfTestRoot, "capture", state.ModuleVersion, state.PackageSha256, ModuleStagingSelfTest.ReceiptName);
            var manifest = Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage) || !File.Exists(expectedSelfTest) || !File.Exists(manifest) ||
                !String.Equals(Sha256File(expectedSelfTest), state.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(manifest), state.PackageManifestSha256, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, state.PrimaryArtifact)))
                throw new InvalidOperationException("Capture Engine authorization state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        public async Task<CaptureModuleInstallResult> EnsureInstalledAsync(
            CaptureModuleReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            var uri = ValidateRelease(release, expectedProjectHost);
            RequireCompatiblePrivateRuntime(release);
            Directory.CreateDirectory(_moduleRoot);
            Directory.CreateDirectory(_stagingRoot);
            Directory.CreateDirectory(_selfTestRoot);

            var currentIdentity = ReadAuthorizationState();
            RejectVersionConflict(currentIdentity, release);
            var current = TryReadVerifiedActiveStateForCurrentBundle();
            if (SameRelease(current, release))
                return new CaptureModuleInstallResult { Active = current, Previous = currentIdentity, Changed = false, Downloaded = false };

            var download = new ModulePackageDownloadRequest
            {
                ModuleId = "capture",
                ModuleVersion = release.Version,
                PackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256,
                DownloadUri = uri,
                ExpectedDownloadHost = uri.Host,
                ExpectedDownloadPath = uri.AbsolutePath,
                ExpectedFileSize = release.FileSize
            };
            var cached = await _cache.DownloadAsync(download, null, cancellationToken).ConfigureAwait(false);
            if (cached.Bytes != release.FileSize)
                throw new InvalidOperationException("Capture Engine cache 크기가 Server release와 일치하지 않습니다.");

            var verificationRequest = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = "capture",
                ModuleVersion = release.Version,
                BundlePackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256,
                ContractSetVersion = release.ContractSetVersion,
                StateSchemaVersion = release.StateSchemaVersion
            };
            var verified = ModulePackageVerifier.Verify(verificationRequest);
            if (!String.Equals(verified.ManifestSha256, release.PackageManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(verified.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("Capture Engine Package Manifest가 Server release와 일치하지 않습니다.");
            VerifyManifestIdentity(cached.PackageFile, release);

            using (var activationGate = ExclusiveFile(_activationLock))
            using (var privateRuntimeGate = ExclusiveFile(_privateRuntimeLock))
            using (var captureGate = ExclusiveFile(_captureLock))
            {
                var latestIdentity = ReadAuthorizationState();
                RejectVersionConflict(latestIdentity, release);
                var latest = TryReadVerifiedActiveStateForCurrentBundle();
                if (SameRelease(latest, release))
                    return new CaptureModuleInstallResult
                    {
                        Active = latest,
                        Previous = latestIdentity,
                        Changed = false,
                        Downloaded = !cached.CacheHit
                    };
                var bundle = RequireCompatibleBundle(release.Channel, release.ContractSetVersion);
                var privateRuntime = RequireCompatiblePrivateRuntime(release);
                var staged = ModuleStagingInstaller.Stage(
                    new ModuleStagingInstallRequest { VerificationRequest = verificationRequest });
                var selfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = staged, Dependencies = Dependencies(bundle) },
                    _stagingRoot,
                    _selfTestRoot);
                var active = Activate(release, staged, selfTest, bundle, privateRuntime);
                WriteActiveState(active);
                return new CaptureModuleInstallResult
                {
                    Active = active,
                    Previous = latestIdentity,
                    Changed = true,
                    Downloaded = !cached.CacheHit
                };
            }
        }

        internal static ActiveCaptureModuleState ActivateForTest(
            CaptureModuleReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime)
        {
            return Activate(release, staged, selfTest, bundle, privateRuntime);
        }

        internal static void RejectVersionConflictForTest(ActiveCaptureModuleState current, CaptureModuleReleaseManifest release)
        {
            RejectVersionConflict(current, release);
        }

        internal static void ValidateReleaseForTest(CaptureModuleReleaseManifest release, string expectedProjectHost)
        {
            ValidateRelease(release, expectedProjectHost);
        }

        private static ActiveCaptureModuleState Activate(
            CaptureModuleReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime)
        {
            if (release == null || staged == null || selfTest == null || bundle == null || privateRuntime == null ||
                !String.Equals(release.ModuleId, "capture", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleId, "capture", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(staged.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleId, "capture", StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !File.Exists(selfTest.ReceiptFile))
                throw new InvalidOperationException("검증되지 않은 Capture Engine은 활성화할 수 없습니다.");
            RequireReleaseContext(release, bundle, privateRuntime);
            return new ActiveCaptureModuleState
            {
                SchemaVersion = 1,
                Channel = release.Channel,
                ModuleId = "capture",
                ModuleVersion = release.Version,
                PackagePath = release.PackagePath,
                PackageSha256 = release.Sha256,
                PackageManifestSha256 = release.PackageManifestSha256,
                ContractSetVersion = release.ContractSetVersion,
                StateSchemaVersion = release.StateSchemaVersion,
                PrimaryArtifact = release.PrimaryArtifact,
                StagedDirectory = staged.StagedDirectory,
                SelfTestReceiptSha256 = Sha256File(selfTest.ReceiptFile),
                RuntimeBundleRevision = bundle.BundleRevision,
                RuntimeBundleLockSha256 = bundle.BundleLockSha256,
                RuntimeModuleSetHash = bundle.ModuleSetHash,
                ParentPrivateRuntimeVersion = privateRuntime.ModuleVersion,
                ParentPrivateRuntimeSha256 = privateRuntime.PackageSha256,
                ParentPrivateRuntimePointerGeneration = privateRuntime.PointerGeneration,
                PointerGeneration = release.PointerGeneration,
                ActivatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private ActiveModuleBundleState RequireCompatibleBundle(string channel, int contractSetVersion)
        {
            var bundle = _readActiveBundle();
            if (bundle == null)
                throw new InvalidOperationException(RuntimeBundleRequiredCode + ": Capture Engine 활성화 전에 검증된 private runtime Bundle이 필요합니다.");
            if (!String.Equals(bundle.Channel, channel, StringComparison.Ordinal) || bundle.ContractSetVersion != contractSetVersion)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": Capture Engine channel/Contract Set과 runtime Bundle이 다릅니다.");
            return bundle;
        }

        private ActiveCaptureModuleState TryReadVerifiedActiveStateForCurrentBundle()
        {
            try { return ReadVerifiedActiveState(); }
            catch (InvalidOperationException error)
            {
                if (error.Message.StartsWith(RuntimeBundleChangedCode + ":", StringComparison.Ordinal) ||
                    error.Message.StartsWith(PrivateRuntimeChangedCode + ":", StringComparison.Ordinal)) return null;
                throw;
            }
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(CaptureModuleReleaseManifest release)
        {
            if (release == null) throw new ArgumentNullException("release");
            return RequireCompatiblePrivateRuntime(
                release.Channel,
                release.RuntimeBundleRevision,
                release.RuntimeBundleLockSha256,
                release.RuntimeModuleSetHash,
                release.ParentPrivateRuntimeVersion,
                release.ParentPrivateRuntimeSha256,
                release.ParentPrivateRuntimePointerGeneration);
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(
            string channel,
            string runtimeBundleRevision,
            string runtimeBundleLockSha256,
            string runtimeModuleSetHash,
            string parentVersion,
            string parentSha256,
            long parentPointerGeneration)
        {
            var runtime = _readActivePrivateRuntime();
            if (runtime == null)
                throw new InvalidOperationException(PrivateRuntimeRequiredCode + ": Capture 개별 활성화 전에 검증된 private runtime whole package가 필요합니다.");
            if (!String.Equals(runtime.Channel, channel, StringComparison.Ordinal) ||
                !String.Equals(runtime.ModuleVersion, parentVersion, StringComparison.Ordinal) ||
                !String.Equals(runtime.PackageSha256, parentSha256, StringComparison.Ordinal) ||
                runtime.PointerGeneration != parentPointerGeneration ||
                !String.Equals(runtime.RuntimeBundleRevision, runtimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeBundleLockSha256, runtimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeModuleSetHash, runtimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(PrivateRuntimeChangedCode + ": Capture release의 parent private runtime 또는 Bundle 고정값이 현재 활성 상태와 다릅니다.");
            return runtime;
        }

        private static void RequireReleaseContext(
            CaptureModuleReleaseManifest release,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime)
        {
            if (release == null || bundle == null || privateRuntime == null ||
                !String.Equals(release.Channel, bundle.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleRevision, bundle.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, bundle.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, bundle.ModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(release.Channel, privateRuntime.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeVersion, privateRuntime.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeSha256, privateRuntime.PackageSha256, StringComparison.Ordinal) ||
                release.ParentPrivateRuntimePointerGeneration != privateRuntime.PointerGeneration ||
                !String.Equals(release.RuntimeBundleRevision, privateRuntime.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, privateRuntime.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, privateRuntime.RuntimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(PrivateRuntimeChangedCode + ": Capture activation context가 exact private runtime/Bundle identity와 일치하지 않습니다.");
        }

        private static ActivePrivateRuntimeState ReadDefaultPrivateRuntime()
        {
            using (var updater = new PrivateRuntimePackageUpdater())
                return updater.ReadVerifiedActiveState();
        }

        private static List<ModuleSelfTestDependency> Dependencies(ActiveModuleBundleState bundle)
        {
            if (bundle == null || bundle.Modules == null) throw new InvalidOperationException(RuntimeBundleRequiredCode);
            var dependencies = bundle.Modules
                .Where(value => value != null && String.Equals(value.ModuleId, "contracts", StringComparison.Ordinal))
                .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
                .Select(value => new ModuleSelfTestDependency
                {
                    ModuleId = value.ModuleId,
                    ModuleVersion = value.ModuleVersion,
                    ArchiveSha256 = value.ArchiveSha256,
                    StagedDirectory = value.StagedDirectory
                }).ToList();
            if (dependencies.Count != 1)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": Capture self-test에는 exact Contracts dependency 하나가 필요합니다.");
            return dependencies;
        }

        private static Uri ValidateRelease(CaptureModuleReleaseManifest release, string expectedProjectHost)
        {
            Uri uri;
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ModuleId, "capture", StringComparison.Ordinal) ||
                !VersionPattern.IsMatch(release.Version ?? "") ||
                !VersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") ||
                release.FileSize <= 0 || release.FileSize > MaximumPackageBytes ||
                !ShaPattern.IsMatch(release.Sha256 ?? "") || !ShaPattern.IsMatch(release.PackageManifestSha256 ?? "") ||
                release.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                release.StateSchemaVersion != 0 ||
                !BundlePattern.IsMatch(release.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeModuleSetHash ?? "") ||
                !VersionPattern.IsMatch(release.ParentPrivateRuntimeVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentPrivateRuntimeSha256 ?? "") ||
                release.ParentPrivateRuntimePointerGeneration < 1 ||
                !String.Equals(release.PrimaryArtifact, "KINOJO.Meter.Capture.dll", StringComparison.Ordinal) ||
                !String.Equals(release.FileName, "KinojoCapture_" + release.Version + "_x64.zip", StringComparison.Ordinal) ||
                !String.Equals(release.PackagePath, "modules/capture/" + release.Version + "/" + release.FileName, StringComparison.Ordinal) ||
                !String.Equals(release.PackageId, release.Channel + ":capture:" + release.Version + ":" + release.Sha256.Substring(0, 16), StringComparison.Ordinal) ||
                !String.Equals(release.IntegrityMode, ModulePackageVerifier.IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(release.SigningKeyId) || !IsRsa3072Signature(release.ManifestSignature) ||
                release.PointerGeneration < 1 || release.ExpiresAt <= DateTimeOffset.UtcNow ||
                release.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/modules/capture/" + release.Channel + "/" + release.Version + "/" + release.FileName ||
                !HasSignedToken(uri))
                throw new InvalidOperationException("Capture Engine release 계약이 올바르지 않습니다.");
            return uri;
        }

        private static bool IsRsa3072Signature(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            try
            {
                return Convert.FromBase64String(value).Length == 384;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void VerifyManifestIdentity(string packageFile, CaptureModuleReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value => String.Equals(value.FullName, ModulePackageVerifier.ManifestPath, StringComparison.Ordinal));
                if (entry == null) throw new InvalidOperationException("Capture Engine Package Manifest가 없습니다.");
                ModulePackageManifest manifest;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Integrity == null ||
                    !String.Equals(manifest.Integrity.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal) ||
                    !String.Equals(manifest.Integrity.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException("Capture Engine Package Manifest 서명 identity가 Server release와 다릅니다.");
            }
        }

        private static void RejectVersionConflict(ActiveCaptureModuleState current, CaptureModuleReleaseManifest release)
        {
            if (current == null || release == null) return;
            if (String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                !String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 Capture Engine version의 다른 SHA는 활성화할 수 없습니다.");
            if (CompareVersions(current.ModuleVersion, release.Version) > 0)
                throw new InvalidOperationException("CAPTURE_DOWNGRADE_BLOCKED: Capture Engine downgrade는 허용되지 않습니다.");
        }

        private static bool SameRelease(ActiveCaptureModuleState current, CaptureModuleReleaseManifest release)
        {
            return current != null &&
                String.Equals(current.Channel, release.Channel, StringComparison.Ordinal) &&
                String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal) &&
                String.Equals(current.PackageManifestSha256, release.PackageManifestSha256, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal) &&
                String.Equals(current.ParentPrivateRuntimeVersion, release.ParentPrivateRuntimeVersion, StringComparison.Ordinal) &&
                String.Equals(current.ParentPrivateRuntimeSha256, release.ParentPrivateRuntimeSha256, StringComparison.Ordinal) &&
                current.ParentPrivateRuntimePointerGeneration == release.ParentPrivateRuntimePointerGeneration &&
                current.PointerGeneration == release.PointerGeneration;
        }

        private void ValidateActiveStateShape(ActiveCaptureModuleState state)
        {
            if (state == null || state.SchemaVersion != 1 ||
                !String.Equals(state.ModuleId, "capture", StringComparison.Ordinal) ||
                (state.Channel != "stable" && state.Channel != "staging") ||
                !VersionPattern.IsMatch(state.ModuleVersion ?? "") ||
                !ShaPattern.IsMatch(state.PackageSha256 ?? "") ||
                !ShaPattern.IsMatch(state.PackageManifestSha256 ?? "") ||
                !ShaPattern.IsMatch(state.SelfTestReceiptSha256 ?? "") ||
                !BundlePattern.IsMatch(state.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(state.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(state.RuntimeModuleSetHash ?? "") ||
                !VersionPattern.IsMatch(state.ParentPrivateRuntimeVersion ?? "") ||
                !ShaPattern.IsMatch(state.ParentPrivateRuntimeSha256 ?? "") ||
                state.ParentPrivateRuntimePointerGeneration < 1 ||
                state.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                state.StateSchemaVersion != 0 || state.PointerGeneration < 1 ||
                !String.Equals(state.PrimaryArtifact, "KINOJO.Meter.Capture.dll", StringComparison.Ordinal))
                throw new InvalidOperationException("Capture Engine active state 기본 계약이 올바르지 않습니다.");
            var expectedPackagePrefix = "modules/capture/" + state.ModuleVersion + "/";
            if (String.IsNullOrWhiteSpace(state.PackagePath) ||
                !state.PackagePath.StartsWith(expectedPackagePrefix, StringComparison.Ordinal) ||
                !state.PackagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Capture Engine active state packagePath가 올바르지 않습니다.");
        }

        private void WriteActiveState(ActiveCaptureModuleState state)
        {
            var temporary = _activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(_activeFile)) File.Replace(temporary, _activeFile, null);
            else File.Move(temporary, _activeFile);
        }

        private string StageDirectory(string version, string sha256)
        {
            return Path.GetFullPath(Path.Combine(_stagingRoot, "capture", version, sha256));
        }

        private static FileStream ExclusiveFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static bool HasSignedToken(Uri uri)
        {
            return uri.Query.TrimStart('?').Split('&').Any(value =>
            {
                var parts = value.Split(new[] { '=' }, 2);
                return parts.Length == 2 && String.Equals(Uri.UnescapeDataString(parts[0]), "token", StringComparison.Ordinal) &&
                    !String.IsNullOrWhiteSpace(Uri.UnescapeDataString(parts[1]));
            });
        }

        private static int CompareVersions(string left, string right)
        {
            var a = (left ?? "").Split('.').Select(Int32.Parse).ToArray();
            var b = (right ?? "").Split('.').Select(Int32.Parse).ToArray();
            for (var index = 0; index < 3; index++)
            {
                var compared = a[index].CompareTo(b[index]);
                if (compared != 0) return compared;
            }
            return 0;
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(path);
            if (!target.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Capture Engine 경로가 modules 루트 밖으로 벗어났습니다.");
        }

        public void Dispose()
        {
            _cache.Dispose();
        }
    }

    internal static class CaptureModuleUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(CaptureModuleUpdater updater)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            var state = updater.ReadAuthorizationState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "moduleId", "capture" },
                { "version", state.ModuleVersion },
                { "sha256", state.PackageSha256 },
                { "runtimeBundleRevision", state.RuntimeBundleRevision },
                { "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 },
                { "runtimeModuleSetHash", state.RuntimeModuleSetHash },
                { "parentPrivateRuntimeVersion", state.ParentPrivateRuntimeVersion },
                { "parentPrivateRuntimeSha256", state.ParentPrivateRuntimeSha256 },
                { "parentPrivateRuntimePointerGeneration", state.ParentPrivateRuntimePointerGeneration }
            };
        }

        public static async Task<CaptureModuleInstallResult> ApplyAsync(
            CaptureModuleUpdater updater,
            CaptureModuleUpdateAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "Capture Engine 업데이트 승인을 받지 못했습니다." : authorization.Message);
            if (authorization.Release == null) return null;
            return await updater.EnsureInstalledAsync(authorization.Release, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
