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
    internal sealed partial class UiAssetPackInstaller
    {
        private static string NormalizeRelativePath(string value)
        {
            return (value ?? "").Trim().Replace('\\', '/').TrimStart('/');
        }

        private void ValidateRelease(UiAssetReleaseManifest release)
        {
            if (release == null || release.SchemaVersion != 1 || !String.Equals(release.Channel, LauncherBuildProfile.Channel, StringComparison.Ordinal) ||
                !String.Equals(release.PackId, UiAssetReleaseIntegrityVerifier.PackId, StringComparison.Ordinal) || !VersionPattern.IsMatch(release.Version ?? "") ||
                !VersionPattern.IsMatch(release.MinimumLauncherVersion ?? "") || CompareVersions(CurrentLauncherVersion(), release.MinimumLauncherVersion) < 0 ||
                String.IsNullOrWhiteSpace(release.PackageId) || String.IsNullOrWhiteSpace(release.FileName) || Path.GetFileName(release.FileName) != release.FileName ||
                !Regex.IsMatch(release.FileName, @"^KinojoUiAssets_\d{1,4}\.\d{1,4}\.\d{1,4}\.zip$", RegexOptions.CultureInvariant) ||
                release.FileSize <= 0 || release.FileSize > MaximumPackageBytes || !IsSha256(release.Sha256) || !IsSha256(release.InstallManifestSha256) || !IsSha256(release.ThemeSha256))
                throw new InvalidOperationException("UI Asset release manifest 계약이 올바르지 않습니다.");
            UiAssetReleaseIntegrityVerifier.VerifyForTest(release, _publicKey, _expectedKeyId);
        }

        private static bool IsActiveStateUsable(ActiveUiAssetState state)
        {
            return state != null && state.SchemaVersion == 1 && String.Equals(state.Channel, LauncherBuildProfile.Channel, StringComparison.Ordinal) &&
                String.Equals(state.PackId, UiAssetReleaseIntegrityVerifier.PackId, StringComparison.Ordinal) && VersionPattern.IsMatch(state.Version ?? "") &&
                !String.IsNullOrWhiteSpace(state.ThemeId) && !String.IsNullOrWhiteSpace(state.InstalledPath) && IsSha256(state.PackageSha256) &&
                IsSha256(state.InstallManifestSha256) && IsSha256(state.ThemeSha256) && !String.IsNullOrWhiteSpace(state.ManifestSignature);
        }

        private static UiAssetReleaseManifest ReleaseFromState(ActiveUiAssetState state)
        {
            return new UiAssetReleaseManifest
            {
                SchemaVersion = 1,
                Channel = state.Channel,
                PackId = state.PackId,
                Version = state.Version,
                MinimumLauncherVersion = state.MinimumLauncherVersion,
                PackageId = state.PackageId,
                FileName = state.FileName,
                FileSize = state.FileSize,
                Sha256 = state.PackageSha256,
                InstallManifestSha256 = state.InstallManifestSha256,
                ThemeSha256 = state.ThemeSha256,
                IntegrityMode = state.IntegrityMode,
                SigningKeyId = state.SigningKeyId,
                ManifestSignature = state.ManifestSignature
            };
        }

        private void WriteActiveState(ActiveUiAssetState state)
        {
            EnsureDirectories();
            var json = _json.Serialize(state);
            var temporary = _activeFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(_activeFile)) File.Replace(temporary, _activeFile, null);
            else File.Move(temporary, _activeFile);
        }

        private static string CurrentLauncherVersion()
        {
            var value = typeof(UiAssetPackInstaller).Assembly.GetName().Version;
            return value == null ? "0.0.0" : value.Major + "." + value.Minor + "." + Math.Max(0, value.Build);
        }

        private static int CompareVersions(string left, string right)
        {
            var a = (left ?? "0.0.0").Split('.');
            var b = (right ?? "0.0.0").Split('.');
            for (var index = 0; index < 3; index++)
            {
                int av, bv;
                if (!Int32.TryParse(index < a.Length ? a[index] : "0", out av)) av = 0;
                if (!Int32.TryParse(index < b.Length ? b[index] : "0", out bv)) bv = 0;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }

        private static bool IsSha256(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create()) return String.Concat(hash.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static string Text(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null ? Convert.ToString(value) : "";
        }

        private static int Number(Dictionary<string, object> source, string key)
        {
            int value;
            return Int32.TryParse(Text(source, key), out value) ? value : 0;
        }
    }
}
