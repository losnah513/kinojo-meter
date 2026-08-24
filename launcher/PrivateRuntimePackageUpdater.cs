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
    internal sealed class PrivateRuntimePackageUpdater : IDisposable
    {
        internal const string VersionShaConflictCode = "PRIVATE_RUNTIME_VERSION_SHA_CONFLICT";
        internal const string RuntimeBundleRequiredCode = "PRIVATE_RUNTIME_RUNTIME_BUNDLE_REQUIRED";
        internal const string RuntimeBundleChangedCode = "PRIVATE_RUNTIME_RUNTIME_BUNDLE_CHANGED";
        private const long MaximumPackageBytes = 256L * 1024L * 1024L;
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
        private readonly Func<ActiveModuleBundleState> _readActiveBundle;

        public PrivateRuntimePackageUpdater()
            : this(
                new ModulePackageDownloadCache(),
                LauncherPaths.ModuleRoot,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                LauncherPaths.ModuleActivePrivateRuntimeFile,
                LauncherPaths.ModuleActivationLockFile,
                LauncherPaths.ModulePrivateRuntimeUpdateLockFile,
                ModuleBundleActivator.ReadVerifiedActiveBundle)
        {
        }

        internal PrivateRuntimePackageUpdater(
            ModulePackageDownloadCache cache,
            string moduleRoot,
            string stagingRoot,
            string selfTestRoot,
            string activeFile,
            string activationLock,
            string privateRuntimeLock,
            Func<ActiveModuleBundleState> readActiveBundle)
        {
            _cache = cache ?? throw new ArgumentNullException("cache");
            _moduleRoot = Path.GetFullPath(moduleRoot ?? throw new ArgumentNullException("moduleRoot"));
            _stagingRoot = Path.GetFullPath(stagingRoot ?? throw new ArgumentNullException("stagingRoot"));
            _selfTestRoot = Path.GetFullPath(selfTestRoot ?? throw new ArgumentNullException("selfTestRoot"));
            _activeFile = Path.GetFullPath(activeFile ?? throw new ArgumentNullException("activeFile"));
            _activationLock = Path.GetFullPath(activationLock ?? throw new ArgumentNullException("activationLock"));
            _privateRuntimeLock = Path.GetFullPath(privateRuntimeLock ?? throw new ArgumentNullException("privateRuntimeLock"));
            _readActiveBundle = readActiveBundle ?? throw new ArgumentNullException("readActiveBundle");
            EnsureUnderRoot(_moduleRoot, _stagingRoot);
            EnsureUnderRoot(_moduleRoot, _selfTestRoot);
            EnsureUnderRoot(_moduleRoot, _activeFile);
            EnsureUnderRoot(_moduleRoot, _activationLock);
            EnsureUnderRoot(_moduleRoot, _privateRuntimeLock);
        }

        public ActivePrivateRuntimeState ReadVerifiedActiveState()
        {
            var state = ReadAuthorizationState();
            if (state == null) return null;

            var bundle = RequireCompatibleBundle(state.Channel, state.ContractSetVersion);
            if (!String.Equals(bundle.BundleRevision, state.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.BundleLockSha256, state.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(bundle.ModuleSetHash, state.RuntimeModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": private runtime이 검증된 runtime Bundle과 현재 Bundle이 다릅니다.");

            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage))
                throw new InvalidOperationException("private runtime staging 경로가 결정 경로와 일치하지 않습니다.");

            var target = new ModuleStagingInstallResult
            {
                ModuleId = "private-runtime",
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
                throw new InvalidOperationException("private runtime active state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        internal ActivePrivateRuntimeState ReadAuthorizationState()
        {
            if (!File.Exists(_activeFile)) return null;
            ActivePrivateRuntimeState state;
            try { state = _json.Deserialize<ActivePrivateRuntimeState>(File.ReadAllText(_activeFile, Encoding.UTF8)); }
            catch (Exception error) { throw new InvalidOperationException("private runtime active state를 신뢰할 수 없습니다.", error); }
            ValidateActiveStateShape(state);
            var expectedStage = StageDirectory(state.ModuleVersion, state.PackageSha256);
            var expectedSelfTest = Path.Combine(_selfTestRoot, "private-runtime", state.ModuleVersion, state.PackageSha256, ModuleStagingSelfTest.ReceiptName);
            var manifest = Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath);
            if (!String.Equals(expectedStage, Path.GetFullPath(state.StagedDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(expectedStage) || !File.Exists(expectedSelfTest) || !File.Exists(manifest) ||
                !String.Equals(Sha256File(expectedSelfTest), state.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(manifest), state.PackageManifestSha256, StringComparison.Ordinal) ||
                !File.Exists(Path.Combine(expectedStage, state.PrimaryArtifact)))
                throw new InvalidOperationException("private runtime authorization state readback 무결성이 올바르지 않습니다.");
            return state;
        }

        public async Task<PrivateRuntimeInstallResult> EnsureInstalledAsync(
            PrivateRuntimeReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            var uri = ValidateRelease(release, expectedProjectHost);
            Directory.CreateDirectory(_moduleRoot);
            Directory.CreateDirectory(_stagingRoot);
            Directory.CreateDirectory(_selfTestRoot);

            var currentIdentity = ReadAuthorizationState();
            RejectVersionConflict(currentIdentity, release);
            var current = TryReadVerifiedActiveStateForCurrentBundle();
            if (SameRelease(current, release))
                return new PrivateRuntimeInstallResult { Active = current, Previous = currentIdentity, Changed = false, Downloaded = false };

            var download = new ModulePackageDownloadRequest
            {
                ModuleId = "private-runtime",
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
                throw new InvalidOperationException("private runtime cache 크기가 Server release와 일치하지 않습니다.");

            var verificationRequest = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = "private-runtime",
                ModuleVersion = release.Version,
                BundlePackagePath = release.PackagePath,
                ExpectedSha256 = release.Sha256,
                ContractSetVersion = release.ContractSetVersion,
                StateSchemaVersion = release.StateSchemaVersion
            };
            var verified = ModulePackageVerifier.Verify(verificationRequest);
            if (!String.Equals(verified.ManifestSha256, release.PackageManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(verified.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("private runtime Package Manifest가 Server release와 일치하지 않습니다.");
            VerifyManifestIdentity(cached.PackageFile, release);
            VerifyRuntimeLock(cached.PackageFile, release);

            using (var activationGate = ExclusiveFile(_activationLock))
            using (var privateRuntimeGate = ExclusiveFile(_privateRuntimeLock))
            {
                var latestIdentity = ReadAuthorizationState();
                RejectVersionConflict(latestIdentity, release);
                var latest = TryReadVerifiedActiveStateForCurrentBundle();
                if (SameRelease(latest, release))
                    return new PrivateRuntimeInstallResult
                    {
                        Active = latest,
                        Previous = latestIdentity,
                        Changed = false,
                        Downloaded = !cached.CacheHit
                    };
                var bundle = RequireCompatibleBundle(release.Channel, release.ContractSetVersion);
                RequireReleaseBundle(release, bundle);
                var staged = ModuleStagingInstaller.Stage(
                    new ModuleStagingInstallRequest { VerificationRequest = verificationRequest });
                var selfTest = ModuleStagingSelfTest.RunForTest(
                    new ModuleSelfTestRequest { Target = staged, Dependencies = Dependencies(bundle) },
                    _stagingRoot,
                    _selfTestRoot);
                var active = Activate(release, staged, selfTest, bundle);
                WriteActiveState(active);
                return new PrivateRuntimeInstallResult
                {
                    Active = active,
                    Previous = latestIdentity,
                    Changed = true,
                    Downloaded = !cached.CacheHit
                };
            }
        }

        internal static ActivePrivateRuntimeState ActivateForTest(
            PrivateRuntimeReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle)
        {
            return Activate(release, staged, selfTest, bundle);
        }

        internal static void RejectVersionConflictForTest(ActivePrivateRuntimeState current, PrivateRuntimeReleaseManifest release)
        {
            RejectVersionConflict(current, release);
        }

        internal static void ValidateReleaseForTest(PrivateRuntimeReleaseManifest release, string expectedProjectHost)
        {
            ValidateRelease(release, expectedProjectHost);
        }

        private static ActivePrivateRuntimeState Activate(
            PrivateRuntimeReleaseManifest release,
            ModuleStagingInstallResult staged,
            ModuleSelfTestResult selfTest,
            ActiveModuleBundleState bundle)
        {
            if (release == null || staged == null || selfTest == null || bundle == null ||
                !String.Equals(release.ModuleId, "private-runtime", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleId, "private-runtime", StringComparison.Ordinal) ||
                !String.Equals(staged.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(staged.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleId, "private-runtime", StringComparison.Ordinal) ||
                !String.Equals(selfTest.ModuleVersion, release.Version, StringComparison.Ordinal) ||
                !String.Equals(selfTest.ArchiveSha256, release.Sha256, StringComparison.Ordinal) ||
                !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !File.Exists(selfTest.ReceiptFile))
                throw new InvalidOperationException("검증되지 않은 private runtime은 활성화할 수 없습니다.");
            RequireReleaseBundle(release, bundle);
            return new ActivePrivateRuntimeState
            {
                SchemaVersion = 1,
                Channel = release.Channel,
                ModuleId = "private-runtime",
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
                PointerGeneration = release.PointerGeneration,
                ActivatedAtUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private ActiveModuleBundleState RequireCompatibleBundle(string channel, int contractSetVersion)
        {
            var bundle = _readActiveBundle();
            if (bundle == null)
                throw new InvalidOperationException(RuntimeBundleRequiredCode + ": private runtime 활성화 전에 검증된 private runtime Bundle이 필요합니다.");
            if (!String.Equals(bundle.Channel, channel, StringComparison.Ordinal) || bundle.ContractSetVersion != contractSetVersion)
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": private runtime channel/Contract Set과 runtime Bundle이 다릅니다.");
            return bundle;
        }

        private ActivePrivateRuntimeState TryReadVerifiedActiveStateForCurrentBundle()
        {
            try { return ReadVerifiedActiveState(); }
            catch (InvalidOperationException error)
            {
                if (error.Message.StartsWith(RuntimeBundleChangedCode + ":", StringComparison.Ordinal)) return null;
                throw;
            }
        }

        private static List<ModuleSelfTestDependency> Dependencies(ActiveModuleBundleState bundle)
        {
            if (bundle == null || bundle.Modules == null) throw new InvalidOperationException(RuntimeBundleRequiredCode);
            return bundle.Modules
                .Where(value => value != null && !String.Equals(value.ModuleId, "private-runtime", StringComparison.Ordinal))
                .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
                .Select(value => new ModuleSelfTestDependency
                {
                    ModuleId = value.ModuleId,
                    ModuleVersion = value.ModuleVersion,
                    ArchiveSha256 = value.ArchiveSha256,
                    StagedDirectory = value.StagedDirectory
                }).ToList();
        }

        private static Uri ValidateRelease(PrivateRuntimeReleaseManifest release, string expectedProjectHost)
        {
            Uri uri;
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.ModuleId, "private-runtime", StringComparison.Ordinal) ||
                !VersionPattern.IsMatch(release.Version ?? "") ||
                !VersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") ||
                release.FileSize <= 0 || release.FileSize > MaximumPackageBytes ||
                !ShaPattern.IsMatch(release.Sha256 ?? "") || !ShaPattern.IsMatch(release.PackageManifestSha256 ?? "") ||
                release.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                release.StateSchemaVersion < 1 ||
                !BundlePattern.IsMatch(release.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(release.RuntimeModuleSetHash ?? "") ||
                !String.Equals(release.PrimaryArtifact, "KINOJO.Meter.EngineHost.exe", StringComparison.Ordinal) ||
                !String.Equals(release.FileName, "KinojoPrivateRuntime_" + release.Version + "_x64.zip", StringComparison.Ordinal) ||
                !String.Equals(release.PackagePath, "modules/private-runtime/" + release.Version + "/" + release.FileName, StringComparison.Ordinal) ||
                !String.Equals(release.PackageId, release.Channel + ":private-runtime:" + release.Version + ":" + release.Sha256.Substring(0, 16), StringComparison.Ordinal) ||
                !String.Equals(release.IntegrityMode, ModulePackageVerifier.IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(release.SigningKeyId) || !IsRsa3072Signature(release.ManifestSignature) ||
                release.PointerGeneration < 1 || release.ExpiresAt <= DateTimeOffset.UtcNow ||
                release.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10) ||
                !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/modules/private-runtime/" + release.Channel + "/" + release.Version + "/" + release.FileName ||
                !HasSignedToken(uri))
                throw new InvalidOperationException("private runtime release 계약이 올바르지 않습니다.");
            return uri;
        }

        private sealed class RuntimeLock
        {
            public int SchemaVersion { get; set; }
            public string BundleRevision { get; set; }
            public string BundleLockSha256 { get; set; }
            public string ModuleSetHash { get; set; }
        }

        private static void VerifyRuntimeLock(string packageFile, PrivateRuntimeReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value =>
                    String.Equals(value.FullName, "runtime-bundle.lock.json", StringComparison.Ordinal));
                if (entry == null || entry.Length <= 0 || entry.Length > 16384)
                    throw new InvalidOperationException("private runtime Bundle Lock이 없습니다.");
                RuntimeLock descriptor;
                using (var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, true)))
                    descriptor = new JavaScriptSerializer().Deserialize<RuntimeLock>(reader.ReadToEnd());
                if (descriptor == null || descriptor.SchemaVersion != 1 ||
                    !String.Equals(descriptor.BundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) ||
                    !String.Equals(descriptor.BundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) ||
                    !String.Equals(descriptor.ModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal))
                    throw new InvalidOperationException("private runtime Bundle Lock이 Server release와 일치하지 않습니다.");
            }
        }

        private static void RequireReleaseBundle(PrivateRuntimeReleaseManifest release, ActiveModuleBundleState bundle)
        {
            if (release == null || bundle == null ||
                !String.Equals(release.RuntimeBundleRevision, bundle.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeBundleLockSha256, bundle.BundleLockSha256, StringComparison.Ordinal) ||
                !String.Equals(release.RuntimeModuleSetHash, bundle.ModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException(RuntimeBundleChangedCode + ": private runtime release와 활성 Bundle이 다릅니다.");
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

        private static void VerifyManifestIdentity(string packageFile, PrivateRuntimeReleaseManifest release)
        {
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                var entry = archive.Entries.SingleOrDefault(value => String.Equals(value.FullName, ModulePackageVerifier.ManifestPath, StringComparison.Ordinal));
                if (entry == null) throw new InvalidOperationException("private runtime Package Manifest가 없습니다.");
                ModulePackageManifest manifest;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true)))
                    manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(reader.ReadToEnd());
                if (manifest == null || manifest.Integrity == null ||
                    !String.Equals(manifest.Integrity.SigningKeyId, release.SigningKeyId, StringComparison.Ordinal) ||
                    !String.Equals(manifest.Integrity.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException("private runtime Package Manifest 서명 identity가 Server release와 다릅니다.");
            }
        }

        private static void RejectVersionConflict(ActivePrivateRuntimeState current, PrivateRuntimeReleaseManifest release)
        {
            if (current == null || release == null) return;
            if (String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                !String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 private runtime version의 다른 SHA는 활성화할 수 없습니다.");
            if (CompareVersions(current.ModuleVersion, release.Version) > 0)
                throw new InvalidOperationException("PRIVATE_RUNTIME_DOWNGRADE_BLOCKED: private runtime downgrade는 허용되지 않습니다.");
        }

        private static bool SameRelease(ActivePrivateRuntimeState current, PrivateRuntimeReleaseManifest release)
        {
            return current != null &&
                String.Equals(current.Channel, release.Channel, StringComparison.Ordinal) &&
                String.Equals(current.ModuleVersion, release.Version, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal) &&
                String.Equals(current.PackageManifestSha256, release.PackageManifestSha256, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleRevision, release.RuntimeBundleRevision, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeBundleLockSha256, release.RuntimeBundleLockSha256, StringComparison.Ordinal) &&
                String.Equals(current.RuntimeModuleSetHash, release.RuntimeModuleSetHash, StringComparison.Ordinal) &&
                current.PointerGeneration == release.PointerGeneration;
        }

        private void ValidateActiveStateShape(ActivePrivateRuntimeState state)
        {
            if (state == null || state.SchemaVersion != 1 ||
                !String.Equals(state.ModuleId, "private-runtime", StringComparison.Ordinal) ||
                (state.Channel != "stable" && state.Channel != "staging") ||
                !VersionPattern.IsMatch(state.ModuleVersion ?? "") ||
                !ShaPattern.IsMatch(state.PackageSha256 ?? "") ||
                !ShaPattern.IsMatch(state.PackageManifestSha256 ?? "") ||
                !ShaPattern.IsMatch(state.SelfTestReceiptSha256 ?? "") ||
                !BundlePattern.IsMatch(state.RuntimeBundleRevision ?? "") ||
                !ShaPattern.IsMatch(state.RuntimeBundleLockSha256 ?? "") ||
                !ShaPattern.IsMatch(state.RuntimeModuleSetHash ?? "") ||
                state.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion ||
                state.StateSchemaVersion < 1 || state.PointerGeneration < 1 ||
                !String.Equals(state.PrimaryArtifact, "KINOJO.Meter.EngineHost.exe", StringComparison.Ordinal))
                throw new InvalidOperationException("private runtime active state 기본 계약이 올바르지 않습니다.");
            var expectedPackagePrefix = "modules/private-runtime/" + state.ModuleVersion + "/";
            if (String.IsNullOrWhiteSpace(state.PackagePath) ||
                !state.PackagePath.StartsWith(expectedPackagePrefix, StringComparison.Ordinal) ||
                !state.PackagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("private runtime active state packagePath가 올바르지 않습니다.");
        }

        private void WriteActiveState(ActivePrivateRuntimeState state)
        {
            var temporary = _activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(_activeFile)) File.Replace(temporary, _activeFile, null);
            else File.Move(temporary, _activeFile);
        }

        private string StageDirectory(string version, string sha256)
        {
            return Path.GetFullPath(Path.Combine(_stagingRoot, "private-runtime", version, sha256));
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
                throw new InvalidOperationException("private runtime 경로가 modules 루트 밖으로 벗어났습니다.");
        }

        public void Dispose()
        {
            _cache.Dispose();
        }
    }

    internal sealed class PrivateRuntimeProcessPlan
    {
        public string ShellExecutable { get; set; }
        public string EngineHostExecutable { get; set; }
        public string RuntimeBundleRevision { get; set; }
        public string RuntimeBundleLockSha256 { get; set; }
    }

    internal static class PrivateRuntimeProcessPlanBuilder
    {
        internal static PrivateRuntimeProcessPlan Build(ActiveShellModuleState shell, ActivePrivateRuntimeState runtime)
        {
            if (shell == null || runtime == null ||
                !String.Equals(shell.Channel, runtime.Channel, StringComparison.Ordinal) ||
                !String.Equals(shell.RuntimeBundleRevision, runtime.RuntimeBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(shell.RuntimeBundleLockSha256, runtime.RuntimeBundleLockSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Shell과 private runtime의 Bundle 고정값이 일치하지 않습니다.");
            var shellExecutable = Path.GetFullPath(Path.Combine(shell.StagedDirectory, shell.PrimaryArtifact));
            var engineHostExecutable = Path.GetFullPath(Path.Combine(runtime.StagedDirectory, runtime.PrimaryArtifact));
            if (!File.Exists(shellExecutable) || !File.Exists(engineHostExecutable))
                throw new InvalidOperationException("Shell 또는 EngineHost 실행 파일이 검증된 Staging 슬롯에 없습니다.");
            return new PrivateRuntimeProcessPlan
            {
                ShellExecutable = shellExecutable,
                EngineHostExecutable = engineHostExecutable,
                RuntimeBundleRevision = runtime.RuntimeBundleRevision,
                RuntimeBundleLockSha256 = runtime.RuntimeBundleLockSha256
            };
        }
    }

    internal static class PrivateRuntimeUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(PrivateRuntimePackageUpdater updater)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            var state = updater.ReadAuthorizationState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "moduleId", "private-runtime" },
                { "version", state.ModuleVersion },
                { "sha256", state.PackageSha256 },
                { "runtimeBundleRevision", state.RuntimeBundleRevision },
                { "runtimeBundleLockSha256", state.RuntimeBundleLockSha256 },
                { "runtimeModuleSetHash", state.RuntimeModuleSetHash }
            };
        }

        public static async Task<PrivateRuntimeInstallResult> ApplyAsync(
            PrivateRuntimePackageUpdater updater,
            PrivateRuntimeUpdateAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (updater == null) throw new ArgumentNullException("updater");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "private runtime 업데이트 승인을 받지 못했습니다." : authorization.Message);
            if (authorization.Release == null) return null;
            return await updater.EnsureInstalledAsync(authorization.Release, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
