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
    internal sealed class CombatEncounterModuleReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string ModuleId { get; set; }
        public string Version { get; set; }
        public string PackageId { get; set; }
        public string PackagePath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string PackageManifestSha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public string PrimaryArtifact { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string IntegrityMode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal sealed class CombatEncounterCompatibilityGroupReleaseManifest
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string CompatibilityGroupId { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public int ContractSetVersion { get; set; }
        public string RuntimeBundleRevision { get; set; }
        public string RuntimeBundleLockSha256 { get; set; }
        public string RuntimeModuleSetHash { get; set; }
        public string ParentPrivateRuntimeVersion { get; set; }
        public string ParentPrivateRuntimeSha256 { get; set; }
        public long ParentPrivateRuntimePointerGeneration { get; set; }
        public string ParentCaptureVersion { get; set; }
        public string ParentCaptureSha256 { get; set; }
        public long ParentCapturePointerGeneration { get; set; }
        public string ParentProtocolVersion { get; set; }
        public string ParentProtocolSha256 { get; set; }
        public long ParentProtocolPointerGeneration { get; set; }
        public CombatEncounterModuleReleaseManifest CombatModule { get; set; }
        public CombatEncounterModuleReleaseManifest EncounterModule { get; set; }
        public long PointerGeneration { get; set; }
        public string ReleaseNote { get; set; }
    }

    internal sealed class CombatEncounterCompatibilityGroupAuthorization
    {
        public bool Authorized { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public CombatEncounterCompatibilityGroupReleaseManifest Release { get; set; }
    }

    internal sealed class ActiveCombatEncounterCompatibilityGroupState
    {
        public int SchemaVersion { get; set; }
        public string Channel { get; set; }
        public string CompatibilityGroupId { get; set; }
        public int ContractSetVersion { get; set; }
        public string RuntimeBundleRevision { get; set; }
        public string RuntimeBundleLockSha256 { get; set; }
        public string RuntimeModuleSetHash { get; set; }
        public string ParentPrivateRuntimeVersion { get; set; }
        public string ParentPrivateRuntimeSha256 { get; set; }
        public long ParentPrivateRuntimePointerGeneration { get; set; }
        public string ParentCaptureVersion { get; set; }
        public string ParentCaptureSha256 { get; set; }
        public long ParentCapturePointerGeneration { get; set; }
        public string ParentProtocolVersion { get; set; }
        public string ParentProtocolSha256 { get; set; }
        public long ParentProtocolPointerGeneration { get; set; }
        public string CombatVersion { get; set; }
        public string CombatPackagePath { get; set; }
        public string CombatPackageSha256 { get; set; }
        public string CombatPackageManifestSha256 { get; set; }
        public string CombatPrimaryArtifact { get; set; }
        public string CombatStagedDirectory { get; set; }
        public string CombatSelfTestReceiptSha256 { get; set; }
        public string EncounterVersion { get; set; }
        public string EncounterPackagePath { get; set; }
        public string EncounterPackageSha256 { get; set; }
        public string EncounterPackageManifestSha256 { get; set; }
        public string EncounterPrimaryArtifact { get; set; }
        public string EncounterStagedDirectory { get; set; }
        public string EncounterSelfTestReceiptSha256 { get; set; }
        public long PointerGeneration { get; set; }
        public string ActivatedAtUtc { get; set; }
    }

    internal sealed class CombatEncounterCompatibilityGroupInstallResult
    {
        public ActiveCombatEncounterCompatibilityGroupState Active { get; set; }
        public ActiveCombatEncounterCompatibilityGroupState Previous { get; set; }
        public bool Changed { get; set; }
        public bool Downloaded { get; set; }
    }

    internal sealed class CombatEncounterCompatibilityGroupUpdater : IDisposable
    {
        internal const string VersionShaConflictCode = "COMBAT_ENCOUNTER_VERSION_SHA_CONFLICT";
        internal const string RuntimeBundleRequiredCode = "COMBAT_ENCOUNTER_RUNTIME_BUNDLE_REQUIRED";
        internal const string RuntimeBundleChangedCode = "COMBAT_ENCOUNTER_RUNTIME_BUNDLE_CHANGED";
        internal const string PrivateRuntimeRequiredCode = "COMBAT_ENCOUNTER_PRIVATE_RUNTIME_REQUIRED";
        internal const string PrivateRuntimeChangedCode = "COMBAT_ENCOUNTER_PRIVATE_RUNTIME_CHANGED";
        internal const string CaptureRequiredCode = "COMBAT_ENCOUNTER_CAPTURE_REQUIRED";
        internal const string CaptureChangedCode = "COMBAT_ENCOUNTER_CAPTURE_CHANGED";
        internal const string ProtocolRequiredCode = "COMBAT_ENCOUNTER_PROTOCOL_REQUIRED";
        internal const string ProtocolChangedCode = "COMBAT_ENCOUNTER_PROTOCOL_CHANGED";
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
        private readonly string _groupLock;
        private readonly Func<ActiveModuleBundleState> _readActiveBundle;
        private readonly Func<ActivePrivateRuntimeState> _readActivePrivateRuntime;
        private readonly Func<ActiveCaptureModuleState> _readActiveCapture;
        private readonly Func<ActiveProtocolModuleState> _readActiveProtocol;

        public CombatEncounterCompatibilityGroupUpdater()
            : this(
                new ModulePackageDownloadCache(),
                LauncherPaths.ModuleRoot,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                LauncherPaths.ModuleActiveCombatEncounterGroupFile,
                LauncherPaths.ModuleActivationLockFile,
                LauncherPaths.ModulePrivateRuntimeUpdateLockFile,
                LauncherPaths.ModuleCaptureUpdateLockFile,
                LauncherPaths.ModuleProtocolUpdateLockFile,
                LauncherPaths.ModuleSyncUpdateLockFile,
                LauncherPaths.ModuleCombatEncounterUpdateLockFile,
                ModuleBundleActivator.ReadVerifiedActiveBundle,
                ReadDefaultPrivateRuntime,
                ReadDefaultCapture,
                ReadDefaultProtocol)
        {
        }

        internal CombatEncounterCompatibilityGroupUpdater(
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
            string groupLock,
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
            _groupLock = Path.GetFullPath(groupLock ?? throw new ArgumentNullException("groupLock"));
            _readActiveBundle = readActiveBundle ?? throw new ArgumentNullException("readActiveBundle");
            _readActivePrivateRuntime = readActivePrivateRuntime ?? throw new ArgumentNullException("readActivePrivateRuntime");
            _readActiveCapture = readActiveCapture ?? throw new ArgumentNullException("readActiveCapture");
            _readActiveProtocol = readActiveProtocol ?? throw new ArgumentNullException("readActiveProtocol");
            foreach (var path in new[] { _stagingRoot, _selfTestRoot, _activeFile, _activationLock, _privateRuntimeLock, _captureLock, _protocolLock, _syncLock, _groupLock })
                EnsureUnderRoot(_moduleRoot, path);
        }

        public ActiveCombatEncounterCompatibilityGroupState ReadVerifiedActiveState()
        {
            var state = ReadAuthorizationState();
            if (state == null) return null;
            var bundle = RequireCompatibleBundle(state.Channel, state.ContractSetVersion);
            var runtime = RequireCompatiblePrivateRuntime(state);
            var capture = RequireCompatibleCapture(state);
            var protocol = RequireCompatibleProtocol(state);
            VerifyStateAgainstContext(state, bundle, runtime, capture, protocol);
            VerifyInstalledModule(state.CombatVersion, state.CombatPackageSha256, state.CombatPackageManifestSha256,
                state.CombatPrimaryArtifact, state.CombatStagedDirectory, state.CombatSelfTestReceiptSha256);
            VerifyInstalledModule(state.EncounterVersion, state.EncounterPackageSha256, state.EncounterPackageManifestSha256,
                state.EncounterPrimaryArtifact, state.EncounterStagedDirectory, state.EncounterSelfTestReceiptSha256);
            return state;
        }

        internal ActiveCombatEncounterCompatibilityGroupState ReadAuthorizationState()
        {
            if (!File.Exists(_activeFile)) return null;
            ActiveCombatEncounterCompatibilityGroupState state;
            try { state = _json.Deserialize<ActiveCombatEncounterCompatibilityGroupState>(File.ReadAllText(_activeFile, Encoding.UTF8)); }
            catch (Exception error) { throw new InvalidOperationException("Combat·Encounter 호환 그룹 active state를 신뢰할 수 없습니다.", error); }
            ValidateActiveStateShape(state);
            return state;
        }

        public async Task<CombatEncounterCompatibilityGroupInstallResult> EnsureInstalledAsync(
            CombatEncounterCompatibilityGroupReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            ValidateRelease(release, expectedProjectHost);
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
                return new CombatEncounterCompatibilityGroupInstallResult { Active = current, Previous = currentIdentity, Changed = false, Downloaded = false };

            var combatPrepared = await DownloadAndVerifyAsync(release.CombatModule, expectedProjectHost, cancellationToken).ConfigureAwait(false);
            var encounterPrepared = await DownloadAndVerifyAsync(release.EncounterModule, expectedProjectHost, cancellationToken).ConfigureAwait(false);

            using (var activationGate = ExclusiveFile(_activationLock))
            using (var runtimeGate = ExclusiveFile(_privateRuntimeLock))
            using (var captureGate = ExclusiveFile(_captureLock))
            using (var protocolGate = ExclusiveFile(_protocolLock))
            using (var syncGate = ExclusiveFile(_syncLock))
            using (var groupGate = ExclusiveFile(_groupLock))
            {
                var latestIdentity = ReadAuthorizationState();
                RejectVersionConflict(latestIdentity, release);
                var latest = TryReadVerifiedActiveStateForCurrentBundle();
                if (SameRelease(latest, release))
                    return new CombatEncounterCompatibilityGroupInstallResult
                    {
                        Active = latest,
                        Previous = latestIdentity,
                        Changed = false,
                        Downloaded = !combatPrepared.CacheHit || !encounterPrepared.CacheHit
                    };

                var bundle = RequireCompatibleBundle(release.Channel, release.ContractSetVersion);
                var runtime = RequireCompatiblePrivateRuntime(release);
                var capture = RequireCompatibleCapture(release);
                var protocol = RequireCompatibleProtocol(release);
                var combatStaged = ModuleStagingInstaller.Stage(new ModuleStagingInstallRequest { VerificationRequest = combatPrepared.Verification });
                var combatSelfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = combatStaged, Dependencies = CombatDependencies(bundle, protocol) },
                    _stagingRoot, _selfTestRoot);
                var encounterStaged = ModuleStagingInstaller.Stage(new ModuleStagingInstallRequest { VerificationRequest = encounterPrepared.Verification });
                var encounterSelfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = encounterStaged, Dependencies = EncounterDependencies(bundle) },
                    _stagingRoot, _selfTestRoot);
                var active = Activate(release, combatStaged, combatSelfTest, encounterStaged, encounterSelfTest, bundle, runtime, capture, protocol);
                WriteActiveState(active);
                return new CombatEncounterCompatibilityGroupInstallResult
                {
                    Active = active,
                    Previous = latestIdentity,
                    Changed = true,
                    Downloaded = !combatPrepared.CacheHit || !encounterPrepared.CacheHit
                };
            }
        }

        internal static ActiveCombatEncounterCompatibilityGroupState ActivateForTest(
            CombatEncounterCompatibilityGroupReleaseManifest release,
            ModuleStagingInstallResult combatStaged,
            ModuleSelfTestResult combatSelfTest,
            ModuleStagingInstallResult encounterStaged,
            ModuleSelfTestResult encounterSelfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState runtime,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            return Activate(release, combatStaged, combatSelfTest, encounterStaged, encounterSelfTest, bundle, runtime, capture, protocol);
        }

        internal static void ValidateReleaseForTest(CombatEncounterCompatibilityGroupReleaseManifest release, string expectedProjectHost)
        {
            ValidateRelease(release, expectedProjectHost);
        }

        internal static void RejectVersionConflictForTest(ActiveCombatEncounterCompatibilityGroupState current, CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            RejectVersionConflict(current, release);
        }

        internal static string CompatibilityGroupIdForTest(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return CompatibilityGroupId(release);
        }

        private sealed class PreparedPackage
        {
            public ModulePackageVerificationRequest Verification { get; set; }
            public bool CacheHit { get; set; }
        }

        private async Task<PreparedPackage> DownloadAndVerifyAsync(
            CombatEncounterModuleReleaseManifest module,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            var uri = ValidateModule(module, LauncherVersion.Channel, expectedProjectHost);
            var cached = await _cache.DownloadAsync(new ModulePackageDownloadRequest
            {
                ModuleId = module.ModuleId,
                ModuleVersion = module.Version,
                PackagePath = module.PackagePath,
                ExpectedSha256 = module.Sha256,
                DownloadUri = uri,
                ExpectedDownloadHost = uri.Host,
                ExpectedDownloadPath = uri.AbsolutePath,
                ExpectedFileSize = module.FileSize
            }, null, cancellationToken).ConfigureAwait(false);
            if (cached.Bytes != module.FileSize)
                throw new InvalidOperationException(module.ModuleId + " cache 크기가 Server release와 일치하지 않습니다.");
            var request = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = module.ModuleId,
                ModuleVersion = module.Version,
                BundlePackagePath = module.PackagePath,
                ExpectedSha256 = module.Sha256,
                ContractSetVersion = module.ContractSetVersion,
                StateSchemaVersion = module.StateSchemaVersion
            };
            var verified = ModulePackageVerifier.Verify(request);
            if (!String.Equals(verified.ManifestSha256, module.PackageManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(verified.SigningKeyId, module.SigningKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException(module.ModuleId + " Package Manifest가 Server release와 일치하지 않습니다.");
            VerifyManifestIdentity(cached.PackageFile, module);
            return new PreparedPackage { Verification = request, CacheHit = cached.CacheHit };
        }

        private static ActiveCombatEncounterCompatibilityGroupState Activate(
            CombatEncounterCompatibilityGroupReleaseManifest release,
            ModuleStagingInstallResult combatStaged,
            ModuleSelfTestResult combatSelfTest,
            ModuleStagingInstallResult encounterStaged,
            ModuleSelfTestResult encounterSelfTest,
            ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState runtime,
            ActiveCaptureModuleState capture,
            ActiveProtocolModuleState protocol)
        {
            ValidateActivationModule("combat", release == null ? null : release.CombatModule, combatStaged, combatSelfTest);
            ValidateActivationModule("encounter", release == null ? null : release.EncounterModule, encounterStaged, encounterSelfTest);
            RequireReleaseContext(release, bundle, runtime, capture, protocol);
            return new ActiveCombatEncounterCompatibilityGroupState
            {
                SchemaVersion = 1,
                Channel = release.Channel,
                CompatibilityGroupId = release.CompatibilityGroupId,
                ContractSetVersion = release.ContractSetVersion,
                RuntimeBundleRevision = release.RuntimeBundleRevision,
                RuntimeBundleLockSha256 = release.RuntimeBundleLockSha256,
                RuntimeModuleSetHash = release.RuntimeModuleSetHash,
                ParentPrivateRuntimeVersion = release.ParentPrivateRuntimeVersion,
                ParentPrivateRuntimeSha256 = release.ParentPrivateRuntimeSha256,
                ParentPrivateRuntimePointerGeneration = release.ParentPrivateRuntimePointerGeneration,
                ParentCaptureVersion = release.ParentCaptureVersion,
                ParentCaptureSha256 = release.ParentCaptureSha256,
                ParentCapturePointerGeneration = release.ParentCapturePointerGeneration,
                ParentProtocolVersion = release.ParentProtocolVersion,
                ParentProtocolSha256 = release.ParentProtocolSha256,
                ParentProtocolPointerGeneration = release.ParentProtocolPointerGeneration,
                CombatVersion = release.CombatModule.Version,
                CombatPackagePath = release.CombatModule.PackagePath,
                CombatPackageSha256 = release.CombatModule.Sha256,
                CombatPackageManifestSha256 = release.CombatModule.PackageManifestSha256,
                CombatPrimaryArtifact = release.CombatModule.PrimaryArtifact,
                CombatStagedDirectory = Path.GetFullPath(combatStaged.StagedDirectory),
                CombatSelfTestReceiptSha256 = Sha256File(combatSelfTest.ReceiptFile),
                EncounterVersion = release.EncounterModule.Version,
                EncounterPackagePath = release.EncounterModule.PackagePath,
                EncounterPackageSha256 = release.EncounterModule.Sha256,
                EncounterPackageManifestSha256 = release.EncounterModule.PackageManifestSha256,
                EncounterPrimaryArtifact = release.EncounterModule.PrimaryArtifact,
                EncounterStagedDirectory = Path.GetFullPath(encounterStaged.StagedDirectory),
                EncounterSelfTestReceiptSha256 = Sha256File(encounterSelfTest.ReceiptFile),
                PointerGeneration = release.PointerGeneration,
                ActivatedAtUtc = DateTimeOffset.UtcNow.ToString("o")
            };
        }

        private static void ValidateActivationModule(string moduleId, CombatEncounterModuleReleaseManifest release, ModuleStagingInstallResult staged, ModuleSelfTestResult selfTest)
        {
            if (release == null || staged == null || selfTest == null ||
                !String.Equals(release.ModuleId, moduleId, StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleId, moduleId, StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(staged.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleId, moduleId, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !File.Exists(selfTest.ReceiptFile))
                throw new InvalidOperationException("검증되지 않은 " + moduleId + " 모듈은 호환 그룹으로 활성화할 수 없습니다.");
        }

        private static void ValidateRelease(CombatEncounterCompatibilityGroupReleaseManifest release, string expectedProjectHost)
        {
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.Ordinal) ||
                !ShaPattern.IsMatch(release.CompatibilityGroupId ?? "") ||
                !VersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") ||
                release.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                !BundlePattern.IsMatch(release.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeModuleSetHash ?? "") ||
                !VersionPattern.IsMatch(release.ParentPrivateRuntimeVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentPrivateRuntimeSha256 ?? "") || release.ParentPrivateRuntimePointerGeneration < 1 ||
                !VersionPattern.IsMatch(release.ParentCaptureVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentCaptureSha256 ?? "") || release.ParentCapturePointerGeneration < 1 ||
                !VersionPattern.IsMatch(release.ParentProtocolVersion ?? "") ||
                !ShaPattern.IsMatch(release.ParentProtocolSha256 ?? "") || release.ParentProtocolPointerGeneration < 1 ||
                release.PointerGeneration < 1)
                throw new InvalidOperationException("Combat·Encounter 호환 그룹 release 계약이 올바르지 않습니다.");
            ValidateModule(release.CombatModule, release.Channel, expectedProjectHost);
            ValidateModule(release.EncounterModule, release.Channel, expectedProjectHost);
            if (release.CombatModule.ContractSetVersion != release.ContractSetVersion ||
                release.EncounterModule.ContractSetVersion != release.ContractSetVersion ||
                !String.Equals(release.CompatibilityGroupId, CompatibilityGroupId(release), StringComparison.Ordinal))
                throw new InvalidOperationException("Combat·Encounter 호환 그룹 identity가 두 모듈과 정확히 일치하지 않습니다.");
        }

        private static Uri ValidateModule(CombatEncounterModuleReleaseManifest module, string channel, string expectedProjectHost)
        {
            Uri uri;
            var expectedName = module == null ? "" : module.ModuleId == "combat" ? "KinojoCombat_" + module.Version + "_x64.zip" : "KinojoEncounter_" + module.Version + "_x64.zip";
            var expectedArtifact = module == null ? "" : module.ModuleId == "combat" ? "KINOJO.Meter.Combat.dll" : "KINOJO.Meter.Encounter.dll";
            if (module == null || module.SchemaVersion != 1 ||
                (module.ModuleId != "combat" && module.ModuleId != "encounter") ||
                !VersionPattern.IsMatch(module.Version ?? "") ||
                module.FileSize <= 0 || module.FileSize > MaximumPackageBytes ||
                !ShaPattern.IsMatch(module.Sha256 ?? "") || !ShaPattern.IsMatch(module.PackageManifestSha256 ?? "") ||
                module.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion || module.StateSchemaVersion != 1 ||
                !String.Equals(module.PrimaryArtifact, expectedArtifact, StringComparison.Ordinal) ||
                !String.Equals(module.FileName, expectedName, StringComparison.Ordinal) ||
                !String.Equals(module.PackagePath, "modules/" + module.ModuleId + "/" + module.Version + "/" + expectedName, StringComparison.Ordinal) ||
                !String.Equals(module.PackageId, channel + ":" + module.ModuleId + ":" + module.Version + ":" + module.Sha256.Substring(0, 16), StringComparison.Ordinal) ||
                !String.Equals(module.IntegrityMode, ModulePackageVerifier.IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(module.SigningKeyId) || !IsRsa3072Signature(module.ManifestSignature) ||
                module.ExpiresAt <= DateTimeOffset.UtcNow || module.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Uri.TryCreate(module.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/modules/" + module.ModuleId + "/" + channel + "/" + module.Version + "/" + expectedName ||
                !HasSignedToken(uri))
                throw new InvalidOperationException((module == null ? "Combat·Encounter" : module.ModuleId) + " package release 계약이 올바르지 않습니다.");
            return uri;
        }

        private static string CompatibilityGroupId(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            if (release == null || release.CombatModule == null || release.EncounterModule == null) return "";
            return CompatibilityGroupId(release.Channel, release.CombatModule.Version, release.CombatModule.Sha256,
                release.EncounterModule.Version, release.EncounterModule.Sha256, release.ContractSetVersion);
        }

        private static string CompatibilityGroupId(ActiveCombatEncounterCompatibilityGroupState state)
        {
            if (state == null) return "";
            return CompatibilityGroupId(state.Channel, state.CombatVersion, state.CombatPackageSha256,
                state.EncounterVersion, state.EncounterPackageSha256, state.ContractSetVersion);
        }

        private static string CompatibilityGroupId(string channel, string combatVersion, string combatSha,
            string encounterVersion, string encounterSha, int contractSetVersion)
        {
            var canonical = String.Join("|", new[]
            {
                channel ?? "", combatVersion ?? "", combatSha ?? "", encounterVersion ?? "", encounterSha ?? "",
                contractSetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)).Select(value => value.ToString("x2")));
        }

        private ActiveModuleBundleState RequireCompatibleBundle(string channel, int contractSetVersion)
        {
            var bundle = _readActiveBundle();
            if (bundle == null) throw new InvalidOperationException(RuntimeBundleRequiredCode + ": 활성 Bundle이 필요합니다.");
            if (!String.Equals(bundle.Channel, channel, StringComparison.Ordinal) || bundle.ContractSetVersion != contractSetVersion)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": 호환 그룹 release와 활성 Bundle이 다릅니다.");
            return bundle;
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return RequireCompatiblePrivateRuntime(release.Channel, release.RuntimeBundleRevision, release.RuntimeBundleLockSha256,
                release.RuntimeModuleSetHash, release.ParentPrivateRuntimeVersion, release.ParentPrivateRuntimeSha256,
                release.ParentPrivateRuntimePointerGeneration);
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(ActiveCombatEncounterCompatibilityGroupState state)
        {
            return RequireCompatiblePrivateRuntime(state.Channel, state.RuntimeBundleRevision, state.RuntimeBundleLockSha256,
                state.RuntimeModuleSetHash, state.ParentPrivateRuntimeVersion, state.ParentPrivateRuntimeSha256,
                state.ParentPrivateRuntimePointerGeneration);
        }

        private ActivePrivateRuntimeState RequireCompatiblePrivateRuntime(string channel, string bundleRevision, string bundleLockSha, string moduleSetHash, string version, string sha, long generation)
        {
            var runtime = _readActivePrivateRuntime();
            if (runtime == null) throw new InvalidOperationException(PrivateRuntimeRequiredCode + ": private runtime이 필요합니다.");
            if (!String.Equals(runtime.Channel, channel, StringComparison.Ordinal) || !String.Equals(runtime.ModuleVersion, version, StringComparison.Ordinal) ||
                !String.Equals(runtime.PackageSha256, sha, StringComparison.Ordinal) || runtime.PointerGeneration != generation ||
                !String.Equals(runtime.RuntimeBundleRevision, bundleRevision, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeBundleLockSha256, bundleLockSha, StringComparison.Ordinal) ||
                !String.Equals(runtime.RuntimeModuleSetHash, moduleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(PrivateRuntimeChangedCode + ": exact private runtime/Bundle identity가 다릅니다.");
            return runtime;
        }

        private ActiveCaptureModuleState RequireCompatibleCapture(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return RequireCompatibleCapture(release.Channel, release.RuntimeBundleRevision, release.RuntimeBundleLockSha256, release.RuntimeModuleSetHash,
                release.ParentPrivateRuntimeVersion, release.ParentPrivateRuntimeSha256, release.ParentPrivateRuntimePointerGeneration,
                release.ParentCaptureVersion, release.ParentCaptureSha256, release.ParentCapturePointerGeneration);
        }

        private ActiveCaptureModuleState RequireCompatibleCapture(ActiveCombatEncounterCompatibilityGroupState state)
        {
            return RequireCompatibleCapture(state.Channel, state.RuntimeBundleRevision, state.RuntimeBundleLockSha256, state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion, state.ParentPrivateRuntimeSha256, state.ParentPrivateRuntimePointerGeneration,
                state.ParentCaptureVersion, state.ParentCaptureSha256, state.ParentCapturePointerGeneration);
        }

        private ActiveCaptureModuleState RequireCompatibleCapture(string channel, string bundleRevision, string bundleLockSha, string moduleSetHash,
            string runtimeVersion, string runtimeSha, long runtimeGeneration, string version, string sha, long generation)
        {
            var capture = _readActiveCapture();
            if (capture == null) throw new InvalidOperationException(CaptureRequiredCode + ": active Capture가 필요합니다.");
            if (!String.Equals(capture.Channel, channel, StringComparison.Ordinal) || !String.Equals(capture.ModuleVersion, version, StringComparison.Ordinal) ||
                !String.Equals(capture.PackageSha256, sha, StringComparison.Ordinal) || capture.PointerGeneration != generation ||
                !String.Equals(capture.RuntimeBundleRevision, bundleRevision, StringComparison.Ordinal) ||
                !String.Equals(capture.RuntimeBundleLockSha256, bundleLockSha, StringComparison.Ordinal) ||
                !String.Equals(capture.RuntimeModuleSetHash, moduleSetHash, StringComparison.Ordinal) ||
                !String.Equals(capture.ParentPrivateRuntimeVersion, runtimeVersion, StringComparison.Ordinal) ||
                !String.Equals(capture.ParentPrivateRuntimeSha256, runtimeSha, StringComparison.Ordinal) ||
                capture.ParentPrivateRuntimePointerGeneration != runtimeGeneration)
                throw new InvalidOperationException(CaptureChangedCode + ": exact Capture/private runtime/Bundle identity가 다릅니다.");
            return capture;
        }

        private ActiveProtocolModuleState RequireCompatibleProtocol(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return RequireCompatibleProtocol(release.Channel, release.RuntimeBundleRevision, release.RuntimeBundleLockSha256, release.RuntimeModuleSetHash,
                release.ParentPrivateRuntimeVersion, release.ParentPrivateRuntimeSha256, release.ParentPrivateRuntimePointerGeneration,
                release.ParentCaptureVersion, release.ParentCaptureSha256, release.ParentCapturePointerGeneration,
                release.ParentProtocolVersion, release.ParentProtocolSha256, release.ParentProtocolPointerGeneration);
        }

        private ActiveProtocolModuleState RequireCompatibleProtocol(ActiveCombatEncounterCompatibilityGroupState state)
        {
            return RequireCompatibleProtocol(state.Channel, state.RuntimeBundleRevision, state.RuntimeBundleLockSha256, state.RuntimeModuleSetHash,
                state.ParentPrivateRuntimeVersion, state.ParentPrivateRuntimeSha256, state.ParentPrivateRuntimePointerGeneration,
                state.ParentCaptureVersion, state.ParentCaptureSha256, state.ParentCapturePointerGeneration,
                state.ParentProtocolVersion, state.ParentProtocolSha256, state.ParentProtocolPointerGeneration);
        }

        private ActiveProtocolModuleState RequireCompatibleProtocol(string channel, string bundleRevision, string bundleLockSha, string moduleSetHash,
            string runtimeVersion, string runtimeSha, long runtimeGeneration, string captureVersion, string captureSha, long captureGeneration,
            string version, string sha, long generation)
        {
            var protocol = _readActiveProtocol();
            if (protocol == null) throw new InvalidOperationException(ProtocolRequiredCode + ": active Protocol이 필요합니다.");
            if (!String.Equals(protocol.Channel, channel, StringComparison.Ordinal) || !String.Equals(protocol.ModuleVersion, version, StringComparison.Ordinal) ||
                !String.Equals(protocol.PackageSha256, sha, StringComparison.Ordinal) || protocol.PointerGeneration != generation ||
                !String.Equals(protocol.RuntimeBundleRevision, bundleRevision, StringComparison.Ordinal) ||
                !String.Equals(protocol.RuntimeBundleLockSha256, bundleLockSha, StringComparison.Ordinal) ||
                !String.Equals(protocol.RuntimeModuleSetHash, moduleSetHash, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentPrivateRuntimeVersion, runtimeVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentPrivateRuntimeSha256, runtimeSha, StringComparison.Ordinal) || protocol.ParentPrivateRuntimePointerGeneration != runtimeGeneration ||
                !String.Equals(protocol.ParentCaptureVersion, captureVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.ParentCaptureSha256, captureSha, StringComparison.Ordinal) || protocol.ParentCapturePointerGeneration != captureGeneration)
                throw new InvalidOperationException(ProtocolChangedCode + ": exact Protocol/Capture/private runtime/Bundle identity가 다릅니다.");
            return protocol;
        }

        private static void RequireReleaseContext(CombatEncounterCompatibilityGroupReleaseManifest release, ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState runtime, ActiveCaptureModuleState capture, ActiveProtocolModuleState protocol)
        {
            if (release == null || bundle == null || runtime == null || capture == null || protocol == null ||
                !String.Equals(release.RuntimeBundleRevision, bundle.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, bundle.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, bundle.ModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeVersion, runtime.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentPrivateRuntimeSha256, runtime.PackageSha256, StringComparison.Ordinal) || release.ParentPrivateRuntimePointerGeneration != runtime.PointerGeneration ||
                !String.Equals(release.ParentCaptureVersion, capture.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentCaptureSha256, capture.PackageSha256, StringComparison.Ordinal) || release.ParentCapturePointerGeneration != capture.PointerGeneration ||
                !String.Equals(release.ParentProtocolVersion, protocol.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(release.ParentProtocolSha256, protocol.PackageSha256, StringComparison.Ordinal) || release.ParentProtocolPointerGeneration != protocol.PointerGeneration)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": 호환 그룹 activation context가 exact parent chain과 다릅니다.");
        }

        private static List<ModuleSelfTestDependency> CombatDependencies(ActiveModuleBundleState bundle, ActiveProtocolModuleState protocol)
        {
            var contracts = BundleDependency(bundle, "contracts");
            return new List<ModuleSelfTestDependency>
            {
                contracts,
                new ModuleSelfTestDependency { ModuleId = "protocol", ModuleVersion = protocol.ModuleVersion, ArchiveSha256 = protocol.PackageSha256, StagedDirectory = protocol.StagedDirectory }
            };
        }

        private static List<ModuleSelfTestDependency> EncounterDependencies(ActiveModuleBundleState bundle)
        {
            return new List<ModuleSelfTestDependency> { BundleDependency(bundle, "contracts") };
        }

        private static ModuleSelfTestDependency BundleDependency(ActiveModuleBundleState bundle, string moduleId)
        {
            var matches = bundle == null || bundle.Modules == null ? new List<ActiveModuleBundleEntry>() : bundle.Modules
                .Where(value => value != null && String.Equals(value.ModuleId, moduleId, StringComparison.Ordinal)).ToList();
            if (matches.Count != 1) throw new InvalidOperationException(RuntimeBundleChangedCode + ": exact " + moduleId + " dependency 하나가 필요합니다.");
            var dependency = matches[0];
            return new ModuleSelfTestDependency { ModuleId = dependency.ModuleId, ModuleVersion = dependency.ModuleVersion, ArchiveSha256 = dependency.ArchiveSha256, StagedDirectory = dependency.StagedDirectory };
        }

        private ActiveCombatEncounterCompatibilityGroupState TryReadVerifiedActiveStateForCurrentBundle()
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

        private static void RejectVersionConflict(ActiveCombatEncounterCompatibilityGroupState current, CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            if (current == null || release == null) return;
            if ((String.Equals(current.CombatVersion, release.CombatModule.Version, StringComparison.Ordinal) &&
                 !String.Equals(current.CombatPackageSha256, release.CombatModule.Sha256, StringComparison.Ordinal)) ||
                (String.Equals(current.EncounterVersion, release.EncounterModule.Version, StringComparison.Ordinal) &&
                 !String.Equals(current.EncounterPackageSha256, release.EncounterModule.Sha256, StringComparison.Ordinal)))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 모듈 version의 다른 SHA는 활성화할 수 없습니다.");
            if (CompareVersions(current.CombatVersion, release.CombatModule.Version) > 0 ||
                CompareVersions(current.EncounterVersion, release.EncounterModule.Version) > 0)
                throw new InvalidOperationException("COMBAT_ENCOUNTER_DOWNGRADE_BLOCKED: 호환 그룹 downgrade는 허용되지 않습니다.");
        }

        private static bool SameRelease(ActiveCombatEncounterCompatibilityGroupState current, CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return current != null && release != null &&
                String.Equals(current.CompatibilityGroupId, release.CompatibilityGroupId, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal) &&
                current.ParentPrivateRuntimePointerGeneration == release.ParentPrivateRuntimePointerGeneration &&
                current.ParentCapturePointerGeneration == release.ParentCapturePointerGeneration &&
                current.ParentProtocolPointerGeneration == release.ParentProtocolPointerGeneration &&
                current.PointerGeneration == release.PointerGeneration;
        }

        private void ValidateActiveStateShape(ActiveCombatEncounterCompatibilityGroupState state)
        {
            if (state == null || state.SchemaVersion != 1 || !ShaPattern.IsMatch(state.CompatibilityGroupId ?? "") ||
                (state.Channel != "stable" && state.Channel != "staging") || state.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                !BundlePattern.IsMatch(state.RuntimeBundleRevision ?? "") || !ShaPattern.IsMatch(state.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(state.RuntimeModuleSetHash ?? "") || !VersionPattern.IsMatch(state.ParentPrivateRuntimeVersion ?? "") ||
                !ShaPattern.IsMatch(state.ParentPrivateRuntimeSha256 ?? "") || state.ParentPrivateRuntimePointerGeneration < 1 ||
                !VersionPattern.IsMatch(state.ParentCaptureVersion ?? "") || !ShaPattern.IsMatch(state.ParentCaptureSha256 ?? "") || state.ParentCapturePointerGeneration < 1 ||
                !VersionPattern.IsMatch(state.ParentProtocolVersion ?? "") || !ShaPattern.IsMatch(state.ParentProtocolSha256 ?? "") || state.ParentProtocolPointerGeneration < 1 ||
                !VersionPattern.IsMatch(state.CombatVersion ?? "") || !ShaPattern.IsMatch(state.CombatPackageSha256 ?? "") || !ShaPattern.IsMatch(state.CombatPackageManifestSha256 ?? "") ||
                !String.Equals(state.CombatPrimaryArtifact, "KINOJO.Meter.Combat.dll", StringComparison.Ordinal) ||
                !String.Equals(state.CombatPackagePath, "modules/combat/" + state.CombatVersion + "/KinojoCombat_" + state.CombatVersion + "_x64.zip", StringComparison.Ordinal) ||
                !VersionPattern.IsMatch(state.EncounterVersion ?? "") || !ShaPattern.IsMatch(state.EncounterPackageSha256 ?? "") || !ShaPattern.IsMatch(state.EncounterPackageManifestSha256 ?? "") ||
                !String.Equals(state.EncounterPrimaryArtifact, "KINOJO.Meter.Encounter.dll", StringComparison.Ordinal) ||
                !String.Equals(state.EncounterPackagePath, "modules/encounter/" + state.EncounterVersion + "/KinojoEncounter_" + state.EncounterVersion + "_x64.zip", StringComparison.Ordinal) ||
                !String.Equals(state.CompatibilityGroupId, CompatibilityGroupId(state), StringComparison.Ordinal) || state.PointerGeneration < 1)
                throw new InvalidOperationException("Combat·Encounter 호환 그룹 active state 기본 계약이 올바르지 않습니다.");
        }

        private void VerifyInstalledModule(string version, string sha, string manifestSha, string artifact, string stagedDirectory, string selfTestSha)
        {
            var expectedStage = Path.GetFullPath(Path.Combine(_stagingRoot, artifact.Contains("Combat") ? "combat" : "encounter", version, sha));
            var expectedSelfTest = Path.Combine(_selfTestRoot, artifact.Contains("Combat") ? "combat" : "encounter", version, sha, ModuleStagingSelfTest.ReceiptName);
            if (!String.Equals(expectedStage, Path.GetFullPath(stagedDirectory), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expectedStage) ||
                !File.Exists(expectedSelfTest) || !String.Equals(Sha256File(expectedSelfTest), selfTestSha, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath)), manifestSha, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, artifact)))
                throw new InvalidOperationException("Combat·Encounter 호환 그룹 설치 readback 무결성이 올바르지 않습니다.");
        }

        private static void VerifyStateAgainstContext(ActiveCombatEncounterCompatibilityGroupState state, ActiveModuleBundleState bundle,
            ActivePrivateRuntimeState runtime, ActiveCaptureModuleState capture, ActiveProtocolModuleState protocol)
        {
            if (!String.Equals(state.RuntimeBundleRevision, bundle.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeBundleLockSha256, bundle.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeModuleSetHash, bundle.ModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(state.ParentPrivateRuntimeVersion, runtime.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(state.ParentPrivateRuntimeSha256, runtime.PackageSha256, StringComparison.Ordinal) || state.ParentPrivateRuntimePointerGeneration != runtime.PointerGeneration ||
                !String.Equals(state.ParentCaptureVersion, capture.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(state.ParentCaptureSha256, capture.PackageSha256, StringComparison.Ordinal) || state.ParentCapturePointerGeneration != capture.PointerGeneration ||
                !String.Equals(state.ParentProtocolVersion, protocol.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(state.ParentProtocolSha256, protocol.PackageSha256, StringComparison.Ordinal) || state.ParentProtocolPointerGeneration != protocol.PointerGeneration)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": active 호환 그룹과 exact parent chain이 다릅니다.");
        }

        private void WriteActiveState(ActiveCombatEncounterCompatibilityGroupState state)
        {
            var temporary = _activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(_activeFile)) File.Replace(temporary, _activeFile, null); else File.Move(temporary, _activeFile);
        }

        private static void VerifyManifestIdentity(string packageFile, CombatEncounterModuleReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value => String.Equals(value.FullName, ModulePackageVerifier.ManifestPath, StringComparison.Ordinal));
                if (entry == null) throw new InvalidOperationException(release.ModuleId + " Package Manifest가 없습니다.");
                ModulePackageManifest manifest;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Integrity == null ||
                    !String.Equals(manifest.Integrity.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal) ||
                    !String.Equals(manifest.Integrity.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException(release.ModuleId + " Package Manifest 서명 identity가 Server release와 다릅니다.");
            }
        }

        private static bool IsRsa3072Signature(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            try { return Convert.FromBase64String(value).Length == 384; }
            catch (FormatException) { return false; }
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
            for (var index = 0; index < 3; index++) { var compared = a[index].CompareTo(b[index]); if (compared != 0) return compared; }
            return 0;
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static FileStream ExclusiveFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Combat·Encounter 호환 그룹 경로가 modules 루트 밖으로 벗어났습니다.");
        }

        private static ActivePrivateRuntimeState ReadDefaultPrivateRuntime() { using (var updater = new PrivateRuntimePackageUpdater()) return updater.ReadVerifiedActiveState(); }
        private static ActiveCaptureModuleState ReadDefaultCapture() { using (var updater = new CaptureModuleUpdater()) return updater.ReadVerifiedActiveState(); }
        private static ActiveProtocolModuleState ReadDefaultProtocol() { using (var updater = new ProtocolModuleUpdater()) return updater.ReadVerifiedActiveState(); }
        public void Dispose() { _cache.Dispose(); }
    }

    internal static class CombatEncounterCompatibilityGroupUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(CombatEncounterCompatibilityGroupUpdater updater)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            var state = updater.ReadAuthorizationState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "compatibilityGroupId", state.CompatibilityGroupId },
                { "combatVersion", state.CombatVersion }, { "combatSha256", state.CombatPackageSha256 },
                { "encounterVersion", state.EncounterVersion }, { "encounterSha256", state.EncounterPackageSha256 },
                { "runtimeBundleRevision", state.RuntimeBundleRevision }, { "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 },
                { "runtimeModuleSetHash", state.RuntimeModuleSetHash },
                { "parentPrivateRuntimeVersion", state.ParentPrivateRuntimeVersion }, { "parentPrivateRuntimeSha256", state.ParentPrivateRuntimeSha256 },
                { "parentPrivateRuntimePointerGeneration", state.ParentPrivateRuntimePointerGeneration },
                { "parentCaptureVersion", state.ParentCaptureVersion }, { "parentCaptureSha256", state.ParentCaptureSha256 },
                { "parentCapturePointerGeneration", state.ParentCapturePointerGeneration },
                { "parentProtocolVersion", state.ParentProtocolVersion }, { "parentProtocolSha256", state.ParentProtocolSha256 },
                { "parentProtocolPointerGeneration", state.ParentProtocolPointerGeneration }, { "pointerGeneration", state.PointerGeneration }
            };
        }

        public static async Task<CombatEncounterCompatibilityGroupInstallResult> ApplyAsync(
            CombatEncounterCompatibilityGroupUpdater updater,
            CombatEncounterCompatibilityGroupAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "Combat·Encounter 호환 그룹 업데이트 승인을 받지 못했습니다." : authorization.Message);
            if (authorization.Release == null) return null;
            return await updater.EnsureInstalledAsync(authorization.Release, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
