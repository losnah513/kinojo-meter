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
    internal sealed class CombatEncounterIndividualModuleAuthorization
    {
        public bool Authorized { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string ModuleId { get; set; }
        public long PointerGeneration { get; set; }
        public CombatEncounterCompatibilityGroupReleaseManifest CompatibilityGroup { get; set; }
    }

    internal sealed class ActiveCombatEncounterIndividualModuleState
    {
        public int SchemaVersion { get; set; }
        public string ModuleId { get; set; }
        public string Channel { get; set; }
        public string CompatibilityGroupId { get; set; }
        public string ModuleVersion { get; set; }
        public string PackagePath { get; set; }
        public string PackageSha256 { get; set; }
        public string PackageManifestSha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public string PrimaryArtifact { get; set; }
        public string StagedDirectory { get; set; }
        public string SelfTestReceiptSha256 { get; set; }
        public string CounterpartModuleId { get; set; }
        public string CounterpartVersion { get; set; }
        public string CounterpartSha256 { get; set; }
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
        public long PointerGeneration { get; set; }
        public long BootstrapGroupPointerGeneration { get; set; }
        public string ActivatedAtUtc { get; set; }
    }

    internal sealed class CombatEncounterIndividualModuleInstallResult
    {
        public ActiveCombatEncounterIndividualModuleState Active { get; set; }
        public ActiveCombatEncounterIndividualModuleState Previous { get; set; }
        public ActiveCombatEncounterCompatibilityGroupState CompatibilityGroup { get; set; }
        public bool Changed { get; set; }
        public bool Downloaded { get; set; }
    }

    internal sealed class CombatEncounterIndividualModuleUpdater : IDisposable
    {
        internal const string VersionShaConflictCode = "COMBAT_ENCOUNTER_INDIVIDUAL_VERSION_SHA_CONFLICT";
        internal const string CompatibilityRequiredCode = "COMBAT_ENCOUNTER_COMPATIBILITY_GROUP_REQUIRED";
        internal const string CompatibilityChangedCode = "COMBAT_ENCOUNTER_COMPATIBILITY_GROUP_CHANGED";
        internal const string MultipleModuleChangeCode = "COMBAT_ENCOUNTER_MULTIPLE_MODULE_CHANGE_BLOCKED";
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex BundlePattern = new Regex("^B[0-9]{6,}$", RegexOptions.CultureInvariant);

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly string _moduleId;
        private readonly ModulePackageDownloadCache _cache;
        private readonly string _moduleRoot;
        private readonly string _stagingRoot;
        private readonly string _selfTestRoot;
        private readonly string _activeFile;
        private readonly string _groupActiveFile;
        private readonly string _activationLock;
        private readonly string _privateRuntimeLock;
        private readonly string _captureLock;
        private readonly string _protocolLock;
        private readonly string _syncLock;
        private readonly string _groupLock;
        private readonly string _combatLock;
        private readonly string _encounterLock;
        private readonly Func<ActiveModuleBundleState> _readActiveBundle;
        private readonly Func<ActiveProtocolModuleState> _readActiveProtocol;
        private readonly Func<ActiveCombatEncounterCompatibilityGroupState> _readActiveGroup;

        public CombatEncounterIndividualModuleUpdater(string moduleId)
            : this(
                moduleId,
                new ModulePackageDownloadCache(),
                LauncherPaths.ModuleRoot,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                String.Equals(moduleId, "combat", StringComparison.Ordinal)
                    ? LauncherPaths.ModuleActiveCombatFile
                    : LauncherPaths.ModuleActiveEncounterFile,
                LauncherPaths.ModuleActiveCombatEncounterGroupFile,
                LauncherPaths.ModuleActivationLockFile,
                LauncherPaths.ModulePrivateRuntimeUpdateLockFile,
                LauncherPaths.ModuleCaptureUpdateLockFile,
                LauncherPaths.ModuleProtocolUpdateLockFile,
                LauncherPaths.ModuleSyncUpdateLockFile,
                LauncherPaths.ModuleCombatEncounterUpdateLockFile,
                LauncherPaths.ModuleCombatUpdateLockFile,
                LauncherPaths.ModuleEncounterUpdateLockFile,
                ModuleBundleActivator.ReadVerifiedActiveBundle,
                ReadDefaultProtocol,
                ReadDefaultGroup)
        {
        }

        internal CombatEncounterIndividualModuleUpdater(
            string moduleId,
            ModulePackageDownloadCache cache,
            string moduleRoot,
            string stagingRoot,
            string selfTestRoot,
            string activeFile,
            string groupActiveFile,
            string activationLock,
            string privateRuntimeLock,
            string captureLock,
            string protocolLock,
            string syncLock,
            string groupLock,
            string combatLock,
            string encounterLock,
            Func<ActiveModuleBundleState> readActiveBundle,
            Func<ActiveProtocolModuleState> readActiveProtocol,
            Func<ActiveCombatEncounterCompatibilityGroupState> readActiveGroup)
        {
            RequireModuleId(moduleId);
            _moduleId = moduleId;
            _cache = cache ?? throw new ArgumentNullException("cache");
            _moduleRoot = Path.GetFullPath(moduleRoot ?? throw new ArgumentNullException("moduleRoot"));
            _stagingRoot = Path.GetFullPath(stagingRoot ?? throw new ArgumentNullException("stagingRoot"));
            _selfTestRoot = Path.GetFullPath(selfTestRoot ?? throw new ArgumentNullException("selfTestRoot"));
            _activeFile = Path.GetFullPath(activeFile ?? throw new ArgumentNullException("activeFile"));
            _groupActiveFile = Path.GetFullPath(groupActiveFile ?? throw new ArgumentNullException("groupActiveFile"));
            _activationLock = Path.GetFullPath(activationLock ?? throw new ArgumentNullException("activationLock"));
            _privateRuntimeLock = Path.GetFullPath(privateRuntimeLock ?? throw new ArgumentNullException("privateRuntimeLock"));
            _captureLock = Path.GetFullPath(captureLock ?? throw new ArgumentNullException("captureLock"));
            _protocolLock = Path.GetFullPath(protocolLock ?? throw new ArgumentNullException("protocolLock"));
            _syncLock = Path.GetFullPath(syncLock ?? throw new ArgumentNullException("syncLock"));
            _groupLock = Path.GetFullPath(groupLock ?? throw new ArgumentNullException("groupLock"));
            _combatLock = Path.GetFullPath(combatLock ?? throw new ArgumentNullException("combatLock"));
            _encounterLock = Path.GetFullPath(encounterLock ?? throw new ArgumentNullException("encounterLock"));
            _readActiveBundle = readActiveBundle ?? throw new ArgumentNullException("readActiveBundle");
            _readActiveProtocol = readActiveProtocol ?? throw new ArgumentNullException("readActiveProtocol");
            _readActiveGroup = readActiveGroup ?? throw new ArgumentNullException("readActiveGroup");
            foreach (var path in new[] {
                _stagingRoot, _selfTestRoot, _activeFile, _groupActiveFile, _activationLock,
                _privateRuntimeLock, _captureLock, _protocolLock, _syncLock, _groupLock, _combatLock, _encounterLock })
                EnsureUnderRoot(_moduleRoot, path);
        }

        public ActiveCombatEncounterIndividualModuleState ReadVerifiedActiveState()
        {
            var group = _readActiveGroup();
            if (group == null) return null;
            var state = ReadStoredState();
            if (state == null) state = FromCompatibilityGroup(group, _moduleId);
            ValidateActiveStateShape(state);
            VerifyAgainstGroup(state, group);
            VerifyInstalledModule(state);
            return state;
        }

        internal ActiveCombatEncounterIndividualModuleState ReadAuthorizationState()
        {
            var stored = ReadStoredState();
            if (stored != null) return stored;
            var group = _readActiveGroup();
            return group == null ? null : FromCompatibilityGroup(group, _moduleId);
        }

        public async Task<CombatEncounterIndividualModuleInstallResult> EnsureInstalledAsync(
            CombatEncounterIndividualModuleAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            ValidateAuthorization(authorization, expectedProjectHost);
            Directory.CreateDirectory(_moduleRoot);
            Directory.CreateDirectory(_stagingRoot);
            Directory.CreateDirectory(_selfTestRoot);

            var release = authorization.CompatibilityGroup;
            var target = Module(release, _moduleId);
            var currentGroup = _readActiveGroup();
            RequireTransition(currentGroup, authorization);
            var currentIdentity = ReadAuthorizationState();
            RejectVersionConflict(currentIdentity, target);
            if (SameRelease(currentIdentity, authorization) && SameWitness(currentGroup, release))
                return new CombatEncounterIndividualModuleInstallResult
                {
                    Active = currentIdentity,
                    Previous = currentIdentity,
                    CompatibilityGroup = currentGroup,
                    Changed = false,
                    Downloaded = false
                };

            var prepared = await DownloadAndVerifyAsync(target, expectedProjectHost, cancellationToken).ConfigureAwait(false);
            using (var activationGate = ExclusiveFile(_activationLock))
            using (var runtimeGate = ExclusiveFile(_privateRuntimeLock))
            using (var captureGate = ExclusiveFile(_captureLock))
            using (var protocolGate = ExclusiveFile(_protocolLock))
            using (var syncGate = ExclusiveFile(_syncLock))
            using (var groupGate = ExclusiveFile(_groupLock))
            using (var combatGate = ExclusiveFile(_combatLock))
            using (var encounterGate = ExclusiveFile(_encounterLock))
            {
                var latestGroup = _readActiveGroup();
                RequireTransition(latestGroup, authorization);
                var latestIdentity = ReadStoredState() ?? FromCompatibilityGroup(latestGroup, _moduleId);
                RejectVersionConflict(latestIdentity, target);
                if (SameRelease(latestIdentity, authorization) && SameWitness(latestGroup, release))
                    return new CombatEncounterIndividualModuleInstallResult
                    {
                        Active = latestIdentity,
                        Previous = latestIdentity,
                        CompatibilityGroup = latestGroup,
                        Changed = false,
                        Downloaded = !prepared.CacheHit
                    };

                var bundle = _readActiveBundle();
                var protocol = _readActiveProtocol();
                RequireSelfTestContext(release, bundle, protocol);
                var staged = ModuleStagingInstaller.Stage(new ModuleStagingInstallRequest { VerificationRequest = prepared.Verification });
                var selfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = staged, Dependencies = Dependencies(bundle, protocol, _moduleId) },
                    _stagingRoot,
                    _selfTestRoot);
                var transition = Activate(latestGroup, authorization, staged, selfTest);
                WriteJson(_activeFile, transition.Item1);
                WriteJson(_groupActiveFile, transition.Item2);
                return new CombatEncounterIndividualModuleInstallResult
                {
                    Active = transition.Item1,
                    Previous = latestIdentity,
                    CompatibilityGroup = transition.Item2,
                    Changed = true,
                    Downloaded = !prepared.CacheHit
                };
            }
        }

        internal static Tuple<ActiveCombatEncounterIndividualModuleState, ActiveCombatEncounterCompatibilityGroupState> ActivateForTest(
            ActiveCombatEncounterCompatibilityGroupState currentGroup,
            CombatEncounterIndividualModuleAuthorization authorization,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest)
        {
            return Activate(currentGroup, authorization, staged, selfTest);
        }

        internal static void ValidateAuthorizationForTest(CombatEncounterIndividualModuleAuthorization authorization, string expectedProjectHost)
        {
            ValidateAuthorization(authorization, expectedProjectHost);
        }

        internal static void RequireTransitionForTest(
            ActiveCombatEncounterCompatibilityGroupState currentGroup,
            CombatEncounterIndividualModuleAuthorization authorization)
        {
            RequireTransition(currentGroup, authorization);
        }

        internal static void VerifyAgainstGroupForTest(
            ActiveCombatEncounterIndividualModuleState state,
            ActiveCombatEncounterCompatibilityGroupState group)
        {
            VerifyAgainstGroup(state, group);
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
            var uri = ValidateDownloadModule(module, LauncherVersion.Channel, expectedProjectHost);
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
                throw new InvalidOperationException(module.ModuleId + " cache 크기가 Server individual release와 일치하지 않습니다.");
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
                throw new InvalidOperationException(module.ModuleId + " Package Manifest가 Server individual release와 일치하지 않습니다.");
            VerifyManifestIdentity(cached.PackageFile, module);
            return new PreparedPackage { Verification = request, CacheHit = cached.CacheHit };
        }

        private static Tuple<ActiveCombatEncounterIndividualModuleState, ActiveCombatEncounterCompatibilityGroupState> Activate(
            ActiveCombatEncounterCompatibilityGroupState currentGroup,
            CombatEncounterIndividualModuleAuthorization authorization,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest)
        {
            RequireTransition(currentGroup, authorization);
            var release = authorization.CompatibilityGroup;
            var target = Module(release, authorization.ModuleId);
            if (staged == null || selfTest == null ||
                !String.Equals(staged.ModuleId, authorization.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleVersion, target.Version, StringComparison.Ordinal) ||
                !String.Equals(staged.ArchiveSha256, target.Sha256, StringComparison.Ordinal) ||
                !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleId, authorization.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleVersion, target.Version, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ArchiveSha256, target.Sha256, StringComparison.Ordinal) ||
                !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !File.Exists(selfTest.ReceiptFile))
                throw new InvalidOperationException("검증되지 않은 " + authorization.ModuleId + " 모듈은 개별 활성화할 수 없습니다.");

            var now = DateTimeOffset.UtcNow.ToString("o");
            var counterpartId = Counterpart(authorization.ModuleId);
            var counterpart = Module(release, counterpartId);
            var active = new ActiveCombatEncounterIndividualModuleState
            {
                SchemaVersion = 1,
                ModuleId = authorization.ModuleId,
                Channel = release.Channel,
                CompatibilityGroupId = release.CompatibilityGroupId,
                ModuleVersion = target.Version,
                PackagePath = target.PackagePath,
                PackageSha256 = target.Sha256,
                PackageManifestSha256 = target.PackageManifestSha256,
                ContractSetVersion = target.ContractSetVersion,
                StateSchemaVersion = target.StateSchemaVersion,
                PrimaryArtifact = target.PrimaryArtifact,
                StagedDirectory = Path.GetFullPath(staged.StagedDirectory),
                SelfTestReceiptSha256 = Sha256File(selfTest.ReceiptFile),
                CounterpartModuleId = counterpartId,
                CounterpartVersion = counterpart.Version,
                CounterpartSha256 = counterpart.Sha256,
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
                PointerGeneration = authorization.PointerGeneration,
                BootstrapGroupPointerGeneration = currentGroup.PointerGeneration,
                ActivatedAtUtc = now
            };

            var next = new ActiveCombatEncounterCompatibilityGroupState
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
                CombatVersion = authorization.ModuleId == "combat" ? target.Version : currentGroup.CombatVersion,
                CombatPackagePath = authorization.ModuleId == "combat" ? target.PackagePath : currentGroup.CombatPackagePath,
                CombatPackageSha256 = authorization.ModuleId == "combat" ? target.Sha256 : currentGroup.CombatPackageSha256,
                CombatPackageManifestSha256 = authorization.ModuleId == "combat" ? target.PackageManifestSha256 : currentGroup.CombatPackageManifestSha256,
                CombatPrimaryArtifact = authorization.ModuleId == "combat" ? target.PrimaryArtifact : currentGroup.CombatPrimaryArtifact,
                CombatStagedDirectory = authorization.ModuleId == "combat" ? Path.GetFullPath(staged.StagedDirectory) : currentGroup.CombatStagedDirectory,
                CombatSelfTestReceiptSha256 = authorization.ModuleId == "combat" ? Sha256File(selfTest.ReceiptFile) : currentGroup.CombatSelfTestReceiptSha256,
                EncounterVersion = authorization.ModuleId == "encounter" ? target.Version : currentGroup.EncounterVersion,
                EncounterPackagePath = authorization.ModuleId == "encounter" ? target.PackagePath : currentGroup.EncounterPackagePath,
                EncounterPackageSha256 = authorization.ModuleId == "encounter" ? target.Sha256 : currentGroup.EncounterPackageSha256,
                EncounterPackageManifestSha256 = authorization.ModuleId == "encounter" ? target.PackageManifestSha256 : currentGroup.EncounterPackageManifestSha256,
                EncounterPrimaryArtifact = authorization.ModuleId == "encounter" ? target.PrimaryArtifact : currentGroup.EncounterPrimaryArtifact,
                EncounterStagedDirectory = authorization.ModuleId == "encounter" ? Path.GetFullPath(staged.StagedDirectory) : currentGroup.EncounterStagedDirectory,
                EncounterSelfTestReceiptSha256 = authorization.ModuleId == "encounter" ? Sha256File(selfTest.ReceiptFile) : currentGroup.EncounterSelfTestReceiptSha256,
                PointerGeneration = currentGroup.PointerGeneration,
                CombatPointerGeneration = authorization.ModuleId == "combat" ? authorization.PointerGeneration : currentGroup.CombatPointerGeneration,
                EncounterPointerGeneration = authorization.ModuleId == "encounter" ? authorization.PointerGeneration : currentGroup.EncounterPointerGeneration,
                ActivatedAtUtc = now
            };
            return Tuple.Create(active, next);
        }

        private static void ValidateAuthorization(CombatEncounterIndividualModuleAuthorization authorization, string expectedProjectHost)
        {
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException("Combat·Encounter 개별 업데이트 승인을 받지 못했습니다.");
            RequireModuleId(authorization.ModuleId);
            if (authorization.PointerGeneration < 1)
                throw new InvalidOperationException("Combat·Encounter 개별 pointer generation이 올바르지 않습니다.");
            var release = authorization.CompatibilityGroup;
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
                !ShaPattern.IsMatch(release.ParentProtocolSha256 ?? "") || release.ParentProtocolPointerGeneration < 1)
                throw new InvalidOperationException("Combat·Encounter individual compatibility 계약이 올바르지 않습니다.");
            var target = Module(release, authorization.ModuleId);
            var counterpart = Module(release, Counterpart(authorization.ModuleId));
            ValidateDownloadModule(target, release.Channel, expectedProjectHost);
            ValidateModuleIdentity(counterpart, release.Channel);
            if (target.ContractSetVersion != release.ContractSetVersion ||
                counterpart.ContractSetVersion != release.ContractSetVersion ||
                !String.Equals(release.CompatibilityGroupId, CompatibilityGroupId(release), StringComparison.Ordinal))
                throw new InvalidOperationException("Combat·Encounter individual compatibility identity가 올바르지 않습니다.");
        }

        private static void RequireTransition(
            ActiveCombatEncounterCompatibilityGroupState current,
            CombatEncounterIndividualModuleAuthorization authorization)
        {
            if (current == null) throw new InvalidOperationException(CompatibilityRequiredCode);
            if (authorization == null || authorization.CompatibilityGroup == null)
                throw new InvalidOperationException(CompatibilityRequiredCode);
            var release = authorization.CompatibilityGroup;
            if (!String.Equals(current.Channel, release.Channel, StringComparison.Ordinal) ||
                current.ContractSetVersion != release.ContractSetVersion ||
                !String.Equals(current.RuntimeBundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(current.RuntimeBundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(current.RuntimeModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(current.ParentPrivateRuntimeVersion, release.ParentPrivateRuntimeVersion, StringComparison.Ordinal) ||
                !String.Equals(current.ParentPrivateRuntimeSha256, release.ParentPrivateRuntimeSha256, StringComparison.Ordinal) ||
                current.ParentPrivateRuntimePointerGeneration != release.ParentPrivateRuntimePointerGeneration ||
                !String.Equals(current.ParentCaptureVersion, release.ParentCaptureVersion, StringComparison.Ordinal) ||
                !String.Equals(current.ParentCaptureSha256, release.ParentCaptureSha256, StringComparison.Ordinal) ||
                current.ParentCapturePointerGeneration != release.ParentCapturePointerGeneration ||
                !String.Equals(current.ParentProtocolVersion, release.ParentProtocolVersion, StringComparison.Ordinal) ||
                !String.Equals(current.ParentProtocolSha256, release.ParentProtocolSha256, StringComparison.Ordinal) ||
                current.ParentProtocolPointerGeneration != release.ParentProtocolPointerGeneration)
                throw new InvalidOperationException(CompatibilityChangedCode + ": exact parent/Bundle 계약이 현재 활성 그룹과 다릅니다.");

            var unchanged = Module(release, Counterpart(authorization.ModuleId));
            var counterpartMatches = authorization.ModuleId == "combat"
                ? SameModule(unchanged, current.EncounterVersion, current.EncounterPackagePath, current.EncounterPackageSha256,
                    current.EncounterPackageManifestSha256, current.EncounterPrimaryArtifact)
                : SameModule(unchanged, current.CombatVersion, current.CombatPackagePath, current.CombatPackageSha256,
                    current.CombatPackageManifestSha256, current.CombatPrimaryArtifact);
            if (!counterpartMatches)
                throw new InvalidOperationException(MultipleModuleChangeCode + ": 한 번에 target 모듈 하나만 변경할 수 있습니다.");
        }

        private static bool SameModule(
            CombatEncounterModuleReleaseManifest release,
            string version,
            string packagePath,
            string sha,
            string manifestSha,
            string artifact)
        {
            return release != null &&
                String.Equals(release.Version, version, StringComparison.Ordinal) &&
                String.Equals(release.PackagePath, packagePath, StringComparison.Ordinal) &&
                String.Equals(release.Sha256, sha, StringComparison.Ordinal) &&
                String.Equals(release.PackageManifestSha256, manifestSha, StringComparison.Ordinal) &&
                String.Equals(release.PrimaryArtifact, artifact, StringComparison.Ordinal);
        }

        private static void RequireSelfTestContext(
            CombatEncounterCompatibilityGroupReleaseManifest release,
            ActiveModuleBundleState bundle,
            ActiveProtocolModuleState protocol)
        {
            if (bundle == null || protocol == null ||
                !String.Equals(bundle.Channel, release.Channel, StringComparison.Ordinal) ||
                bundle.ContractSetVersion != release.ContractSetVersion ||
                !String.Equals(bundle.BundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(bundle.ModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                !String.Equals(protocol.ModuleVersion, release.ParentProtocolVersion, StringComparison.Ordinal) ||
                !String.Equals(protocol.PackageSha256, release.ParentProtocolSha256, StringComparison.Ordinal) ||
                protocol.PointerGeneration != release.ParentProtocolPointerGeneration)
                throw new InvalidOperationException(CompatibilityChangedCode + ": self-test parent/Bundle context가 변경됐습니다.");
        }

        private static List<ModuleSelfTestDependency> Dependencies(
            ActiveModuleBundleState bundle,
            ActiveProtocolModuleState protocol,
            string moduleId)
        {
            var dependencies = bundle.Modules
                .Where(value => value != null && String.Equals(value.ModuleId, "contracts", StringComparison.Ordinal))
                .Select(value => new ModuleSelfTestDependency
                {
                    ModuleId = value.ModuleId,
                    ModuleVersion = value.ModuleVersion,
                    ArchiveSha256 = value.ArchiveSha256,
                    StagedDirectory = value.StagedDirectory
                }).ToList();
            if (moduleId == "combat")
                dependencies.Add(new ModuleSelfTestDependency
                {
                    ModuleId = "protocol",
                    ModuleVersion = protocol.ModuleVersion,
                    ArchiveSha256 = protocol.PackageSha256,
                    StagedDirectory = protocol.StagedDirectory
                });
            dependencies = dependencies.OrderBy(value => value.ModuleId, StringComparer.Ordinal).ToList();
            var expected = moduleId == "combat" ? new[] { "contracts", "protocol" } : new[] { "contracts" };
            if (!dependencies.Select(value => value.ModuleId).SequenceEqual(expected))
                throw new InvalidOperationException(CompatibilityChangedCode + ": " + moduleId + " self-test dependency가 올바르지 않습니다.");
            return dependencies;
        }

        private static Uri ValidateDownloadModule(CombatEncounterModuleReleaseManifest module, string channel, string expectedProjectHost)
        {
            ValidateModuleIdentity(module, channel);
            Uri uri;
            if (module.ExpiresAt <= DateTimeOffset.UtcNow ||
                module.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Uri.TryCreate(module.DownloadUrl, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) ||
                !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/modules/" + module.ModuleId + "/" +
                    channel + "/" + module.Version + "/" + module.FileName ||
                !HasSignedToken(uri))
                throw new InvalidOperationException(module.ModuleId + " individual signed download 계약이 올바르지 않습니다.");
            return uri;
        }

        private static void ValidateModuleIdentity(CombatEncounterModuleReleaseManifest module, string channel)
        {
            var moduleId = module == null ? "" : module.ModuleId;
            RequireModuleId(moduleId);
            var expectedArtifact = moduleId == "combat" ? "KINOJO.Meter.Combat.dll" : "KINOJO.Meter.Encounter.dll";
            var expectedName = moduleId == "combat"
                ? "KinojoCombat_" + module.Version + "_x64.zip"
                : "KinojoEncounter_" + module.Version + "_x64.zip";
            if (module.SchemaVersion != 1 ||
                !VersionPattern.IsMatch(module.Version ?? "") ||
                module.FileSize <= 0 || module.FileSize > MaximumPackageBytes ||
                !ShaPattern.IsMatch(module.Sha256 ?? "") ||
                !ShaPattern.IsMatch(module.PackageManifestSha256 ?? "") ||
                module.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                module.StateSchemaVersion != 1 ||
                !String.Equals(module.PrimaryArtifact, expectedArtifact, StringComparison.Ordinal) ||
                !String.Equals(module.FileName, expectedName, StringComparison.Ordinal) ||
                !String.Equals(module.PackagePath, "modules/" + moduleId + "/" + module.Version + "/" + expectedName, StringComparison.Ordinal) ||
                !String.Equals(module.PackageId, channel + ":" + moduleId + ":" + module.Version + ":" + module.Sha256.Substring(0, 16), StringComparison.Ordinal) ||
                !String.Equals(module.IntegrityMode, ModulePackageVerifier.IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(module.SigningKeyId) ||
                !IsRsa3072Signature(module.ManifestSignature))
                throw new InvalidOperationException(moduleId + " individual package identity가 올바르지 않습니다.");
        }

        private static string CompatibilityGroupId(CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return Sha256Text(String.Join("|", new[] {
                release.Channel, release.CombatModule.Version, release.CombatModule.Sha256,
                release.EncounterModule.Version, release.EncounterModule.Sha256,
                release.ContractSetVersion.ToString()
            }));
        }

        private static string Sha256Text(string value)
        {
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2")));
        }

        private ActiveCombatEncounterIndividualModuleState ReadStoredState()
        {
            if (!File.Exists(_activeFile)) return null;
            try
            {
                var state = _json.Deserialize<ActiveCombatEncounterIndividualModuleState>(File.ReadAllText(_activeFile, Encoding.UTF8));
                ValidateActiveStateShape(state);
                return state;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(_moduleId + " individual active state를 신뢰할 수 없습니다.", error);
            }
        }

        private static ActiveCombatEncounterIndividualModuleState FromCompatibilityGroup(
            ActiveCombatEncounterCompatibilityGroupState group,
            string moduleId)
        {
            RequireModuleId(moduleId);
            return new ActiveCombatEncounterIndividualModuleState
            {
                SchemaVersion = 1,
                ModuleId = moduleId,
                Channel = group.Channel,
                CompatibilityGroupId = group.CompatibilityGroupId,
                ModuleVersion = moduleId == "combat" ? group.CombatVersion : group.EncounterVersion,
                PackagePath = moduleId == "combat" ? group.CombatPackagePath : group.EncounterPackagePath,
                PackageSha256 = moduleId == "combat" ? group.CombatPackageSha256 : group.EncounterPackageSha256,
                PackageManifestSha256 = moduleId == "combat" ? group.CombatPackageManifestSha256 : group.EncounterPackageManifestSha256,
                ContractSetVersion = group.ContractSetVersion,
                StateSchemaVersion = 1,
                PrimaryArtifact = moduleId == "combat" ? group.CombatPrimaryArtifact : group.EncounterPrimaryArtifact,
                StagedDirectory = moduleId == "combat" ? group.CombatStagedDirectory : group.EncounterStagedDirectory,
                SelfTestReceiptSha256 = moduleId == "combat" ? group.CombatSelfTestReceiptSha256 : group.EncounterSelfTestReceiptSha256,
                CounterpartModuleId = Counterpart(moduleId),
                CounterpartVersion = moduleId == "combat" ? group.EncounterVersion : group.CombatVersion,
                CounterpartSha256 = moduleId == "combat" ? group.EncounterPackageSha256 : group.CombatPackageSha256,
                RuntimeBundleRevision = group.RuntimeBundleRevision,
                RuntimeBundleLockSha256 = group.RuntimeBundleLockSha256,
                RuntimeModuleSetHash = group.RuntimeModuleSetHash,
                ParentPrivateRuntimeVersion = group.ParentPrivateRuntimeVersion,
                ParentPrivateRuntimeSha256 = group.ParentPrivateRuntimeSha256,
                ParentPrivateRuntimePointerGeneration = group.ParentPrivateRuntimePointerGeneration,
                ParentCaptureVersion = group.ParentCaptureVersion,
                ParentCaptureSha256 = group.ParentCaptureSha256,
                ParentCapturePointerGeneration = group.ParentCapturePointerGeneration,
                ParentProtocolVersion = group.ParentProtocolVersion,
                ParentProtocolSha256 = group.ParentProtocolSha256,
                ParentProtocolPointerGeneration = group.ParentProtocolPointerGeneration,
                PointerGeneration = moduleId == "combat" ? group.CombatPointerGeneration : group.EncounterPointerGeneration,
                BootstrapGroupPointerGeneration = group.PointerGeneration,
                ActivatedAtUtc = group.ActivatedAtUtc
            };
        }

        private static void ValidateActiveStateShape(ActiveCombatEncounterIndividualModuleState state)
        {
            if (state == null) throw new InvalidOperationException("Combat·Encounter individual active state가 비어 있습니다.");
            RequireModuleId(state.ModuleId);
            var expectedArtifact = state.ModuleId == "combat" ? "KINOJO.Meter.Combat.dll" : "KINOJO.Meter.Encounter.dll";
            if (state.SchemaVersion != 1 ||
                (state.Channel != "stable" && state.Channel != "staging") ||
                !ShaPattern.IsMatch(state.CompatibilityGroupId ?? "") ||
                !VersionPattern.IsMatch(state.ModuleVersion ?? "") ||
                !ShaPattern.IsMatch(state.PackageSha256 ?? "") ||
                !ShaPattern.IsMatch(state.PackageManifestSha256 ?? "") ||
                !ShaPattern.IsMatch(state.SelfTestReceiptSha256 ?? "") ||
                state.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                state.StateSchemaVersion != 1 ||
                !String.Equals(state.PrimaryArtifact, expectedArtifact, StringComparison.Ordinal) ||
                !String.Equals(state.CounterpartModuleId, Counterpart(state.ModuleId), StringComparison.Ordinal) ||
                !VersionPattern.IsMatch(state.CounterpartVersion ?? "") ||
                !ShaPattern.IsMatch(state.CounterpartSha256 ?? "") ||
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
                state.BootstrapGroupPointerGeneration < 1)
                throw new InvalidOperationException("Combat·Encounter individual active state 계약이 올바르지 않습니다.");
            if (state.PointerGeneration < 0)
                throw new InvalidOperationException("Combat·Encounter individual pointer generation이 올바르지 않습니다.");
        }

        private static void VerifyAgainstGroup(
            ActiveCombatEncounterIndividualModuleState state,
            ActiveCombatEncounterCompatibilityGroupState group)
        {
            var version = state.ModuleId == "combat" ? group.CombatVersion : group.EncounterVersion;
            var path = state.ModuleId == "combat" ? group.CombatPackagePath : group.EncounterPackagePath;
            var sha = state.ModuleId == "combat" ? group.CombatPackageSha256 : group.EncounterPackageSha256;
            var manifest = state.ModuleId == "combat" ? group.CombatPackageManifestSha256 : group.EncounterPackageManifestSha256;
            var artifact = state.ModuleId == "combat" ? group.CombatPrimaryArtifact : group.EncounterPrimaryArtifact;
            var pointerGeneration = state.ModuleId == "combat" ? group.CombatPointerGeneration : group.EncounterPointerGeneration;
            if (!String.Equals(state.Channel, group.Channel, StringComparison.Ordinal) ||
                !String.Equals(state.ModuleVersion, version, StringComparison.Ordinal) ||
                !String.Equals(state.PackagePath, path, StringComparison.Ordinal) ||
                !String.Equals(state.PackageSha256, sha, StringComparison.Ordinal) ||
                !String.Equals(state.PackageManifestSha256, manifest, StringComparison.Ordinal) ||
                !String.Equals(state.PrimaryArtifact, artifact, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeBundleRevision, group.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeBundleLockSha256, group.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeModuleSetHash, group.RuntimeModuleSetHash, StringComparison.Ordinal) ||
                state.ParentPrivateRuntimePointerGeneration != group.ParentPrivateRuntimePointerGeneration ||
                state.ParentCapturePointerGeneration != group.ParentCapturePointerGeneration ||
                state.ParentProtocolPointerGeneration != group.ParentProtocolPointerGeneration ||
                state.PointerGeneration != pointerGeneration ||
                state.BootstrapGroupPointerGeneration != group.PointerGeneration)
                throw new InvalidOperationException(CompatibilityChangedCode + ": individual pointer와 compatibility witness가 다릅니다.");
        }

        private void VerifyInstalledModule(ActiveCombatEncounterIndividualModuleState state)
        {
            if (String.IsNullOrWhiteSpace(state.StagedDirectory) || !Directory.Exists(state.StagedDirectory) ||
                !File.Exists(Path.Combine(state.StagedDirectory, ModulePackageVerifier.ManifestPath)) ||
                !File.Exists(Path.Combine(state.StagedDirectory, state.PrimaryArtifact)))
                throw new InvalidOperationException(state.ModuleId + " individual staged package를 확인할 수 없습니다.");
            var receipt = Path.Combine(
                _selfTestRoot,
                state.ModuleId,
                state.ModuleVersion,
                state.PackageSha256,
                ModuleStagingSelfTest.ReceiptName);
            if (!File.Exists(receipt) || !String.Equals(Sha256File(receipt), state.SelfTestReceiptSha256, StringComparison.Ordinal))
                throw new InvalidOperationException(state.ModuleId + " individual self-test receipt를 확인할 수 없습니다.");
        }

        private static void RejectVersionConflict(
            ActiveCombatEncounterIndividualModuleState current,
            CombatEncounterModuleReleaseManifest release)
        {
            if (current == null || release == null) return;
            if (String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                !String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 module version의 다른 SHA는 활성화할 수 없습니다.");
            if (CompareVersions(current.ModuleVersion, release.Version) > 0)
                throw new InvalidOperationException("COMBAT_ENCOUNTER_INDIVIDUAL_DOWNGRADE_BLOCKED");
        }

        private static bool SameRelease(
            ActiveCombatEncounterIndividualModuleState current,
            CombatEncounterIndividualModuleAuthorization authorization)
        {
            var release = authorization.CompatibilityGroup;
            var module = Module(release, authorization.ModuleId);
            return current != null &&
                String.Equals(current.ModuleId, authorization.ModuleId, StringComparison.Ordinal) &&
                String.Equals(current.CompatibilityGroupId, release.CompatibilityGroupId, StringComparison.Ordinal) &&
                String.Equals(current.ModuleVersion, module.Version, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, module.Sha256, StringComparison.Ordinal) &&
                String.Equals(current.PackageManifestSha256, module.PackageManifestSha256, StringComparison.Ordinal) &&
                current.PointerGeneration == authorization.PointerGeneration;
        }

        private static bool SameWitness(
            ActiveCombatEncounterCompatibilityGroupState current,
            CombatEncounterCompatibilityGroupReleaseManifest release)
        {
            return current != null && release != null &&
                String.Equals(current.CompatibilityGroupId, release.CompatibilityGroupId, StringComparison.Ordinal);
        }

        private static CombatEncounterModuleReleaseManifest Module(
            CombatEncounterCompatibilityGroupReleaseManifest release,
            string moduleId)
        {
            if (release == null) return null;
            RequireModuleId(moduleId);
            return moduleId == "combat" ? release.CombatModule : release.EncounterModule;
        }

        private static string Counterpart(string moduleId)
        {
            RequireModuleId(moduleId);
            return moduleId == "combat" ? "encounter" : "combat";
        }

        private static void RequireModuleId(string moduleId)
        {
            if (moduleId != "combat" && moduleId != "encounter")
                throw new InvalidOperationException("Combat·Encounter individual moduleId가 올바르지 않습니다.");
        }

        private void WriteJson(string path, object value)
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        private static void VerifyManifestIdentity(string packageFile, CombatEncounterModuleReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value =>
                    String.Equals(value.FullName, ModulePackageVerifier.ManifestPath, StringComparison.Ordinal));
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
                return parts.Length == 2 &&
                    String.Equals(Uri.UnescapeDataString(parts[0]), "token", StringComparison.Ordinal) &&
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

        private static FileStream ExclusiveFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Combat·Encounter individual 경로가 modules 루트 밖으로 벗어났습니다.");
        }

        private static ActiveProtocolModuleState ReadDefaultProtocol()
        {
            using (var updater = new ProtocolModuleUpdater()) return updater.ReadVerifiedActiveState();
        }

        private static ActiveCombatEncounterCompatibilityGroupState ReadDefaultGroup()
        {
            using (var updater = new CombatEncounterCompatibilityGroupUpdater()) return updater.ReadVerifiedActiveState();
        }

        public void Dispose()
        {
            _cache.Dispose();
        }
    }

    internal static class CombatEncounterIndividualModuleUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(CombatEncounterIndividualModuleUpdater updater)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            var state = updater.ReadAuthorizationState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "moduleId", state.ModuleId },
                { "version", state.ModuleVersion },
                { "sha256", state.PackageSha256 },
                { "compatibilityGroupId", state.CompatibilityGroupId },
                { "counterpartModuleId", state.CounterpartModuleId },
                { "counterpartVersion", state.CounterpartVersion },
                { "counterpartSha256", state.CounterpartSha256 },
                { "runtimeBundleRevision", state.RuntimeBundleRevision },
                { "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 },
                { "runtimeModuleSetHash", state.RuntimeModuleSetHash },
                { "parentPrivateRuntimePointerGeneration", state.ParentPrivateRuntimePointerGeneration },
                { "parentCapturePointerGeneration", state.ParentCapturePointerGeneration },
                { "parentProtocolPointerGeneration", state.ParentProtocolPointerGeneration },
                { "pointerGeneration", state.PointerGeneration }
            };
        }

        public static async Task<CombatEncounterIndividualModuleInstallResult> ApplyAsync(
            CombatEncounterIndividualModuleUpdater updater,
            CombatEncounterIndividualModuleAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "Combat·Encounter 개별 업데이트 승인을 받지 못했습니다."
                    : authorization.Message);
            if (authorization.CompatibilityGroup == null) return null;
            return await updater.EnsureInstalledAsync(authorization, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
