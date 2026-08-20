using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleSelfTestDependency
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public string StagedDirectory { get; set; }
    }

    internal sealed class ModuleSelfTestRequest
    {
        public ModuleStagingInstallResult Target { get; set; }
        public List<ModuleSelfTestDependency> Dependencies { get; set; }
    }

    internal sealed class ModuleSelfTestResult
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public string ReceiptFile { get; set; }
        public bool AlreadyPassed { get; set; }
        public string Status { get; set; }
    }

    internal static class ModuleStagingSelfTest
    {
        public const string PassedStatus = "SELF_TEST_PASSED";
        public const string ReceiptName = "self-test.json";

        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SemVerPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);

        public static ModuleSelfTestResult Run(ModuleSelfTestRequest request)
        {
            return RunInternal(request, LauncherPaths.ModuleStaging, LauncherPaths.ModuleSelfTests);
        }

        internal static ModuleSelfTestResult RunForTest(
            ModuleSelfTestRequest request,
            string stagingRoot,
            string selfTestRoot)
        {
            return RunInternal(request, stagingRoot, selfTestRoot);
        }

        private static ModuleSelfTestResult RunInternal(
            ModuleSelfTestRequest request,
            string stagingRoot,
            string selfTestRoot)
        {
            ValidateRequest(request, stagingRoot, selfTestRoot);

            var target = request.Target;
            var targetReceipt = ReadStageReceipt(
                target.StagedDirectory,
                target.ModuleId,
                target.ModuleVersion,
                target.ArchiveSha256);
            var manifest = ReadAndValidateManifest(target.StagedDirectory, targetReceipt);
            ValidateStagedFiles(target.StagedDirectory, manifest);
            ValidatePrimaryArtifact(target.StagedDirectory, manifest);

            var dependencies = (request.Dependencies ?? new List<ModuleSelfTestDependency>())
                .OrderBy(value => value.ModuleId, StringComparer.Ordinal)
                .ToList();
            ValidateDependencies(manifest, dependencies, stagingRoot, targetReceipt.ContractSetVersion);

            var stageReceiptSha256 = Sha256File(Path.Combine(target.StagedDirectory, ModuleStagingInstaller.InstallReceiptName));
            var manifestSha256 = Sha256File(Path.Combine(target.StagedDirectory, ModulePackageVerifier.ManifestPath));
            var dependencyFingerprint = DependencyFingerprint(dependencies);
            var receiptDirectory = ReceiptDirectory(selfTestRoot, target.ModuleId, target.ModuleVersion, target.ArchiveSha256);
            var receiptPath = Path.Combine(receiptDirectory, ReceiptName);

            if (File.Exists(receiptPath) && ExistingReceiptMatches(
                receiptPath,
                target,
                targetReceipt.ContractSetVersion,
                targetReceipt.StateSchemaVersion,
                stageReceiptSha256,
                manifestSha256,
                dependencyFingerprint))
            {
                return Result(target, receiptPath, true);
            }

            Directory.CreateDirectory(receiptDirectory);
            var temporary = receiptPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteReceipt(
                    temporary,
                    target,
                    targetReceipt.ContractSetVersion,
                    targetReceipt.StateSchemaVersion,
                    stageReceiptSha256,
                    manifestSha256,
                    dependencyFingerprint,
                    dependencies.Count);
                if (File.Exists(receiptPath)) File.Replace(temporary, receiptPath, null);
                else File.Move(temporary, receiptPath);
            }
            catch
            {
                SafeDeleteFile(temporary);
                throw;
            }

            if (!ExistingReceiptMatches(
                receiptPath,
                target,
                targetReceipt.ContractSetVersion,
                targetReceipt.StateSchemaVersion,
                stageReceiptSha256,
                manifestSha256,
                dependencyFingerprint))
            {
                SafeDeleteFile(receiptPath);
                throw new InvalidOperationException("모듈 self-test receipt readback 검증에 실패했습니다.");
            }

            return Result(target, receiptPath, false);
        }

        private sealed class StageReceipt
        {
            public string ModuleId { get; set; }
            public string ModuleVersion { get; set; }
            public string ArchiveSha256 { get; set; }
            public string ManifestSha256 { get; set; }
            public int ContractSetVersion { get; set; }
            public int StateSchemaVersion { get; set; }
        }

        private static void ValidateRequest(ModuleSelfTestRequest request, string stagingRoot, string selfTestRoot)
        {
            if (request == null || request.Target == null)
                throw new ArgumentNullException("request");
            if (String.IsNullOrWhiteSpace(stagingRoot) || String.IsNullOrWhiteSpace(selfTestRoot))
                throw new ArgumentException("self-test root");

            var target = request.Target;
            if (!String.Equals(target.InstallStatus, ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal))
                throw new InvalidOperationException("5-5 STAGED 결과만 self-test할 수 있습니다.");
            if (String.IsNullOrWhiteSpace(target.ModuleId) ||
                !SemVerPattern.IsMatch(target.ModuleVersion ?? "") ||
                !Sha256Pattern.IsMatch(target.ArchiveSha256 ?? "") ||
                String.IsNullOrWhiteSpace(target.StagedDirectory) ||
                !Directory.Exists(target.StagedDirectory))
                throw new InvalidOperationException("5-5 Staging 결과 형식이 올바르지 않습니다.");

            EnsureUnderRoot(Path.GetFullPath(stagingRoot), Path.GetFullPath(target.StagedDirectory));
            var expected = Path.GetFullPath(Path.Combine(stagingRoot, target.ModuleId, target.ModuleVersion, target.ArchiveSha256));
            if (!String.Equals(expected, Path.GetFullPath(target.StagedDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staging 슬롯이 module/version/SHA 결정 경로와 일치하지 않습니다.");
        }

        private static StageReceipt ReadStageReceipt(string stagedDirectory, string moduleId, string version, string sha256)
        {
            var path = Path.Combine(stagedDirectory, ModuleStagingInstaller.InstallReceiptName);
            if (!File.Exists(path))
                throw new InvalidOperationException("5-5 staging-install.json이 없습니다.");

            IDictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("5-5 staging-install.json을 읽을 수 없습니다.", error);
            }

            if (root == null ||
                Convert.ToInt32(root["schemaVersion"], CultureInfo.InvariantCulture) != 1 ||
                !String.Equals(Convert.ToString(root["installStatus"], CultureInfo.InvariantCulture), ModuleStagingInstaller.StagedStatus, StringComparison.Ordinal) ||
                !String.Equals(Convert.ToString(root["moduleId"], CultureInfo.InvariantCulture), moduleId, StringComparison.Ordinal) ||
                !String.Equals(Convert.ToString(root["moduleVersion"], CultureInfo.InvariantCulture), version, StringComparison.Ordinal) ||
                !String.Equals(Convert.ToString(root["archiveSha256"], CultureInfo.InvariantCulture), sha256, StringComparison.Ordinal) ||
                Convert.ToBoolean(root["activationAllowed"], CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(root["activeBundleChanged"], CultureInfo.InvariantCulture))
                throw new InvalidOperationException("5-5 STAGED receipt가 self-test 대상과 일치하지 않습니다.");

            return new StageReceipt
            {
                ModuleId = moduleId,
                ModuleVersion = version,
                ArchiveSha256 = sha256,
                ManifestSha256 = Convert.ToString(root["manifestSha256"], CultureInfo.InvariantCulture),
                ContractSetVersion = Convert.ToInt32(root["contractSetVersion"], CultureInfo.InvariantCulture),
                StateSchemaVersion = Convert.ToInt32(root["stateSchemaVersion"], CultureInfo.InvariantCulture)
            };
        }

        private static ModulePackageManifest ReadAndValidateManifest(string stagedDirectory, StageReceipt receipt)
        {
            var path = Path.Combine(stagedDirectory, ModulePackageVerifier.ManifestPath);
            if (!File.Exists(path))
                throw new InvalidOperationException("Staging 슬롯에 package.manifest.json이 없습니다.");

            var actualManifestSha256 = Sha256File(path);
            if (!String.Equals(actualManifestSha256, receipt.ManifestSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Staging Package Manifest SHA가 5-5 receipt와 일치하지 않습니다.");

            ModulePackageManifest manifest;
            try
            {
                manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(File.ReadAllText(path));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Staging Package Manifest를 읽을 수 없습니다.", error);
            }

            if (manifest == null || manifest.PrimaryArtifact == null || manifest.State == null || manifest.Files == null ||
                !String.Equals(manifest.ModuleId, receipt.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(manifest.ModuleVersion, receipt.ModuleVersion, StringComparison.Ordinal) ||
                manifest.ContractSetVersion != receipt.ContractSetVersion ||
                manifest.State.StateSchemaVersion != receipt.StateSchemaVersion)
                throw new InvalidOperationException("Staging Package Manifest 계약이 5-5 receipt와 일치하지 않습니다.");

            return manifest;
        }

        private static void ValidateStagedFiles(string stagedDirectory, ModulePackageManifest manifest)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                ModulePackageVerifier.ManifestPath,
                ModuleStagingInstaller.InstallReceiptName
            };

            foreach (var file in manifest.Files)
            {
                if (file == null || String.IsNullOrWhiteSpace(file.Path) || !Sha256Pattern.IsMatch(file.Sha256 ?? "") || file.Size <= 0)
                    throw new InvalidOperationException("Self-test 대상 Manifest files 계약이 올바르지 않습니다.");
                expected.Add(file.Path);
                var path = ResolveUnderRoot(stagedDirectory, file.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
                    !String.Equals(Sha256File(path), file.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("Staging 모듈 파일 무결성 self-test에 실패했습니다: " + file.Path);
            }

            var actual = Directory.GetFiles(stagedDirectory, "*", SearchOption.AllDirectories)
                .Select(path => RelativePath(stagedDirectory, path))
                .ToArray();
            if (actual.Length != expected.Count || actual.Any(path => !expected.Contains(path)))
                throw new InvalidOperationException("Staging 모듈 파일 집합에 예상하지 못한 파일이 있습니다.");
        }

        private static void ValidatePrimaryArtifact(string stagedDirectory, ModulePackageManifest manifest)
        {
            var primary = manifest.PrimaryArtifact.Path;
            if (String.IsNullOrWhiteSpace(primary))
                throw new InvalidOperationException("모듈 primary artifact 경로가 없습니다.");
            var path = ResolveUnderRoot(stagedDirectory, primary);
            if (!File.Exists(path))
                throw new InvalidOperationException("모듈 primary artifact가 없습니다.");

            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(path);
                if (assemblyName == null || String.IsNullOrWhiteSpace(assemblyName.Name))
                    throw new InvalidOperationException("관리형 assembly metadata가 비어 있습니다.");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("모듈 primary artifact 기본 로드 가능성 검사에 실패했습니다.", error);
            }
        }

        private static void ValidateDependencies(
            ModulePackageManifest manifest,
            List<ModuleSelfTestDependency> dependencies,
            string stagingRoot,
            int expectedContractSetVersion)
        {
            var expectedIds = (manifest.DependencyModuleIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var suppliedIds = dependencies.Select(value => value == null ? null : value.ModuleId).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (expectedIds.Length != suppliedIds.Length || !expectedIds.SequenceEqual(suppliedIds, StringComparer.Ordinal))
                throw new InvalidOperationException("모듈 dependency 집합이 Package Manifest와 정확히 일치하지 않습니다.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in dependencies)
            {
                if (dependency == null || !seen.Add(dependency.ModuleId) ||
                    String.IsNullOrWhiteSpace(dependency.ModuleId) ||
                    !SemVerPattern.IsMatch(dependency.ModuleVersion ?? "") ||
                    !Sha256Pattern.IsMatch(dependency.ArchiveSha256 ?? "") ||
                    String.IsNullOrWhiteSpace(dependency.StagedDirectory) ||
                    !Directory.Exists(dependency.StagedDirectory))
                    throw new InvalidOperationException("모듈 dependency Staging 정보가 올바르지 않습니다.");

                EnsureUnderRoot(Path.GetFullPath(stagingRoot), Path.GetFullPath(dependency.StagedDirectory));
                var expectedPath = Path.GetFullPath(Path.Combine(stagingRoot, dependency.ModuleId, dependency.ModuleVersion, dependency.ArchiveSha256));
                if (!String.Equals(expectedPath, Path.GetFullPath(dependency.StagedDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("dependency Staging 슬롯이 module/version/SHA 결정 경로와 일치하지 않습니다.");

                var receipt = ReadStageReceipt(
                    dependency.StagedDirectory,
                    dependency.ModuleId,
                    dependency.ModuleVersion,
                    dependency.ArchiveSha256);
                if (receipt.ContractSetVersion != expectedContractSetVersion)
                    throw new InvalidOperationException("dependency Contract Set 버전이 대상 모듈과 일치하지 않습니다.");
                var dependencyManifest = ReadAndValidateManifest(dependency.StagedDirectory, receipt);
                ValidateStagedFiles(dependency.StagedDirectory, dependencyManifest);
                ValidatePrimaryArtifact(dependency.StagedDirectory, dependencyManifest);
            }
        }

        private static string DependencyFingerprint(List<ModuleSelfTestDependency> dependencies)
        {
            var text = String.Join("\n", dependencies.Select(value =>
                value.ModuleId + "=" + value.ModuleVersion + "@" + value.ArchiveSha256));
            return Sha256Bytes(Encoding.UTF8.GetBytes(text));
        }

        private static string ReceiptDirectory(string root, string moduleId, string version, string sha256)
        {
            var directory = Path.GetFullPath(Path.Combine(root, moduleId, version, sha256));
            EnsureUnderRoot(Path.GetFullPath(root), directory);
            return directory;
        }

        private static bool ExistingReceiptMatches(
            string path,
            ModuleStagingInstallResult target,
            int contractSetVersion,
            int stateSchemaVersion,
            string stageReceiptSha256,
            string manifestSha256,
            string dependencyFingerprint)
        {
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
                return root != null &&
                    Convert.ToInt32(root["schemaVersion"], CultureInfo.InvariantCulture) == 1 &&
                    String.Equals(Convert.ToString(root["status"], CultureInfo.InvariantCulture), PassedStatus, StringComparison.Ordinal) &&
                    String.Equals(Convert.ToString(root["moduleId"], CultureInfo.InvariantCulture), target.ModuleId, StringComparison.Ordinal) &&
                    String.Equals(Convert.ToString(root["moduleVersion"], CultureInfo.InvariantCulture), target.ModuleVersion, StringComparison.Ordinal) &&
                    String.Equals(Convert.ToString(root["archiveSha256"], CultureInfo.InvariantCulture), target.ArchiveSha256, StringComparison.Ordinal) &&
                    Convert.ToInt32(root["contractSetVersion"], CultureInfo.InvariantCulture) == contractSetVersion &&
                    Convert.ToInt32(root["stateSchemaVersion"], CultureInfo.InvariantCulture) == stateSchemaVersion &&
                    String.Equals(Convert.ToString(root["stageReceiptSha256"], CultureInfo.InvariantCulture), stageReceiptSha256, StringComparison.Ordinal) &&
                    String.Equals(Convert.ToString(root["manifestSha256"], CultureInfo.InvariantCulture), manifestSha256, StringComparison.Ordinal) &&
                    String.Equals(Convert.ToString(root["dependencyFingerprint"], CultureInfo.InvariantCulture), dependencyFingerprint, StringComparison.Ordinal) &&
                    !Convert.ToBoolean(root["activationAllowed"], CultureInfo.InvariantCulture) &&
                    !Convert.ToBoolean(root["activeBundleChanged"], CultureInfo.InvariantCulture);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteReceipt(
            string path,
            ModuleStagingInstallResult target,
            int contractSetVersion,
            int stateSchemaVersion,
            string stageReceiptSha256,
            string manifestSha256,
            string dependencyFingerprint,
            int dependencyCount)
        {
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "status", PassedStatus },
                { "moduleId", target.ModuleId },
                { "moduleVersion", target.ModuleVersion },
                { "archiveSha256", target.ArchiveSha256 },
                { "contractSetVersion", contractSetVersion },
                { "stateSchemaVersion", stateSchemaVersion },
                { "stageReceiptSha256", stageReceiptSha256 },
                { "manifestSha256", manifestSha256 },
                { "dependencyFingerprint", dependencyFingerprint },
                { "dependencyCount", dependencyCount },
                { "assemblyMetadataLoad", true },
                { "testedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "activationAllowed", false },
                { "activeBundleChanged", false }
            };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(payload), new UTF8Encoding(false));
        }

        private static ModuleSelfTestResult Result(ModuleStagingInstallResult target, string receiptPath, bool alreadyPassed)
        {
            return new ModuleSelfTestResult
            {
                ModuleId = target.ModuleId,
                ModuleVersion = target.ModuleVersion,
                ArchiveSha256 = target.ArchiveSha256,
                ReceiptFile = receiptPath,
                AlreadyPassed = alreadyPassed,
                Status = PassedStatus
            };
        }

        private static string ResolveUnderRoot(string root, string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith("/", StringComparison.Ordinal) ||
                relativePath.IndexOf('\\') >= 0 || relativePath.IndexOf(':') >= 0 ||
                relativePath.Split('/').Any(segment => String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidOperationException("Self-test 상대 경로가 안전하지 않습니다: " + relativePath);
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            EnsureUnderRoot(Path.GetFullPath(root), full);
            return full;
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            var prefix = rootFull + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Self-test 경로가 허용된 루트 밖으로 벗어났습니다.");
        }

        private static string RelativePath(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Self-test 파일이 staging root 밖에 있습니다.");
            return full.Substring(rootFull.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(stream));
        }

        private static string Sha256Bytes(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(bytes));
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
