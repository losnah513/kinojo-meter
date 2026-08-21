using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class ModulePackageDownloadRequest
    {
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string PackagePath { get; set; }
        public string ExpectedSha256 { get; set; }
        public Uri DownloadUri { get; set; }
        public string ExpectedDownloadHost { get; set; }
        public string ExpectedDownloadPath { get; set; }
        public long ExpectedFileSize { get; set; }
    }

    internal sealed class ModulePackageCacheResult
    {
        public string PackageFile { get; set; }
        public string MetadataFile { get; set; }
        public long Bytes { get; set; }
        public bool CacheHit { get; set; }
        public bool RequiresVerification { get; set; }
        public string VerificationStatus { get; set; }
    }

    internal sealed class ModulePackageDownloadProgress
    {
        public string ModuleId { get; set; }
        public long BytesReceived { get; set; }
        public string Stage { get; set; }
    }

    internal sealed class ModulePackageCacheMetadata
    {
        public int SchemaVersion { get; set; }
        public string ModuleId { get; set; }
        public string ModuleVersion { get; set; }
        public string BundlePackagePath { get; set; }
        public string ExpectedSha256 { get; set; }
        public long Bytes { get; set; }
        public string VerificationStatus { get; set; }
        public string DownloadedAtUtc { get; set; }
    }

    internal sealed class ModulePackageDownloadCache : IDisposable
    {
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private const string UnverifiedStatus = "UNVERIFIED";
        private static readonly string[] ModuleIds =
        {
            "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell"
        };

        private readonly HttpClient _http;
        private readonly string _cacheRoot;

        public ModulePackageDownloadCache()
            : this(new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, LauncherPaths.ModulePackageCache)
        {
        }

        internal ModulePackageDownloadCache(HttpMessageHandler handler, string cacheRoot)
            : this(new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(3) }, cacheRoot)
        {
        }

        private ModulePackageDownloadCache(HttpClient http, string cacheRoot)
        {
            if (http == null) throw new ArgumentNullException("http");
            if (String.IsNullOrWhiteSpace(cacheRoot)) throw new ArgumentException("cacheRoot");
            _http = http;
            _cacheRoot = Path.GetFullPath(cacheRoot);
        }

        public async Task<ModulePackageCacheResult> DownloadAsync(
            ModulePackageDownloadRequest request,
            IProgress<ModulePackageDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRequest(request);

            var cached = TryGetCachedCandidate(request);
            if (cached != null)
            {
                progress?.Report(new ModulePackageDownloadProgress
                {
                    ModuleId = request.ModuleId,
                    BytesReceived = cached.Bytes,
                    Stage = "MODULE_CACHE_HIT_UNVERIFIED"
                });
                return cached;
            }

            Directory.CreateDirectory(_cacheRoot);
            var incomingRoot = Path.Combine(_cacheRoot, ".incoming");
            Directory.CreateDirectory(incomingRoot);
            var stagingDirectory = Path.Combine(incomingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagingPackage = Path.Combine(stagingDirectory, "package.zip");
            var stagingMetadata = Path.Combine(stagingDirectory, "download.json");

            try
            {
                progress?.Report(new ModulePackageDownloadProgress
                {
                    ModuleId = request.ModuleId,
                    BytesReceived = 0,
                    Stage = "MODULE_DOWNLOAD_START"
                });

                long total = 0;
                using (var response = await _http.GetAsync(
                    request.DownloadUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var finalUri = response.RequestMessage == null ? request.DownloadUri : response.RequestMessage.RequestUri;
                    RequireHttps(finalUri);
                    ValidateApprovedDownloadUri(request, finalUri);

                    var announced = response.Content.Headers.ContentLength;
                    if (request.ExpectedFileSize > 0 && announced.HasValue && announced.Value != request.ExpectedFileSize)
                        throw new InvalidOperationException("모듈 패키지 응답 크기가 Server release와 일치하지 않습니다.");
                    if (announced.HasValue && (announced.Value <= 0 || announced.Value > MaximumPackageBytes))
                        throw new InvalidOperationException("모듈 패키지 응답 크기가 허용 범위를 벗어났습니다.");

                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(
                        stagingPackage,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        128 * 1024,
                        true))
                    {
                        var buffer = new byte[128 * 1024];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                            if (read <= 0) break;
                            total += read;
                            if (total > MaximumPackageBytes)
                                throw new InvalidOperationException("모듈 패키지가 허용 크기를 초과했습니다.");
                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            progress?.Report(new ModulePackageDownloadProgress
                            {
                                ModuleId = request.ModuleId,
                                BytesReceived = total,
                                Stage = "MODULE_DOWNLOAD_STREAMING"
                            });
                        }
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (total <= 0)
                    throw new InvalidOperationException("빈 모듈 패키지는 캐시할 수 없습니다.");
                if (request.ExpectedFileSize > 0 && total != request.ExpectedFileSize)
                    throw new InvalidOperationException("모듈 패키지 크기가 Server release와 일치하지 않습니다.");

                var metadata = new ModulePackageCacheMetadata
                {
                    SchemaVersion = 1,
                    ModuleId = request.ModuleId,
                    ModuleVersion = request.ModuleVersion,
                    BundlePackagePath = request.PackagePath,
                    ExpectedSha256 = request.ExpectedSha256,
                    Bytes = total,
                    VerificationStatus = UnverifiedStatus,
                    DownloadedAtUtc = DateTime.UtcNow.ToString("o")
                };
                File.WriteAllText(stagingMetadata, new JavaScriptSerializer().Serialize(metadata));

                var finalDirectory = CacheDirectory(request);
                var finalParent = Path.GetDirectoryName(finalDirectory);
                if (String.IsNullOrWhiteSpace(finalParent))
                    throw new InvalidOperationException("모듈 캐시 경로를 만들 수 없습니다.");
                Directory.CreateDirectory(finalParent);

                if (Directory.Exists(finalDirectory))
                {
                    cached = TryGetCachedCandidate(request);
                    if (cached != null)
                    {
                        SafeDeleteDirectory(stagingDirectory);
                        return cached;
                    }
                    SafeDeleteDirectory(finalDirectory);
                }

                Directory.Move(stagingDirectory, finalDirectory);
                var result = ResultFor(finalDirectory, metadata, false);
                progress?.Report(new ModulePackageDownloadProgress
                {
                    ModuleId = request.ModuleId,
                    BytesReceived = total,
                    Stage = "MODULE_CACHE_COMMITTED_UNVERIFIED"
                });
                return result;
            }
            catch
            {
                SafeDeleteDirectory(stagingDirectory);
                throw;
            }
        }

        internal ModulePackageCacheResult TryGetCachedCandidateForTest(ModulePackageDownloadRequest request)
        {
            ValidateRequest(request);
            return TryGetCachedCandidate(request);
        }

        internal string CacheDirectoryForTest(ModulePackageDownloadRequest request)
        {
            ValidateRequest(request);
            return CacheDirectory(request);
        }

        internal static long MaximumPackageBytesForTest
        {
            get { return MaximumPackageBytes; }
        }

        internal static void ValidateRequestForTest(ModulePackageDownloadRequest request)
        {
            ValidateRequest(request);
        }

        private ModulePackageCacheResult TryGetCachedCandidate(ModulePackageDownloadRequest request)
        {
            var directory = CacheDirectory(request);
            var package = Path.Combine(directory, "package.zip");
            var metadataPath = Path.Combine(directory, "download.json");
            if (!File.Exists(package) || !File.Exists(metadataPath)) return null;

            try
            {
                var metadata = new JavaScriptSerializer().Deserialize<ModulePackageCacheMetadata>(File.ReadAllText(metadataPath));
                if (metadata == null || metadata.SchemaVersion != 1 ||
                    !String.Equals(metadata.ModuleId, request.ModuleId, StringComparison.Ordinal) ||
                    !String.Equals(metadata.ModuleVersion, request.ModuleVersion, StringComparison.Ordinal) ||
                    !String.Equals(metadata.BundlePackagePath, request.PackagePath, StringComparison.Ordinal) ||
                    !String.Equals(metadata.ExpectedSha256, request.ExpectedSha256, StringComparison.Ordinal) ||
                    !String.Equals(metadata.VerificationStatus, UnverifiedStatus, StringComparison.Ordinal) ||
                    metadata.Bytes <= 0 || metadata.Bytes > MaximumPackageBytes ||
                    new FileInfo(package).Length != metadata.Bytes)
                {
                    SafeDeleteDirectory(directory);
                    return null;
                }
                return ResultFor(directory, metadata, true);
            }
            catch
            {
                SafeDeleteDirectory(directory);
                return null;
            }
        }

        private static ModulePackageCacheResult ResultFor(string directory, ModulePackageCacheMetadata metadata, bool cacheHit)
        {
            return new ModulePackageCacheResult
            {
                PackageFile = Path.Combine(directory, "package.zip"),
                MetadataFile = Path.Combine(directory, "download.json"),
                Bytes = metadata.Bytes,
                CacheHit = cacheHit,
                RequiresVerification = true,
                VerificationStatus = UnverifiedStatus
            };
        }

        private string CacheDirectory(ModulePackageDownloadRequest request)
        {
            var directory = Path.GetFullPath(Path.Combine(
                _cacheRoot,
                request.ModuleId,
                request.ModuleVersion,
                request.ExpectedSha256));
            var rootWithSeparator = _cacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("모듈 캐시 경로가 루트 밖으로 벗어났습니다.");
            return directory;
        }

        private static void ValidateRequest(ModulePackageDownloadRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (Array.IndexOf(ModuleIds, request.ModuleId) < 0)
                throw new InvalidOperationException("지원하지 않는 모듈 ID입니다.");
            if (!Regex.IsMatch(request.ModuleVersion ?? "", @"^\d{1,4}\.\d{1,4}\.\d{1,4}$"))
                throw new InvalidOperationException("모듈 버전 형식이 올바르지 않습니다.");
            if (!Regex.IsMatch(request.ExpectedSha256 ?? "", "^[0-9a-f]{64}$"))
                throw new InvalidOperationException("Bundle Lock 모듈 SHA-256 형식이 올바르지 않습니다.");
            ValidatePackagePath(request);
            RequireHttps(request.DownloadUri);
            ValidateApprovedDownloadUri(request, request.DownloadUri);
            if (request.ExpectedFileSize < 0 || request.ExpectedFileSize > MaximumPackageBytes)
                throw new InvalidOperationException("모듈 패키지 Server release 크기가 허용 범위를 벗어났습니다.");
        }

        private static void ValidatePackagePath(ModulePackageDownloadRequest request)
        {
            var path = request.PackagePath ?? "";
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0)
                throw new InvalidOperationException("Bundle Lock packagePath 형식이 올바르지 않습니다.");

            var expectedPrefix = "modules/" + request.ModuleId + "/" + request.ModuleVersion + "/";
            if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Bundle Lock packagePath가 모듈 ID/버전과 일치하지 않습니다.");

            var segments = path.Split('/');
            if (segments.Length < 4)
                throw new InvalidOperationException("Bundle Lock packagePath가 너무 짧습니다.");
            foreach (var segment in segments)
            {
                if (String.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." ||
                    !Regex.IsMatch(segment, "^[A-Za-z0-9._-]+$"))
                    throw new InvalidOperationException("Bundle Lock packagePath에 안전하지 않은 경로가 있습니다.");
            }
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("모듈 packagePath는 ZIP이어야 합니다.");
        }

        private static void RequireHttps(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("모듈 패키지는 Server가 승인한 HTTPS 주소에서만 내려받을 수 있습니다.");
        }

        private static void ValidateApprovedDownloadUri(ModulePackageDownloadRequest request, Uri uri)
        {
            if (request == null || uri == null) throw new InvalidOperationException("모듈 다운로드 주소가 없습니다.");
            if (String.IsNullOrWhiteSpace(request.ExpectedDownloadHost) && String.IsNullOrWhiteSpace(request.ExpectedDownloadPath)) return;
            if (String.IsNullOrWhiteSpace(request.ExpectedDownloadHost) || String.IsNullOrWhiteSpace(request.ExpectedDownloadPath) ||
                !String.Equals(uri.Host, request.ExpectedDownloadHost, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(uri.AbsolutePath, request.ExpectedDownloadPath, StringComparison.Ordinal) ||
                !HasSignedToken(uri))
                throw new InvalidOperationException("모듈 패키지 signed URL이 Server 승인 경계를 벗어났습니다.");
        }

        private static bool HasSignedToken(Uri uri)
        {
            if (uri == null) return false;
            foreach (var item in uri.Query.TrimStart('?').Split('&'))
            {
                var parts = item.Split(new[] { '=' }, 2);
                if (parts.Length == 2 &&
                    String.Equals(Uri.UnescapeDataString(parts[0]), "token", StringComparison.Ordinal) &&
                    !String.IsNullOrWhiteSpace(Uri.UnescapeDataString(parts[1]))) return true;
            }
            return false;
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

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
