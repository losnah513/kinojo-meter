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
    internal sealed class SyncModuleUpdater : IDisposable
    {
        internal const string VersionShaConflictCode = "SYNC_VERSION_SHA_CONFLICT";
        internal const string RuntimeBundleRequiredCode = "SYNC_RUNTIME_BUNDLE_REQUIRED";
        internal const string RuntimeBundleChangedCode = "SYNC_RUNTIME_BUNDLE_CHANGED";
        internal const string PrivateRuntimeRequiredCode = "SYNC_PRIVATE_RUNTIME_REQUIRED";
        internal const string PrivateRuntimeChangedCode = "SYNC_PRIVATE_RUNTIME_CHANGED";
        internal const string CaptureRequiredCode = "SYNC_CAPTURE_REQUIRED";
        internal const string CaptureChangedCode = "SYNC_CAPTURE_CHANGED";
        internal const string ProtocolRequiredCode = "SYNC_PROTOCOL_REQUIRED";
        internal const string ProtocolChangedCode = "SYNC_PROTOCOL_CHANGED";
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
        private readonly string _protocolLock;
        private readonly string _syncLock;
        private readonly Func<ActiveModuleBundleState> _readActiveBundle;
        private readonly Func<ActivePrivateRuntimeState> _readActivePrivateRuntime;
        private readonly Func<ActiveCaptureModuleState> _readActiveCapture;
        private readonly Func<ActiveProtocolModuleState> _readActiveProtocol;

        public SyncModuleUpdater()
            : this(
                new ModulePackageDownloadCache(),
                LauncherPaths.ModuleRoot,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                LauncherPaths.ModuleActiveSyncFile,
                LauncherPaths.ModuleActivationLockFile,
                LauncherPaths.ModulePrivateRuntimeUpdateLockFile,
                LauncherPaths.ModuleCaptureUpdateLockFile,
                LauncherPaths.ModuleProtocolUpdateLockFile,
                LauncherPaths.ModuleSyncUpdateLockFile,
                ModuleBundleActivator.ReadVerifiedActiveBundle,
                ReadDefaultPrivateRuntime,
                ReadDefaultCapture,
                ReadDefaultProtocol)
        {
        }

        internal SyncModuleUpdater(
            ModulePackageDownloadCache cache,
            string moduleRoot,
            string stagingRoot,
            string selfTestRoot,
            string activeFile,
            string activationLock,
            string privateRuntimeLock,
            string captureLock,
            string protocolLock,
            string syncLock,
            Func<ActiveModuleBundleState> readActiveBundle,
            Func<ActivePrivateRuntimeState> readActivePrivateRuntime,
            Func<ActiveCaptureModuleState> readActiveCapture,
            Func<ActiveProtocolModuleState> readActiveProtocol)
        {
            _cache = cache ?? throw new ArgumentNullException("cache");
            _moduleRoot = Path.GetFullPath(moduleRoot ?? throw new ArgumentNullException("moduleRoot"));
            _stagingRoot = Path.GetFullPath(stagingRoot ?? throw new ArgumentNullException("stagingRoot"));
            _selfTestRoot = Path.GetFullPath(selfTestRoot ?? throw new ArgumentNullException("selfTestRoot"));
            _activeFile = Path.GetFullPath(activeFile ?? throw new ArgumentNullException("activeFile"));
            _activationLock = Path.GetFullPath(activationLock ?? throw new ArgumentNullException("activationLock"));
            _privateRuntimeLock = Path.GetFullPath(privateRuntimeLock ?? throw new ArgumentNullException("privateRuntimeLock"));
            _captureLock = Path.GetFullPath(captureLock ?? throw new ArgumentNullException("captureLock"));
            _protocolLock = Path.GetFullPath(protocolLock ?? throw new ArgumentNullException("protocolLock"));
            _syncLock = Path.GetFullPath(syncLock ?? throw new ArgumentNullException("syncLock"));
            _readActiveBundle = readActiveBundle ?? throw new ArgumentNullException("readActiveBundle");
            _readActivePrivateRuntime = readActivePrivateRuntime ?? throw new ArgumentNullException("readActivePrivateRuntime");
            _readActiveCapture = readActiveCapture ?? throw new ArgumentNullException("readActiveCapture");
            _readActiveProtocol = readActiveProtocol ?? throw new ArgumentNullException("readActiveProtocol");
            EnsureUnderRoot(_moduleRoot, _stagingRoot);
            EnsureUnderRoot(_moduleRoot, _selfTestRoot);
            EnsureUnderRoot(_moduleRoot, _activeFile);
            EnsureUnderRoot(_moduleRoot, _activationLock);
            EnsureUnderRoot(_moduleRoot, _privateRuntimeLock);
            EnsureUnderRoot(_moduleRoot, _captureLock);
            EnsureUnderRoot(_moduleRoot, _protocolLock);
            EnsureUnderRoot(_moduleRoot, _syncLock);
        }

        public ActiveSyncModuleState ReadVerifiedActiveState()
        {
            var state = ReadAuthorizationState();
            if (state == null) return null;

            var bundle = RequireCompatibleBundle(state.Channel, state.ContractSetVersion);
            if (!String.Equals(bundle.BundleRevision, state.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleLockSha256, state.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(bundle.ModuleSetHash, state.RuntimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": Sync Engine이 검증된 runtime Bundle과 현재 Bundle이 다릅니다.");
            RequireCompatiblePrivateRuntime(
                state.Channel,
                state.RuntimeBundleRevision,
                state.RuntimeBundleLockSha256,
                state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion,
                state.ParentPrivateRuntimeSha256,
                state.ParentPrivateRuntimePointerGeneration);
            var capture = RequireCompatibleCapture(
                state.Channel,
                state.RuntimeBundleRevision,
                state.RuntimeBundleLockSha256,
                state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion,
                state.ParentPrivateRuntimeSha256,
                state.ParentPrivateRuntimePointerGeneration,
                state.ParentCaptureVersion,
                state.ParentCaptureSha256,
                state.ParentCapturePointerGeneration);
            var protocol = RequireCompatibleProtocol(
                state.Channel,
                state.RuntimeBundleRevision,
                state.RuntimeBundleLockSha256,
                state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion,
                state.ParentPrivateRuntimeSha256,
                state.ParentPrivateRuntimePointerGeneration,
                state.ParentCaptureVersion,
                state.ParentCaptureSha256,
                state.ParentCapturePointerGeneration,
                state.ParentProtocolVersion,
                state.ParentProtocolSha256,
                state.ParentProtocolPointerGeneration);

            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage))
                throw new InvalidOperationException("Sync Engine staging 경로가 결정 경로와 일치하지 않습니다.");

            var target = new ModuleStagingInstallResult
            {
                ModuleId = "sync",
                ModuleVersion = state.ModuleVersion,
                ArchiveSha256 = state.PackageSha256,
                StagedDirectory = expectedStage,
                InstallReceiptFile = Path.Combine(expectedStage, ModuleStagingInstaller.InstallReceiptName),
                AlreadyStaged = true,
                InstallStatus = ModuleStagingInstaller.StagedStatus
            };
            var selfTest = ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = target, Dependencies = Dependencies(bundle, capture, protocol) },
                _stagingRoot,
                _selfTestRoot);
            if (!String.Equals(Sha256File(selfTest.ReceiptFile), state.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath)), state.PackageManifestSha256, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, state.PrimaryArtifact)))
                throw new InvalidOperationException("Sync Engine active state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        internal ActiveSyncModuleState ReadAuthorizationState()
        {
            if (!File.Exists(_activeFile)) return null;
            ActiveSyncModuleState state;
            try { state = _json.Deserialize<ActiveSyncModuleState>(File.ReadAllText(_activeFile, Encoding.UTF8)); }
            catch (Exception error) { throw new InvalidOperationException("Sync Engine active state를 신뢰할 수 없습니다.", error); }
            ValidateActiveStateShape(state);
            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            var expectedSelfTest = Path.Combine(_selfTestRoot, "sync", state.ModuleVersion, state.PackageSha256, ModuleStagingSelfTest.ReceiptName);
            var manifest = Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage) || !File.Exists(expectedSelfTest) || !File.Exists(manifest) ||
                !String.Equals(Sha256File(expectedSelfTest), state.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(manifest), state.PackageManifestSha256, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, state.PrimaryArtifact)))
                throw new InvalidOperationException("Sync Engine authorization state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        public async Task<SyncModuleInstallResult> EnsureInstalledAsync(
            SyncModuleReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            var uri = ValidateRelease(release, expectedProjectHost);
            RequireCompatiblePrivateRuntime(release);
            RequireCompatibleCapture(release);
            RequireCompatibleProtocol(release);
            Directory.CreateDirectory(_moduleRoot);
            Directory.CreateDirectory(_stagingRoot);
            Directory.CreateDirectory(_selfTestRoot);

            var currentIdentity = ReadAuthorizationState();
            RejectVersionConflict(currentIdentity, release);
            var current = TryReadVerifiedActiveStateForCurrentBundle();
            if (SameRelease(current, release))
                return new SyncModuleInstallResult { Active = current, Previous = currentIdentity, Changed = false, Downloaded = false };

            var download = new ModulePackageDownloadRequest
            {
                ModuleId = "sync",
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
                throw new InvalidOperationException("Sync Engine cache 크기가 Server release와 일치하지 않습니다.");

            var verificationRequest = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = "sync",
                ModuleVersion = release.Version,
                BundlePackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256,
                ContractSetVersion = release.ContractSetVersion,
                StateSchemaVersion = release.StateSchemaVersion
            };
            var verified = ModulePackageVerifier.Verify(verificationRequest);
            if (!String.Equals(verified.ManifestSha256, release.PackageManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(verified.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("Sync Engine Package Manifest가 Server release와 일치하지 않습니다.");
            VerifyManifestIdentity(cached.PackageFile, release);

            using (var activationGate = ExclusiveFile(_activationLock))
            using (var privateRuntimeGate = ExclusiveFile(_privateRuntimeLock))
            using (var captureGate = ExclusiveFile(_captureLock))
            using (var protocolGate = ExclusiveFile(_protocolLock))
            using (var syncGate = ExclusiveFile(_syncLock))
            {
                var latestIdentity = ReadAuthorizationState();
                RejectVersionConflict(latestIdentity, release);
                var latest = TryReadVerifiedActiveStateForCurrentBundle();
                if (SameRelease(latest, release))
                    return new SyncModuleInstallResult
                    {
                        Active = latest,
                        Previous = latestIdentity,
                        Changed = false,
                        Downloaded = !cached.CacheHit
                    };
                var bundle = RequireCompatibleBundle(release.Channel, release.ContractSetVersion);
                var privateRuntime = RequireCompatiblePrivateRuntime(release);
                var capture = RequireCompatibleCapture(release);
                var protocol = RequireCompatibleProtocol(release);
                var staged = ModuleStagingInstaller.Stage(
                    new ModuleStagingInstallRequest { VerificationRequest = verificationRequest });
                var selfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = staged, Dependencies = Dependencies(bundle, capture, protocol) },
                    _stagingRoot,
                    _selfTestRoot);
                var active = Activate(release, staged, selfTest, bundle, privateRuntime, capture, protocol);
                WriteActiveState(active);
                return new SyncModuleInstallResult
                {
                    Active = active,
                    Previous = latestIdentity,
                    Changed = true,
                    Downloaded = !cached.CacheHit
                };
            }
        }

        internal static ActiveSyncModuleState ActivateForTest(
            SyncModuleReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            return Activate(release, staged, selfTest, bundle, privateRuntime, capture, protocol);
        }

        internal static void RejectVersionConflictForTest(ActiveSyncModuleState current, SyncModuleReleaseManifest release)
        {
            RejectVersionConflict(current, release);
        }

        internal static void ValidateReleaseForTest(SyncModuleReleaseManifest release, string expectedProjectHost)
        {
            ValidateRelease(release, expectedProjectHost);
        }

        private static ActiveSyncModuleState Activate(
            SyncModuleReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            if (release == null || staged == null || selfTest == null || bundle == null || privateRuntime == null || capture == null || protocol == null ||
                !String.Equals(release.ModuleId, "sync", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleId, "sync", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(staged.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleId, "sync", StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !File.Exists(selfTest.ReceiptFile))
                throw new InvalidOperationException("검증되지 않은 Sync Engine은 활성화할 수 없습니다.");
            RequireReleaseContext(release, bundle, privateRuntime, capture, protocol);
            return new ActiveSyncModuleState
            {
                SchemaVersion = 1,
                Channel = release.Channel,
                ModuleId = "sync",
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
                ParentCaptureVersion = capture.ModuleVersion,
                ParentCaptureSha256 = capture.PackageSha256,
                ParentCapturePointerGeneration = capture.PointerGeneration,
                ParentProtocolVersion = protocol.ModuleVersion,
                ParentProtocolSha256 = protocol.PackageSha256,
                ParentProtocolPointerGeneration = protocol.PointerGeneration,
                PointerGeneration = release.PointerGeneration,
                ActivatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private ActiveModuleBundleState RequireCompatibleBundle(string channel, int contractSetVersion)
        {
            var bundle = _readActiveBundle();
            if (bundle == null)
                throw new InvalidOperationException(RuntimeBundleRequiredCode + ": Sync Engine 활성화 전에 검증된 private runtime Bundle이 필요합니다.");
            if (!String.Equals(bundle.Channel, channel, StringComparison.Ordinal) || bundle.ContractSetVersion != contractSetVersion)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": Sync Engine channel/Contract Set과 runtime Bundle이 다릅니다.");
            return bundle;
        }

        private ActiveSyncModuleState TryReadVerifiedActiveStateForCurrentBundle()
        {
            try { return ReadVerifiedActiveState(); }
            catch (InvalidOperationException error)
            {
                if (error.Message.StartsWith(RuntimeBundleChangedCode + ":", StringComparison.Ordinal) ||
                    error.Message.StartsWith(PrivateRuntimeChangedCode + ":", StringComparison.Ordinal) ||
                    error.Message.StartsWith(CaptureChangedCode + ":", StringComparison.Ordinal) ||
                    error.Message.StartsWith(ProtocolChangedCode + ":", StringComparison.Ordinal)) return null;
                throw;
            }
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(SyncModuleReleaseManifest release)
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
                throw new InvalidOperationException(PrivateRuntimeRequiredCode + ": Sync 개별 활성화 전에 검증된 private runtime whole package가 필요합니다.");
            if (!String.Equals(runtime.Channel, channel, StringComparison.Ordinal) ||
                !String.Equals(runtime.ModuleVersion, parentVersion, StringComparison.Ordinal) ||
                !String.Equals(runtime.PackageSha256, parentSha256, StringComparison.Ordinal) ||
                runtime.PointerGeneration != parentPointerGeneration ||
                !String.Equals(runtime.RuntimeBundleRevision, runtimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeBundleLockSha256, runtimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeModuleSetHash, runtimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(PrivateRuntimeChangedCode + ": Sync release의 parent private runtime 또는 Bundle 고정값이 현재 활성 상태와 다릅니다.");
            return runtime;
        }

        private static void RequireReleaseContext(
            SyncModuleReleaseManifest release,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState privateRuntime,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            if (release == null || bundle == null || privateRuntime == null || capture == null || protocol == null ||
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
                !String.Equals(release.RuntimeModuleSetHash, privateRuntime.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(release.Channel, capture.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ParentCaptureVersion, capture.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentCaptureSha256, capture.PackageSha256, StringComparison.Ordinal) ||
                release.ParentCapturePointerGeneration != capture.PointerGeneration ||
                !String.Equals(release.RuntimeBundleRevision, capture.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, capture.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, capture.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeVersion, capture.ParentPrivateRuntimeVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeSha256, capture.ParentPrivateRuntimeSha256, StringComparison.Ordinal) ||
                release.ParentPrivateRuntimePointerGeneration != capture.ParentPrivateRuntimePointerGeneration ||
                !String.Equals(release.Channel, protocol.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ParentProtocolVersion, protocol.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentProtocolSha256, protocol.PackageSha256, StringComparison.Ordinal) ||
                release.ParentProtocolPointerGeneration != protocol.PointerGeneration ||
                !String.Equals(release.RuntimeBundleRevision, protocol.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, protocol.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, protocol.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeVersion, protocol.ParentPrivateRuntimeVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeSha256, protocol.ParentPrivateRuntimeSha256, StringComparison.Ordinal) ||
                release.ParentPrivateRuntimePointerGeneration != protocol.ParentPrivateRuntimePointerGeneration ||
                !String.Equals(release.ParentCaptureVersion, protocol.ParentCaptureVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentCaptureSha256, protocol.ParentCaptureSha256, StringComparison.Ordinal) ||
                release.ParentCapturePointerGeneration != protocol.ParentCapturePointerGeneration)
                throw new InvalidOperationException(ProtocolChangedCode + ": Sync activation context가 exact Protocol/Capture/private runtime/Bundle identity와 일치하지 않습니다.");
        }

        private static ActivePrivateRuntimeState ReadDefaultPrivateRuntime()
        {
            using (var updater = new PrivateRuntimePackageUpdater())
                return updater.ReadVerifiedActiveState();
        }

        private ActiveCaptureModuleState RequireCompatibleCapture(SyncModuleReleaseManifest release)
        {
            if (release == null) throw new ArgumentNullException("release");
            return RequireCompatibleCapture(
                release.Channel,
                release.RuntimeBundleRevision,
                release.RuntimeBundleLockSha256,
                release.RuntimeModuleSetHash,
                release.ParentPrivateRuntimeVersion,
                release.ParentPrivateRuntimeSha256,
                release.ParentPrivateRuntimePointerGeneration,
                release.ParentCaptureVersion,
                release.ParentCaptureSha256,
                release.ParentCapturePointerGeneration);
        }

        private ActiveCaptureModuleState RequireCompatibleCapture(
            string channel,
            string runtimeBundleRevision,
            string runtimeBundleLockSha256,
            string runtimeModuleSetHash,
            string parentRuntimeVersion,
            string parentRuntimeSha256,
            long parentRuntimePointerGeneration,
            string captureVersion,
            string captureSha256,
            long capturePointerGeneration)
        {
            var capture = _readActiveCapture();
            if (capture == null)
                throw new InvalidOperationException(CaptureRequiredCode + ": Sync 개별 활성화 전에 검증된 Capture 개별 패키지가 필요합니다.");
            if (!String.Equals(capture.Channel, channel, StringComparison.Ordinal) ||
                !String.Equals(capture.ModuleVersion, captureVersion, StringComparison.Ordinal) ||
                !String.Equals(capture.PackageSha256, captureSha256, StringComparison.Ordinal) ||
                capture.PointerGeneration != capturePointerGeneration ||
                !String.Equals(capture.RuntimeBundleRevision, runtimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(capture.RuntimeBundleLockSha256, runtimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(capture.RuntimeModuleSetHash, runtimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(capture.ParentPrivateRuntimeVersion, parentRuntimeVersion, StringComparison.Ordinal) ||
                !String.Equals(capture.ParentPrivateRuntimeSha256, parentRuntimeSha256, StringComparison.Ordinal) ||
                capture.ParentPrivateRuntimePointerGeneration != parentRuntimePointerGeneration)
                throw new InvalidOperationException(CaptureChangedCode + ": Sync release의 exact Capture/private runtime/Bundle dependency가 현재 활성 상태와 다릅니다.");
            return capture;
        }

        private static ActiveCaptureModuleState ReadDefaultCapture()
        {
            using (var updater = new CaptureModuleUpdater())
                return updater.ReadVerifiedActiveState();
        }

        private ActiveProtocolModuleState RequireCompatibleProtocol(SyncModuleReleaseManifest release)
        {
            if (release == null) throw new ArgumentNullException("release");
            return RequireCompatibleProtocol(
                release.Channel,
                release.RuntimeBundleRevision,
                release.RuntimeBundleLockSha256,
                release.RuntimeModuleSetHash,
                release.ParentPrivateRuntimeVersion,
                release.ParentPrivateRuntimeSha256,
                release.ParentPrivateRuntimePointerGeneration,
                release.ParentCaptureVersion,
                release.ParentCaptureSha256,
                release.ParentCapturePointerGeneration,
                release.ParentProtocolVersion,
                release.ParentProtocolSha256,
                release.ParentProtocolPointerGeneration);
        }

        private ActiveProtocolModuleState RequireCompatibleProtocol(
            string channel,
            string runtimeBundleRevision,
            string runtimeBundleLockSha256,
            string runtimeModuleSetHash,
            string parentRuntimeVersion,
            string parentRuntimeSha256,
            long parentRuntimePointerGeneration,
            string captureVersion,
            string captureSha256,
            long capturePointerGeneration,
            string protocolVersion,
            string protocolSha256,
            long protocolPointerGeneration)
        {
            var protocol = _readActiveProtocol();
            if (protocol == null)
                throw new InvalidOperationException(ProtocolRequiredCode + ": Sync 개별 활성화 전에 검증된 Protocol 개별 패키지가 필요합니다.");
            if (!String.Equals(protocol.Channel, channel, StringComparison.Ordinal) ||
                !String.Equals(protocol.ModuleVersion, protocolVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.PackageSha256, protocolSha256, StringComparison.Ordinal) ||
                protocol.PointerGeneration != protocolPointerGeneration ||
                !String.Equals(protocol.RuntimeBundleRevision, runtimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(protocol.RuntimeBundleLockSha256, runtimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(protocol.RuntimeModuleSetHash, runtimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentPrivateRuntimeVersion, parentRuntimeVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentPrivateRuntimeSha256, parentRuntimeSha256, StringComparison.Ordinal) ||
                protocol.ParentPrivateRuntimePointerGeneration != parentRuntimePointerGeneration ||
                !String.Equals(protocol.ParentCaptureVersion, captureVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentCaptureSha256, captureSha256, StringComparison.Ordinal) ||
                protocol.ParentCapturePointerGeneration != capturePointerGeneration)
                throw new InvalidOperationException(ProtocolChangedCode + ": Sync release의 exact Protocol/Capture/private runtime/Bundle dependency가 현재 활성 상태와 다릅니다.");
            return protocol;
        }

        private static ActiveProtocolModuleState ReadDefaultProtocol()
        {
            using (var updater = new ProtocolModuleUpdater())
                return updater.ReadVerifiedActiveState();
        }

        private static List<ModuleSelfTestDependency> Dependencies(
            ActiveModuleBundleState bundle,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            if (bundle == null || bundle.Modules == null || capture == null || protocol == null)
                throw new InvalidOperationException(ProtocolRequiredCode);
            var dependencies = bundle.Modules
                .Where(value => value != null &&
                    (String.Equals(value.ModuleId, "contracts", StringComparison.Ordinal) ||
                     String.Equals(value.ModuleId, "combat", StringComparison.Ordinal)))
                .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
                .Select(value => new ModuleSelfTestDependency
                {
                    ModuleId = value.ModuleId,
                    ModuleVersion = value.ModuleVersion,
                    ArchiveSha256 = value.ArchiveSha256,
                    StagedDirectory = value.StagedDirectory
                }).ToList();
            dependencies.Add(new ModuleSelfTestDependency
            {
                ModuleId = "capture",
                ModuleVersion = capture.ModuleVersion,
                ArchiveSha256 = capture.PackageSha256,
                StagedDirectory = capture.StagedDirectory
            });
            dependencies.Add(new ModuleSelfTestDependency
            {
                ModuleId = "protocol",
                ModuleVersion = protocol.ModuleVersion,
                ArchiveSha256 = protocol.PackageSha256,
                StagedDirectory = protocol.StagedDirectory
            });
            dependencies = dependencies.OrderBy(value => value.ModuleId, StringComparer.Ordinal).ToList();
            if (dependencies.Count != 4 ||
                dependencies[0].ModuleId != "capture" || dependencies[1].ModuleId != "combat" ||
                dependencies[2].ModuleId != "contracts" || dependencies[3].ModuleId != "protocol")
                throw new InvalidOperationException(ProtocolChangedCode + ": Sync self-test에는 exact Capture + Combat + Contracts + Protocol dependency가 필요합니다.");
            return dependencies;
        }

        private static Uri ValidateRelease(SyncModuleReleaseManifest release, string expectedProjectHost)
        {
            Uri uri;
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ModuleId, "sync", StringComparison.Ordinal) ||
                !VersionPattern.IsMatch(release.Version ?? "") ||
                !VersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") ||
                release.FileSize <= 0 || release.FileSize > MaximumPackageBytes ||
                !ShaPattern.IsMatch(release.Sha256 ?? "") || !ShaPattern.IsMatch(release.PackageManifestSha256 ?? "") ||
                release.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                release.StateSchemaVersion != 1 ||
                !BundlePattern.IsMatch(release.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeModuleSetHash ?? "") ||
                !VersionPattern.IsMatch(release.ParentPrivateRuntimeVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentPrivateRuntimeSha256 ?? "") ||
                release.ParentPrivateRuntimePointerGeneration < 1 ||
                !VersionPattern.IsMatch(release.ParentCaptureVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentCaptureSha256 ?? "") ||
                release.ParentCapturePointerGeneration < 1 ||
                !VersionPattern.IsMatch(release.ParentProtocolVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentProtocolSha256 ?? "") ||
                release.ParentProtocolPointerGeneration < 1 ||
                !String.Equals(release.PrimaryArtifact, "KINOJO.Meter.Sync.dll", StringComparison.Ordinal) ||
                !String.Equals(release.FileName, "KinojoSync_" + release.Version + "_x64.zip", StringComparison.Ordinal) ||
                !String.Equals(release.PackagePath, "modules/sync/" + release.Version + "/" + release.FileName, StringComparison.Ordinal) ||
                !String.Equals(release.PackageId, release.Channel + ":sync:" + release.Version + ":" + release.Sha256.Substring(0, 16), StringComparison.Ordinal) ||
                !String.Equals(release.IntegrityMode, ModulePackageVerifier.IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(release.SigningKeyId) || !IsRsa3072Signature(release.ManifestSignature) ||
                release.PointerGeneration < 1 || release.ExpiresAt <= DateTimeOffset.UtcNow ||
                release.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/modules/sync/" + release.Channel + "/" + release.Version + "/" + release.FileName ||
                !HasSignedToken(uri))
                throw new InvalidOperationException("Sync Engine release 계약이 올바르지 않습니다.");
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

        private static void VerifyManifestIdentity(string packageFile, SyncModuleReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value => String.Equals(value.FullName, ModulePackageVerifier.ManifestPath, StringComparison.Ordinal));
                if (entry == null) throw new InvalidOperationException("Sync Engine Package Manifest가 없습니다.");
                ModulePackageManifest manifest;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Integrity == null ||
                    !String.Equals(manifest.Integrity.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal) ||
                    !String.Equals(manifest.Integrity.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException("Sync Engine Package Manifest 서명 identity가 Server release와 다릅니다.");
            }
        }

        private static void RejectVersionConflict(ActiveSyncModuleState current, SyncModuleReleaseManifest release)
        {
            if (current == null || release == null) return;
            if (String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                !String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 Sync Engine version의 다른 SHA는 활성화할 수 없습니다.");
            if (CompareVersions(current.ModuleVersion, release.Version) > 0)
                throw new InvalidOperationException("SYNC_DOWNGRADE_BLOCKED: Sync Engine downgrade는 허용되지 않습니다.");
        }

        private static bool SameRelease(ActiveSyncModuleState current, SyncModuleReleaseManifest release)
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
                String.Equals(current.ParentCaptureVersion, release.ParentCaptureVersion, StringComparison.Ordinal) &&
                String.Equals(current.ParentCaptureSha256, release.ParentCaptureSha256, StringComparison.Ordinal) &&
                current.ParentCapturePointerGeneration == release.ParentCapturePointerGeneration &&
                String.Equals(current.ParentProtocolVersion, release.ParentProtocolVersion, StringComparison.Ordinal) &&
                String.Equals(current.ParentProtocolSha256, release.ParentProtocolSha256, StringComparison.Ordinal) &&
                current.ParentProtocolPointerGeneration == release.ParentProtocolPointerGeneration &&
                current.PointerGeneration == release.PointerGeneration;
        }

        private void ValidateActiveStateShape(ActiveSyncModuleState state)
        {
            if (state == null || state.SchemaVersion != 1 ||
                !String.Equals(state.ModuleId, "sync", StringComparison.Ordinal) ||
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
                !VersionPattern.IsMatch(state.ParentCaptureVersion ?? "") ||
                !ShaPattern.IsMatch(state.ParentCaptureSha256 ?? "") ||
                state.ParentCapturePointerGeneration < 1 ||
                !VersionPattern.IsMatch(state.ParentProtocolVersion ?? "") ||
                !ShaPattern.IsMatch(state.ParentProtocolSha256 ?? "") ||
                state.ParentProtocolPointerGeneration < 1 ||
                state.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                state.StateSchemaVersion != 1 || state.PointerGeneration < 1 ||
                !String.Equals(state.PrimaryArtifact, "KINOJO.Meter.Sync.dll", StringComparison.Ordinal))
                throw new InvalidOperationException("Sync Engine active state 기본 계약이 올바르지 않습니다.");
            var expectedPackagePrefix = "modules/sync/" + state.ModuleVersion + "/";
            if (String.IsNullOrWhiteSpace(state.PackagePath) ||
                !state.PackagePath.StartsWith(expectedPackagePrefix, StringComparison.Ordinal) ||
                !state.PackagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Sync Engine active state packagePath가 올바르지 않습니다.");
        }

        private void WriteActiveState(ActiveSyncModuleState state)
        {
            var temporary = _activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(_activeFile)) File.Replace(temporary, _activeFile, null);
            else File.Move(temporary, _activeFile);
        }

        private string StageDirectory(string version, string sha256)
        {
            return Path.GetFullPath(Path.Combine(_stagingRoot, "sync", version, sha256));
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
                throw new InvalidOperationException("Sync Engine 경로가 modules 루트 밖으로 벗어났습니다.");
        }

        public void Dispose()
        {
            _cache.Dispose();
        }
    }

    internal static class SyncModuleUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(SyncModuleUpdater updater)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            var state = updater.ReadAuthorizationState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "moduleId", "sync" },
                { "version", state.ModuleVersion },
                { "sha256", state.PackageSha256 },
                { "runtimeBundleRevision", state.RuntimeBundleRevision },
                { "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 },
                { "runtimeModuleSetHash", state.RuntimeModuleSetHash },
                { "parentPrivateRuntimeVersion", state.ParentPrivateRuntimeVersion },
                { "parentPrivateRuntimeSha256", state.ParentPrivateRuntimeSha256 },
                { "parentPrivateRuntimePointerGeneration", state.ParentPrivateRuntimePointerGeneration },
                { "parentCaptureVersion", state.ParentCaptureVersion },
                { "parentCaptureSha256", state.ParentCaptureSha256 },
                { "parentCapturePointerGeneration", state.ParentCapturePointerGeneration },
                { "parentProtocolVersion", state.ParentProtocolVersion },
                { "parentProtocolSha256", state.ParentProtocolSha256 },
                { "parentProtocolPointerGeneration", state.ParentProtocolPointerGeneration }
            };
        }

        public static async Task<SyncModuleInstallResult> ApplyAsync(
            SyncModuleUpdater updater,
            SyncModuleUpdateAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "Sync Engine 업데이트 승인을 받지 못했습니다." : authorization.Message);
            if (authorization.Release == null) return null;
            return await updater.EnsureInstalledAsync(authorization.Release, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
