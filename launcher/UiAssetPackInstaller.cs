using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    // Stage 3-2 owns local install/version/SHA/signature/rollback. Remote acquisition
    // and bundle-wide atomic activation are intentionally deferred to Stages 5 and 6.
    internal sealed partial class UiAssetPackInstaller
    {
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private const long MaximumExtractedBytes = 128L * 1024L * 1024L;
        private const int MaximumArchiveEntries = 512;
        private static readonly Regex VersionPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public ActiveUiAssetState ReadVerifiedActiveState()
        {
            try
            {
                if (!File.Exists(LauncherPaths.ActiveUiAssetFile)) return null;
                var state = _json.Deserialize<ActiveUiAssetState>(File.ReadAllText(LauncherPaths.ActiveUiAssetFile, Encoding.UTF8));
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
            LauncherPaths.EnsureDirectories();
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
                return new UiAssetInstallResult { Active = current, Previous = current, Changed = false };
            }

            var transactionRoot = Path.Combine(LauncherPaths.UiAssetStaging, Guid.NewGuid().ToString("N"));
            var extracted = Path.Combine(transactionRoot, "extracted");
            Directory.CreateDirectory(transactionRoot);
            try
            {
                var manifest = ExtractAndVerify(package, extracted, release);
                var target = LauncherPaths.UiAssetVersionDirectory(release.Version);
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
                return new UiAssetInstallResult { Active = active, Previous = current, Changed = true };
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
            try { if (File.Exists(LauncherPaths.ActiveUiAssetFile)) File.Delete(LauncherPaths.ActiveUiAssetFile); }
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
    }
}
