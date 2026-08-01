using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KinojoMeterPrototype
{
    internal sealed class UpdateProgressInfo
    {
        public int Percentage { get; set; }
        public string Stage { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class DesktopUpdateService
    {
        private const long MaximumInstallerBytes = 536870912L;
        private const int MaximumRedirects = 5;
        private static readonly Regex SemverPattern = new Regex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex FileVersionPattern = new Regex(@"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256Pattern = new Regex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
        private static readonly Regex GitHubPartPattern = new Regex(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> AllowedRedirectHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "github.com",
            "release-assets.githubusercontent.com",
            "objects.githubusercontent.com",
            "github-releases.githubusercontent.com"
        };

        private readonly HttpClient _http;

        public DesktopUpdateService()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KINOJO-Meter/" + KinojoApiClient.ClientVersion);
        }

        public bool IsUpdateAvailable(MeterUpdateManifest manifest, out bool mandatory)
        {
            mandatory = false;
            string error;
            if (!TryValidateManifest(manifest, out error))
            {
                if (manifest != null) DiagnosticLog.Info("UPDATE", "Rejected update manifest: " + error);
                return false;
            }

            Version current;
            Version latest;
            if (!TryParseReleaseVersion(KinojoApiClient.ClientVersion, out current) || !TryParseReleaseVersion(manifest.Version, out latest))
            {
                DiagnosticLog.Info("UPDATE", "Invalid version comparison: current=" + KinojoApiClient.ClientVersion + ", latest=" + manifest.Version);
                return false;
            }
            if (latest <= current) return false;

            mandatory = manifest.Mandatory;
            Version minimum;
            if (TryParseReleaseVersion(manifest.MinimumVersion, out minimum) && current < minimum) mandatory = true;
            return true;
        }

        public bool TryValidateManifest(MeterUpdateManifest manifest, out string error)
        {
            error = "";
            if (manifest == null) { error = "manifest is missing"; return false; }
            if (!SemverPattern.IsMatch(manifest.Version ?? "")) { error = "version must use major.minor.patch"; return false; }
            if (!FileVersionPattern.IsMatch(manifest.FileVersion ?? "") || !(manifest.FileVersion ?? "").StartsWith(manifest.Version + ".", StringComparison.Ordinal))
            { error = "fileVersion does not match version"; return false; }
            if (!SemverPattern.IsMatch(manifest.MinimumVersion ?? "")) { error = "minimumVersion must use major.minor.patch"; return false; }

            Version releaseVersion;
            Version minimumVersion;
            if (!TryParseReleaseVersion(manifest.Version, out releaseVersion) || !TryParseReleaseVersion(manifest.MinimumVersion, out minimumVersion) || minimumVersion > releaseVersion)
            { error = "minimumVersion is greater than version"; return false; }

            var expectedFileName = "KINOJO_Meter_" + manifest.Version + "_Setup.exe";
            if (!String.Equals(manifest.FileName, expectedFileName, StringComparison.Ordinal))
            { error = "installer file name does not match version"; return false; }
            if (!Sha256Pattern.IsMatch(NormalizeSha256(manifest.Sha256)))
            { error = "sha256 must contain 64 hexadecimal characters"; return false; }
            if (manifest.FileSize <= 0 || manifest.FileSize > MaximumInstallerBytes)
            { error = "fileSize is outside the allowed range"; return false; }
            if (!String.IsNullOrWhiteSpace(manifest.Channel) && !String.Equals(manifest.Channel, KinojoVersion.Channel, StringComparison.OrdinalIgnoreCase))
            { error = "release channel does not match the installed channel"; return false; }

            Uri downloadUri;
            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out downloadUri) || !IsValidGitHubReleaseUri(downloadUri, manifest.Version, manifest.FileName))
            { error = "downloadUrl is not an allowed fixed GitHub Release URL"; return false; }
            return true;
        }

        public async Task<bool> DownloadAndLaunchAsync(MeterUpdateManifest manifest, IProgress<UpdateProgressInfo> progress)
        {
            bool mandatory;
            if (!IsUpdateAvailable(manifest, out mandatory)) return false;

            string contractError;
            if (!TryValidateManifest(manifest, out contractError))
                throw new InvalidOperationException("업데이트 배포 정보가 올바르지 않습니다. " + contractError);

            var tempDirectory = Path.Combine(Path.GetTempPath(), "KINOJO-Meter-Update", manifest.Version);
            Directory.CreateDirectory(tempDirectory);
            var installerPath = Path.Combine(tempDirectory, manifest.FileName);
            if (File.Exists(installerPath)) File.Delete(installerPath);
            Report(progress, 2, "업데이트 연결", "GitHub Release 확인 중");

            using (var response = await GetAllowedResponseAsync(new Uri(manifest.DownloadUrl)))
            {
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("업데이트 서버가 HTTP " + (int)response.StatusCode + " 응답을 반환했습니다.");

                ValidateResponseHeaders(response, manifest);
                var total = response.Content.Headers.ContentLength;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        received += read;
                        if (received > manifest.FileSize || received > MaximumInstallerBytes)
                            throw new InvalidOperationException("업데이트 파일이 서버에 등록된 크기를 초과했습니다.");
                        await output.WriteAsync(buffer, 0, read);
                        var percent = 5 + (int)Math.Min(80, received * 80L / manifest.FileSize);
                        Report(progress, percent, "업데이트 다운로드 중", FormatBytes(received) + " / " + FormatBytes(manifest.FileSize));
                    }
                    await output.FlushAsync();
                    if (received != manifest.FileSize)
                        throw new InvalidOperationException("업데이트 파일 크기가 서버 등록값과 일치하지 않습니다.");
                }
            }

            Report(progress, 88, "무결성 확인", "파일명·크기·SHA-256 확인 중");
            if (!VerifySha256(installerPath, manifest.Sha256))
                throw new InvalidOperationException("업데이트 파일 SHA-256 검증에 실패했습니다.");

            Report(progress, 93, "버전 확인", "설치기 내부 버전 확인 중");
            VerifyInstallerVersion(installerPath, manifest);

            Report(progress, 96, "설치 준비", "현재 실행 상태 정리 중");
            var currentDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var arguments = "/silent /update /launch /waitpid " + Process.GetCurrentProcess().Id + " /path \"" + currentDirectory + "\"";
            Process.Start(new ProcessStartInfo(installerPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = tempDirectory
            });
            DiagnosticLog.Info("UPDATE", "Verified updater launched: version=" + manifest.Version + ", file=" + manifest.FileName + ", bytes=" + manifest.FileSize);
            Report(progress, 100, "설치 시작", "KINOJO Meter 재실행 준비 완료");
            return true;
        }

        private async Task<HttpResponseMessage> GetAllowedResponseAsync(Uri initialUri)
        {
            var current = initialUri;
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                if (!IsAllowedTransportUri(current)) throw new InvalidOperationException("허용되지 않은 업데이트 다운로드 경로입니다.");
                var response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead);
                var status = (int)response.StatusCode;
                if (status < 300 || status >= 400) return response;

                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidOperationException("업데이트 리디렉션 주소가 없습니다.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
            throw new InvalidOperationException("업데이트 다운로드 리디렉션 횟수를 초과했습니다.");
        }

        private static void ValidateResponseHeaders(HttpResponseMessage response, MeterUpdateManifest manifest)
        {
            var length = response.Content.Headers.ContentLength;
            if (length.HasValue && length.Value != manifest.FileSize)
                throw new InvalidOperationException("업데이트 응답 크기가 서버 등록값과 일치하지 않습니다.");

            var disposition = response.Content.Headers.ContentDisposition;
            var responseName = disposition == null ? "" : (disposition.FileNameStar ?? disposition.FileName ?? "");
            responseName = responseName.Trim().Trim('"');
            if (!String.IsNullOrWhiteSpace(responseName) && !String.Equals(Path.GetFileName(responseName), manifest.FileName, StringComparison.Ordinal))
                throw new InvalidOperationException("업데이트 응답 파일명이 서버 등록값과 일치하지 않습니다.");
        }

        private static void VerifyInstallerVersion(string path, MeterUpdateManifest manifest)
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var actualFileVersion = (info.FileVersion ?? "").Trim();
            if (!String.Equals(actualFileVersion, manifest.FileVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("다운로드한 설치기의 파일 버전이 서버 등록값과 일치하지 않습니다.");
            if (!String.IsNullOrWhiteSpace(info.ProductName) && !String.Equals(info.ProductName.Trim(), "KINOJO Meter", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("다운로드한 파일은 KINOJO Meter 설치기가 아닙니다.");
        }

        private static bool IsValidGitHubReleaseUri(Uri uri, string version, string fileName)
        {
            if (!IsAllowedTransportUri(uri) || !String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)) return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 6) return false;
            if (!GitHubPartPattern.IsMatch(parts[0]) || !GitHubPartPattern.IsMatch(parts[1])) return false;
            if (!String.Equals(parts[2], "releases", StringComparison.OrdinalIgnoreCase) || !String.Equals(parts[3], "download", StringComparison.OrdinalIgnoreCase)) return false;
            if (!String.Equals(Uri.UnescapeDataString(parts[4]), "v" + version, StringComparison.OrdinalIgnoreCase)) return false;
            return String.Equals(Uri.UnescapeDataString(parts[5]), fileName, StringComparison.Ordinal);
        }

        private static bool IsAllowedTransportUri(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && String.IsNullOrEmpty(uri.UserInfo) && (uri.IsDefaultPort || uri.Port == 443) && AllowedRedirectHosts.Contains(uri.Host);
        }

        private static bool TryParseReleaseVersion(string value, out Version parsed)
        {
            parsed = null;
            if (!SemverPattern.IsMatch(value ?? "")) return false;
            return Version.TryParse(value, out parsed);
        }

        private static void Report(IProgress<UpdateProgressInfo> progress, int percentage, string stage, string detail)
        {
            if (progress == null) return;
            progress.Report(new UpdateProgressInfo { Percentage = percentage, Stage = stage, Detail = detail });
        }

        private static string FormatBytes(long value)
        {
            if (value >= 1024L * 1024L) return (value / 1024d / 1024d).ToString("0.0") + " MB";
            if (value >= 1024L) return (value / 1024d).ToString("0.0") + " KB";
            return value.ToString("N0") + " B";
        }

        private static string NormalizeSha256(string value)
        {
            return (value ?? "").Replace("-", "").Trim();
        }

        private static bool VerifySha256(string path, string expected)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                return String.Equals(actual, NormalizeSha256(expected), StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
