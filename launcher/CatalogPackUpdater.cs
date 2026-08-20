using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    internal static class CatalogPackReleaseIntegrityVerifier
    {
        public const string IntegrityMode = "RSA_SHA256_MANIFEST_V1";
        private static readonly Dictionary<string, string> SigningDomains = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "dungeon-boss-catalog", "KINOJO_DUNGEON_BOSS_CATALOG_RELEASE_V1" },
            { "class-skill-catalog", "KINOJO_CLASS_SKILL_CATALOG_RELEASE_V1" },
            { "boss-hp-fingerprint", "KINOJO_BOSS_HP_FINGERPRINT_RELEASE_V1" }
        };

        public static void Verify(CatalogPackReleaseManifest release)
        {
            Verify(release, new RSAParameters
            {
                Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
            }, LauncherBuildProfile.CoreSigningKeyId);
        }

        internal static void VerifyForTest(CatalogPackReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            Verify(release, publicKey, expectedKeyId);
        }

        internal static string Canonicalize(CatalogPackReleaseManifest release)
        {
            string domain;
            if (release == null || !SigningDomains.TryGetValue(release.PackId ?? "", out domain))
                throw new InvalidOperationException("지원하지 않는 Catalog Pack 서명 도메인입니다.");
            return String.Join("\n", new[]
            {
                domain,
                "schemaVersion=" + release.SchemaVersion,
                "channel=" + release.Channel,
                "packId=" + release.PackId,
                "catalogVersion=" + release.CatalogVersion,
                "minimumLauncherVersion=" + release.MinimumLauncherVersion,
                "packageId=" + release.PackageId,
                "fileName=" + release.FileName,
                "fileSize=" + release.FileSize,
                "sha256=" + release.Sha256,
                "installManifestSha256=" + release.InstallManifestSha256,
                "catalogSha256=" + release.CatalogSha256
            });
        }

        private static void Verify(CatalogPackReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            if (release == null || !String.Equals(release.IntegrityMode, IntegrityMode, StringComparison.Ordinal) ||
                !String.Equals(release.SigningKeyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack 서명 신뢰 계약이 올바르지 않습니다.");
            byte[] signature;
            try { signature = Convert.FromBase64String(release.ManifestSignature ?? ""); }
            catch { throw new InvalidOperationException("Catalog Pack 전자서명 형식이 올바르지 않습니다."); }
            if (signature.Length != 384 || publicKey.Modulus == null || publicKey.Modulus.Length != 384)
                throw new InvalidOperationException("Catalog Pack은 RSA-3072 서명만 허용합니다.");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Canonicalize(release)), CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidOperationException("Catalog Pack release 전자서명 검증에 실패했습니다.");
            }
        }
    }

    internal sealed class CatalogPackInstaller : IDisposable
    {
        public const string DungeonBossPackId = "dungeon-boss-catalog";
        public const string ClassSkillPackId = "class-skill-catalog";
        public const string BossHpPackId = "boss-hp-fingerprint";
        public const string VersionShaConflictCode = "CATALOG_VERSION_SHA_CONFLICT";
        private const long MaximumPackageBytes = 32L * 1024L * 1024L;
        private const long MaximumCatalogBytes = 64L * 1024L * 1024L;
        private static readonly Regex Sha256Pattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex SemanticVersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Dictionary<string, Regex> CatalogVersionPatterns = new Dictionary<string, Regex>(StringComparer.Ordinal)
        {
            { DungeonBossPackId, new Regex("^METER_CATALOG_[0-9]{8}_[0-9]{2}$", RegexOptions.CultureInvariant) },
            { ClassSkillPackId, new Regex("^CLASS_SKILL_CATALOG_[0-9]{8}_[0-9]{2}$", RegexOptions.CultureInvariant) },
            { BossHpPackId, new Regex("^BOSS_HP_FINGERPRINT_[0-9]{8}_[0-9]{2}$", RegexOptions.CultureInvariant) }
        };

        private readonly HttpClient _http;
        private readonly string _root;
        private readonly RSAParameters _publicKey;
        private readonly string _expectedKeyId;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public CatalogPackInstaller()
            : this(new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, LauncherPaths.CatalogPackRoot,
                new RSAParameters
                {
                    Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                    Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
                }, LauncherBuildProfile.CoreSigningKeyId)
        {
        }

        internal CatalogPackInstaller(HttpMessageHandler handler, string root, RSAParameters publicKey, string expectedKeyId)
            : this(new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(3) }, root, publicKey, expectedKeyId)
        {
        }

        private CatalogPackInstaller(HttpClient http, string root, RSAParameters publicKey, string expectedKeyId)
        {
            if (http == null) throw new ArgumentNullException("http");
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("root");
            _http = http;
            _root = Path.GetFullPath(root);
            _publicKey = publicKey;
            _expectedKeyId = expectedKeyId ?? "";
        }

        public ActiveCatalogPackState ReadVerifiedActiveState(string packId)
        {
            ValidatePackId(packId);
            try
            {
                var path = ActiveFile(packId);
                if (!File.Exists(path)) return null;
                var state = _json.Deserialize<ActiveCatalogPackState>(File.ReadAllText(path, Encoding.UTF8));
                VerifyInstalled(state);
                return state;
            }
            catch { return null; }
        }

        public async Task<CatalogPackInstallResult> EnsureInstalledAsync(
            CatalogPackReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            ValidateRelease(release, true);
            CatalogPackReleaseIntegrityVerifier.VerifyForTest(release, _publicKey, _expectedKeyId);
            var uri = RequireApprovedDownloadUri(release, expectedProjectHost);
            Directory.CreateDirectory(_root);
            using (var updateLock = new FileStream(Path.Combine(_root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var current = ReadVerifiedActiveState(release.PackId);
                var activeFile = ActiveFile(release.PackId);
                if (current == null && File.Exists(activeFile))
                {
                    ActiveCatalogPackState raw;
                    try { raw = _json.Deserialize<ActiveCatalogPackState>(File.ReadAllText(activeFile, Encoding.UTF8)); }
                    catch (Exception error) { throw new InvalidOperationException("Catalog Pack active state를 신뢰할 수 없습니다.", error); }
                    if (raw == null || !String.Equals(raw.PackId, release.PackId, StringComparison.Ordinal))
                        throw new InvalidOperationException("Catalog Pack active state identity가 올바르지 않습니다.");
                    if (String.Equals(raw.CatalogVersion, release.CatalogVersion, StringComparison.Ordinal) &&
                        !String.Equals(raw.PackageSha256, release.Sha256, StringComparison.Ordinal))
                        throw new InvalidOperationException(VersionShaConflictCode + ": 같은 Catalog version의 다른 SHA는 활성화할 수 없습니다.");
                }
                if (current != null && String.Equals(current.CatalogVersion, release.CatalogVersion, StringComparison.Ordinal) &&
                    !String.Equals(current.PackageSha256, release.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException(VersionShaConflictCode + ": 같은 Catalog version의 다른 SHA는 활성화할 수 없습니다.");
                if (current != null && SameRelease(current, release))
                    return new CatalogPackInstallResult { Active = current, Previous = current, Changed = false, Downloaded = false };

                var transactionRoot = Path.Combine(_root, ".incoming", Guid.NewGuid().ToString("N"));
                var packagePath = Path.Combine(transactionRoot, "package.zip");
                var extracted = Path.Combine(transactionRoot, "content");
                Directory.CreateDirectory(transactionRoot);
                try
                {
                    await DownloadAsync(uri, release, packagePath, cancellationToken).ConfigureAwait(false);
                    Directory.CreateDirectory(extracted);
                    ExtractAndVerify(packagePath, extracted, release);

                    var target = VersionDirectory(release.PackId, release.CatalogVersion, release.Sha256);
                    var targetParent = Path.GetDirectoryName(target);
                    if (String.IsNullOrWhiteSpace(targetParent)) throw new InvalidOperationException("Catalog Pack 설치 경로를 만들 수 없습니다.");
                    Directory.CreateDirectory(targetParent);
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    Directory.Move(extracted, target);

                    var active = StateFromRelease(release, target);
                    VerifyInstalled(active);
                    WriteActive(active);
                    return new CatalogPackInstallResult { Active = active, Previous = current, Changed = true, Downloaded = true };
                }
                catch
                {
                    SafeDeleteDirectory(extracted);
                    throw;
                }
                finally { SafeDeleteDirectory(transactionRoot); }
            }
        }

        public void Rollback(CatalogPackInstallResult install)
        {
            if (install == null || !install.Changed || install.Active == null) return;
            using (var updateLock = new FileStream(Path.Combine(_root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (install.Previous != null)
                {
                    VerifyInstalled(install.Previous);
                    WriteActive(install.Previous);
                    install.Active = install.Previous;
                }
                else
                {
                    var path = ActiveFile(install.Active.PackId);
                    if (File.Exists(path)) File.Delete(path);
                    install.Active = null;
                }
            }
        }

        internal static void ValidatePackId(string packId)
        {
            if (!CatalogVersionPatterns.ContainsKey(packId ?? ""))
                throw new InvalidOperationException("지원하지 않는 Catalog Pack ID입니다.");
        }

        internal static void ValidateIdentity(string packId, string catalogVersion, string packageSha256)
        {
            ValidatePackId(packId);
            if (!CatalogVersionPatterns[packId].IsMatch(catalogVersion ?? ""))
                throw new InvalidOperationException("Catalog Pack version 형식이 올바르지 않습니다.");
            if (!Sha256Pattern.IsMatch(packageSha256 ?? ""))
                throw new InvalidOperationException("Catalog Pack SHA-256 형식이 올바르지 않습니다.");
        }

        internal string ActiveFileForTest(string packId) { return ActiveFile(packId); }
        internal string VersionDirectoryForTest(string packId, string version, string sha) { return VersionDirectory(packId, version, sha); }

        private async Task DownloadAsync(Uri uri, CatalogPackReleaseManifest release, string destination, CancellationToken cancellationToken)
        {
            long total = 0;
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage == null ? uri : response.RequestMessage.RequestUri;
                RequireApprovedDownloadUri(release, finalUri == null ? "" : finalUri.Host);
                if (finalUri == null || !String.Equals(finalUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                    finalUri.AbsolutePath != uri.AbsolutePath || !HasSignedToken(finalUri))
                    throw new InvalidOperationException("Catalog Pack 다운로드 redirect가 승인 경계를 벗어났습니다.");
                var announced = response.Content.Headers.ContentLength;
                if (announced.HasValue && announced.Value != release.FileSize)
                    throw new InvalidOperationException("Catalog Pack 응답 크기가 release와 일치하지 않습니다.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read <= 0) break;
                        total += read;
                        if (total > release.FileSize || total > MaximumPackageBytes)
                            throw new InvalidOperationException("Catalog Pack 다운로드 크기가 release 경계를 초과했습니다.");
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (total != release.FileSize || !String.Equals(Sha256(destination), release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack ZIP SHA-256 검증에 실패했습니다.");
        }

        private void ExtractAndVerify(string packagePath, string destination, CatalogPackReleaseManifest release)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (String.IsNullOrEmpty(entry.Name)) throw new InvalidOperationException("Catalog Pack ZIP에는 디렉터리 항목을 둘 수 없습니다.");
                    var name = entry.FullName.Replace('\\', '/');
                    if ((name != "catalog.json" && name != "install-manifest.json") || !names.Add(name))
                        throw new InvalidOperationException("Catalog Pack ZIP 파일 집합이 허용 계약과 다릅니다.");
                    total += entry.Length;
                    if (entry.Length <= 0 || total > MaximumCatalogBytes)
                        throw new InvalidOperationException("Catalog Pack 압축 해제 크기가 허용 범위를 벗어났습니다.");
                    using (var input = entry.Open())
                    using (var output = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }
            }
            if (!names.SetEquals(new[] { "catalog.json", "install-manifest.json" }))
                throw new InvalidOperationException("Catalog Pack ZIP 필수 파일이 없습니다.");
            VerifyContent(destination, release);
        }

        private void VerifyInstalled(ActiveCatalogPackState state)
        {
            if (state == null) throw new InvalidOperationException("Catalog Pack active state가 없습니다.");
            var release = ReleaseFromState(state);
            ValidateRelease(release, false);
            CatalogPackReleaseIntegrityVerifier.VerifyForTest(release, _publicKey, _expectedKeyId);
            var expected = VersionDirectory(state.PackId, state.CatalogVersion, state.PackageSha256);
            var installed = Path.GetFullPath(state.InstalledPath ?? "");
            if (!String.Equals(expected.TrimEnd(Path.DirectorySeparatorChar), installed.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expected))
                throw new InvalidOperationException("Catalog Pack active path가 deterministic slot과 일치하지 않습니다.");
            VerifyContent(expected, release);
        }

        private void VerifyContent(string root, CatalogPackReleaseManifest release)
        {
            var manifestPath = Path.Combine(root, "install-manifest.json");
            var catalogPath = Path.Combine(root, "catalog.json");
            if (!File.Exists(manifestPath) || !File.Exists(catalogPath))
                throw new InvalidOperationException("Catalog Pack payload 필수 파일이 없습니다.");
            var actualManifestSha256 = Sha256(manifestPath);
            var actualCatalogSha256 = Sha256(catalogPath);
            if (!String.Equals(actualManifestSha256, release.InstallManifestSha256, StringComparison.Ordinal) ||
                !String.Equals(actualCatalogSha256, release.CatalogSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack payload SHA-256 검증에 실패했습니다.");
            var manifest = _json.Deserialize<CatalogPackInstallManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || manifest.SchemaVersion != 1 ||
                !String.Equals(manifest.PackId, release.PackId, StringComparison.Ordinal) ||
                !String.Equals(manifest.CatalogVersion, release.CatalogVersion, StringComparison.Ordinal) ||
                manifest.Files == null || manifest.Files.Count != 1)
                throw new InvalidOperationException("Catalog Pack install manifest identity가 올바르지 않습니다.");
            var item = manifest.Files[0];
            if (item == null || item.Path != "catalog.json" || item.Size <= 0 ||
                !String.Equals(item.Sha256, release.CatalogSha256, StringComparison.Ordinal) ||
                new FileInfo(catalogPath).Length != item.Size)
                throw new InvalidOperationException("Catalog Pack install manifest 파일 계약이 올바르지 않습니다.");
            var catalog = _json.DeserializeObject(File.ReadAllText(catalogPath, Encoding.UTF8)) as Dictionary<string, object>;
            object packValue, versionValue;
            if (catalog == null || !catalog.TryGetValue("packId", out packValue) || !catalog.TryGetValue("catalogVersion", out versionValue) ||
                !String.Equals(Convert.ToString(packValue), release.PackId, StringComparison.Ordinal) ||
                !String.Equals(Convert.ToString(versionValue), release.CatalogVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack payload identity가 release와 일치하지 않습니다.");
            var actual = new HashSet<string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/')), StringComparer.Ordinal);
            if (!actual.SetEquals(new[] { "catalog.json", "install-manifest.json" }))
                throw new InvalidOperationException("Catalog Pack 설치 파일 집합이 정확하지 않습니다.");
        }

        private void ValidateRelease(CatalogPackReleaseManifest release, bool requireDownload)
        {
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.Channel, LauncherBuildProfile.Channel, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack release envelope가 올바르지 않습니다.");
            ValidateIdentity(release.PackId, release.CatalogVersion, release.Sha256);
            if (!SemanticVersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") ||
                !Sha256Pattern.IsMatch(release.InstallManifestSha256 ?? "") || !Sha256Pattern.IsMatch(release.CatalogSha256 ?? "") ||
                release.FileSize <= 0 || release.FileSize > MaximumPackageBytes || Path.GetFileName(release.FileName ?? "") != release.FileName)
                throw new InvalidOperationException("Catalog Pack release 필드가 올바르지 않습니다.");
            var expectedName = ExpectedFileName(release.PackId, release.CatalogVersion);
            var expectedPackageId = release.Channel + ":" + release.PackId + ":" + release.CatalogVersion + ":" + release.Sha256.Substring(0, 16);
            if (!String.Equals(release.FileName, expectedName, StringComparison.Ordinal) || !String.Equals(release.PackageId, expectedPackageId, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog Pack 파일명 또는 packageId가 canonical identity와 일치하지 않습니다.");
            if (CompareVersions(LauncherVersion.Current, release.MinimumLauncherVersion) < 0)
                throw new InvalidOperationException("현재 Launcher 버전은 이 Catalog Pack을 설치할 수 없습니다.");
            if (requireDownload && (String.IsNullOrWhiteSpace(release.DownloadUrl) || release.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(5)))
                throw new InvalidOperationException("Catalog Pack signed URL이 없거나 만료되었습니다.");
        }

        private Uri RequireApprovedDownloadUri(CatalogPackReleaseManifest release, string expectedProjectHost)
        {
            Uri uri;
            if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, expectedProjectHost ?? "", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Catalog Pack 다운로드 host가 승인된 Supabase project와 다릅니다.");
            var expectedPath = "/storage/v1/object/sign/meter-core-private/catalog-packs/" + release.Channel + "/" + release.PackId + "/" + release.CatalogVersion + "/" + release.FileName;
            if (!String.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal) || !HasSignedToken(uri))
                throw new InvalidOperationException("Catalog Pack signed URL 경로가 release identity와 일치하지 않습니다.");
            return uri;
        }

        private static bool HasSignedToken(Uri uri)
        {
            if (uri == null) return false;
            return uri.Query.TrimStart('?').Split('&').Any(value =>
                value.StartsWith("token=", StringComparison.OrdinalIgnoreCase) && value.Length > "token=".Length);
        }

        private string ActiveFile(string packId)
        {
            ValidatePackId(packId);
            return Path.Combine(_root, packId, "active.json");
        }

        private string VersionDirectory(string packId, string version, string sha)
        {
            ValidateIdentity(packId, version, sha);
            var value = Path.GetFullPath(Path.Combine(_root, packId, "versions", version, sha));
            var boundary = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!value.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Catalog Pack slot이 root 밖으로 벗어났습니다.");
            return value;
        }

        private void WriteActive(ActiveCatalogPackState state)
        {
            var path = ActiveFile(state.PackId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static bool SameRelease(ActiveCatalogPackState state, CatalogPackReleaseManifest release)
        {
            return String.Equals(state.Channel, release.Channel, StringComparison.Ordinal) &&
                String.Equals(state.PackId, release.PackId, StringComparison.Ordinal) &&
                String.Equals(state.CatalogVersion, release.CatalogVersion, StringComparison.Ordinal) &&
                String.Equals(state.PackageSha256, release.Sha256, StringComparison.Ordinal) &&
                String.Equals(state.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal);
        }

        private static ActiveCatalogPackState StateFromRelease(CatalogPackReleaseManifest release, string target)
        {
            return new ActiveCatalogPackState
            {
                SchemaVersion = 1, Channel = release.Channel, PackId = release.PackId, CatalogVersion = release.CatalogVersion,
                MinimumLauncherVersion = release.MinimumLauncherVersion, PackageId = release.PackageId, FileName = release.FileName,
                FileSize = release.FileSize, InstalledPath = target, ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
                PackageSha256 = release.Sha256, InstallManifestSha256 = release.InstallManifestSha256,
                CatalogSha256 = release.CatalogSha256, IntegrityMode = release.IntegrityMode,
                SigningKeyId = release.SigningKeyId, ManifestSignature = release.ManifestSignature
            };
        }

        private static CatalogPackReleaseManifest ReleaseFromState(ActiveCatalogPackState state)
        {
            return new CatalogPackReleaseManifest
            {
                SchemaVersion = state.SchemaVersion, Channel = state.Channel, PackId = state.PackId,
                CatalogVersion = state.CatalogVersion, MinimumLauncherVersion = state.MinimumLauncherVersion,
                PackageId = state.PackageId, FileName = state.FileName, FileSize = state.FileSize,
                Sha256 = (state.PackageSha256 ?? "").ToLowerInvariant(),
                InstallManifestSha256 = (state.InstallManifestSha256 ?? "").ToLowerInvariant(),
                CatalogSha256 = (state.CatalogSha256 ?? "").ToLowerInvariant(), IntegrityMode = state.IntegrityMode,
                SigningKeyId = state.SigningKeyId, ManifestSignature = state.ManifestSignature
            };
        }

        private static string ExpectedFileName(string packId, string version)
        {
            if (packId == DungeonBossPackId) return "KinojoDungeonBossCatalog_" + version + ".zip";
            if (packId == ClassSkillPackId) return "KinojoClassSkillCatalog_" + version + ".zip";
            if (packId == BossHpPackId) return "KinojoBossHpFingerprint_" + version + ".zip";
            throw new InvalidOperationException("지원하지 않는 Catalog Pack ID입니다.");
        }

        private static int CompareVersions(string left, string right)
        {
            var leftParts = (left ?? "").Split('.');
            var rightParts = (right ?? "").Split('.');
            if (leftParts.Length != 3 || rightParts.Length != 3) return -1;
            for (var index = 0; index < 3; index++)
            {
                int leftPart, rightPart;
                if (!Int32.TryParse(leftParts[index], out leftPart) || !Int32.TryParse(rightParts[index], out rightPart)) return -1;
                var compared = leftPart.CompareTo(rightPart);
                if (compared != 0) return compared;
            }
            return 0;
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
                return String.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static void SafeDeleteDirectory(string path)
        {
            try { if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        public void Dispose() { _http.Dispose(); }
    }

    internal static class CatalogPackUpdateCoordinator
    {
        public static readonly string[] PackOrder =
        {
            CatalogPackInstaller.DungeonBossPackId,
            CatalogPackInstaller.ClassSkillPackId,
            CatalogPackInstaller.BossHpPackId
        };

        public static List<Dictionary<string, object>> CurrentStatePayload(CatalogPackInstaller installer)
        {
            if (installer == null) throw new ArgumentNullException("installer");
            return PackOrder.Select(packId => installer.ReadVerifiedActiveState(packId)).Where(state => state != null)
                .Select(state => new Dictionary<string, object>
                {
                    { "packId", state.PackId }, { "catalogVersion", state.CatalogVersion }, { "sha256", state.PackageSha256 }
                }).ToList();
        }

        public static async Task<List<CatalogPackInstallResult>> ApplyAsync(
            CatalogPackInstaller installer,
            CatalogPackUpdateAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (installer == null) throw new ArgumentNullException("installer");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "Catalog Pack 업데이트 승인을 받지 못했습니다." : authorization.Message);
            var releases = authorization.Releases ?? new List<CatalogPackReleaseManifest>();
            if (releases.Count > PackOrder.Length || releases.Any(release => release == null) ||
                releases.GroupBy(release => release.PackId, StringComparer.Ordinal).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Server Catalog Pack release 집합에 중복 또는 초과 항목이 있습니다.");
            var byId = releases.ToDictionary(release => release.PackId, StringComparer.Ordinal);
            if (byId.Keys.Any(packId => Array.IndexOf(PackOrder, packId) < 0))
                throw new InvalidOperationException("Server가 지원하지 않는 Catalog Pack을 승인했습니다.");
            var results = new List<CatalogPackInstallResult>();
            foreach (var packId in PackOrder)
            {
                CatalogPackReleaseManifest release;
                if (!byId.TryGetValue(packId, out release)) continue;
                results.Add(await installer.EnsureInstalledAsync(release, expectedProjectHost, cancellationToken).ConfigureAwait(false));
            }
            return results;
        }
    }
}
