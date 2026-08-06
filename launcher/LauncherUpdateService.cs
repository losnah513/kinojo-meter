using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherUpdateService : IDisposable
    {
        private const long MaximumInstallerBytes = 10L * 1024L * 1024L;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        public bool IsUpdateAvailable(LauncherUpdateManifest release)
        {
            ValidateManifest(release);
            return CompareVersions(release.Version, LauncherVersion.Current) > 0;
        }

        public async Task<bool> DownloadAndLaunchAsync(
            LauncherUpdateManifest release,
            IProgress<LauncherUpdateProgress> progress,
            CancellationToken cancellationToken)
        {
            if (!IsUpdateAvailable(release)) return false;
            var uri = RequireApprovedDownloadUri(release);
            var updateDirectory = Path.Combine(Path.GetTempPath(), "kinojo-launcher-update-" + Guid.NewGuid().ToString("N"));
            var installerPath = Path.Combine(updateDirectory, release.FileName);
            Directory.CreateDirectory(updateDirectory);
            try
            {
                progress?.Report(new LauncherUpdateProgress { Percentage = 8, Stage = "Launcher 업데이트 다운로드 준비" });
                using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var finalUri = response.RequestMessage == null ? uri : response.RequestMessage.RequestUri;
                    if (!IsApprovedFinalHost(finalUri))
                        throw new InvalidOperationException("Launcher 업데이트가 허용되지 않은 주소로 이동했습니다.");
                    var announced = response.Content.Headers.ContentLength;
                    if (announced.HasValue && announced.Value != release.FileSize)
                        throw new InvalidOperationException("Launcher 설치 파일 응답 크기가 Server manifest와 다릅니다.");

                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                    {
                        var buffer = new byte[128 * 1024];
                        long total = 0;
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                            if (read <= 0) break;
                            total += read;
                            if (total > release.FileSize || total > MaximumInstallerBytes)
                                throw new InvalidOperationException("Launcher 설치 파일이 허용 크기를 초과했습니다.");
                            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            progress?.Report(new LauncherUpdateProgress
                            {
                                Percentage = 8 + (int)Math.Min(80, total * 80L / Math.Max(1, release.FileSize)),
                                Stage = "Launcher 업데이트 다운로드 중"
                            });
                        }
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        if (total != release.FileSize)
                            throw new InvalidOperationException("Launcher 설치 파일 다운로드 크기가 일치하지 않습니다.");
                    }
                }

                progress?.Report(new LauncherUpdateProgress { Percentage = 92, Stage = "Launcher 파일 무결성 확인 중" });
                if (!String.Equals(Sha256(installerPath), release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Launcher 설치 파일 SHA-256 검증에 실패했습니다.");

                progress?.Report(new LauncherUpdateProgress { Percentage = 100, Stage = "Launcher 업데이트 적용 중" });
                var process = Process.Start(new ProcessStartInfo(installerPath, "/silent")
                {
                    WorkingDirectory = updateDirectory,
                    UseShellExecute = true
                });
                if (process == null) throw new InvalidOperationException("Launcher 업데이트 설치기를 시작하지 못했습니다.");
                process.Dispose();
                return true;
            }
            catch
            {
                try { if (Directory.Exists(updateDirectory)) Directory.Delete(updateDirectory, true); }
                catch { }
                throw;
            }
        }

        internal static void ValidateManifestForTest(LauncherUpdateManifest release)
        {
            ValidateManifest(release);
        }

        internal static int CompareVersionsForTest(string left, string right)
        {
            return CompareVersions(left, right);
        }

        private static void ValidateManifest(LauncherUpdateManifest release)
        {
            if (release == null || release.SchemaVersion != 1)
                throw new InvalidOperationException("지원하지 않는 Launcher release manifest입니다.");
            if (!String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher release 채널이 다릅니다.");
            if (!IsSemanticVersion(release.Version) || !IsSemanticVersion(release.MinimumVersion) ||
                !String.Equals(release.FileVersion, release.Version + ".0", StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher 버전 계약이 올바르지 않습니다.");
            if (release.FileSize <= 0 || release.FileSize > MaximumInstallerBytes)
                throw new InvalidOperationException("Launcher 설치 파일 크기가 허용 범위를 벗어났습니다.");
            if (!Regex.IsMatch(release.Sha256 ?? "", "^[0-9a-f]{64}$"))
                throw new InvalidOperationException("Launcher SHA-256 형식이 올바르지 않습니다.");
            var expectedFileName = LauncherVersion.IsStaging
                ? "KINOJO_Meter_Launcher_Staging_" + release.Version + ".exe"
                : "KINOJO_Meter_Launcher_" + release.Version + ".exe";
            if (!String.Equals(release.FileName, expectedFileName, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher 설치 파일명과 버전이 일치하지 않습니다.");
            if (release.CodeSignatureRequired || !String.IsNullOrWhiteSpace(release.PublisherSubject) ||
                !String.Equals(release.TrustMode, "WINDOWS_UNSIGNED_HOBBY", StringComparison.Ordinal) ||
                !release.SmartScreenWarningExpected)
                throw new InvalidOperationException("무료 개인 배포 Launcher 신뢰 계약이 올바르지 않습니다.");
            RequireApprovedDownloadUri(release);
        }

        private static Uri RequireApprovedDownloadUri(LauncherUpdateManifest release)
        {
            Uri uri;
            if (!Uri.TryCreate(release == null ? "" : release.DownloadUrl, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("허용되지 않은 Launcher 다운로드 주소입니다.");
            var tag = LauncherVersion.IsStaging ? "launcher-staging-v" : "launcher-v";
            var expectedPath = "/losnah513/kinojo-meter/releases/download/" + tag + release.Version + "/" + release.FileName;
            if (!String.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher 다운로드 주소와 release 버전이 일치하지 않습니다.");
            return uri;
        }

        private static bool IsApprovedFinalHost(Uri uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps) return false;
            return String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareVersions(string left, string right)
        {
            if (!IsSemanticVersion(left) || !IsSemanticVersion(right))
                throw new InvalidOperationException("Launcher 버전 형식이 올바르지 않습니다.");
            return Version.Parse(left).CompareTo(Version.Parse(right));
        }

        private static bool IsSemanticVersion(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^\d+\.\d+\.\d+$");
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
