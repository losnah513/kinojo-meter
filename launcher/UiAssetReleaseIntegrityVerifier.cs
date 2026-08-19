using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KinojoMeterLauncher
{
    internal static class UiAssetReleaseIntegrityVerifier
    {
        public const string IntegrityMode = "RSA_SHA256_MANIFEST_V1";
        public const string PackId = "ui-assets";
        public const string SigningDomain = "KINOJO_UI_ASSET_RELEASE_V1";

        public static void Verify(UiAssetReleaseManifest release)
        {
            Verify(release, new RSAParameters
            {
                Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
            }, LauncherBuildProfile.CoreSigningKeyId);
        }

        internal static void VerifyForTest(UiAssetReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            Verify(release, publicKey, expectedKeyId);
        }

        internal static string Canonicalize(UiAssetReleaseManifest release)
        {
            if (release == null) throw new InvalidOperationException("UI Asset release manifest가 없습니다.");
            var values = new Dictionary<string, string>
            {
                { "channel", release.Channel },
                { "packId", release.PackId },
                { "version", release.Version },
                { "minimumLauncherVersion", release.MinimumLauncherVersion },
                { "packageId", release.PackageId },
                { "fileName", release.FileName },
                { "sha256", release.Sha256 },
                { "installManifestSha256", release.InstallManifestSha256 },
                { "themeSha256", release.ThemeSha256 }
            };
            foreach (var pair in values)
            {
                if (String.IsNullOrWhiteSpace(pair.Value) || pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("UI Asset 서명 계약 필드가 올바르지 않습니다: " + pair.Key);
            }
            if (!String.Equals(release.PackId, PackId, StringComparison.Ordinal))
                throw new InvalidOperationException("지원하지 않는 UI Asset Pack입니다.");

            return String.Join("\n", new[]
            {
                SigningDomain,
                "schemaVersion=" + release.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "channel=" + release.Channel,
                "packId=" + release.PackId,
                "version=" + release.Version,
                "minimumLauncherVersion=" + release.MinimumLauncherVersion,
                "packageId=" + release.PackageId,
                "fileName=" + release.FileName,
                "fileSize=" + release.FileSize.ToString(CultureInfo.InvariantCulture),
                "sha256=" + release.Sha256.ToLowerInvariant(),
                "installManifestSha256=" + release.InstallManifestSha256.ToLowerInvariant(),
                "themeSha256=" + release.ThemeSha256.ToLowerInvariant()
            });
        }

        private static void Verify(UiAssetReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            if (release == null || release.SchemaVersion != 1 ||
                !String.Equals(release.IntegrityMode, IntegrityMode, StringComparison.Ordinal) ||
                !String.Equals(release.SigningKeyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("지원하지 않는 UI Asset 무결성 서명 계약입니다.");

            byte[] signature;
            try { signature = Convert.FromBase64String(release.ManifestSignature ?? ""); }
            catch (FormatException) { throw new InvalidOperationException("UI Asset manifest 전자서명 형식이 올바르지 않습니다."); }
            if (signature.Length != 384) throw new InvalidOperationException("UI Asset manifest 전자서명 길이가 올바르지 않습니다.");

            var payload = Encoding.UTF8.GetBytes(Canonicalize(release));
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidOperationException("UI Asset release manifest RSA 전자서명 검증에 실패했습니다.");
            }
        }
    }
}
