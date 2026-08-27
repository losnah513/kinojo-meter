using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KinojoMeterLauncher
{
    internal sealed class ModuleBundleBootstrapAuthorization
    {
        public bool Authorized { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public ServerBundleManifest ServerManifest { get; set; }
        public ModuleBundlePointerContext Pointer { get; set; }
        public ModuleBundleLockDownloadAuthorization BundleLockDownload { get; set; }
        public List<ModuleBundlePackageAuthorization> Modules { get; set; }
    }

    internal sealed class ModuleBundleLockDownloadAuthorization
    {
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class ModuleBundlePackageAuthorization
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string PackagePath { get; set; }
        public long FileSize { get; set; }
        public string Sha256 { get; set; }
        public string DownloadUrl { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int ContractSetVersion { get; set; }
        public int StateSchemaVersion { get; set; }
    }

    internal sealed class ModuleBundleBootstrapResult
    {
        public string BundleRevision { get; set; }
        public string BundleLockSha256 { get; set; }
        public bool Changed { get; set; }
        public int DownloadedModuleCount { get; set; }
        public int CacheHitModuleCount { get; set; }
    }

    internal static class ModuleBundleBootstrapCoordinator
    {
        private const long MaximumBundleLockBytes = 512L * 1024L;
        private static readonly string[] ModuleOrder =
        {
            "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell"
        };

        public static async Task<ModuleBundleBootstrapResult> ApplyAsync(
            ModuleBundleBootstrapAuthorization authorization,
            string expectedProjectHost,
            IProgress<ModulePackageDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            LauncherPaths.EnsureDirectories();
            ValidateAuthorization(authorization, expectedProjectHost, DateTimeOffset.UtcNow);
            ServerBundleManifestVerifier.Verify(authorization.ServerManifest);

            var current = ModuleBundleActivator.ReadVerifiedActiveBundle();
            if (current != null &&
                String.Equals(current.BundleRevision, authorization.ServerManifest.BundleRevision, StringComparison.Ordinal) &&
                String.Equals(current.BundleLockSha256, authorization.ServerManifest.BundleLock.Sha256, StringComparison.Ordinal))
            {
                return Result(current.BundleRevision, current.BundleLockSha256, false, 0, ModuleOrder.Length);
            }

            var lockFile = await DownloadBundleLockAsync(
                authorization,
                expectedProjectHost,
                LauncherPaths.ModuleBundleLocks,
                cancellationToken).ConfigureAwait(false);
            var bundle = ModuleBundleActivator.ReadAndValidateBundleLockForCoordinator(lockFile, authorization.Pointer.BundleOriginChannel);
            ValidateBundleAgainstAuthorization(bundle, authorization);

            var staged = new Dictionary<string, ModuleStagingInstallResult>(StringComparer.Ordinal);
            var downloaded = 0;
            var cacheHits = 0;
            using (var cache = new ModulePackageDownloadCache())
            {
                foreach (var moduleId in ModuleOrder)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = bundle.Modules.Single(value => String.Equals(value.ModuleId, moduleId, StringComparison.Ordinal));
                    var approved = authorization.Modules.Single(value => String.Equals(value.ModuleId, moduleId, StringComparison.Ordinal));
                    var download = DownloadRequest(approved, expectedProjectHost, authorization.Pointer.BundleOriginChannel);
                    var cached = await cache.DownloadAsync(download, progress, cancellationToken).ConfigureAwait(false);
                    if (cached.CacheHit) cacheHits++; else downloaded++;

                    var verification = new ModulePackageVerificationRequest
                    {
                        Cache = cached,
                        ModuleId = entry.ModuleId,
                        ModuleVersion = entry.ModuleVersion,
                        BundlePackagePath = entry.PackagePath,
                        ExpectedSha256 = entry.Sha256,
                        ContractSetVersion = entry.ContractSetVersion,
                        StateSchemaVersion = entry.StateSchemaVersion
                    };
                    var verified = ModulePackageVerifier.Verify(verification);
                    if (verified == null ||
                        !String.Equals(verified.VerificationStatus, ModulePackageVerifier.VerifiedStatus, StringComparison.Ordinal))
                        throw new InvalidOperationException("Bundle 모듈 서명 검증이 완료되지 않았습니다: " + moduleId);

                    var installed = ModuleStagingInstaller.Stage(new ModuleStagingInstallRequest { VerificationRequest = verification });
                    var dependencies = DependencyList(moduleId, staged);
                    var selfTest = ModuleStagingSelfTest.Run(new ModuleSelfTestRequest
                    {
                        Target = installed,
                        Dependencies = dependencies
                    });
                    if (selfTest == null || !String.Equals(selfTest.Status, ModuleStagingSelfTest.PassedStatus, StringComparison.Ordinal))
                        throw new InvalidOperationException("Bundle 모듈 self-test가 완료되지 않았습니다: " + moduleId);
                    staged.Add(moduleId, installed);
                }
            }

            var activated = ModuleBundleActivator.Activate(new ModuleBundleActivationRequest
            {
                BundleLockFile = lockFile,
                ExpectedBundleLockSha256 = authorization.ServerManifest.BundleLock.Sha256,
                ExpectedChannel = authorization.ServerManifest.Channel,
                ExpectedCurrentBundleRevision = current == null
                    ? authorization.ServerManifest.ParentBundleRevision
                    : current.BundleRevision,
                ExpectedCurrentBundleLockSha256 = current == null ? "" : current.BundleLockSha256,
                PointerContext = authorization.Pointer
            });
            return Result(activated.BundleRevision, activated.BundleLockSha256, activated.Changed, downloaded, cacheHits);
        }

        internal static void ValidateAuthorizationForTest(
            ModuleBundleBootstrapAuthorization authorization,
            string expectedProjectHost,
            DateTimeOffset now)
        {
            ValidateAuthorization(authorization, expectedProjectHost, now);
        }

        internal static void ValidateBundleAgainstAuthorizationForTest(
            ModuleBundleLock bundle,
            ModuleBundleBootstrapAuthorization authorization)
        {
            ValidateBundleAgainstAuthorization(bundle, authorization);
        }

        private static void ValidateAuthorization(
            ModuleBundleBootstrapAuthorization authorization,
            string expectedProjectHost,
            DateTimeOffset now)
        {
            if (authorization == null || !authorization.Authorized || authorization.ServerManifest == null ||
                authorization.Pointer == null || authorization.BundleLockDownload == null || authorization.Modules == null)
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(authorization == null ? "" : authorization.Message)
                    ? "Server가 Bundle bootstrap을 승인하지 않았습니다."
                    : authorization.Message);
            if (String.IsNullOrWhiteSpace(expectedProjectHost))
                throw new InvalidOperationException("Bundle 다운로드 Server host가 없습니다.");
            if (authorization.Modules.Count != ModuleOrder.Length ||
                authorization.Modules.Any(value => value == null) ||
                authorization.Modules.Select(value => value.ModuleId).Distinct(StringComparer.Ordinal).Count() != ModuleOrder.Length ||
                ModuleOrder.Any(id => !authorization.Modules.Any(value => String.Equals(value.ModuleId, id, StringComparison.Ordinal))))
                throw new InvalidOperationException("Bundle 다운로드 승인은 정확히 7개 모듈이어야 합니다.");

            var manifest = authorization.ServerManifest;
            if (!String.Equals(authorization.Pointer.ServingChannel, manifest.Channel, StringComparison.Ordinal) ||
                !String.Equals(authorization.Pointer.BundleOriginChannel, manifest.BundleLock.OriginChannel, StringComparison.Ordinal) ||
                !String.Equals(authorization.Pointer.BundleRevision, manifest.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(authorization.Pointer.BundleLockSha256, manifest.BundleLock.Sha256, StringComparison.Ordinal) ||
                authorization.Pointer.PointerGeneration < 1)
                throw new InvalidOperationException("Server Bundle Manifest와 pointer identity가 일치하지 않습니다.");

            ValidateSignedUrl(
                authorization.BundleLockDownload.DownloadUrl,
                expectedProjectHost,
                "/storage/v1/object/sign/meter-core-private/bundles/" + manifest.BundleRevision + "/bundle.lock.json");
            if (authorization.BundleLockDownload.ExpiresAt <= now.AddSeconds(10) ||
                authorization.BundleLockDownload.FileSize <= 0 ||
                authorization.BundleLockDownload.FileSize > MaximumBundleLockBytes ||
                !String.Equals(authorization.BundleLockDownload.Sha256, manifest.BundleLock.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Bundle Lock 다운로드 승인 identity/만료/크기가 올바르지 않습니다.");

            foreach (var module in authorization.Modules)
            {
                if (module.ExpiresAt <= now.AddSeconds(10))
                    throw new InvalidOperationException("Bundle 모듈 signed URL이 만료되었거나 너무 임박했습니다: " + module.ModuleId);
                ModulePackageDownloadCache.ValidateRequestForTest(DownloadRequest(module, expectedProjectHost, authorization.Pointer.BundleOriginChannel));
            }
        }

        private static void ValidateBundleAgainstAuthorization(
            ModuleBundleLock bundle,
            ModuleBundleBootstrapAuthorization authorization)
        {
            var manifest = authorization.ServerManifest;
            if (bundle == null ||
                !String.Equals(bundle.BundleRevision, manifest.BundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.ParentBundleRevision, manifest.ParentBundleRevision, StringComparison.Ordinal) ||
                !String.Equals(bundle.ProductVersion, manifest.ProductVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("다운로드한 Bundle Lock이 서명된 Server Bundle Manifest와 일치하지 않습니다.");

            foreach (var entry in bundle.Modules)
            {
                var approved = authorization.Modules.SingleOrDefault(value => String.Equals(value.ModuleId, entry.ModuleId, StringComparison.Ordinal));
                if (approved == null ||
                    !String.Equals(approved.ModuleVersion, entry.ModuleVersion, StringComparison.Ordinal) ||
                    !String.Equals(approved.PackagePath, entry.PackagePath, StringComparison.Ordinal) ||
                    !String.Equals(approved.Sha256, entry.Sha256, StringComparison.Ordinal) ||
                    approved.ContractSetVersion != entry.ContractSetVersion ||
                    approved.StateSchemaVersion != entry.StateSchemaVersion)
                    throw new InvalidOperationException("Bundle Lock과 모듈 다운로드 승인이 일치하지 않습니다: " + entry.ModuleId);
            }
        }

        private static ModulePackageDownloadRequest DownloadRequest(ModuleBundlePackageAuthorization value, string host, string originChannel)
        {
            Uri uri;
            if (value == null || !Uri.TryCreate(value.DownloadUrl, UriKind.Absolute, out uri))
                throw new InvalidOperationException("Bundle 모듈 다운로드 URL이 올바르지 않습니다.");
            var parts = (value.PackagePath ?? "").Split('/');
            if (parts.Length != 4 || parts[0] != "modules" || parts[1] != value.ModuleId || parts[2] != value.ModuleVersion ||
                (originChannel != "stable" && originChannel != "staging"))
                throw new InvalidOperationException("Bundle 모듈 packagePath/origin channel 조합이 올바르지 않습니다.");
            return new ModulePackageDownloadRequest
            {
                ModuleId = value.ModuleId,
                ModuleVersion = value.ModuleVersion,
                PackagePath = value.PackagePath,
                ExpectedSha256 = value.Sha256,
                DownloadUri = uri,
                ExpectedDownloadHost = host,
                ExpectedDownloadPath = "/storage/v1/object/sign/meter-core-private/modules/" +
                    value.ModuleId + "/" + originChannel + "/" + value.ModuleVersion + "/" + parts[3],
                ExpectedFileSize = value.FileSize
            };
        }

        private static List<ModuleSelfTestDependency> DependencyList(
            string moduleId,
            IDictionary<string, ModuleStagingInstallResult> staged)
        {
            string[] ids;
            switch (moduleId)
            {
                case "contracts": ids = new string[0]; break;
                case "capture": ids = new[] { "contracts" }; break;
                case "protocol": ids = new[] { "contracts", "capture" }; break;
                case "combat": ids = new[] { "contracts", "protocol" }; break;
                case "encounter": ids = new[] { "contracts" }; break;
                case "sync": ids = new[] { "contracts", "capture", "protocol", "combat" }; break;
                case "shell": ids = new[] { "contracts", "capture", "protocol", "combat", "encounter", "sync" }; break;
                default: throw new InvalidOperationException("지원하지 않는 Bundle 모듈입니다: " + moduleId);
            }
            return ids.Select(id =>
            {
                ModuleStagingInstallResult value;
                if (!staged.TryGetValue(id, out value))
                    throw new InvalidOperationException("Bundle dependency가 먼저 staging되지 않았습니다: " + id);
                return new ModuleSelfTestDependency
                {
                    ModuleId = value.ModuleId,
                    ModuleVersion = value.ModuleVersion,
                    ArchiveSha256 = value.ArchiveSha256,
                    StagedDirectory = value.StagedDirectory
                };
            }).ToList();
        }

        private static async Task<string> DownloadBundleLockAsync(
            ModuleBundleBootstrapAuthorization authorization,
            string expectedHost,
            string locksRoot,
            CancellationToken cancellationToken)
        {
            var approved = authorization.BundleLockDownload;
            var revision = authorization.ServerManifest.BundleRevision;
            var expectedPath = "/storage/v1/object/sign/meter-core-private/bundles/" + revision + "/bundle.lock.json";
            ValidateSignedUrl(approved.DownloadUrl, expectedHost, expectedPath);
            var finalDirectory = Path.Combine(Path.GetFullPath(locksRoot), revision);
            var finalFile = Path.Combine(finalDirectory, "bundle.lock.json");
            if (File.Exists(finalFile) &&
                String.Equals(Sha256File(finalFile), approved.Sha256, StringComparison.Ordinal) &&
                new FileInfo(finalFile).Length == approved.FileSize)
                return finalFile;

            Directory.CreateDirectory(finalDirectory);
            var temporary = finalFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
                using (var response = await http.GetAsync(approved.DownloadUrl, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var finalUri = response.RequestMessage == null ? new Uri(approved.DownloadUrl) : response.RequestMessage.RequestUri;
                    ValidateSignedUrl(finalUri.ToString(), expectedHost, expectedPath);
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytes.LongLength != approved.FileSize || bytes.LongLength <= 0 || bytes.LongLength > MaximumBundleLockBytes ||
                        !String.Equals(Sha256(bytes), approved.Sha256, StringComparison.Ordinal))
                        throw new InvalidOperationException("다운로드한 Bundle Lock의 크기/SHA-256이 Server 승인과 일치하지 않습니다.");
                    File.WriteAllBytes(temporary, bytes);
                }
                if (File.Exists(finalFile)) File.Replace(temporary, finalFile, null);
                else File.Move(temporary, finalFile);
                return finalFile;
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
        }

        private static void ValidateSignedUrl(string text, string expectedHost, string expectedPath)
        {
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal) ||
                !uri.Query.TrimStart('?').Split('&').Any(value => value.StartsWith("token=", StringComparison.Ordinal) && value.Length > 6))
                throw new InvalidOperationException("Bundle signed URL이 Server 승인 경계를 벗어났습니다.");
        }

        private static string Sha256File(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(stream));
        }

        private static string Sha256(byte[] value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(value));
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static ModuleBundleBootstrapResult Result(string revision, string sha, bool changed, int downloaded, int cacheHits)
        {
            return new ModuleBundleBootstrapResult
            {
                BundleRevision = revision,
                BundleLockSha256 = sha,
                Changed = changed,
                DownloadedModuleCount = downloaded,
                CacheHitModuleCount = cacheHits
            };
        }
    }
}
