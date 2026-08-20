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
    internal sealed class ModulePackageVerificationRequest
    {
        public ModulePackageCacheResult Cache { get; set; }
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string BundlePackagePath { get; set; }
        public string ExpectedSha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
    }

    internal sealed class ModulePackageVerificationResult
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string PackageFile { get; set; }
        public string VerificationReceiptFile { get; set; }
        public string ArchiveSha256 { get; set; }
        public string ManifestSha256 { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
        public string SigningKeyId { get; set; }
        public string VerificationStatus { get; set; }
        public string VerifiedAtUtc { get; set; }
    }

    internal sealed class ModulePackageManifest
    {
        public int SchemaVersion { get; set; }
        public string ManifestType { get; set; }
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string SourceCommit { get; set; }
        public string TargetPlatform { get; set; }
        public ModulePackagePrimaryArtifact PrimaryArtifact { get; set; }
        public List<string> DependencyModuleIds { get; set; }
        public int ContractSetVersion { get; set; }
        public ModulePackageState State { get; set; }
        public List<ModulePackageFile> Files { get; set; }
        public ModulePackageIntegrity Integrity { get; set; }
    }

    internal sealed class ModulePackagePrimaryArtifact
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public string LoadTarget { get; set; }
    }

    internal sealed class ModulePackageState
    {
        public string Mode { get; set; }
        public int StateSchemaVersion { get; set; }
        public int MinimumReadableSchema { get; set; }
        public bool RollbackReadableByPrevious { get; set; }
        public bool MigrationRequired { get; set; }
    }

    internal sealed class ModulePackageFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
        public string Role { get; set; }
    }

    internal sealed class ModulePackageIntegrity
    {
        public string Mode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal static class ModulePackageVerifier
    {
        public const int SupportedManifestSchemaVersion = 1;
        public const int SupportedContractSetVersion = 1;
        public const string ManifestType = "KINOJO_METER_MODULE_PACKAGE";
        public const string TargetPlatform = "win-x64";
        public const string IntegrityMode = "RSA_SHA256";
        public const string SigningDomain = "KINOJO_MODULE_PACKAGE_MANIFEST_V1";
        public const string VerifiedStatus = "VERIFIED";
        public const string ManifestPath = "package.manifest.json";
        public const string VerificationReceiptName = "verification.json";

        private const long MaximumManifestBytes = 512L * 1024L;
        private const long MaximumTotalDeclaredBytes = 256L * 1024L * 1024L;
        private static readonly Regex SemVerPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha1Pattern = new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SafePathSegmentPattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

        private static readonly string[] ModuleIds =
        {
            "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell"
        };

        private static readonly Dictionary<string, string[]> Dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "contracts", new string[0] },
            { "capture", new[] { "contracts" } },
            { "protocol", new[] { "contracts", "capture" } },
            { "combat", new[] { "contracts", "protocol" } },
            { "encounter", new[] { "contracts" } },
            { "sync", new[] { "contracts", "capture", "protocol", "combat" } },
            { "shell", new[] { "contracts", "capture", "protocol", "combat", "encounter", "sync" } }
        };

        private static readonly Dictionary<string, string> PrimaryArtifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "contracts", "KINOJO.Meter.Contracts.dll" },
            { "capture", "KINOJO.Meter.Capture.dll" },
            { "protocol", "KINOJO.Meter.Protocol.dll" },
            { "combat", "KINOJO.Meter.Combat.dll" },
            { "encounter", "KINOJO.Meter.Encounter.dll" },
            { "sync", "KINOJO.Meter.Sync.dll" },
            { "shell", "KINOJO.Meter.Shell.exe" }
        };

        private static readonly Dictionary<string, string> PrimaryKinds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "contracts", "DLL" },
            { "capture", "DLL" },
            { "protocol", "DLL" },
            { "combat", "DLL" },
            { "encounter", "DLL" },
            { "sync", "DLL" },
            { "shell", "EXE" }
        };

        private static readonly Dictionary<string, string> LoadTargets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "contracts", "SHARED_RUNTIME" },
            { "capture", "ENGINE_HOST_PROCESS" },
            { "protocol", "ENGINE_HOST_PROCESS" },
            { "combat", "ENGINE_HOST_PROCESS" },
            { "encounter", "ENGINE_HOST_PROCESS" },
            { "sync", "ENGINE_HOST_PROCESS" },
            { "shell", "SHELL_PROCESS" }
        };

        private static readonly Dictionary<string, string> StateModes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "contracts", "NONE" },
            { "capture", "NONE" },
            { "protocol", "NONE" },
            { "combat", "OWNED" },
            { "encounter", "OWNED" },
            { "sync", "OWNED" },
            { "shell", "OWNED" }
        };

        public static ModulePackageVerificationResult Verify(ModulePackageVerificationRequest request)
        {
            return Verify(request, new RSAParameters
            {
                Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
            }, LauncherBuildProfile.CoreSigningKeyId);
        }

        internal static ModulePackageVerificationResult VerifyForTest(
            ModulePackageVerificationRequest request,
            RSAParameters publicKey,
            string expectedKeyId)
        {
            return Verify(request, publicKey, expectedKeyId);
        }

        internal static string CanonicalizeForTest(ModulePackageManifest manifest)
        {
            return Canonicalize(manifest);
        }

        private static ModulePackageVerificationResult Verify(
            ModulePackageVerificationRequest request,
            RSAParameters publicKey,
            string expectedKeyId)
        {
            ValidateRequest(request);
            var receiptPath = ReceiptPath(request.Cache.PackageFile);
            SafeDeleteFile(receiptPath);

            try
            {
                ReadCacheMetadata(request);
                var archiveSha256 = Sha256File(request.Cache.PackageFile);
                if (!String.Equals(archiveSha256, request.ExpectedSha256, StringComparison.Ordinal))
                    throw new InvalidOperationException("모듈 패키지 archive SHA-256이 Bundle Lock과 일치하지 않습니다.");

                string manifestJson;
                byte[] manifestBytes;
                ModulePackageManifest manifest;
                using (var archive = ZipFile.OpenRead(request.Cache.PackageFile))
                {
                    var fileEntries = BuildArchiveFileMap(archive);
                    ZipArchiveEntry manifestEntry;
                    if (!fileEntries.TryGetValue(ManifestPath, out manifestEntry))
                        throw new InvalidOperationException("모듈 패키지 루트에 package.manifest.json이 없습니다.");
                    if (manifestEntry.Length <= 0 || manifestEntry.Length > MaximumManifestBytes)
                        throw new InvalidOperationException("모듈 Package Manifest 크기가 허용 범위를 벗어났습니다.");

                    manifestBytes = ReadEntryBytes(manifestEntry, MaximumManifestBytes);
                    try { manifestJson = new UTF8Encoding(false, true).GetString(manifestBytes); }
                    catch (DecoderFallbackException) { throw new InvalidOperationException("모듈 Package Manifest가 유효한 UTF-8이 아닙니다."); }

                    ValidateManifestJsonShape(manifestJson);
                    manifest = new JavaScriptSerializer().Deserialize<ModulePackageManifest>(manifestJson);
                    ValidateManifestContract(manifest, request);
                    VerifyManifestSignature(manifest, publicKey, expectedKeyId);
                    VerifyArchiveFiles(fileEntries, manifest);
                }

                var verifiedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                var manifestSha256 = Sha256Bytes(manifestBytes);
                WriteVerificationReceipt(
                    receiptPath,
                    request,
                    archiveSha256,
                    manifestSha256,
                    manifest.Integrity.SigningKeyId,
                    verifiedAtUtc);

                return new ModulePackageVerificationResult
                {
                    ModuleId = request.ModuleId,
                    ModuleVersion = request.ModuleVersion,
                    PackageFile = request.Cache.PackageFile,
                    VerificationReceiptFile = receiptPath,
                    ArchiveSha256 = archiveSha256,
                    ManifestSha256 = manifestSha256,
                    ContractSetVersion = manifest.ContractSetVersion,
                    StateSchemaVersion = manifest.State.StateSchemaVersion,
                    SigningKeyId = manifest.Integrity.SigningKeyId,
                    VerificationStatus = VerifiedStatus,
                    VerifiedAtUtc = verifiedAtUtc
                };
            }
            catch
            {
                SafeDeleteFile(receiptPath);
                throw;
            }
        }

        private static void ValidateRequest(ModulePackageVerificationRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Cache == null) throw new InvalidOperationException("검증할 모듈 캐시 결과가 없습니다.");
            if (Array.IndexOf(ModuleIds, request.ModuleId) < 0)
                throw new InvalidOperationException("지원하지 않는 모듈 ID입니다.");
            if (!SemVerPattern.IsMatch(request.ModuleVersion ?? ""))
                throw new InvalidOperationException("모듈 버전 형식이 올바르지 않습니다.");
            if (!Sha256Pattern.IsMatch(request.ExpectedSha256 ?? ""))
                throw new InvalidOperationException("Bundle Lock 모듈 SHA-256 형식이 올바르지 않습니다.");
            if (request.ContractSetVersion != SupportedContractSetVersion)
                throw new InvalidOperationException("Launcher가 지원하지 않는 Contract Set 버전입니다.");
            if (request.StateSchemaVersion < 0)
                throw new InvalidOperationException("Bundle Lock state schema 버전이 올바르지 않습니다.");
            ValidateBundlePackagePath(request);

            if (!request.Cache.RequiresVerification ||
                !String.Equals(request.Cache.VerificationStatus, "UNVERIFIED", StringComparison.Ordinal))
                throw new InvalidOperationException("5-3 UNVERIFIED 캐시 후보만 검증할 수 있습니다.");
            if (String.IsNullOrWhiteSpace(request.Cache.PackageFile) || !File.Exists(request.Cache.PackageFile))
                throw new InvalidOperationException("검증할 모듈 package.zip이 없습니다.");
            if (String.IsNullOrWhiteSpace(request.Cache.MetadataFile) || !File.Exists(request.Cache.MetadataFile))
                throw new InvalidOperationException("모듈 download.json이 없습니다.");

            var packageFull = Path.GetFullPath(request.Cache.PackageFile);
            var metadataFull = Path.GetFullPath(request.Cache.MetadataFile);
            if (!String.Equals(Path.GetFileName(packageFull), "package.zip", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(metadataFull), "download.json", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetDirectoryName(packageFull), Path.GetDirectoryName(metadataFull), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("모듈 캐시 package/metadata 경계가 올바르지 않습니다.");
        }

        private static ModulePackageCacheMetadata ReadCacheMetadata(ModulePackageVerificationRequest request)
        {
            ModulePackageCacheMetadata metadata;
            try
            {
                metadata = new JavaScriptSerializer().Deserialize<ModulePackageCacheMetadata>(File.ReadAllText(request.Cache.MetadataFile));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("모듈 download.json을 읽을 수 없습니다.", error);
            }

            if (metadata == null || metadata.SchemaVersion != 1 ||
                !String.Equals(metadata.ModuleId, request.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(metadata.ModuleVersion, request.ModuleVersion, StringComparison.Ordinal) ||
                !String.Equals(metadata.BundlePackagePath, request.BundlePackagePath, StringComparison.Ordinal) ||
                !String.Equals(metadata.ExpectedSha256, request.ExpectedSha256, StringComparison.Ordinal) ||
                !String.Equals(metadata.VerificationStatus, "UNVERIFIED", StringComparison.Ordinal) ||
                metadata.Bytes <= 0 || metadata.Bytes != new FileInfo(request.Cache.PackageFile).Length ||
                metadata.Bytes != request.Cache.Bytes)
                throw new InvalidOperationException("5-3 캐시 metadata가 Bundle Lock 검증 요청과 일치하지 않습니다.");
            return metadata;
        }

        private static Dictionary<string, ZipArchiveEntry> BuildArchiveFileMap(ZipArchive archive)
        {
            if (archive == null) throw new InvalidOperationException("모듈 ZIP을 열 수 없습니다.");
            if (archive.Entries.Count > 4096)
                throw new InvalidOperationException("모듈 ZIP entry 수가 허용 범위를 초과했습니다.");

            var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName ?? "";
                var directory = String.IsNullOrEmpty(entry.Name);
                ValidateRelativePath(path, directory);
                if (directory) continue;
                if (files.ContainsKey(path))
                    throw new InvalidOperationException("모듈 ZIP에 중복 파일 경로가 있습니다: " + path);
                files.Add(path, entry);
            }
            return files;
        }

        private static void ValidateManifestJsonShape(string json)
        {
            object raw;
            try { raw = new JavaScriptSerializer().DeserializeObject(json); }
            catch (Exception error) { throw new InvalidOperationException("모듈 Package Manifest JSON이 올바르지 않습니다.", error); }

            var root = raw as IDictionary<string, object>;
            if (root == null) throw new InvalidOperationException("모듈 Package Manifest root는 object여야 합니다.");
            RequireExactKeys(root, "manifest",
                "schemaVersion", "manifestType", "moduleId", "moduleVersion", "sourceCommit", "targetPlatform",
                "primaryArtifact", "dependencyModuleIds", "contractSetVersion", "state", "files", "integrity");
            RequireExactKeys(RequireObject(root, "primaryArtifact"), "primaryArtifact", "path", "kind", "loadTarget");
            RequireExactKeys(RequireObject(root, "state"), "state",
                "mode", "stateSchemaVersion", "minimumReadableSchema", "rollbackReadableByPrevious", "migrationRequired");
            RequireExactKeys(RequireObject(root, "integrity"), "integrity", "mode", "signingKeyId", "manifestSignature");

            var files = RequireArray(root, "files");
            foreach (var item in files)
            {
                var file = item as IDictionary<string, object>;
                if (file == null) throw new InvalidOperationException("files 항목은 object여야 합니다.");
                RequireExactKeys(file, "files[]", "path", "size", "sha256", "role");
            }
        }

        private static void ValidateManifestContract(ModulePackageManifest manifest, ModulePackageVerificationRequest request)
        {
            if (manifest == null || manifest.SchemaVersion != SupportedManifestSchemaVersion ||
                !String.Equals(manifest.ManifestType, ManifestType, StringComparison.Ordinal) ||
                !String.Equals(manifest.ModuleId, request.ModuleId, StringComparison.Ordinal) ||
                !String.Equals(manifest.ModuleVersion, request.ModuleVersion, StringComparison.Ordinal) ||
                !Sha1Pattern.IsMatch(manifest.SourceCommit ?? "") ||
                !String.Equals(manifest.TargetPlatform, TargetPlatform, StringComparison.Ordinal))
                throw new InvalidOperationException("모듈 Package Manifest 기본 계약이 올바르지 않습니다.");

            if (manifest.ContractSetVersion != request.ContractSetVersion ||
                manifest.ContractSetVersion != SupportedContractSetVersion)
                throw new InvalidOperationException("모듈 Contract Set 버전이 Bundle Lock/Launcher 지원 범위와 일치하지 않습니다.");

            if (manifest.PrimaryArtifact == null ||
                !String.Equals(manifest.PrimaryArtifact.Path, PrimaryArtifacts[request.ModuleId], StringComparison.Ordinal) ||
                !String.Equals(manifest.PrimaryArtifact.Kind, PrimaryKinds[request.ModuleId], StringComparison.Ordinal) ||
                !String.Equals(manifest.PrimaryArtifact.LoadTarget, LoadTargets[request.ModuleId], StringComparison.Ordinal))
                throw new InvalidOperationException("모듈 primary artifact 계약이 올바르지 않습니다.");

            var dependencies = manifest.DependencyModuleIds ?? new List<string>();
            if (!dependencies.SequenceEqual(Dependencies[request.ModuleId], StringComparer.Ordinal) ||
                dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
                throw new InvalidOperationException("모듈 dependency Contract가 기준 topology와 일치하지 않습니다.");

            if (manifest.State == null ||
                !String.Equals(manifest.State.Mode, StateModes[request.ModuleId], StringComparison.Ordinal) ||
                manifest.State.StateSchemaVersion != request.StateSchemaVersion)
                throw new InvalidOperationException("모듈 state schema가 Bundle Lock과 일치하지 않습니다.");

            if (String.Equals(manifest.State.Mode, "NONE", StringComparison.Ordinal))
            {
                if (manifest.State.StateSchemaVersion != 0 || manifest.State.MinimumReadableSchema != 0 || manifest.State.MigrationRequired)
                    throw new InvalidOperationException("state=NONE 계약이 올바르지 않습니다.");
            }
            else
            {
                if (manifest.State.StateSchemaVersion < 1 || manifest.State.MinimumReadableSchema < 1 ||
                    manifest.State.MinimumReadableSchema > manifest.State.StateSchemaVersion)
                    throw new InvalidOperationException("state=OWNED schema 호환 범위가 올바르지 않습니다.");
            }

            if (manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 2048)
                throw new InvalidOperationException("모듈 Package Manifest files 계약이 올바르지 않습니다.");
            if (manifest.Integrity == null ||
                !String.Equals(manifest.Integrity.Mode, IntegrityMode, StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(manifest.Integrity.SigningKeyId) ||
                String.IsNullOrWhiteSpace(manifest.Integrity.ManifestSignature))
                throw new InvalidOperationException("모듈 Package Manifest integrity 계약이 올바르지 않습니다.");
        }

        private static void VerifyManifestSignature(ModulePackageManifest manifest, RSAParameters publicKey, string expectedKeyId)
        {
            if (!String.Equals(manifest.Integrity.SigningKeyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("지원하지 않는 모듈 Package signing key입니다.");

            byte[] signature;
            try { signature = Convert.FromBase64String(manifest.Integrity.ManifestSignature); }
            catch (FormatException) { throw new InvalidOperationException("모듈 Package Manifest 전자서명 형식이 올바르지 않습니다."); }
            if (signature.Length != 384)
                throw new InvalidOperationException("모듈 Package Manifest 전자서명 길이가 올바르지 않습니다.");

            var payload = Encoding.UTF8.GetBytes(Canonicalize(manifest));
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidOperationException("모듈 Package Manifest RSA 전자서명 검증에 실패했습니다.");
            }
        }

        private static string Canonicalize(ModulePackageManifest manifest)
        {
            if (manifest == null || manifest.PrimaryArtifact == null || manifest.State == null || manifest.Integrity == null || manifest.Files == null)
                throw new InvalidOperationException("서명할 모듈 Package Manifest가 완전하지 않습니다.");

            var dependencies = (manifest.DependencyModuleIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var files = manifest.Files.OrderBy(value => value == null ? "" : value.Path, StringComparer.Ordinal).ToArray();
            var lines = new List<string>
            {
                SigningDomain,
                "schemaVersion=" + manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "manifestType=" + CanonicalValue(manifest.ManifestType, "manifestType"),
                "moduleId=" + CanonicalValue(manifest.ModuleId, "moduleId"),
                "moduleVersion=" + CanonicalValue(manifest.ModuleVersion, "moduleVersion"),
                "sourceCommit=" + CanonicalValue(manifest.SourceCommit, "sourceCommit"),
                "targetPlatform=" + CanonicalValue(manifest.TargetPlatform, "targetPlatform"),
                "primaryArtifact.path=" + CanonicalValue(manifest.PrimaryArtifact.Path, "primaryArtifact.path"),
                "primaryArtifact.kind=" + CanonicalValue(manifest.PrimaryArtifact.Kind, "primaryArtifact.kind"),
                "primaryArtifact.loadTarget=" + CanonicalValue(manifest.PrimaryArtifact.LoadTarget, "primaryArtifact.loadTarget"),
                "dependencyModuleIds=" + String.Join(",", dependencies.Select(value => CanonicalValue(value, "dependencyModuleIds"))),
                "contractSetVersion=" + manifest.ContractSetVersion.ToString(CultureInfo.InvariantCulture),
                "state.mode=" + CanonicalValue(manifest.State.Mode, "state.mode"),
                "state.stateSchemaVersion=" + manifest.State.StateSchemaVersion.ToString(CultureInfo.InvariantCulture),
                "state.minimumReadableSchema=" + manifest.State.MinimumReadableSchema.ToString(CultureInfo.InvariantCulture),
                "state.rollbackReadableByPrevious=" + Bool(manifest.State.RollbackReadableByPrevious),
                "state.migrationRequired=" + Bool(manifest.State.MigrationRequired),
                "fileCount=" + files.Length.ToString(CultureInfo.InvariantCulture)
            };

            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (file == null) throw new InvalidOperationException("files 항목이 null입니다.");
                lines.Add("file[" + index.ToString(CultureInfo.InvariantCulture) + "].path=" + CanonicalValue(file.Path, "files.path"));
                lines.Add("file[" + index.ToString(CultureInfo.InvariantCulture) + "].size=" + file.Size.ToString(CultureInfo.InvariantCulture));
                lines.Add("file[" + index.ToString(CultureInfo.InvariantCulture) + "].sha256=" + CanonicalValue(file.Sha256, "files.sha256").ToLowerInvariant());
                lines.Add("file[" + index.ToString(CultureInfo.InvariantCulture) + "].role=" + CanonicalValue(file.Role, "files.role"));
            }

            lines.Add("integrity.mode=" + CanonicalValue(manifest.Integrity.Mode, "integrity.mode"));
            lines.Add("integrity.signingKeyId=" + CanonicalValue(manifest.Integrity.SigningKeyId, "integrity.signingKeyId"));
            return String.Join("\n", lines);
        }

        private static void VerifyArchiveFiles(Dictionary<string, ZipArchiveEntry> archiveFiles, ModulePackageManifest manifest)
        {
            var declared = new Dictionary<string, ModulePackageFile>(StringComparer.Ordinal);
            long totalDeclared = 0;
            foreach (var file in manifest.Files)
            {
                if (file == null) throw new InvalidOperationException("모듈 files 항목이 null입니다.");
                ValidateRelativePath(file.Path, false);
                if (String.Equals(file.Path, ManifestPath, StringComparison.Ordinal))
                    throw new InvalidOperationException("package.manifest.json은 files 자기 해시에 포함할 수 없습니다.");
                if (file.Size <= 0 || file.Size > MaximumTotalDeclaredBytes || !Sha256Pattern.IsMatch(file.Sha256 ?? "") ||
                    (file.Role != "PRIMARY" && file.Role != "RUNTIME_DEPENDENCY" && file.Role != "RESOURCE"))
                    throw new InvalidOperationException("모듈 files 항목 계약이 올바르지 않습니다: " + file.Path);
                if (declared.ContainsKey(file.Path))
                    throw new InvalidOperationException("모듈 Manifest에 중복 파일 경로가 있습니다: " + file.Path);
                declared.Add(file.Path, file);
                checked { totalDeclared += file.Size; }
                if (totalDeclared > MaximumTotalDeclaredBytes)
                    throw new InvalidOperationException("모듈 Package 압축 해제 예상 크기가 허용 범위를 초과했습니다.");
            }

            var primaryRows = manifest.Files.Where(file => file != null &&
                String.Equals(file.Path, manifest.PrimaryArtifact.Path, StringComparison.Ordinal) &&
                String.Equals(file.Role, "PRIMARY", StringComparison.Ordinal)).ToList();
            if (primaryRows.Count != 1)
                throw new InvalidOperationException("모듈 primary artifact가 files PRIMARY와 정확히 연결되지 않았습니다.");

            if (archiveFiles.Count != declared.Count + 1)
                throw new InvalidOperationException("모듈 ZIP 파일 집합이 Package Manifest와 정확히 일치하지 않습니다.");

            foreach (var pair in declared)
            {
                ZipArchiveEntry entry;
                if (!archiveFiles.TryGetValue(pair.Key, out entry))
                    throw new InvalidOperationException("모듈 ZIP에 선언 파일이 없습니다: " + pair.Key);
                if (entry.Length != pair.Value.Size)
                    throw new InvalidOperationException("모듈 ZIP 파일 크기가 Manifest와 다릅니다: " + pair.Key);
                using (var stream = entry.Open())
                {
                    var actual = Sha256Stream(stream);
                    if (!String.Equals(actual, pair.Value.Sha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("모듈 ZIP 내부 파일 SHA-256 검증에 실패했습니다: " + pair.Key);
                }
            }
        }

        private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long maximumBytes)
        {
            using (var input = entry.Open())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[32 * 1024];
                long total = 0;
                while (true)
                {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    total += read;
                    if (total > maximumBytes)
                        throw new InvalidOperationException("모듈 Package Manifest가 허용 크기를 초과했습니다.");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path)) return Sha256Stream(stream);
        }

        private static string Sha256Bytes(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string Sha256Stream(Stream stream)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes)
        {
            return String.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void WriteVerificationReceipt(
            string receiptPath,
            ModulePackageVerificationRequest request,
            string archiveSha256,
            string manifestSha256,
            string signingKeyId,
            string verifiedAtUtc)
        {
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "verificationStatus", VerifiedStatus },
                { "moduleId", request.ModuleId },
                { "moduleVersion", request.ModuleVersion },
                { "bundlePackagePath", request.BundlePackagePath },
                { "archiveSha256", archiveSha256 },
                { "manifestSha256", manifestSha256 },
                { "contractSetVersion", request.ContractSetVersion },
                { "stateSchemaVersion", request.StateSchemaVersion },
                { "signingKeyId", signingKeyId },
                { "verifiedAtUtc", verifiedAtUtc },
                { "installAllowed", false },
                { "activationAllowed", false }
            };
            var temporary = receiptPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(payload), new UTF8Encoding(false));
            if (File.Exists(receiptPath)) File.Replace(temporary, receiptPath, null);
            else File.Move(temporary, receiptPath);
        }

        private static string ReceiptPath(string packageFile)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(packageFile));
            if (String.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("모듈 검증 receipt 경로를 만들 수 없습니다.");
            return Path.Combine(directory, VerificationReceiptName);
        }

        private static void ValidateBundlePackagePath(ModulePackageVerificationRequest request)
        {
            var path = request.BundlePackagePath ?? "";
            ValidateRelativePath(path, false);
            var expectedPrefix = "modules/" + request.ModuleId + "/" + request.ModuleVersion + "/";
            if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Bundle Lock packagePath가 모듈 ID/버전 ZIP과 일치하지 않습니다.");
        }

        private static void ValidateRelativePath(string path, bool directory)
        {
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0)
                throw new InvalidOperationException("안전하지 않은 모듈 상대 경로입니다: " + path);

            var normalized = path;
            if (directory && normalized.EndsWith("/", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);
            if (String.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("빈 모듈 상대 경로는 허용하지 않습니다.");

            var segments = normalized.Split('/');
            foreach (var segment in segments)
            {
                if (String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." || !SafePathSegmentPattern.IsMatch(segment))
                    throw new InvalidOperationException("안전하지 않은 모듈 상대 경로가 있습니다: " + path);
            }
        }

        private static string CanonicalValue(string value, string field)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new InvalidOperationException("모듈 서명 계약 필드가 올바르지 않습니다: " + field);
            return value;
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static IDictionary<string, object> RequireObject(IDictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value)) throw new InvalidOperationException("Manifest 필드가 없습니다: " + key);
            var result = value as IDictionary<string, object>;
            if (result == null) throw new InvalidOperationException("Manifest object 필드 형식이 올바르지 않습니다: " + key);
            return result;
        }

        private static object[] RequireArray(IDictionary<string, object> parent, string key)
        {
            object value;
            if (!parent.TryGetValue(key, out value)) throw new InvalidOperationException("Manifest 배열 필드가 없습니다: " + key);
            var result = value as object[];
            if (result == null) throw new InvalidOperationException("Manifest 배열 필드 형식이 올바르지 않습니다: " + key);
            return result;
        }

        private static void RequireExactKeys(IDictionary<string, object> value, string context, params string[] keys)
        {
            var expected = new HashSet<string>(keys, StringComparer.Ordinal);
            if (value.Count != expected.Count || value.Keys.Any(key => !expected.Contains(key)))
                throw new InvalidOperationException("Manifest " + context + " 필드 집합이 schema v1과 일치하지 않습니다.");
        }

        private static void SafeDeleteFile(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
