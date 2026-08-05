using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KinojoMeterLauncher
{
    internal static class CoreReleaseIntegrityVerifier
    {
        public const string IntegrityMode = "RSA_SHA256_MANIFEST_V1";
        public const string SigningKeyId = "kinojo-core-rsa-2026-01";

        private const string PublicModulusBase64 = "ybj1cE8V1GiCTUF83fSfBcf/lKYPNvtlYREmfnfjvP9aJ/791Gu4WKpqVPxwWAl/U99t9BHJJJXcSSMoCP/ay8uxlmNO3efIaS7nwZhmKuYAyUAZNFI181LK9laUnA20zbd7dmlH+YuiGhfW9x0d47ynJNzPR9vp80hBsIKqQEJ+xHEvQWJCapC/EAzRyMBoHeyy1Ff/ej713Z0+6GDNwVdBDh36M3SzHMbvVGGVh1xfQSkGXrQGitubrsJrUZDCZSNQgcJOBnxN3OuEoRxCX/LOzT/VzT28mL7suU+S///yMmwbwLMhUvoVGnVJ1vQ0L6jpUOo5YJ0OW9efMf4zc36LnhMFVT8w9kS3LDWFSPezAkhERlAbnp6FTZ8ZKTM/cgqTeB5FH316RL/xgescWFJYdNJSOZd1nXo0EzgqkGPy76PnDZlP2ObsQbtkVzD5Rxp+iJiBAeXEhG+VxoYw5NGiGPqrHVm/088T6NtKS/4aaDGhtH6Yz1hewTl/mGIV";
        private const string PublicExponentBase64 = "AQAB";

        public static void Verify(CoreReleaseManifest release)
        {
            Verify(release, new RSAParameters
            {
                Modulus = Convert.FromBase64String(PublicModulusBase64),
                Exponent = Convert.FromBase64String(PublicExponentBase64)
            }, SigningKeyId);
        }

        internal static void VerifyForTest(CoreReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            Verify(release, publicKey, expectedKeyId);
        }

        internal static string Canonicalize(CoreReleaseManifest release)
        {
            if (release == null) throw new InvalidOperationException("Core release manifest가 없습니다.");
            var values = new Dictionary<string, string>
            {
                { "channel", release.Channel },
                { "coreVersion", release.CoreVersion },
                { "minimumCoreVersion", release.MinimumCoreVersion },
                { "minimumLauncherVersion", release.MinimumLauncherVersion },
                { "packageId", release.PackageId },
                { "fileName", release.FileName },
                { "sha256", release.Sha256 },
                { "installManifestSha256", release.InstallManifestSha256 },
                { "entryPoint", release.EntryPoint }
            };
            foreach (var pair in values)
            {
                if (String.IsNullOrWhiteSpace(pair.Value) || pair.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("Core 서명 계약 필드가 올바르지 않습니다: " + pair.Key);
            }

            return String.Join("\n", new[]
            {
                "KINOJO_CORE_RELEASE_V1",
                "schemaVersion=" + release.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "channel=" + release.Channel,
                "coreVersion=" + release.CoreVersion,
                "minimumCoreVersion=" + release.MinimumCoreVersion,
                "minimumLauncherVersion=" + release.MinimumLauncherVersion,
                "packageId=" + release.PackageId,
                "fileName=" + release.FileName,
                "fileSize=" + release.FileSize.ToString(CultureInfo.InvariantCulture),
                "sha256=" + release.Sha256.ToLowerInvariant(),
                "installManifestSha256=" + release.InstallManifestSha256.ToLowerInvariant(),
                "entryPoint=" + release.EntryPoint,
                "mandatory=" + (release.Mandatory ? "true" : "false")
            });
        }

        private static void Verify(CoreReleaseManifest release, RSAParameters publicKey, string expectedKeyId)
        {
            if (!String.Equals(release.IntegrityMode, IntegrityMode, StringComparison.Ordinal) ||
                !String.Equals(release.SigningKeyId, expectedKeyId, StringComparison.Ordinal))
                throw new InvalidOperationException("지원하지 않는 Core 무결성 서명 계약입니다.");

            byte[] signature;
            try { signature = Convert.FromBase64String(release.ManifestSignature ?? ""); }
            catch (FormatException) { throw new InvalidOperationException("Core manifest 전자서명 형식이 올바르지 않습니다."); }
            if (signature.Length != 384) throw new InvalidOperationException("Core manifest 전자서명 길이가 올바르지 않습니다.");

            var payload = Encoding.UTF8.GetBytes(Canonicalize(release));
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidOperationException("Core release manifest RSA 전자서명 검증에 실패했습니다.");
            }
        }
    }
}
