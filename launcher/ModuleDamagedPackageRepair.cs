using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleDamagedPackageRepairRequest
    {
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public string Channel { get; set; }
        public ModulePackageDownloadRequest Download { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public List<ModuleSelfTestDependency> Dependencies { get; set; }
        public string ReasonCode { get; set; }
    }

    internal sealed class ModuleDamagedPackageRepairResult
    {
        public string Status { get; set; }
        public string BundleRevision { get; set; }
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public string StagedDirectory { get; set; }
        public string SelfTestReceiptFile { get; set; }
        public string RepairReceiptFile { get; set; }
        public bool DownloadedFresh { get; set; }
        public bool ActiveBundleChanged { get; set; }
        public bool ReleasePointerChanged { get; set; }
    }

    internal sealed class ModuleDamagedPackageRepairReceipt
    {
        public int SchemaVersion { get; set; }
        public string Status { get; set; }
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public string Channel { get; set; }
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public string ReasonCode { get; set; }
        public long DownloadedBytes { get; set; }
        public string VerificationStatus { get; set; }
        public string StagingStatus { get; set; }
        public string SelfTestStatus { get; set; }
        public string RepairedAtUtc { get; set; }
        public bool DownloadedFresh { get; set; }
        public bool ActiveBundleChanged { get; set; }
        public bool ReleasePointerChanged { get; set; }
    }

    internal static class ModuleDamagedPackageRepair
    {
        public const string RepairedStatus = "REPAIRED";
        public const string ActiveModuleRepairBlockedCode = "ACTIVE_MODULE_REPAIR_REQUIRES_ROLLBACK";
        public const string ActiveModuleShaConflictCode = "ACTIVE_MODULE_SHA_CONFLICT";
        public const string ReceiptName = "repair.json";

        private static readonly Regex BundleRevisionPattern = new Regex("^B[0-9]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex ReasonCodePattern = new Regex("^[A-Z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static async Task<ModuleDamagedPackageRepairResult> RepairAsync(
            ModuleDamagedPackageRepairRequest request,
            IProgress<ModulePackageDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            LauncherPaths.EnsureDirectories();
            using (var cache = new ModulePackageDownloadCache())
            {
                return await RepairInternalAsync(
                    request,
                    progress,
                    cancellationToken,
                    LauncherPaths.ModuleRoot,
                    cache,
                    verification => ModulePackageVerifier.Verify(verification),
                    staging => ModuleStagingInstaller.Stage(staging),
                    selfTest => ModuleStagingSelfTest.Run(selfTest)).ConfigureAwait(false);
            }
        }

        internal static async Task<ModuleDamagedPackageRepairResult> RepairForTestAsync(
            ModuleDamagedPackageRepairRequest request,
            IProgress<ModulePackageDownloadProgress> progress,
            CancellationToken cancellationToken,
            string moduleRoot,
            HttpMessageHandler handler,
            RSAParameters publicKey,
            string expectedKeyId)
        {
            if (String.IsNullOrWhiteSpace(moduleRoot)) throw new ArgumentException("moduleRoot");
            if (handler == null) throw new ArgumentNullException("handler");
            var root = Path.GetFullPath(moduleRoot);
            Directory.CreateDirectory(root);
            using (var cache = new ModulePackageDownloadCache(handler, Path.Combine(root, "cache")))
            {
                return await RepairInternalAsync(
                    request,
                    progress,
                    cancellationToken,
                    root,
                    cache,
                    verification => ModulePackageVerifier.VerifyForTest(verification, publicKey, expectedKeyId),
                    staging => ModuleStagingInstaller.StageForTest(staging, Path.Combine(root, "staging"), publicKey, expectedKeyId),
                    selfTest => ModuleStagingSelfTest.RunForTest(selfTest, Path.Combine(root, "staging"), Path.Combine(root, "self-tests"))).ConfigureAwait(false);
            }
        }

        private static async Task<ModuleDamagedPackageRepairResult> RepairInternalAsync(
            ModuleDamagedPackageRepairRequest request,
            IProgress<ModulePackageDownloadProgress> progress,
            CancellationToken cancellationToken,
            string moduleRoot,
            ModulePackageDownloadCache cache,
            Func<ModulePackageVerificationRequest, ModulePackageVerificationResult> verify,
            Func<ModuleStagingInstallRequest, ModuleStagingInstallResult> stage,
            Func<ModuleSelfTestRequest, ModuleSelfTestResult> selfTest)
        {
            ValidateRequest(request);
            if (cache == null) throw new ArgumentNullException("cache");
            if (verify == null) throw new ArgumentNullException("verify");
            if (stage == null) throw new ArgumentNullException("stage");
            if (selfTest == null) throw new ArgumentNullException("selfTest");

            var root = Path.GetFullPath(moduleRoot);
            var download = request.Download;
            var stagingSlot = DeterministicSlot(Path.Combine(root, "staging"), download.ModuleId, download.ModuleVersion, download.ExpectedSha256);
            var selfTestSlot = DeterministicSlot(Path.Combine(root, "self-tests"), download.ModuleId, download.ModuleVersion, download.ExpectedSha256);
            var repairSlot = DeterministicSlot(Path.Combine(root, "repairs"), download.ModuleId, download.ModuleVersion, download.ExpectedSha256);
            var receiptFile = Path.Combine(repairSlot, ReceiptName);
            var repairLockFile = Path.Combine(repairSlot, ".repair.lock");
            var activationLockFile = Path.Combine(root, ".activation.lock");

            Directory.CreateDirectory(repairSlot);
            using (var repairLock = OpenExclusiveLock(repairLockFile, "다른 Launcher가 같은 모듈 복구를 진행 중입니다."))
            using (var activationLock = OpenExclusiveLock(activationLockFile, "다른 Launcher가 Bundle activation을 진행 중입니다."))
            {
                var activeHashBefore = ValidateTargetIsNotActive(root, download);
                SafeDeleteFile(receiptFile);

                try
                {
                    var cacheDirectory = cache.CacheDirectoryForTest(download);
                    DeleteDirectoryStrict(cacheDirectory, "손상 모듈 캐시 슬롯을 비우지 못했습니다.");
                    DeleteDirectoryStrict(stagingSlot, "손상 모듈 staging 슬롯을 비우지 못했습니다.");
                    DeleteDirectoryStrict(selfTestSlot, "손상 모듈 self-test 슬롯을 비우지 못했습니다.");

                    Report(progress, download.ModuleId, 0, "MODULE_REPAIR_PURGED");

                    var cached = await cache.DownloadAsync(download, progress, cancellationToken).ConfigureAwait(false);
                    if (cached == null || cached.CacheHit || !cached.RequiresVerification ||
                        !String.Equals(cached.VerificationStatus, "UNVERIFIED", StringComparison.Ordinal))
                        throw new InvalidOperationException("5-9 복구 다운로드는 기존 캐시를 재사용할 수 없습니다.");

                    var verificationRequest = new ModulePackageVerificationRequest
                    {
                        Cache = cached,
                        ModuleId = download.ModuleId,
                        ModuleVersion = download.ModuleVersion,
                        BundlePackagePath = download.PackagePath,
                        ExpectedSha256 = download.ExpectedSha256,
                        ContractSetVersion = request.ContractSetVersion,
                        StateSchemaVersion = request.StateSchemaVersion
                    };
                    var verified = verify(verificationRequest);
                    if (verified == null ||
                        !String.Equals(verified.VerificationStatus, ModulePackageVerifier.VerifiedStatus, StringComparison.Ordinal) ||
                        !String.Equals(verified.ModuleId, download.ModuleId, StringComparison.Ordinal) ||
                        !String.Equals(verified.ModuleVersion, download.ModuleVersion, StringComparison.Ordinal) ||
                        !String.Equals(verified.ArchiveSha256, download.ExpectedSha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("재다운로드한 모듈의 5-4 검증 결과가 Bundle Lock 대상과 일치하지 않습니다.");

                    Report(progress, download.ModuleId, cached.Bytes, "MODULE_REPAIR_VERIFIED");

                    var staged = stage(new ModuleStagingInstallRequest { VerificationRequest = verificationRequest });
                    if (staged == null || staged.AlreadyStaged ||
                        !String.Equals(staged.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                        !String.Equals(staged.ModuleId, download.ModuleId, StringComparison.Ordinal) ||
                        !String.Equals(staged.ModuleVersion, download.ModuleVersion, StringComparison.Ordinal) ||
                        !String.Equals(staged.ArchiveSha256, download.ExpectedSha256, StringComparison.Ordinal) ||
                        !String.Equals(Path.GetFullPath(staged.StagedDirectory), stagingSlot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("재다운로드한 모듈의 5-5 staging 결과가 정확한 복구 슬롯과 일치하지 않습니다.");

                    Report(progress, download.ModuleId, cached.Bytes, "MODULE_REPAIR_STAGED");

                    var passed = selfTest(new ModuleSelfTestRequest
                    {
                        Target = staged,
                        Dependencies = request.Dependencies ?? new List<ModuleSelfTestDependency>()
                    });
                    if (passed == null || passed.AlreadyPassed ||
                        !String.Equals(passed.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal) ||
                        !String.Equals(passed.ModuleId, download.ModuleId, StringComparison.Ordinal) ||
                        !String.Equals(passed.ModuleVersion, download.ModuleVersion, StringComparison.Ordinal) ||
                        !String.Equals(passed.ArchiveSha256, download.ExpectedSha256, StringComparison.Ordinal) ||
                        !String.Equals(Path.GetFullPath(Path.GetDirectoryName(passed.ReceiptFile)), selfTestSlot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("재다운로드한 모듈의 5-6 self-test 결과가 정확한 복구 슬롯과 일치하지 않습니다.");

                    var activeHashAfter = ValidateTargetIsNotActive(root, download);
                    if (!String.Equals(activeHashBefore, activeHashAfter, StringComparison.Ordinal))
                        throw new InvalidOperationException("5-9 복구 중 active-bundle 포인터가 변경되었습니다.");

                    WriteRepairReceipt(
                        receiptFile,
                        request,
                        cached.Bytes,
                        verified.VerificationStatus,
                        staged.InstallStatus,
                        passed.Status);

                    Report(progress, download.ModuleId, cached.Bytes, "MODULE_REPAIR_COMPLETED");
                    return new ModuleDamagedPackageRepairResult
                    {
                        Status = RepairedStatus,
                        BundleRevision = request.BundleRevision,
                        ModuleId = download.ModuleId,
                        ModuleVersion = download.ModuleVersion,
                        ArchiveSha256 = download.ExpectedSha256,
                        StagedDirectory = staged.StagedDirectory,
                        SelfTestReceiptFile = passed.ReceiptFile,
                        RepairReceiptFile = receiptFile,
                        DownloadedFresh = true,
                        ActiveBundleChanged = false,
                        ReleasePointerChanged = false
                    };
                }
                catch
                {
                    SafeDeleteFile(receiptFile);
                    SafeDeleteDirectory(cache.CacheDirectoryForTest(download));
                    SafeDeleteDirectory(stagingSlot);
                    SafeDeleteDirectory(selfTestSlot);
                    throw;
                }
            }
        }

        private static void ValidateRequest(ModuleDamagedPackageRepairRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (!BundleRevisionPattern.IsMatch(request.BundleRevision ?? ""))
                throw new InvalidOperationException("5-9 Bundle revision 형식이 올바르지 않습니다.");
            if (!Sha256Pattern.IsMatch(request.BundleLockSha256 ?? ""))
                throw new InvalidOperationException("5-9 Bundle Lock SHA-256 형식이 올바르지 않습니다.");
            if (request.Channel != "stable" && request.Channel != "staging")
                throw new InvalidOperationException("5-9 Bundle channel이 올바르지 않습니다.");
            if (request.ContractSetVersion != ModulePackageVerifier.SupportedContractSetVersion)
                throw new InvalidOperationException("5-9 Contract Set 버전이 Launcher 지원 범위와 일치하지 않습니다.");
            if (request.StateSchemaVersion < 0)
                throw new InvalidOperationException("5-9 state schema 버전이 올바르지 않습니다.");
            if (!ReasonCodePattern.IsMatch(request.ReasonCode ?? ""))
                throw new InvalidOperationException("5-9 손상 사유 코드 형식이 올바르지 않습니다.");
            if (request.Download == null)
                throw new InvalidOperationException("5-9 재다운로드 대상이 없습니다.");

            ModulePackageDownloadCache.ValidateRequestForTest(request.Download);

            var dependencies = request.Dependencies ?? new List<ModuleSelfTestDependency>();
            if (dependencies.Any(value => value == null))
                throw new InvalidOperationException("5-9 dependency 목록에 빈 항목이 있습니다.");
        }

        private static string ValidateTargetIsNotActive(string moduleRoot, ModulePackageDownloadRequest target)
        {
            var activeFile = Path.Combine(moduleRoot, "active-bundle.json");
            if (!File.Exists(activeFile)) return "";

            ActiveModuleBundleState active;
            try { active = Json.Deserialize<ActiveModuleBundleState>(File.ReadAllText(activeFile, Encoding.UTF8)); }
            catch (Exception error) { throw new InvalidOperationException("active-bundle.json을 읽을 수 없어 손상 모듈을 안전하게 복구할 수 없습니다.", error); }

            if (active == null || active.SchemaVersion != 1 ||
                !String.Equals(active.Status, ModuleBundleActivator.ActiveStatus, StringComparison.Ordinal) ||
                !active.ActivationAtomic || active.Modules == null)
                throw new InvalidOperationException("active-bundle.json 기본 계약이 올바르지 않아 손상 모듈을 복구하지 않습니다.");

            foreach (var entry in active.Modules)
            {
                if (entry == null || !String.Equals(entry.ModuleId, target.ModuleId, StringComparison.Ordinal))
                    continue;
                if (!String.Equals(entry.ModuleVersion, target.ModuleVersion, StringComparison.Ordinal))
                    continue;
                if (!String.Equals(entry.ArchiveSha256, target.ExpectedSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException(ActiveModuleShaConflictCode + ": 같은 moduleId/version의 active SHA가 Bundle Lock 대상과 다릅니다.");
                throw new InvalidOperationException(ActiveModuleRepairBlockedCode + ": active 모듈은 5-8 rollback 후에만 5-9 재다운로드할 수 있습니다.");
            }

            return Sha256File(activeFile);
        }

        private static FileStream OpenExclusiveLock(string path, string busyMessage)
        {
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (String.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("lock parent가 없습니다.");
                Directory.CreateDirectory(parent);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error)
            {
                throw new InvalidOperationException(busyMessage, error);
            }
        }

        private static string DeterministicSlot(string root, string moduleId, string moduleVersion, string sha256)
        {
            var fullRoot = Path.GetFullPath(root);
            var full = Path.GetFullPath(Path.Combine(fullRoot, moduleId, moduleVersion, sha256));
            var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("5-9 모듈 복구 슬롯이 허용 루트 밖으로 벗어났습니다.");
            return full;
        }

        private static void DeleteDirectoryStrict(string path, string message)
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, true);
            if (Directory.Exists(path)) throw new InvalidOperationException(message);
        }

        private static void WriteRepairReceipt(
            string receiptFile,
            ModuleDamagedPackageRepairRequest request,
            long downloadedBytes,
            string verificationStatus,
            string stagingStatus,
            string selfTestStatus)
        {
            var receipt = new ModuleDamagedPackageRepairReceipt
            {
                SchemaVersion = 1,
                Status = RepairedStatus,
                BundleRevision = request.BundleRevision,
                BundleLockSha256 = request.BundleLockSha256,
                Channel = request.Channel,
                ModuleId = request.Download.ModuleId,
                ModuleVersion = request.Download.ModuleVersion,
                ArchiveSha256 = request.Download.ExpectedSha256,
                ContractSetVersion = request.ContractSetVersion,
                StateSchemaVersion = request.StateSchemaVersion,
                ReasonCode = request.ReasonCode,
                DownloadedBytes = downloadedBytes,
                VerificationStatus = verificationStatus,
                StagingStatus = stagingStatus,
                SelfTestStatus = selfTestStatus,
                RepairedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                DownloadedFresh = true,
                ActiveBundleChanged = false,
                ReleasePointerChanged = false
            };

            Directory.CreateDirectory(Path.GetDirectoryName(receiptFile));
            var temporary = receiptFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, Json.Serialize(receipt), new UTF8Encoding(false));
                if (File.Exists(receiptFile)) File.Replace(temporary, receiptFile, null);
                else File.Move(temporary, receiptFile);
            }
            catch
            {
                SafeDeleteFile(temporary);
                throw;
            }
        }

        private static void Report(
            IProgress<ModulePackageDownloadProgress> progress,
            string moduleId,
            long bytes,
            string stage)
        {
            progress?.Report(new ModulePackageDownloadProgress
            {
                ModuleId = moduleId,
                BytesReceived = bytes,
                Stage = stage
            });
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static void SafeDeleteFile(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void SafeDeleteDirectory(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }
    }
}
