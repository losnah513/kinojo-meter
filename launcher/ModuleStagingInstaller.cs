using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleStagingInstallRequest
    {
        public ModulePackageVerificationRequest VerificationRequest { get; set; }
    }

    internal sealed class ModuleStagingInstallResult
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string ArchiveSha256 { get; set; }
        public string StagedDirectory { get; set; }
        public string InstallReceiptFile { get; set; }
        public bool AlreadyStaged { get; set; }
        public string InstallStatus { get; set; }
    }

    internal static class ModuleStagingInstaller
    {
        public const string StagedStatus = "STAGED";
        public const string InstallReceiptName = "staging-install.json";

        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SemVerPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex SafePathSegmentPattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

        public static ModuleStagingInstallResult Stage(ModuleStagingInstallRequest request)
        {
            return StageInternal(
                request,
                LauncherPaths.ModuleStaging,
                verification => ModulePackageVerifier.Verify(verification));
        }

        internal static ModuleStagingInstallResult StageForTest(
            ModuleStagingInstallRequest request,
            string stagingRoot,
            RSAParameters publicKey,
            string expectedKeyId)
        {
            return StageInternal(
                request,
                stagingRoot,
                verification => ModulePackageVerifier.VerifyForTest(verification, publicKey, expectedKeyId));
        }

        private static ModuleStagingInstallResult StageInternal(
            ModuleStagingInstallRequest request,
            string stagingRoot,
            Func<ModulePackageVerificationRequest, ModulePackageVerificationResult> reverify)
        {
            if (request == null || request.VerificationRequest == null)
                throw new ArgumentNullException("request");
            if (String.IsNullOrWhiteSpace(stagingRoot))
                throw new ArgumentException("stagingRoot");
            if (reverify == null)
                throw new ArgumentNullException("reverify");

            var verification = request.VerificationRequest;
            ValidateVerificationRequestShape(verification);

            var originalReceipt = ReadVerificationReceipt(verification);
            var originalReceiptSha256 = Sha256File(ReceiptPath(verification.Cache.PackageFile));

            var reverified = reverify(verification);
            CompareReverification(originalReceipt, reverified, verification);

            var root = Path.GetFullPath(stagingRoot);
            Directory.CreateDirectory(root);
            var finalDirectory = StagedDirectory(root, verification.ModuleId, verification.ModuleVersion, verification.ExpectedSha256);
            EnsureNoDifferentShaSibling(root, verification.ModuleId, verification.ModuleVersion, verification.ExpectedSha256);

            if (Directory.Exists(finalDirectory))
            {
                if (ValidateExistingSlot(finalDirectory, verification, reverified))
                    return Result(finalDirectory, verification, true);
                SafeDeleteDirectory(finalDirectory);
            }

            var incomingRoot = Path.Combine(root, ".incoming");
            Directory.CreateDirectory(incomingRoot);
            var temporaryDirectory = Path.Combine(incomingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                ExtractSafely(verification.Cache.PackageFile, temporaryDirectory);
                WriteInstallReceipt(
                    Path.Combine(temporaryDirectory, InstallReceiptName),
                    verification,
                    reverified,
                    originalReceiptSha256);

                var parent = Path.GetDirectoryName(finalDirectory);
                if (String.IsNullOrWhiteSpace(parent))
                    throw new InvalidOperationException("Staging install parent 경로를 만들 수 없습니다.");
                Directory.CreateDirectory(parent);

                if (Directory.Exists(finalDirectory))
                {
                    if (ValidateExistingSlot(finalDirectory, verification, reverified))
                    {
                        SafeDeleteDirectory(temporaryDirectory);
                        return Result(finalDirectory, verification, true);
                    }
                    throw new InvalidOperationException("동일 모듈 Staging 슬롯이 병행 생성되었으나 검증 상태가 일치하지 않습니다.");
                }

                Directory.Move(temporaryDirectory, finalDirectory);
                if (!ValidateExistingSlot(finalDirectory, verification, reverified))
                {
                    SafeDeleteDirectory(finalDirectory);
                    throw new InvalidOperationException("Staging 설치 직후 readback 검증에 실패했습니다.");
                }

                return Result(finalDirectory, verification, false);
            }
            catch
            {
                SafeDeleteDirectory(temporaryDirectory);
                throw;
            }
        }

        private sealed class VerifiedReceipt
        {
            public string ModuleId { get; set; }
            public string ModuleVersion { get; set; }
            public string BundlePackagePath { get; set; }
            public string ArchiveSha256 { get; set; }
            public string ManifestSha256 { get; set; }
            public int ContractSetVersion { get; set; }
            public int StateSchemaVersion { get; set; }
            public string SigningKeyId { get; set; }
        }

        private static void ValidateVerificationRequestShape(ModulePackageVerificationRequest request)
        {
            if (request.Cache == null)
                throw new InvalidOperationException("검증된 모듈 cache 정보가 없습니다.");
            if (!SemVerPattern.IsMatch(request.ModuleVersion ?? ""))
                throw new InvalidOperationException("모듈 버전 형식이 올바르지 않습니다.");
            if (!Sha256Pattern.IsMatch(request.ExpectedSha256 ?? ""))
                throw new InvalidOperationException("모듈 archive SHA-256 형식이 올바르지 않습니다.");
            if (String.IsNullOrWhiteSpace(request.Cache.PackageFile) || !File.Exists(request.Cache.PackageFile))
                throw new InvalidOperationException("Staging에 설치할 package.zip이 없습니다.");

            var receiptPath = ReceiptPath(request.Cache.PackageFile);
            if (!File.Exists(receiptPath))
                throw new InvalidOperationException("5-4 VERIFIED verification.json이 없습니다.");
        }

        private static VerifiedReceipt ReadVerificationReceipt(ModulePackageVerificationRequest request)
        {
            var receiptPath = ReceiptPath(request.Cache.PackageFile);
            IDictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(receiptPath)) as IDictionary<string, object>;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("5-4 verification.json을 읽을 수 없습니다.", error);
            }
            if (root == null)
                throw new InvalidOperationException("5-4 verification.json root가 올바르지 않습니다.");

            var expectedKeys = new HashSet<string>(new[]
            {
                "schemaVersion", "verificationStatus", "moduleId", "moduleVersion", "bundlePackagePath",
                "archiveSha256", "manifestSha256", "contractSetVersion", "stateSchemaVersion", "signingKeyId",
                "verifiedAtUtc", "installAllowed", "activationAllowed"
            }, StringComparer.Ordinal);
            if (root.Count != expectedKeys.Count || root.Keys.Any(key => !expectedKeys.Contains(key)))
                throw new InvalidOperationException("5-4 verification.json 필드 집합이 schema v1과 일치하지 않습니다.");

            if (Convert.ToInt32(root["schemaVersion"], CultureInfo.InvariantCulture) != 1 ||
                !String.Equals(Convert.ToString(root["verificationStatus"], CultureInfo.InvariantCulture), ModulePackageVerifier.VerifiedStatus, StringComparison.Ordinal) ||
                Convert.ToBoolean(root["installAllowed"], CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(root["activationAllowed"], CultureInfo.InvariantCulture))
                throw new InvalidOperationException("5-4 VERIFIED receipt의 install/activation 경계가 올바르지 않습니다.");

            var result = new VerifiedReceipt
            {
                ModuleId = Convert.ToString(root["moduleId"], CultureInfo.InvariantCulture),
                ModuleVersion = Convert.ToString(root["moduleVersion"], CultureInfo.InvariantCulture),
                BundlePackagePath = Convert.ToString(root["bundlePackagePath"], CultureInfo.InvariantCulture),
                ArchiveSha256 = Convert.ToString(root["archiveSha256"], CultureInfo.InvariantCulture),
                ManifestSha256 = Convert.ToString(root["manifestSha256"], CultureInfo.InvariantCulture),
                ContractSetVersion = Convert.ToInt32(root["contractSetVersion"], CultureInfo.InvariantCulture),
                StateSchemaVersion = Convert.ToInt32(root["stateSchemaVersion"], CultureInfo.InvariantCulture),
                SigningKeyId = Convert.ToString(root["signingKeyId"], CultureInfo.InvariantCulture)
            };

            if (!String.Equals(result.ModuleId, request.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(result.ModuleVersion, request.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(result.BundlePackagePath, request.BundlePackagePath, StringComparison.Ordinal) ||
                !String.Equals(result.ArchiveSha256, request.ExpectedSha256, StringComparison.Ordinal) ||
                result.ContractSetVersion != request.ContractSetVersion ||
                result.StateSchemaVersion != request.StateSchemaVersion ||
                !Sha256Pattern.IsMatch(result.ManifestSha256 ?? "") ||
                String.IsNullOrWhiteSpace(result.SigningKeyId))
                throw new InvalidOperationException("5-4 VERIFIED receipt가 현재 Bundle Lock 검증 요청과 일치하지 않습니다.");

            return result;
        }

        private static void CompareReverification(
            VerifiedReceipt original,
            ModulePackageVerificationResult reverified,
            ModulePackageVerificationRequest request)
        {
            if (reverified == null ||
                !String.Equals(reverified.VerificationStatus, ModulePackageVerifier.VerifiedStatus, StringComparison.Ordinal) ||
                !String.Equals(reverified.ModuleId, original.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(reverified.ModuleVersion, original.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(reverified.ArchiveSha256, original.ArchiveSha256, StringComparison.Ordinal) ||
                !String.Equals(reverified.ManifestSha256, original.ManifestSha256, StringComparison.Ordinal) ||
                reverified.ContractSetVersion != original.ContractSetVersion ||
                reverified.StateSchemaVersion != original.StateSchemaVersion ||
                !String.Equals(reverified.SigningKeyId, original.SigningKeyId, StringComparison.Ordinal) ||
                !String.Equals(reverified.ArchiveSha256, request.ExpectedSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("package.zip 재검증 결과가 기존 5-4 VERIFIED receipt와 일치하지 않습니다.");
        }

        private static string StagedDirectory(string root, string moduleId, string version, string sha256)
        {
            ValidateSegment(moduleId, "moduleId");
            ValidateSegment(version, "moduleVersion");
            ValidateSegment(sha256, "archiveSha256");

            var directory = Path.GetFullPath(Path.Combine(root, moduleId, version, sha256));
            EnsureUnderRoot(root, directory);
            return directory;
        }

        private static void EnsureNoDifferentShaSibling(string root, string moduleId, string version, string expectedSha)
        {
            var versionRoot = Path.GetFullPath(Path.Combine(root, moduleId, version));
            EnsureUnderRoot(root, versionRoot);
            if (!Directory.Exists(versionRoot)) return;

            foreach (var directory in Directory.GetDirectories(versionRoot))
            {
                var name = Path.GetFileName(directory);
                if (!String.Equals(name, expectedSha, StringComparison.Ordinal))
                    throw new InvalidOperationException("동일 moduleId + moduleVersion에 다른 SHA Staging 슬롯이 이미 존재합니다.");
            }
        }

        private static void ExtractSafely(string packageFile, string destinationRoot)
        {
            var root = Path.GetFullPath(destinationRoot);
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                if (archive.Entries.Count > 4096)
                    throw new InvalidOperationException("모듈 ZIP entry 수가 허용 범위를 초과했습니다.");

                var seenFiles = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                {
                    var path = entry.FullName ?? "";
                    var isDirectory = String.IsNullOrEmpty(entry.Name);
                    ValidateRelativePath(path, isDirectory);
                    if (isDirectory)
                    {
                        var directoryPath = ResolveUnderRoot(root, path.TrimEnd('/'));
                        Directory.CreateDirectory(directoryPath);
                        continue;
                    }

                    if (String.Equals(path, InstallReceiptName, StringComparison.Ordinal))
                        throw new InvalidOperationException("모듈 ZIP은 Staging install receipt 경로를 포함할 수 없습니다.");
                    if (!seenFiles.Add(path))
                        throw new InvalidOperationException("모듈 ZIP에 중복 파일 경로가 있습니다: " + path);

                    var target = ResolveUnderRoot(root, path);
                    var parent = Path.GetDirectoryName(target);
                    if (String.IsNullOrWhiteSpace(parent))
                        throw new InvalidOperationException("Staging 파일 parent 경로를 만들 수 없습니다.");
                    Directory.CreateDirectory(parent);

                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }

            if (!File.Exists(Path.Combine(root, ModulePackageVerifier.ManifestPath)))
                throw new InvalidOperationException("Staging 결과에 package.manifest.json이 없습니다.");
        }

        private static bool ValidateExistingSlot(
            string directory,
            ModulePackageVerificationRequest request,
            ModulePackageVerificationResult reverified)
        {
            try
            {
                var receiptPath = Path.Combine(directory, InstallReceiptName);
                if (!File.Exists(receiptPath)) return false;
                var receipt = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(receiptPath))
                    as IDictionary<string, object>;
                if (receipt == null ||
                    Convert.ToInt32(receipt["schemaVersion"], CultureInfo.InvariantCulture) != 1 ||
                    !String.Equals(Convert.ToString(receipt["installStatus"], CultureInfo.InvariantCulture), StagedStatus, StringComparison.Ordinal) ||
                    !String.Equals(Convert.ToString(receipt["moduleId"], CultureInfo.InvariantCulture), request.ModuleId, StringComparison.Ordinal) ||
                    !String.Equals(Convert.ToString(receipt["moduleVersion"], CultureInfo.InvariantCulture), request.ModuleVersion, StringComparison.Ordinal) ||
                    !String.Equals(Convert.ToString(receipt["bundlePackagePath"], CultureInfo.InvariantCulture), request.BundlePackagePath, StringComparison.Ordinal) ||
                    !String.Equals(Convert.ToString(receipt["archiveSha256"], CultureInfo.InvariantCulture), request.ExpectedSha256, StringComparison.Ordinal) ||
                    !String.Equals(Convert.ToString(receipt["manifestSha256"], CultureInfo.InvariantCulture), reverified.ManifestSha256, StringComparison.Ordinal) ||
                    Convert.ToInt32(receipt["contractSetVersion"], CultureInfo.InvariantCulture) != request.ContractSetVersion ||
                    Convert.ToInt32(receipt["stateSchemaVersion"], CultureInfo.InvariantCulture) != request.StateSchemaVersion ||
                    !String.Equals(Convert.ToString(receipt["signingKeyId"], CultureInfo.InvariantCulture), reverified.SigningKeyId, StringComparison.Ordinal) ||
                    !Sha256Pattern.IsMatch(Convert.ToString(receipt["verificationReceiptSha256"], CultureInfo.InvariantCulture) ?? "") ||
                    Convert.ToBoolean(receipt["activationAllowed"], CultureInfo.InvariantCulture) ||
                    Convert.ToBoolean(receipt["activeBundleChanged"], CultureInfo.InvariantCulture))
                    return false;

                return StagedFilesMatchPackage(request.Cache.PackageFile, directory);
            }
            catch
            {
                return false;
            }
        }

        private static bool StagedFilesMatchPackage(string packageFile, string stagedDirectory)
        {
            var expected = new HashSet<string>(StringComparer.Ordinal);
            using (var archive = ZipFile.OpenRead(packageFile))
            {
                foreach (var entry in archive.Entries)
                {
                    var isDirectory = String.IsNullOrEmpty(entry.Name);
                    if (isDirectory) continue;
                    var relative = entry.FullName;
                    expected.Add(relative);
                    var target = ResolveUnderRoot(Path.GetFullPath(stagedDirectory), relative);
                    if (!File.Exists(target)) return false;
                    using (var source = entry.Open())
                    using (var installed = File.OpenRead(target))
                    {
                        if (!String.Equals(Sha256Stream(source), Sha256Stream(installed), StringComparison.Ordinal))
                            return false;
                    }
                }
            }

            var actual = Directory.GetFiles(stagedDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !String.Equals(Path.GetFileName(path), InstallReceiptName, StringComparison.Ordinal))
                .Select(path => RelativePath(stagedDirectory, path))
                .ToArray();
            return actual.Length == expected.Count && actual.All(expected.Contains);
        }

        private static void WriteInstallReceipt(
            string path,
            ModulePackageVerificationRequest request,
            ModulePackageVerificationResult verification,
            string verificationReceiptSha256)
        {
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "installStatus", StagedStatus },
                { "moduleId", request.ModuleId },
                { "moduleVersion", request.ModuleVersion },
                { "bundlePackagePath", request.BundlePackagePath },
                { "archiveSha256", request.ExpectedSha256 },
                { "manifestSha256", verification.ManifestSha256 },
                { "contractSetVersion", request.ContractSetVersion },
                { "stateSchemaVersion", request.StateSchemaVersion },
                { "signingKeyId", verification.SigningKeyId },
                { "verificationReceiptSha256", verificationReceiptSha256 },
                { "stagedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "activationAllowed", false },
                { "activeBundleChanged", false }
            };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(payload), new UTF8Encoding(false));
        }

        private static ModuleStagingInstallResult Result(
            string directory,
            ModulePackageVerificationRequest request,
            bool alreadyStaged)
        {
            return new ModuleStagingInstallResult
            {
                ModuleId = request.ModuleId,
                ModuleVersion = request.ModuleVersion,
                ArchiveSha256 = request.ExpectedSha256,
                StagedDirectory = directory,
                InstallReceiptFile = Path.Combine(directory, InstallReceiptName),
                AlreadyStaged = alreadyStaged,
                InstallStatus = StagedStatus
            };
        }

        private static string ReceiptPath(string packageFile)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(packageFile));
            if (String.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("verification.json 경로를 만들 수 없습니다.");
            return Path.Combine(directory, ModulePackageVerifier.VerificationReceiptName);
        }

        private static string ResolveUnderRoot(string root, string relativePath)
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, normalized));
            EnsureUnderRoot(root, full);
            return full;
        }

        private static void EnsureUnderRoot(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !String.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staging 경로가 허용된 루트 밖으로 벗어났습니다.");
        }

        private static void ValidateRelativePath(string path, bool directory)
        {
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0)
                throw new InvalidOperationException("안전하지 않은 모듈 상대 경로입니다: " + path);

            var normalized = directory && path.EndsWith("/", StringComparison.Ordinal)
                ? path.Substring(0, path.Length - 1)
                : path;
            if (String.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("빈 모듈 상대 경로는 허용하지 않습니다.");
            foreach (var segment in normalized.Split('/'))
                ValidateSegment(segment, "relativePath");
        }

        private static void ValidateSegment(string value, string field)
        {
            if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".." || !SafePathSegmentPattern.IsMatch(value))
                throw new InvalidOperationException("안전하지 않은 Staging 경로 segment입니다: " + field);
        }

        private static string RelativePath(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staging 파일이 root 밖에 있습니다.");
            return full.Substring(rootFull.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path)) return Sha256Stream(stream);
        }

        private static string Sha256Stream(Stream stream)
        {
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch { }
        }
    }
}
