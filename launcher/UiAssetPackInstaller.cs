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
    // Stage 6-2 keeps the Stage 3-2 local install/rollback contract and adds only
    // Server-authorized private acquisition for the independently active UI Asset slot.
    internal sealed partial class UiAssetPackInstaller : IDisposable
    {
        public const string VersionShaConflictCode = "UI_ASSET_VERSION_SHA_CONFLICT";
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private const long MaximumExtractedBytes = 128L * 1024L * 1024L;
        private const int MaximumArchiveEntries = 512;
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly HttpClient _http;
        private readonly string _root;
        private readonly string _versions;
        private readonly string _staging;
        private readonly string _activeFile;
        private readonly RSAParameters _publicKey;
        private readonly string _expectedKeyId;

        public UiAssetPackInstaller()
            : this(new HttpClient { Timeout = TimeSpan.FromMinutes(3) }, LauncherPaths.UiAssetRoot,
                new RSAParameters
                {
                    Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                    Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
                }, LauncherBuildProfile.CoreSigningKeyId)
        {
        }

        internal UiAssetPackInstaller(HttpMessageHandler handler, string root, RSAParameters publicKey, string expectedKeyId)
            : this(new HttpClient(handler, true) { Timeout = TimeSpan.FromMinutes(3) }, root, publicKey, expectedKeyId)
        {
        }

        private UiAssetPackInstaller(HttpClient http, string root, RSAParameters publicKey, string expectedKeyId)
        {
            if (http == null) throw new ArgumentNullException("http");
            if (String.IsNullOrWhiteSpace(root)) throw new ArgumentException("root");
            _http = http;
            _root = Path.GetFullPath(root);
            _versions = Path.Combine(_root, "versions");
            _staging = Path.Combine(_root, "staging");
            _activeFile = Path.Combine(_root, "active.json");
            _publicKey = publicKey;
            _expectedKeyId = expectedKeyId ?? "";
        }

        public ActiveUiAssetState ReadVerifiedActiveState()
        {
            try
            {
                if (!File.Exists(_activeFile)) return null;
                var state = _json.Deserialize<ActiveUiAssetState>(File.ReadAllText(_activeFile, Encoding.UTF8));
                if (!IsActiveStateUsable(state)) return null;
                VerifyInstalledFiles(state, ReleaseFromState(state));
                return state;
            }
            catch { return null; }
        }

        public UiAssetInstallResult InstallPackage(UiAssetReleaseManifest release, string packagePath)
        {
            ValidateRelease(release);
            if (String.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                throw new InvalidOperationException("UI Asset Pack 파일이 없습니다.");
            EnsureDirectories();
            var package = Path.GetFullPath(packagePath);
            var packageInfo = new FileInfo(package);
            if (packageInfo.Length != release.FileSize || packageInfo.Length <= 0 || packageInfo.Length > MaximumPackageBytes)
                throw new InvalidOperationException("UI Asset Pack 파일 크기가 release manifest와 다릅니다.");
            if (!String.Equals(Sha256(package), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI Asset Pack SHA-256 검증에 실패했습니다.");

            var current = ReadVerifiedActiveState();
            if (current != null && String.Equals(current.Version, release.Version, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(current.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal))
            {
                return new UiAssetInstallResult { Active = current, Previous = current, Changed = false, Downloaded = false };
            }

            var transactionRoot = Path.Combine(_staging, Guid.NewGuid().ToString("N"));
            var extracted = Path.Combine(transactionRoot, "extracted");
            Directory.CreateDirectory(transactionRoot);
            try
            {
                var manifest = ExtractAndVerify(package, extracted, release);
                var target = VersionDirectory(release.Version);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(extracted, target);
                var active = new ActiveUiAssetState
                {
                    SchemaVersion = 1,
                    Channel = release.Channel,
                    PackId = release.PackId,
                    Version = release.Version,
                    MinimumLauncherVersion = release.MinimumLauncherVersion,
                    PackageId = release.PackageId,
                    FileName = release.FileName,
                    FileSize = release.FileSize,
                    ThemeId = manifest.ThemeId,
                    InstalledPath = target,
                    ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
                    PackageSha256 = release.Sha256,
                    InstallManifestSha256 = release.InstallManifestSha256,
                    ThemeSha256 = release.ThemeSha256,
                    IntegrityMode = release.IntegrityMode,
                    SigningKeyId = release.SigningKeyId,
                    ManifestSignature = release.ManifestSignature
                };
                WriteActiveState(active);
                return new UiAssetInstallResult { Active = active, Previous = current, Changed = true, Downloaded = false };
            }
            finally
            {
                try { if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true); }
                catch { }
            }
        }

        public void Rollback(UiAssetInstallResult install)
        {
            if (install == null || !install.Changed) return;
            if (install.Previous != null)
            {
                try
                {
                    VerifyInstalledFiles(install.Previous, ReleaseFromState(install.Previous));
                    WriteActiveState(install.Previous);
                    install.Active = install.Previous;
                    install.Changed = false;
                    return;
                }
                catch { }
            }
            try { if (File.Exists(_activeFile)) File.Delete(_activeFile); }
            catch { }
            install.Active = null;
            install.Changed = false;
        }

        internal UiAssetInstallManifest ExtractAndVerifyForTest(string packagePath, string destination, UiAssetReleaseManifest release)
        {
            ValidateRelease(release);
            if (!String.Equals(Sha256(packagePath), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI Asset test package SHA-256 mismatch.");
            return ExtractAndVerify(packagePath, destination, release);
        }

        public async Task<UiAssetInstallResult> EnsureInstalledAsync(
            UiAssetReleaseManifest release,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            ValidateRelease(release);
            var uri = RequireApprovedDownloadUri(release, expectedProjectHost);
            EnsureDirectories();
            using (var updateLock = new FileStream(Path.Combine(_root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var current = ReadVerifiedActiveState();
                if (current == null && File.Exists(_activeFile))
                {
                    ActiveUiAssetState raw;
                    try { raw = _json.Deserialize<ActiveUiAssetState>(File.ReadAllText(_activeFile, Encoding.UTF8)); }
                    catch (Exception error) { throw new InvalidOperationException("UI Asset active state를 신뢰할 수 없습니다.", error); }
                    RejectVersionShaConflict(raw, release);
                }
                RejectVersionShaConflict(current, release);
                if (current != null && SameRelease(current, release))
                    return new UiAssetInstallResult { Active = current, Previous = current, Changed = false, Downloaded = false };

                var transactionRoot = Path.Combine(_staging, "download-" + Guid.NewGuid().ToString("N"));
                var packagePath = Path.Combine(transactionRoot, release.FileName);
                Directory.CreateDirectory(transactionRoot);
                try
                {
                    await DownloadAsync(uri, release, packagePath, cancellationToken).ConfigureAwait(false);
                    var result = InstallPackage(release, packagePath);
                    result.Downloaded = true;
                    return result;
                }
                finally
                {
                    try { if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true); }
                    catch { }
                }
            }
        }

        private async Task DownloadAsync(Uri uri, UiAssetReleaseManifest release, string destination, CancellationToken cancellationToken)
        {
            long total = 0;
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage == null ? uri : response.RequestMessage.RequestUri;
                RequireApprovedDownloadUri(release, finalUri == null ? "" : finalUri.Host);
                if (finalUri == null || !String.Equals(finalUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                    finalUri.AbsolutePath != uri.AbsolutePath || !HasSignedToken(finalUri))
                    throw new InvalidOperationException("UI Asset 다운로드 redirect가 승인 경계를 벗어났습니다.");
                var announced = response.Content.Headers.ContentLength;
                if (announced.HasValue && announced.Value != release.FileSize)
                    throw new InvalidOperationException("UI Asset 응답 크기가 release와 일치하지 않습니다.");
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
                            throw new InvalidOperationException("UI Asset 다운로드 크기가 release 경계를 초과했습니다.");
                        await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            if (total != release.FileSize || !String.Equals(Sha256(destination), release.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("UI Asset ZIP SHA-256 검증에 실패했습니다.");
        }

        private static Uri RequireApprovedDownloadUri(UiAssetReleaseManifest release, string expectedProjectHost)
        {
            Uri uri;
            if (release == null || !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath != "/storage/v1/object/sign/meter-core-private/ui-assets/" + release.Channel + "/" + release.Version + "/" + release.FileName ||
                !HasSignedToken(uri) || release.ExpiresAt <= DateTimeOffset.UtcNow || release.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10))
                throw new InvalidOperationException("UI Asset signed URL 계약이 올바르지 않습니다.");
            return uri;
        }

        private static bool HasSignedToken(Uri uri)
        {
            if (uri == null) return false;
            return uri.Query.TrimStart('?').Split('&').Any(value =>
            {
                var parts = value.Split(new[] { '=' }, 2);
                return parts.Length == 2 && String.Equals(Uri.UnescapeDataString(parts[0]), "token", StringComparison.Ordinal) &&
                    !String.IsNullOrWhiteSpace(Uri.UnescapeDataString(parts[1]));
            });
        }

        private static void RejectVersionShaConflict(ActiveUiAssetState current, UiAssetReleaseManifest release)
        {
            if (current != null && String.Equals(current.Version, release.Version, StringComparison.Ordinal) &&
                !String.Equals(current.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(VersionShaConflictCode + ": 같은 UI Asset version의 다른 SHA는 활성화할 수 없습니다.");
        }

        private static bool SameRelease(ActiveUiAssetState current, UiAssetReleaseManifest release)
        {
            return String.Equals(current.Channel, release.Channel, StringComparison.Ordinal) &&
                String.Equals(current.Version, release.Version, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(current.InstallManifestSha256, release.InstallManifestSha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(current.ThemeSha256, release.ThemeSha256, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(current.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal);
        }

        private void EnsureDirectories()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_versions);
            Directory.CreateDirectory(_staging);
        }

        private string VersionDirectory(string version)
        {
            if (!VersionPattern.IsMatch(version ?? "")) throw new InvalidOperationException("UI Asset Pack version 형식이 올바르지 않습니다.");
            return Path.Combine(_versions, version);
        }

        public void Dispose() { _http.Dispose(); }
    }

    internal static class UiAssetPackUpdateCoordinator
    {
        public static Dictionary<string, object> CurrentStatePayload(UiAssetPackInstaller installer)
        {
            if (installer == null) throw new ArgumentNullException("installer");
            var state = installer.ReadVerifiedActiveState();
            return state == null ? null : new Dictionary<string, object>
            {
                { "packId", state.PackId }, { "version", state.Version }, { "sha256", state.PackageSha256 }
            };
        }

        public static async Task<UiAssetInstallResult> ApplyAsync(
            UiAssetPackInstaller installer,
            UiAssetPackUpdateAuthorization authorization,
            string expectedProjectHost,
            CancellationToken cancellationToken)
        {
            if (installer == null) throw new ArgumentNullException("installer");
            if (authorization == null || !authorization.Authorized)
                throw new InvalidOperationException(authorization == null || String.IsNullOrWhiteSpace(authorization.Message)
                    ? "UI Asset Pack 업데이트 승인을 받지 못했습니다." : authorization.Message);
            if (authorization.Release == null) return null;
            return await installer.EnsureInstalledAsync(authorization.Release, expectedProjectHost, cancellationToken).ConfigureAwait(false);
        }
    }
}
