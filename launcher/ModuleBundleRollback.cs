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
    internal sealed class ModuleBundleRollbackResult
    {
        public ModuleBundleActivationResult Activation { get; set; }
        public bool RolledBack { get; set; }
        public string FailedBundleRevision { get; set; }
        public string ActiveBundleRevision { get; set; }
        public string Status { get; set; }
        public string ReceiptFile { get; set; }
    }

    internal sealed class ModuleBundleRollbackPlan
    {
        public int SchemaVersion { get; set; }
        public string CandidateBundleRevision { get; set; }
        public bool PreviousAvailable { get; set; }
        public string PreviousBundleRevision { get; set; }
        public string PreviousBundleSha256 { get; set; }
        public string Channel { get; set; }
        public string PreparedAtUtc { get; set; }
        public bool ReleasePointerChanged { get; set; }
    }

    internal sealed class ModuleBundleRollbackReceipt
    {
        public int SchemaVersion { get; set; }
        public string Status { get; set; }
        public string FailedBundleRevision { get; set; }
        public string RestoredBundleRevision { get; set; }
        public string ReasonCode { get; set; }
        public string RolledBackAtUtc { get; set; }
        public bool ActiveBundleChanged { get; set; }
        public bool ReleasePointerChanged { get; set; }
    }

    internal static class ModuleBundleRollback
    {
        public const string RolledBackStatus = "ROLLED_BACK";
        public const string RollbackUnavailableStatus = "ROLLBACK_UNAVAILABLE";

        private static readonly string[] RequiredModuleIds =
        {
            "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell"
        };

        private static readonly Regex BundleRevisionPattern = new Regex("^B[0-9]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SafeReasonPattern = new Regex("^[A-Z0-9_.-]{1,64}$", RegexOptions.CultureInvariant);
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static ModuleBundleRollbackResult ActivateAndVerify(
            ModuleBundleActivationRequest request,
            Action<ActiveModuleBundleState> readinessCheck)
        {
            LauncherPaths.EnsureDirectories();
            return ActivateAndVerifyInternal(
                request,
                readinessCheck,
                LauncherPaths.ModuleRoot,
                value => ModuleBundleActivator.Activate(value));
        }

        public static ModuleBundleRollbackResult RollbackCurrentToPrevious(
            string failedBundleRevision,
            string reasonCode)
        {
            LauncherPaths.EnsureDirectories();
            return RollbackInternal(LauncherPaths.ModuleRoot, failedBundleRevision, reasonCode, true);
        }

        internal static ModuleBundleRollbackResult ActivateAndVerifyForTest(
            ModuleBundleActivationRequest request,
            Action<ActiveModuleBundleState> readinessCheck,
            string moduleRoot)
        {
            if (String.IsNullOrWhiteSpace(moduleRoot)) throw new ArgumentException("moduleRoot");
            var root = Path.GetFullPath(moduleRoot);
            Directory.CreateDirectory(root);
            return ActivateAndVerifyInternal(
                request,
                readinessCheck,
                root,
                value => ModuleBundleActivator.ActivateForTest(value, root));
        }

        internal static ModuleBundleRollbackResult RollbackCurrentToPreviousForTest(
            string moduleRoot,
            string failedBundleRevision,
            string reasonCode)
        {
            if (String.IsNullOrWhiteSpace(moduleRoot)) throw new ArgumentException("moduleRoot");
            return RollbackInternal(Path.GetFullPath(moduleRoot), failedBundleRevision, reasonCode, true);
        }

        private static ModuleBundleRollbackResult ActivateAndVerifyInternal(
            ModuleBundleActivationRequest request,
            Action<ActiveModuleBundleState> readinessCheck,
            string moduleRoot,
            Func<ModuleBundleActivationRequest, ModuleBundleActivationResult> activate)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (readinessCheck == null) throw new ArgumentNullException("readinessCheck");
            if (activate == null) throw new ArgumentNullException("activate");

            Directory.CreateDirectory(moduleRoot);
            Directory.CreateDirectory(RollbackRoot(moduleRoot));
            var candidateRevision = ReadCandidateRevision(request.BundleLockFile);
            PrepareRollbackPlan(moduleRoot, candidateRevision);

            var activation = activate(request);
            var active = ReadActiveState(ActiveFile(moduleRoot));
            if (active == null || !String.Equals(active.BundleRevision, activation.BundleRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("활성 Bundle readback이 activation 결과와 일치하지 않습니다.");

            try
            {
                readinessCheck(active);
                return new ModuleBundleRollbackResult
                {
                    Activation = activation,
                    RolledBack = false,
                    FailedBundleRevision = null,
                    ActiveBundleRevision = active.BundleRevision,
                    Status = activation.Status,
                    ReceiptFile = null
                };
            }
            catch (Exception activationError)
            {
                ModuleBundleRollbackResult rollback;
                try
                {
                    rollback = RollbackInternal(moduleRoot, active.BundleRevision, "READINESS_FAILURE", true);
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        "새 Bundle 준비와 이전 Bundle 자동 복구가 모두 실패했습니다.",
                        new AggregateException(activationError, rollbackError));
                }

                var restored = ReadActiveState(ActiveFile(moduleRoot));
                if (restored == null || !String.Equals(restored.BundleRevision, rollback.ActiveBundleRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException("rollback 후 active Bundle readback이 복구 결과와 일치하지 않습니다.");
                try
                {
                    readinessCheck(restored);
                }
                catch (Exception rollbackReadinessError)
                {
                    throw new InvalidOperationException(
                        "새 Bundle 준비 실패 후 이전 Bundle은 복구됐지만 준비 확인에 실패했습니다.",
                        new AggregateException(activationError, rollbackReadinessError));
                }

                rollback.Activation = activation;
                return rollback;
            }
        }

        private static void PrepareRollbackPlan(string moduleRoot, string candidateRevision)
        {
            var activeFile = ActiveFile(moduleRoot);
            var previousFile = PreviousFile(moduleRoot);
            var planFile = PlanFile(moduleRoot);
            var current = File.Exists(activeFile) ? ReadActiveState(activeFile) : null;

            if (current != null && String.Equals(current.BundleRevision, candidateRevision, StringComparison.Ordinal))
                return;

            var plan = new ModuleBundleRollbackPlan
            {
                SchemaVersion = 1,
                CandidateBundleRevision = candidateRevision,
                PreviousAvailable = current != null,
                PreviousBundleRevision = current == null ? "" : current.BundleRevision,
                PreviousBundleSha256 = "",
                Channel = current == null ? "" : current.Channel,
                PreparedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ReleasePointerChanged = false
            };

            if (current == null)
            {
                SafeDeleteFile(previousFile);
            }
            else
            {
                ValidateRollbackTarget(current, moduleRoot);
                var bytes = File.ReadAllBytes(activeFile);
                WriteAtomicBytes(previousFile, bytes);
                plan.PreviousBundleSha256 = Sha256(bytes);
            }

            WriteJsonAtomic(planFile, plan);
        }

        private static ModuleBundleRollbackResult RollbackInternal(
            string moduleRoot,
            string failedBundleRevision,
            string reasonCode,
            bool clearFailedOnRollbackFailure)
        {
            ValidateRollbackRequest(failedBundleRevision, reasonCode);
            var activeFile = ActiveFile(moduleRoot);
            var planFile = PlanFile(moduleRoot);
            var previousFile = PreviousFile(moduleRoot);
            var receiptFile = ReceiptFile(moduleRoot);
            Directory.CreateDirectory(RollbackRoot(moduleRoot));

            if (!File.Exists(planFile))
                throw new InvalidOperationException("이전 Bundle rollback plan이 없습니다.");

            var plan = ReadRollbackPlan(planFile);
            if (!String.Equals(plan.CandidateBundleRevision, failedBundleRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("rollback plan이 실패한 Bundle revision과 일치하지 않습니다.");

            ActiveModuleBundleState failed = null;
            if (File.Exists(activeFile))
            {
                try { failed = ReadActiveState(activeFile); }
                catch { }
            }
            if (failed != null && !String.Equals(failed.BundleRevision, failedBundleRevision, StringComparison.Ordinal))
                throw new InvalidOperationException("현재 active Bundle이 rollback 대상 실패 Bundle과 다릅니다.");

            if (!plan.PreviousAvailable)
            {
                if (failed != null && String.Equals(failed.BundleRevision, failedBundleRevision, StringComparison.Ordinal))
                    SafeDeleteFile(activeFile);
                WriteRollbackReceipt(receiptFile, RollbackUnavailableStatus, failedBundleRevision, "", reasonCode, false);
                throw new InvalidOperationException("자동 복구할 이전 active Bundle이 없습니다.");
            }

            try
            {
                if (!File.Exists(previousFile))
                    throw new InvalidOperationException("이전 Bundle snapshot이 없습니다.");
                var previousBytes = File.ReadAllBytes(previousFile);
                if (!String.Equals(Sha256(previousBytes), plan.PreviousBundleSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("이전 Bundle snapshot SHA-256이 rollback plan과 일치하지 않습니다.");

                var previous = DeserializeActiveState(previousBytes, "이전 Bundle snapshot");
                if (!String.Equals(previous.BundleRevision, plan.PreviousBundleRevision, StringComparison.Ordinal) ||
                    !String.Equals(previous.Channel, plan.Channel, StringComparison.Ordinal))
                    throw new InvalidOperationException("이전 Bundle snapshot이 rollback plan과 일치하지 않습니다.");
                if (failed != null &&
                    (!String.Equals(failed.ParentBundleRevision, previous.BundleRevision, StringComparison.Ordinal) ||
                     !String.Equals(failed.Channel, previous.Channel, StringComparison.Ordinal)))
                    throw new InvalidOperationException("현재 Bundle의 parent/channel이 이전 Bundle snapshot과 일치하지 않습니다.");

                ValidateRollbackTarget(previous, moduleRoot);
                WriteAtomicBytes(activeFile, previousBytes);
                var restored = ReadActiveState(activeFile);
                ValidateRollbackTarget(restored, moduleRoot);
                if (!String.Equals(restored.BundleRevision, previous.BundleRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException("rollback active readback이 이전 Bundle revision과 일치하지 않습니다.");

                WriteRollbackReceipt(
                    receiptFile,
                    RolledBackStatus,
                    failedBundleRevision,
                    restored.BundleRevision,
                    reasonCode,
                    true);

                return new ModuleBundleRollbackResult
                {
                    Activation = null,
                    RolledBack = true,
                    FailedBundleRevision = failedBundleRevision,
                    ActiveBundleRevision = restored.BundleRevision,
                    Status = RolledBackStatus,
                    ReceiptFile = receiptFile
                };
            }
            catch
            {
                if (clearFailedOnRollbackFailure && failed != null &&
                    String.Equals(failed.BundleRevision, failedBundleRevision, StringComparison.Ordinal))
                    SafeDeleteFile(activeFile);
                throw;
            }
        }

        private static void ValidateRollbackTarget(ActiveModuleBundleState state, string moduleRoot)
        {
            if (state == null || state.SchemaVersion != 1 ||
                !String.Equals(state.Status, ModuleBundleActivator.ActiveStatus, StringComparison.Ordinal) ||
                !state.ActivationAtomic || !BundleRevisionPattern.IsMatch(state.BundleRevision ?? "") ||
                !BundleRevisionPattern.IsMatch(state.ParentBundleRevision ?? "") ||
                (state.Channel != "stable" && state.Channel != "staging") ||
                state.ContractSetVersion < 1 || !Sha256Pattern.IsMatch(state.BundleLockSha256 ?? "") ||
                state.Modules == null || state.Modules.Count != RequiredModuleIds.Length)
                throw new InvalidOperationException("이전 Bundle active snapshot 기본 계약이 올바르지 않습니다.");

            var byId = new Dictionary<string, ActiveModuleBundleEntry>(StringComparer.Ordinal);
            foreach (var entry in state.Modules)
            {
                if (entry == null || Array.IndexOf(RequiredModuleIds, entry.ModuleId) < 0 || byId.ContainsKey(entry.ModuleId) ||
                    String.IsNullOrWhiteSpace(entry.ModuleVersion) || !Sha256Pattern.IsMatch(entry.ArchiveSha256 ?? "") ||
                    !Sha256Pattern.IsMatch(entry.ManifestSha256 ?? "") || !Sha256Pattern.IsMatch(entry.SelfTestReceiptSha256 ?? ""))
                    throw new InvalidOperationException("이전 Bundle module snapshot 계약이 올바르지 않습니다.");
                byId.Add(entry.ModuleId, entry);
            }
            if (RequiredModuleIds.Any(id => !byId.ContainsKey(id)))
                throw new InvalidOperationException("이전 Bundle snapshot 필수 모듈이 누락되었습니다.");

            foreach (var id in RequiredModuleIds)
                ValidateRollbackModule(state, byId[id], byId, moduleRoot);
        }

        private static void ValidateRollbackModule(
            ActiveModuleBundleState state,
            ActiveModuleBundleEntry entry,
            IDictionary<string, ActiveModuleBundleEntry> byId,
            string moduleRoot)
        {
            var stagingRoot = Path.Combine(moduleRoot, "staging");
            var selfTestRoot = Path.Combine(moduleRoot, "self-tests");
            var expectedStage = DeterministicSlot(stagingRoot, entry.ModuleId, entry.ModuleVersion, entry.ArchiveSha256);
            if (!String.Equals(Path.GetFullPath(entry.StagedDirectory ?? ""), expectedStage, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expectedStage))
                throw new InvalidOperationException("rollback target Staging 슬롯이 정확한 deterministic path가 아닙니다: " + entry.ModuleId);

            var manifestFile = Path.Combine(expectedStage, ModulePackageVerifier.ManifestPath);
            var stageReceiptFile = Path.Combine(expectedStage, ModuleStagingInstaller.InstallReceiptName);
            var selfReceiptFile = Path.Combine(selfTestRoot, entry.ModuleId, entry.ModuleVersion, entry.ArchiveSha256, ModuleStagingSelfTest.ReceiptName);
            if (!File.Exists(manifestFile) || !File.Exists(stageReceiptFile) || !File.Exists(selfReceiptFile))
                throw new InvalidOperationException("rollback target 검증 receipt/manifest가 누락되었습니다: " + entry.ModuleId);
            if (!String.Equals(Sha256File(manifestFile), entry.ManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(Sha256File(selfReceiptFile), entry.SelfTestReceiptSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("rollback target manifest/self-test SHA가 active snapshot과 일치하지 않습니다: " + entry.ModuleId);

            var stage = ReadObject(stageReceiptFile, "staging receipt");
            Require(stage, "installStatus", ModuleStagingInstaller.StagedStatus, entry.ModuleId);
            Require(stage, "moduleId", entry.ModuleId, entry.ModuleId);
            Require(stage, "moduleVersion", entry.ModuleVersion, entry.ModuleId);
            Require(stage, "archiveSha256", entry.ArchiveSha256, entry.ModuleId);
            RequireInt(stage, "contractSetVersion", state.ContractSetVersion, entry.ModuleId);
            RequireInt(stage, "stateSchemaVersion", entry.StateSchemaVersion, entry.ModuleId);
            RequireBool(stage, "activationAllowed", false, entry.ModuleId);
            RequireBool(stage, "activeBundleChanged", false, entry.ModuleId);

            var self = ReadObject(selfReceiptFile, "self-test receipt");
            Require(self, "status", ModuleStagingSelfTest.PassedStatus, entry.ModuleId);
            Require(self, "moduleId", entry.ModuleId, entry.ModuleId);
            Require(self, "moduleVersion", entry.ModuleVersion, entry.ModuleId);
            Require(self, "archiveSha256", entry.ArchiveSha256, entry.ModuleId);
            RequireInt(self, "contractSetVersion", state.ContractSetVersion, entry.ModuleId);
            RequireInt(self, "stateSchemaVersion", entry.StateSchemaVersion, entry.ModuleId);
            Require(self, "manifestSha256", entry.ManifestSha256, entry.ModuleId);
            Require(self, "stageReceiptSha256", Sha256File(stageReceiptFile), entry.ModuleId);
            RequireBool(self, "assemblyMetadataLoad", true, entry.ModuleId);
            RequireBool(self, "activationAllowed", false, entry.ModuleId);
            RequireBool(self, "activeBundleChanged", false, entry.ModuleId);

            ModulePackageManifest manifest;
            try { manifest = Json.Deserialize<ModulePackageManifest>(ReadUtf8(manifestFile)); }
            catch (Exception error) { throw new InvalidOperationException("rollback target Package Manifest를 읽을 수 없습니다: " + entry.ModuleId, error); }
            if (manifest == null || manifest.State == null || manifest.PrimaryArtifact == null || manifest.Files == null ||
                !String.Equals(manifest.ModuleId, entry.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(manifest.ModuleVersion, entry.ModuleVersion, StringComparison.Ordinal) ||
                manifest.ContractSetVersion != state.ContractSetVersion || manifest.State.StateSchemaVersion != entry.StateSchemaVersion)
                throw new InvalidOperationException("rollback target Package Manifest가 active snapshot과 일치하지 않습니다: " + entry.ModuleId);

            ValidateDeclaredFiles(expectedStage, manifest, entry.ModuleId);
            var dependencyIds = manifest.DependencyModuleIds ?? new List<string>();
            var fingerprint = DependencyFingerprint(dependencyIds, byId);
            Require(self, "dependencyFingerprint", fingerprint, entry.ModuleId);
            RequireInt(self, "dependencyCount", dependencyIds.Count, entry.ModuleId);
        }

        private static void ValidateDeclaredFiles(string stagedDirectory, ModulePackageManifest manifest, string moduleId)
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file == null || !SafeRelativePath(file.Path) || file.Size < 0 || !Sha256Pattern.IsMatch(file.Sha256 ?? ""))
                    throw new InvalidOperationException("rollback target declared file 계약이 올바르지 않습니다: " + moduleId);
                var full = SafeCombine(stagedDirectory, file.Path);
                if (!declared.Add(full) || !File.Exists(full) || new FileInfo(full).Length != file.Size ||
                    !String.Equals(Sha256File(full), file.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("rollback target declared file 무결성이 깨졌습니다: " + moduleId + "/" + file.Path);
            }
            if (!SafeRelativePath(manifest.PrimaryArtifact.Path) ||
                !manifest.Files.Any(file => file != null && String.Equals(file.Path, manifest.PrimaryArtifact.Path, StringComparison.Ordinal)))
                throw new InvalidOperationException("rollback target primary artifact가 선언 파일에 없습니다: " + moduleId);
        }

        private static string DependencyFingerprint(
            IEnumerable<string> dependencyIds,
            IDictionary<string, ActiveModuleBundleEntry> byId)
        {
            var ids = (dependencyIds ?? Enumerable.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToList();
            if (ids.Any(id => !byId.ContainsKey(id)))
                throw new InvalidOperationException("rollback target dependency가 active Bundle에 없습니다.");
            var text = String.Join("\n", ids.Select(id =>
            {
                var item = byId[id];
                return item.ModuleId + "=" + item.ModuleVersion + "@" + item.ArchiveSha256;
            }));
            return Sha256(Encoding.UTF8.GetBytes(text));
        }

        private static ModuleBundleRollbackPlan ReadRollbackPlan(string path)
        {
            ModuleBundleRollbackPlan plan;
            try { plan = Json.Deserialize<ModuleBundleRollbackPlan>(ReadUtf8(path)); }
            catch (Exception error) { throw new InvalidOperationException("rollback plan을 읽을 수 없습니다.", error); }
            if (plan == null || plan.SchemaVersion != 1 || !BundleRevisionPattern.IsMatch(plan.CandidateBundleRevision ?? "") || plan.ReleasePointerChanged)
                throw new InvalidOperationException("rollback plan 계약이 올바르지 않습니다.");
            if (plan.PreviousAvailable)
            {
                if (!BundleRevisionPattern.IsMatch(plan.PreviousBundleRevision ?? "") ||
                    !Sha256Pattern.IsMatch(plan.PreviousBundleSha256 ?? "") ||
                    (plan.Channel != "stable" && plan.Channel != "staging"))
                    throw new InvalidOperationException("rollback plan previous Bundle 계약이 올바르지 않습니다.");
            }
            return plan;
        }

        private static ActiveModuleBundleState ReadActiveState(string path)
        {
            if (!File.Exists(path)) return null;
            return DeserializeActiveState(File.ReadAllBytes(path), "active Bundle");
        }

        private static ActiveModuleBundleState DeserializeActiveState(byte[] bytes, string label)
        {
            try
            {
                var value = Json.Deserialize<ActiveModuleBundleState>(Encoding.UTF8.GetString(bytes ?? new byte[0]));
                if (value == null) throw new InvalidOperationException(label + "가 비어 있습니다.");
                return value;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(label + " JSON을 읽을 수 없습니다.", error);
            }
        }

        private static string ReadCandidateRevision(string bundleLockFile)
        {
            if (String.IsNullOrWhiteSpace(bundleLockFile) || !File.Exists(bundleLockFile))
                throw new InvalidOperationException("rollback 준비용 Bundle Lock 파일이 없습니다.");
            try
            {
                var bundle = Json.Deserialize<ModuleBundleLock>(ReadUtf8(bundleLockFile));
                if (bundle == null || !BundleRevisionPattern.IsMatch(bundle.BundleRevision ?? ""))
                    throw new InvalidOperationException("candidate Bundle revision 형식이 올바르지 않습니다.");
                return bundle.BundleRevision;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception error) { throw new InvalidOperationException("candidate Bundle Lock을 읽을 수 없습니다.", error); }
        }

        private static IDictionary<string, object> ReadObject(string path, string label)
        {
            try
            {
                var value = Json.DeserializeObject(ReadUtf8(path)) as IDictionary<string, object>;
                if (value == null) throw new InvalidOperationException(label + " root는 object여야 합니다.");
                return value;
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception error) { throw new InvalidOperationException(label + " JSON을 읽을 수 없습니다.", error); }
        }

        private static void Require(IDictionary<string, object> value, string key, string expected, string moduleId)
        {
            object raw;
            if (!value.TryGetValue(key, out raw) || !String.Equals(Convert.ToString(raw, CultureInfo.InvariantCulture), expected, StringComparison.Ordinal))
                throw new InvalidOperationException("rollback target receipt " + key + " 불일치: " + moduleId);
        }

        private static void RequireInt(IDictionary<string, object> value, string key, int expected, string moduleId)
        {
            object raw;
            int actual;
            if (!value.TryGetValue(key, out raw) || !Int32.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out actual) || actual != expected)
                throw new InvalidOperationException("rollback target receipt " + key + " 불일치: " + moduleId);
        }

        private static void RequireBool(IDictionary<string, object> value, string key, bool expected, string moduleId)
        {
            object raw;
            bool actual;
            if (!value.TryGetValue(key, out raw) || !Boolean.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), out actual) || actual != expected)
                throw new InvalidOperationException("rollback target receipt " + key + " 불일치: " + moduleId);
        }

        private static void ValidateRollbackRequest(string failedBundleRevision, string reasonCode)
        {
            if (!BundleRevisionPattern.IsMatch(failedBundleRevision ?? ""))
                throw new InvalidOperationException("rollback 실패 Bundle revision 형식이 올바르지 않습니다.");
            if (!SafeReasonPattern.IsMatch(reasonCode ?? ""))
                throw new InvalidOperationException("rollback reasonCode 형식이 올바르지 않습니다.");
        }

        private static void WriteRollbackReceipt(
            string path,
            string status,
            string failedRevision,
            string restoredRevision,
            string reasonCode,
            bool activeChanged)
        {
            WriteJsonAtomic(path, new ModuleBundleRollbackReceipt
            {
                SchemaVersion = 1,
                Status = status,
                FailedBundleRevision = failedRevision,
                RestoredBundleRevision = restoredRevision ?? "",
                ReasonCode = reasonCode,
                RolledBackAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ActiveBundleChanged = activeChanged,
                ReleasePointerChanged = false
            });
        }

        private static void WriteJsonAtomic(string path, object value)
        {
            WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(Json.Serialize(value)));
        }

        private static void WriteAtomicBytes(string path, byte[] bytes)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (String.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("atomic write parent가 없습니다.");
            Directory.CreateDirectory(parent);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch
            {
                SafeDeleteFile(temporary);
                throw;
            }
        }

        private static string DeterministicSlot(string root, string moduleId, string version, string sha256)
        {
            return Path.GetFullPath(Path.Combine(root, moduleId, version, sha256));
        }

        private static string SafeCombine(string root, string relative)
        {
            var rootFull = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("rollback target file path가 Staging 슬롯 밖을 가리킵니다.");
            return full;
        }

        private static bool SafeRelativePath(string path)
        {
            return !String.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 &&
                path.Split('/').All(segment => !String.IsNullOrWhiteSpace(segment) && segment != "." && segment != "..");
        }

        private static string ReadUtf8(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private static string Sha256File(string path)
        {
            return Sha256(File.ReadAllBytes(path));
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void SafeDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static string RollbackRoot(string moduleRoot) { return Path.Combine(moduleRoot, "rollback"); }
        private static string ActiveFile(string moduleRoot) { return Path.Combine(moduleRoot, "active-bundle.json"); }
        private static string PreviousFile(string moduleRoot) { return Path.Combine(RollbackRoot(moduleRoot), "previous-bundle.json"); }
        private static string PlanFile(string moduleRoot) { return Path.Combine(RollbackRoot(moduleRoot), "rollback-plan.json"); }
        private static string ReceiptFile(string moduleRoot) { return Path.Combine(RollbackRoot(moduleRoot), "last-rollback.json"); }
    }
}
