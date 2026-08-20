using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KinojoMeterLauncher
{
    internal static class ModuleDownloadCacheTests
    {
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "kinojo-module-cache-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("download into unverified quarantine cache", () => VerifyDownload(root));
                Run("reuse exact module/version/SHA cache candidate", () => VerifyCacheHit(root));
                Run("different expected SHA uses a different cache slot", () => VerifyDifferentSha(root));
                Run("reject module/version packagePath mismatch", VerifyPackagePathMismatch);
                Run("reject packagePath traversal", VerifyTraversalRejected);
                Run("reject non-HTTPS package URL", VerifyHttpsRequired);
                Run("reject announced oversized package", () => VerifyOversizedResponse(root));
                Console.WriteLine("Module download cache tests passed: " + _passed);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { }
            }
        }

        private static void VerifyDownload(string root)
        {
            var cacheRoot = Path.Combine(root, "download");
            var payload = Encoding.UTF8.GetBytes("stage-5-3-module-package-payload");
            var handler = new StubHandler(payload);
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                var result = cache.DownloadAsync(Request(), null, CancellationToken.None).GetAwaiter().GetResult();
                if (result == null || result.CacheHit || !result.RequiresVerification ||
                    result.VerificationStatus != "UNVERIFIED")
                    throw new InvalidOperationException("Downloaded module was not kept in the unverified cache state.");
                if (!File.Exists(result.PackageFile) || !File.ReadAllBytes(result.PackageFile).SequenceEqual(payload))
                    throw new InvalidOperationException("Downloaded module package bytes were not committed to cache.");
                if (!File.Exists(result.MetadataFile))
                    throw new InvalidOperationException("Module cache metadata is missing.");
                var metadata = File.ReadAllText(result.MetadataFile);
                if (metadata.IndexOf("UNVERIFIED", StringComparison.Ordinal) < 0 ||
                    metadata.IndexOf("https://", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidOperationException("Cache metadata must be unverified and must not persist signed download URLs.");
                if (handler.Calls != 1)
                    throw new InvalidOperationException("Expected exactly one module package request.");
                var normalizedRoot = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(result.PackageFile).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Module cache escaped its configured root.");
            }
        }

        private static void VerifyCacheHit(string root)
        {
            var cacheRoot = Path.Combine(root, "cache-hit");
            var handler = new StubHandler(Encoding.UTF8.GetBytes("cache-hit-payload"));
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                var request = Request();
                var first = cache.DownloadAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
                var second = cache.DownloadAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
                if (first.CacheHit || !second.CacheHit || handler.Calls != 1)
                    throw new InvalidOperationException("Exact cached module candidate was not reused.");
                if (!second.RequiresVerification || second.VerificationStatus != "UNVERIFIED")
                    throw new InvalidOperationException("Cache hit bypassed Stage 5-4 verification gate.");
            }
        }

        private static void VerifyDifferentSha(string root)
        {
            var cacheRoot = Path.Combine(root, "different-sha");
            var handler = new StubHandler(Encoding.UTF8.GetBytes("different-sha-payload"));
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                var firstRequest = Request();
                var secondRequest = Request();
                secondRequest.ExpectedSha256 = new String('b', 64);
                var first = cache.DownloadAsync(firstRequest, null, CancellationToken.None).GetAwaiter().GetResult();
                var second = cache.DownloadAsync(secondRequest, null, CancellationToken.None).GetAwaiter().GetResult();
                if (handler.Calls != 2 || String.Equals(first.PackageFile, second.PackageFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Different Bundle Lock SHA values shared a cache slot.");
            }
        }

        private static void VerifyPackagePathMismatch()
        {
            var request = Request();
            request.PackagePath = "modules/combat/1.6.2/KinojoCombat.zip";
            ExpectFailure(() => ModulePackageDownloadCache.ValidateRequestForTest(request));
        }

        private static void VerifyTraversalRejected()
        {
            var request = Request();
            request.PackagePath = "modules/combat/1.6.3/../outside.zip";
            ExpectFailure(() => ModulePackageDownloadCache.ValidateRequestForTest(request));
        }

        private static void VerifyHttpsRequired()
        {
            var request = Request();
            request.DownloadUri = new Uri("http://example.invalid/modules/combat/1.6.3/KinojoCombat.zip");
            ExpectFailure(() => ModulePackageDownloadCache.ValidateRequestForTest(request));
        }

        private static void VerifyOversizedResponse(string root)
        {
            var handler = new StubHandler(new byte[] { 1 })
            {
                AnnouncedLength = ModulePackageDownloadCache.MaximumPackageBytesForTest + 1
            };
            using (var cache = new ModulePackageDownloadCache(handler, Path.Combine(root, "oversized")))
            {
                ExpectFailure(() => cache.DownloadAsync(Request(), null, CancellationToken.None).GetAwaiter().GetResult());
            }
        }

        private static ModulePackageDownloadRequest Request()
        {
            return new ModulePackageDownloadRequest
            {
                ModuleId = "combat",
                ModuleVersion = "1.6.3",
                PackagePath = "modules/combat/1.6.3/KinojoCombat.zip",
                ExpectedSha256 = new String('a', 64),
                DownloadUri = new Uri("https://example.invalid/modules/combat/1.6.3/KinojoCombat.zip")
            };
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private static void ExpectFailure(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }
            throw new InvalidOperationException("Expected failure was not raised.");
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly byte[] _payload;

            public StubHandler(byte[] payload)
            {
                _payload = payload ?? new byte[0];
            }

            public int Calls { get; private set; }
            public long? AnnouncedLength { get; set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var content = new ByteArrayContent(_payload);
                if (AnnouncedLength.HasValue)
                    content.Headers.ContentLength = AnnouncedLength.Value;
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = content
                };
                return Task.FromResult(response);
            }
        }
    }
}
