using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleBundleActivationRequest
    {
        public string BundleLockFile { get; set; }
        public string ExpectedBundleLockSha256 { get; set; }
        public string ExpectedChannel { get; set; }
        public string ExpectedCurrentBundleRevision { get; set; }
    }

    internal sealed class ModuleBundleActivationResult
    {
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public string ActiveBundleFile { get; set; }
        public bool Changed { get; set; }
        public string Status { get; set; }
    }

    internal sealed class ModuleBundleLock
    {
        public int SchemaVersion { get; set; }
        public string ProductVersion { get; set; }
        public string Channel { get; set; }
        public string BundleRevision { get; set; }
        public string ParentBundleRevision { get; set; }
        public string SourceCommit { get; set; }
        public int ContractSetVersion { get; set; }
        public string ModuleSetHash { get; set; }
        public string ActivationMode { get; set; }
        public bool Immutable { get; set; }
        public List<ModuleBundleLockEntry> Modules { get; set; }
    }

    internal sealed class ModuleBundleLockEntry
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string Sha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public string PackagePath { get; set; }
    }

    internal sealed class ActiveModuleBundleState
    {
        public int SchemaVersion { get; set; }
        public string Status { get; set; }
        public string Channel { get; set; }
        public string ProductVersion { get; set; }
        public string BundleRevision { get; set; }
        public string ParentBundleRevision { get; set; }
        public string SourceCommit { get; set; }
        public int ContractSetVersion { get; set; }
        public string ModuleSetHash { get; set; }
        public string BundleLockSha256 { get; set; }
        public string ActivatedAtUtc { get; set; }
        public bool ActivationAtomic { get; set; }
        public List<ActiveModuleBundleEntry> Modules { get; set; }
    }

    internal sealed class ActiveModuleBundleEntry
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public int StateSchemaVersion { get; set; }
        public string PackagePath { get; set; }
        public string StagedDirectory { get; set; }
        public string SelfTestReceiptSha256 { get; set; }
        public string ManifestSha256 { get; set; }
    }

    internal static class ModuleBundleActivator
    {
        public const string ActiveStatus = "ACTIVE_BUNDLE";
        public const string RequiredActivationMode = "ATOMIC_BUNDLE";
        public const string StaleBundleBaseCode = "STALE_BUNDLE_BASE";

        private static readonly string[] RequiredModuleIds =
        {
            "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell"
        };

        private static readonly Regex SemVerPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex BundleRevisionPattern = new Regex("^B[0-9]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha1Pattern = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static ModuleBundleActivationResult Activate(ModuleBundleActivationRequest request)
        {
            LauncherPaths.EnsureDirectories();
            return ActivateInternal(
                request,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests,
                LauncherPaths.ModuleActiveBundleFile,
                LauncherPaths.ModuleActivationLockFile);
        }

        public static ActiveModuleBundleState ReadVerifiedActiveBundle()
        {
            LauncherPaths.EnsureDirectories();
            if (!File.Exists(LauncherPaths.ModuleActiveBundleFile)) return null;
            return ReadAndValidateActiveState(
                LauncherPaths.ModuleActiveBundleFile,
                LauncherPaths.ModuleStaging,
                LauncherPaths.ModuleSelfTests);
        }

        internal static ModuleBundleActivationResult ActivateForTest(
            ModuleBundleActivationRequest request,
            string moduleRoot)
        {
            if (String.IsNullOrWhiteSpace(moduleRoot)) throw new ArgumentException("moduleRoot");
            var root = Path.GetFullPath(moduleRoot);
            var stagingRoot = Path.Combine(root, "staging");
            var selfTestRoot = Path.Combine(root, "self-tests");
            var activeFile = Path.Combine(root, "active-bundle.json");
            var lockFile = Path.Combine(root, ".activation.lock");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(selfTestRoot);
            return ActivateInternal(request, stagingRoot, selfTestRoot, activeFile, lockFile);
        }

        internal static string ComputeModuleSetHashForTest(IEnumerable<ModuleBundleLockEntry> modules)
        {
            return ComputeModuleSetHash(modules == null ? null : modules.ToList());
        }

        private static ModuleBundleActivationResult ActivateInternal(
            ModuleBundleActivationRequest request,
            string stagingRoot,
            string selfTestRoot,
            string activeFile,
            string activationLockFile)
        {
            ValidateRequest(request);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(activeFile)));

            using (var activationLock = OpenActivationLock(activationLockFile))
            {
                var bundleLockSha256 = Sha256File(request.BundleLockFile);
                if (!String.Equals(bundleLockSha256, request.ExpectedBundleLockSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Bundle Lock SHA-256이 Server Bundle Manifest 기대값과 일치하지 않습니다.");

                var bundle = ReadAndValidateBundleLock(request.BundleLockFile, request.ExpectedChannel);
                if (!String.Equals(bundle.ParentBundleRevision, request.ExpectedCurrentBundleRevision, StringComparison.Ordinal))
                    throw StaleBundleBase(bundle.ParentBundleRevision, request.ExpectedCurrentBundleRevision);

                ActiveModuleBundleState current = null;
                if (File.Exists(activeFile))
                {
                    current = ReadAndValidateActiveState(activeFile, stagingRoot, selfTestRoot);
                    if (String.Equals(current.BundleRevision, bundle.BundleRevision, StringComparison.Ordinal) &&
                        String.Equals(current.BundleLockSha256, bundleLockSha256, StringComparison.Ordinal))
                    {
                        return Result(bundle.BundleRevision, bundleLockSha256, activeFile, false);
                    }
                    if (!String.Equals(current.BundleRevision, request.ExpectedCurrentBundleRevision, StringComparison.Ordinal))
                        throw StaleBundleBase(bundle.ParentBundleRevision, current.BundleRevision);
                }

                if (BundleNumber(bundle.BundleRevision) <= BundleNumber(bundle.ParentBundleRevision))
                    throw new InvalidOperationException("Bundle revision은 parent bundle보다 커야 합니다.");

                var activeModules = ValidateWholeBundle(bundle, stagingRoot, selfTestRoot);
                var state = new ActiveModuleBundleState
                {
                    SchemaVersion = 1,
                    Status = ActiveStatus,
                    Channel = bundle.Channel,
                    ProductVersion = bundle.ProductVersion,
                    BundleRevision = bundle.BundleRevision,
                    ParentBundleRevision = bundle.ParentBundleRevision,
                    SourceCommit = bundle.SourceCommit,
                    ContractSetVersion = bundle.ContractSetVersion,
                    ModuleSetHash = bundle.ModuleSetHash,
                    BundleLockSha256 = bundleLockSha256,
                    ActivatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ActivationAtomic = true,
                    Modules = activeModules
                };

                CommitAtomicActiveState(state, activeFile, stagingRoot, selfTestRoot);
                return Result(bundle.BundleRevision, bundleLockSha256, activeFile, true);
            }
        }

        private static FileStream OpenActivationLock(string path)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (String.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("Bundle activation lock parent가 없습니다.");
                Directory.CreateDirectory(parent);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                throw new InvalidOperationException("다른 Launcher가 Bundle activation을 진행 중입니다.", error);
            }
        }

        private static void ValidateRequest(ModuleBundleActivationRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (String.IsNullOrWhiteSpace(request.BundleLockFile) || !File.Exists(request.BundleLockFile))
                throw new InvalidOperationException("활성화할 Bundle Lock 파일이 없습니다.");
            if (!Sha256Pattern.IsMatch(request.ExpectedBundleLockSha256 ?? ""))
                throw new InvalidOperationException("Server Bundle Manifest Bundle Lock SHA-256 형식이 올바르지 않습니다.");
            if (request.ExpectedChannel != "stable" && request.ExpectedChannel != "staging")
                throw new InvalidOperationException("Bundle activation channel이 올바르지 않습니다.");
            if (!BundleRevisionPattern.IsMatch(request.ExpectedCurrentBundleRevision ?? ""))
                throw new InvalidOperationException("현재 channel Bundle revision 형식이 올바르지 않습니다.");
        }

        private static ModuleBundleLock ReadAndValidateBundleLock(string path, string expectedChannel)
        {
            var text = ReadUtf8(path);
            IDictionary<string, object> raw;
            try { raw = Json.DeserializeObject(text) as IDictionary<string, object>; }
            catch (Exception error) { throw new InvalidOperationException("Bundle Lock JSON을 읽을 수 없습니다.", error); }
            if (raw == null) throw new InvalidOperationException("Bundle Lock root는 object여야 합니다.");

            RequireExactKeys(raw, "Bundle Lock",
                "schemaVersion", "productVersion", "channel", "bundleRevision", "parentBundleRevision",
                "sourceCommit", "contractSetVersion", "moduleSetHash", "activationMode", "immutable", "modules");

            object modulesRaw;
            if (!raw.TryGetValue("modules", out modulesRaw)) throw new InvalidOperationException("Bundle Lock modules가 없습니다.");
            var moduleArray = modulesRaw as object[];
            if (moduleArray == null) throw new InvalidOperationException("Bundle Lock modules는 array여야 합니다.");
            foreach (var item in moduleArray)
            {
                var module = item as IDictionary<string, object>;
                if (module == null) throw new InvalidOperationException("Bundle Lock module 항목은 object여야 합니다.");
                RequireExactKeys(module, "Bundle Lock module",
                    "moduleId", "moduleVersion", "sha256", "contractSetVersion", "stateSchemaVersion", "packagePath");
            }

            ModuleBundleLock bundle;
            try { bundle = Json.Deserialize<ModuleBundleLock>(text); }
            catch (Exception error) { throw new InvalidOperationException("Bundle Lock schema를 읽을 수 없습니다.", error); }

            if (bundle == null || bundle.SchemaVersion != 1 || !bundle.Immutable)
                throw new InvalidOperationException("Bundle Lock schemaVersion/immutable 계약이 올바르지 않습니다.");
            if (!String.Equals(bundle.ActivationMode, RequiredActivationMode, StringComparison.Ordinal))
                throw new InvalidOperationException("5-7은 ATOMIC_BUNDLE Bundle Lock만 활성화할 수 있습니다.");
            if (!String.Equals(bundle.Channel, expectedChannel, StringComparison.Ordinal))
                throw new InvalidOperationException("Bundle Lock channel이 Launcher 요청 channel과 일치하지 않습니다.");
            if (!SemVerPattern.IsMatch(bundle.ProductVersion ?? "") ||
                !BundleRevisionPattern.IsMatch(bundle.BundleRevision ?? "") ||
                !BundleRevisionPattern.IsMatch(bundle.ParentBundleRevision ?? "") ||
                !Sha1Pattern.IsMatch(bundle.SourceCommit ?? "") ||
                bundle.ContractSetVersion < 1 ||
                !Sha256Pattern.IsMatch(bundle.ModuleSetHash ?? ""))
                throw new InvalidOperationException("Bundle Lock 기본 필드 형식이 올바르지 않습니다.");

            ValidateBundleModules(bundle);
            var computed = ComputeModuleSetHash(bundle.Modules);
            if (!String.Equals(computed, bundle.ModuleSetHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Bundle Lock moduleSetHash가 모듈 조합과 일치하지 않습니다.");
            return bundle;
        }

        private static void ValidateBundleModules(ModuleBundleLock bundle)
        {
            if (bundle.Modules == null || bundle.Modules.Count != RequiredModuleIds.Length)
                throw new InvalidOperationException("Bundle Lock은 정확히 7개 모듈을 포함해야 합니다.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in bundle.Modules)
            {
                if (module == null || Array.IndexOf(RequiredModuleIds, module.ModuleId) < 0 || !seen.Add(module.ModuleId))
                    throw new InvalidOperationException("Bundle Lock moduleId 집합이 올바르지 않습니다.");
                if (!SemVerPattern.IsMatch(module.ModuleVersion ?? "") || !Sha256Pattern.IsMatch(module.Sha256 ?? "") ||
                    module.ContractSetVersion != bundle.ContractSetVersion || module.StateSchemaVersion < 0)
                    throw new InvalidOperationException("Bundle Lock 모듈 version/SHA/Contract/state 계약이 올바르지 않습니다: " + module.ModuleId);
                ValidatePackagePath(module);
            }
            if (RequiredModuleIds.Any(id => !seen.Contains(id)))
                throw new InvalidOperationException("Bundle Lock에 필수 모듈이 누락되었습니다.");
        }

        private static void ValidatePackagePath(ModuleBundleLockEntry module)
        {
            var path = module.PackagePath ?? "";
            var prefix = "modules/" + module.ModuleId + "/" + module.ModuleVersion + "/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length <= prefix.Length ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal) ||
                path.Split('/').Any(segment => String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidOperationException("Bundle Lock packagePath가 안전하지 않습니다: " + module.ModuleId);
        }

        private static List<ActiveModuleBundleEntry> ValidateWholeBundle(
            ModuleBundleLock bundle,
            string stagingRoot,
            string selfTestRoot)
        {
            var byId = bundle.Modules.ToDictionary(value => value.ModuleId, StringComparer.Ordinal);
            var result = new List<ActiveModuleBundleEntry>();

            foreach (var module in bundle.Modules.OrderBy(value => Array.IndexOf(RequiredModuleIds, value.ModuleId)))
            {
                var stagedDirectory = DeterministicSlot(stagingRoot, module.ModuleId, module.ModuleVersion, module.Sha256);
                if (!Directory.Exists(stagedDirectory))
                    throw new InvalidOperationException("Bundle 모듈 Staging 슬롯이 없습니다: " + module.ModuleId);

                var stageReceiptPath = Path.Combine(stagedDirectory, ModuleStagingInstaller.InstallReceiptName);
                var stageReceipt = ReadStageReceipt(stageReceiptPath, module, bundle.ContractSetVersion);
                var manifestPath = Path.Combine(stagedDirectory, ModulePackageVerifier.ManifestPath);
                if (!File.Exists(manifestPath))
                    throw new InvalidOperationException("Staging Package Manifest가 없습니다: " + module.ModuleId);
                var manifestSha256 = Sha256File(manifestPath);
                if (!String.Equals(manifestSha256, stageReceipt.ManifestSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Staging Package Manifest SHA가 staging receipt와 일치하지 않습니다: " + module.ModuleId);

                ModulePackageManifest manifest;
                try { manifest = Json.Deserialize<ModulePackageManifest>(ReadUtf8(manifestPath)); }
                catch (Exception error) { throw new InvalidOperationException("Staging Package Manifest를 읽을 수 없습니다: " + module.ModuleId, error); }
                if (manifest == null || manifest.State == null ||
                    !String.Equals(manifest.ModuleId, module.ModuleId, StringComparison.Ordinal) ||
                    !String.Equals(manifest.ModuleVersion, module.ModuleVersion, StringComparison.Ordinal) ||
                    manifest.ContractSetVersion != bundle.ContractSetVersion ||
                    manifest.State.StateSchemaVersion != module.StateSchemaVersion)
                    throw new InvalidOperationException("Staging Package Manifest가 Bundle Lock과 일치하지 않습니다: " + module.ModuleId);

                var selfReceiptPath = DeterministicSelfTestReceipt(selfTestRoot, module.ModuleId, module.ModuleVersion, module.Sha256);
                var selfReceipt = ReadSelfTestReceipt(selfReceiptPath, module, bundle.ContractSetVersion);
                var stageReceiptSha256 = Sha256File(stageReceiptPath);
                if (!String.Equals(selfReceipt.StageReceiptSha256, stageReceiptSha256, StringComparison.Ordinal) ||
                    !String.Equals(selfReceipt.ManifestSha256, manifestSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("SELF_TEST_PASSED receipt가 현재 Staging 파일과 일치하지 않습니다: " + module.ModuleId);

                var dependencyIds = manifest.DependencyModuleIds ?? new List<string>();
                if (dependencyIds.Count != dependencyIds.Distinct(StringComparer.Ordinal).Count() ||
                    dependencyIds.Any(id => !byId.ContainsKey(id)))
                    throw new InvalidOperationException("Package Manifest dependency가 Bundle 모듈 조합과 일치하지 않습니다: " + module.ModuleId);
                var expectedDependencyFingerprint = ComputeDependencyFingerprint(dependencyIds, byId);
                if (!String.Equals(expectedDependencyFingerprint, selfReceipt.DependencyFingerprint, StringComparison.Ordinal) ||
                    selfReceipt.DependencyCount != dependencyIds.Count)
                    throw new InvalidOperationException("SELF_TEST_PASSED dependency 조합이 현재 Bundle Lock과 일치하지 않습니다: " + module.ModuleId);

                result.Add(new ActiveModuleBundleEntry
                {
                    ModuleId = module.ModuleId,
                    ModuleVersion = module.ModuleVersion,
                    ArchiveSha256 = module.Sha256,
                    StateSchemaVersion = module.StateSchemaVersion,
                    PackagePath = module.PackagePath,
                    StagedDirectory = stagedDirectory,
                    SelfTestReceiptSha256 = Sha256File(selfReceiptPath),
                    ManifestSha256 = manifestSha256
                });
            }
            return result;
        }

        private sealed class StageReceiptData
        {
            public string ManifestSha256 { get; set; }
        }

        private sealed class SelfTestReceiptData
        {
            public string StageReceiptSha256 { get; set; }
            public string ManifestSha256 { get; set; }
            public string DependencyFingerprint { get; set; }
            public int DependencyCount { get; set; }
        }

        private static StageReceiptData ReadStageReceipt(
            string path,
            ModuleBundleLockEntry module,
            int contractSetVersion)
        {
            var root = ReadObject(path, "staging-install.json");
            RequireExactKeys(root, "staging-install.json",
                "schemaVersion", "installStatus", "moduleId", "moduleVersion", "bundlePackagePath",
                "archiveSha256", "manifestSha256", "contractSetVersion", "stateSchemaVersion", "signingKeyId",
                "verificationReceiptSha256", "stagedAtUtc", "activationAllowed", "activeBundleChanged");

            if (AsInt(root, "schemaVersion") != 1 ||
                !String.Equals(AsString(root, "installStatus"), ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "moduleId"), module.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "moduleVersion"), module.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "bundlePackagePath"), module.PackagePath, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "archiveSha256"), module.Sha256, StringComparison.Ordinal) ||
                AsInt(root, "contractSetVersion") != contractSetVersion ||
                AsInt(root, "stateSchemaVersion") != module.StateSchemaVersion ||
                !Sha256Pattern.IsMatch(AsString(root, "manifestSha256") ?? "") ||
                !Sha256Pattern.IsMatch(AsString(root, "verificationReceiptSha256") ?? "") ||
                String.IsNullOrWhiteSpace(AsString(root, "signingKeyId")) ||
                AsBool(root, "activationAllowed") || AsBool(root, "activeBundleChanged"))
                throw new InvalidOperationException("5-5 STAGED receipt가 Bundle Lock과 일치하지 않습니다: " + module.ModuleId);

            return new StageReceiptData { ManifestSha256 = AsString(root, "manifestSha256") };
        }

        private static SelfTestReceiptData ReadSelfTestReceipt(
            string path,
            ModuleBundleLockEntry module,
            int contractSetVersion)
        {
            var root = ReadObject(path, "self-test.json");
            RequireExactKeys(root, "self-test.json",
                "schemaVersion", "status", "moduleId", "moduleVersion", "archiveSha256", "contractSetVersion",
                "stateSchemaVersion", "stageReceiptSha256", "manifestSha256", "dependencyFingerprint",
                "dependencyCount", "assemblyMetadataLoad", "testedAtUtc", "activationAllowed", "activeBundleChanged");

            if (AsInt(root, "schemaVersion") != 1 ||
                !String.Equals(AsString(root, "status"), ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "moduleId"), module.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "moduleVersion"), module.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(AsString(root, "archiveSha256"), module.Sha256, StringComparison.Ordinal) ||
                AsInt(root, "contractSetVersion") != contractSetVersion ||
                AsInt(root, "stateSchemaVersion") != module.StateSchemaVersion ||
                !Sha256Pattern.IsMatch(AsString(root, "stageReceiptSha256") ?? "") ||
                !Sha256Pattern.IsMatch(AsString(root, "manifestSha256") ?? "") ||
                !Sha256Pattern.IsMatch(AsString(root, "dependencyFingerprint") ?? "") ||
                AsInt(root, "dependencyCount") < 0 || !AsBool(root, "assemblyMetadataLoad") ||
                AsBool(root, "activationAllowed") || AsBool(root, "activeBundleChanged"))
                throw new InvalidOperationException("5-6 SELF_TEST_PASSED receipt가 Bundle Lock과 일치하지 않습니다: " + module.ModuleId);

            return new SelfTestReceiptData
            {
                StageReceiptSha256 = AsString(root, "stageReceiptSha256"),
                ManifestSha256 = AsString(root, "manifestSha256"),
                DependencyFingerprint = AsString(root, "dependencyFingerprint"),
                DependencyCount = AsInt(root, "dependencyCount")
            };
        }

        private static void CommitAtomicActiveState(
            ActiveModuleBundleState state,
            string activeFile,
            string stagingRoot,
            string selfTestRoot)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(activeFile));
            if (String.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("active-bundle parent 경로가 없습니다.");
            Directory.CreateDirectory(parent);
            var temporary = activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, SerializeActiveState(state), new UTF8Encoding(false));
                ReadAndValidateActiveState(temporary, stagingRoot, selfTestRoot);
                var expectedSha = Sha256File(temporary);
                if (File.Exists(activeFile)) File.Replace(temporary, activeFile, null);
                else File.Move(temporary, activeFile);
                if (!File.Exists(activeFile) || !String.Equals(Sha256File(activeFile), expectedSha, StringComparison.Ordinal))
                    throw new InvalidOperationException("active-bundle 원자 교체 직후 readback SHA 검증에 실패했습니다.");
                var readback = ReadAndValidateActiveState(activeFile, stagingRoot, selfTestRoot);
                if (!String.Equals(readback.BundleRevision, state.BundleRevision, StringComparison.Ordinal) ||
                    !String.Equals(readback.BundleLockSha256, state.BundleLockSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("active-bundle 원자 교체 직후 readback 내용이 일치하지 않습니다.");
            }
            finally
            {
                SafeDeleteFile(temporary);
            }
        }

        private static ActiveModuleBundleState ReadAndValidateActiveState(
            string path,
            string stagingRoot,
            string selfTestRoot)
        {
            var text = ReadUtf8(path);
            var raw = Json.DeserializeObject(text) as IDictionary<string, object>;
            if (raw == null) throw new InvalidOperationException("active-bundle.json root가 올바르지 않습니다.");
            RequireExactKeys(raw, "active-bundle.json",
                "schemaVersion", "status", "channel", "productVersion", "bundleRevision", "parentBundleRevision",
                "sourceCommit", "contractSetVersion", "moduleSetHash", "bundleLockSha256", "activatedAtUtc",
                "activationAtomic", "modules");

            var state = Json.Deserialize<ActiveModuleBundleState>(text);
            if (state == null || state.SchemaVersion != 1 ||
                !String.Equals(state.Status, ActiveStatus, StringComparison.Ordinal) ||
                (state.Channel != "stable" && state.Channel != "staging") ||
                !SemVerPattern.IsMatch(state.ProductVersion ?? "") ||
                !BundleRevisionPattern.IsMatch(state.BundleRevision ?? "") ||
                !BundleRevisionPattern.IsMatch(state.ParentBundleRevision ?? "") ||
                !Sha1Pattern.IsMatch(state.SourceCommit ?? "") || state.ContractSetVersion < 1 ||
                !Sha256Pattern.IsMatch(state.ModuleSetHash ?? "") ||
                !Sha256Pattern.IsMatch(state.BundleLockSha256 ?? "") || !state.ActivationAtomic ||
                state.Modules == null || state.Modules.Count != RequiredModuleIds.Length)
                throw new InvalidOperationException("active-bundle.json 기본 계약이 올바르지 않습니다.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var module in state.Modules)
            {
                if (module == null || Array.IndexOf(RequiredModuleIds, module.ModuleId) < 0 || !seen.Add(module.ModuleId) ||
                    !SemVerPattern.IsMatch(module.ModuleVersion ?? "") || !Sha256Pattern.IsMatch(module.ArchiveSha256 ?? "") ||
                    module.StateSchemaVersion < 0 || !Sha256Pattern.IsMatch(module.SelfTestReceiptSha256 ?? "") ||
                    !Sha256Pattern.IsMatch(module.ManifestSha256 ?? ""))
                    throw new InvalidOperationException("active-bundle module 계약이 올바르지 않습니다.");
                var expectedStaged = DeterministicSlot(stagingRoot, module.ModuleId, module.ModuleVersion, module.ArchiveSha256);
                if (!String.Equals(Path.GetFullPath(module.StagedDirectory ?? ""), expectedStaged, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expectedStaged))
                    throw new InvalidOperationException("active-bundle module Staging 경로가 올바르지 않습니다: " + module.ModuleId);
                var selfReceipt = DeterministicSelfTestReceipt(selfTestRoot, module.ModuleId, module.ModuleVersion, module.ArchiveSha256);
                var manifest = Path.Combine(expectedStaged, ModulePackageVerifier.ManifestPath);
                if (!File.Exists(selfReceipt) || !String.Equals(Sha256File(selfReceipt), module.SelfTestReceiptSha256, StringComparison.Ordinal) ||
                    !File.Exists(manifest) || !String.Equals(Sha256File(manifest), module.ManifestSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("active-bundle module readback 무결성이 올바르지 않습니다: " + module.ModuleId);
            }
            if (RequiredModuleIds.Any(id => !seen.Contains(id)))
                throw new InvalidOperationException("active-bundle 필수 모듈이 누락되었습니다.");
            return state;
        }

        private static string SerializeActiveState(ActiveModuleBundleState state)
        {
            var modules = state.Modules.Select(module => (object)new Dictionary<string, object>
            {
                { "moduleId", module.ModuleId },
                { "moduleVersion", module.ModuleVersion },
                { "archiveSha256", module.ArchiveSha256 },
                { "stateSchemaVersion", module.StateSchemaVersion },
                { "packagePath", module.PackagePath },
                { "stagedDirectory", module.StagedDirectory },
                { "selfTestReceiptSha256", module.SelfTestReceiptSha256 },
                { "manifestSha256", module.ManifestSha256 }
            }).ToArray();
            var root = new Dictionary<string, object>
            {
                { "schemaVersion", state.SchemaVersion },
                { "status", state.Status },
                { "channel", state.Channel },
                { "productVersion", state.ProductVersion },
                { "bundleRevision", state.BundleRevision },
                { "parentBundleRevision", state.ParentBundleRevision },
                { "sourceCommit", state.SourceCommit },
                { "contractSetVersion", state.ContractSetVersion },
                { "moduleSetHash", state.ModuleSetHash },
                { "bundleLockSha256", state.BundleLockSha256 },
                { "activatedAtUtc", state.ActivatedAtUtc },
                { "activationAtomic", state.ActivationAtomic },
                { "modules", modules }
            };
            return Json.Serialize(root);
        }

        private static string ComputeModuleSetHash(List<ModuleBundleLockEntry> modules)
        {
            if (modules == null) throw new InvalidOperationException("moduleSetHash 대상이 없습니다.");
            var items = new List<string>();
            foreach (var module in modules.OrderBy(value => value.ModuleId, StringComparer.Ordinal))
            {
                items.Add("{" +
                    "\"contractSetVersion\":" + module.ContractSetVersion.ToString(CultureInfo.InvariantCulture) + "," +
                    "\"moduleId\":" + JsonString(module.ModuleId) + "," +
                    "\"moduleVersion\":" + JsonString(module.ModuleVersion) + "," +
                    "\"packagePath\":" + JsonString(module.PackagePath) + "," +
                    "\"sha256\":" + JsonString(module.Sha256) + "," +
                    "\"stateSchemaVersion\":" + module.StateSchemaVersion.ToString(CultureInfo.InvariantCulture) + "}");
            }
            return Sha256Bytes(Encoding.UTF8.GetBytes("[" + String.Join(",", items) + "]"));
        }

        private static string ComputeDependencyFingerprint(
            IEnumerable<string> dependencyIds,
            IDictionary<string, ModuleBundleLockEntry> byId)
        {
            var text = String.Join("\n", dependencyIds.OrderBy(value => value, StringComparer.Ordinal).Select(id =>
            {
                ModuleBundleLockEntry dependency;
                if (!byId.TryGetValue(id, out dependency))
                    throw new InvalidOperationException("Bundle dependency가 누락되었습니다: " + id);
                return dependency.ModuleId + "=" + dependency.ModuleVersion + "@" + dependency.Sha256;
            }));
            return Sha256Bytes(Encoding.UTF8.GetBytes(text));
        }

        private static string JsonString(string value)
        {
            return Json.Serialize(value ?? "");
        }

        private static string DeterministicSlot(string root, string moduleId, string version, string sha256)
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(rootFull, moduleId, version, sha256));
            EnsureUnderRoot(rootFull, full);
            return full;
        }

        private static string DeterministicSelfTestReceipt(string root, string moduleId, string version, string sha256)
        {
            var rootFull = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(rootFull, moduleId, version, sha256, ModuleStagingSelfTest.ReceiptName));
            EnsureUnderRoot(rootFull, full);
            return full;
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            var prefix = rootFull + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Bundle activation 경로가 허용된 root 밖으로 벗어났습니다.");
        }

        private static IDictionary<string, object> ReadObject(string path, string name)
        {
            if (!File.Exists(path)) throw new InvalidOperationException(name + " 파일이 없습니다.");
            try
            {
                var root = Json.DeserializeObject(ReadUtf8(path)) as IDictionary<string, object>;
                if (root == null) throw new InvalidOperationException(name + " root가 object가 아닙니다.");
                return root;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception error) { throw new InvalidOperationException(name + "을 읽을 수 없습니다.", error); }
        }

        private static void RequireExactKeys(IDictionary<string, object> value, string name, params string[] keys)
        {
            var expected = new HashSet<string>(keys, StringComparer.Ordinal);
            if (value == null || value.Count != expected.Count || value.Keys.Any(key => !expected.Contains(key)))
                throw new InvalidOperationException(name + " 필드 집합이 schema v1과 일치하지 않습니다.");
        }

        private static string AsString(IDictionary<string, object> value, string key)
        {
            object raw;
            if (!value.TryGetValue(key, out raw)) throw new InvalidOperationException("필수 필드가 없습니다: " + key);
            return Convert.ToString(raw, CultureInfo.InvariantCulture);
        }

        private static int AsInt(IDictionary<string, object> value, string key)
        {
            object raw;
            if (!value.TryGetValue(key, out raw)) throw new InvalidOperationException("필수 필드가 없습니다: " + key);
            return Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        }

        private static bool AsBool(IDictionary<string, object> value, string key)
        {
            object raw;
            if (!value.TryGetValue(key, out raw)) throw new InvalidOperationException("필수 필드가 없습니다: " + key);
            return Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
        }

        private static string ReadUtf8(string path)
        {
            try { return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)); }
            catch (Exception error) { throw new InvalidOperationException("UTF-8 파일을 읽을 수 없습니다: " + path, error); }
        }

        private static int BundleNumber(string revision)
        {
            if (!BundleRevisionPattern.IsMatch(revision ?? "")) throw new InvalidOperationException("Bundle revision 형식이 올바르지 않습니다.");
            return Int32.Parse(revision.Substring(1), CultureInfo.InvariantCulture);
        }

        private static InvalidOperationException StaleBundleBase(string parent, string current)
        {
            return new InvalidOperationException(StaleBundleBaseCode + ": parent=" + (parent ?? "null") + " current=" + (current ?? "null"));
        }

        private static ModuleBundleActivationResult Result(string revision, string sha256, string activeFile, bool changed)
        {
            return new ModuleBundleActivationResult
            {
                BundleRevision = revision,
                BundleLockSha256 = sha256,
                ActiveBundleFile = activeFile,
                Changed = changed,
                Status = ActiveStatus
            };
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string Sha256Bytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes)
        {
            return String.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void SafeDeleteFile(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
