using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KinojoMeterLauncher
{
    internal sealed class ServerBundleManifest
    {
        public int SchemaVersion { get; set; }
        public string ManifestType { get; set; }
        public string Channel { get; set; }
        public string ProductVersion { get; set; }
        public string BundleRevision { get; set; }
        public string ParentBundleRevision { get; set; }
        public string MinimumLauncherVersion { get; set; }
        public string ActivationMode { get; set; }
        public ServerBundleLockReference BundleLock { get; set; }
        public string IssuedAt { get; set; }
        public string ExpiresAt { get; set; }
        public string ReleaseNote { get; set; }
        public ServerBundleManifestIntegrity Integrity { get; set; }
    }

    internal sealed class ServerBundleLockReference
    {
        public int SchemaVersion { get; set; }
        public string Revision { get; set; }
        public string Sha256 { get; set; }
        public string Url { get; set; }
        public bool Immutable { get; set; }
        public string OriginChannel { get; set; }
    }

    internal sealed class ServerBundleManifestIntegrity
    {
        public string Mode { get; set; }
        public string SigningKeyId { get; set; }
        public string ManifestSignature { get; set; }
    }

    internal static class ServerBundleManifestVerifier
    {
        public const string ManifestType = "KINOJO_METER_SERVER_BUNDLE";
        public const string ActivationMode = "ATOMIC_BUNDLE";
        public const string IntegrityMode = "RSA_SHA256";
        public const string SigningDomain = "KINOJO_METER_SERVER_BUNDLE_MANIFEST_V1";

        private const string ExpectedHost = "josvoltpktvwysrasffq.supabase.co";
        private static readonly Regex SemVerPattern = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);
        private static readonly Regex BundlePattern = new Regex("^B[0-9]{6}$", RegexOptions.CultureInvariant);
        private static readonly Regex ShaPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

        public static void Verify(ServerBundleManifest manifest)
        {
            Verify(
                manifest,
                new RSAParameters
                {
                    Modulus = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicModulusBase64),
                    Exponent = Convert.FromBase64String(LauncherBuildProfile.CoreSigningPublicExponentBase64)
                },
                LauncherBuildProfile.CoreSigningKeyId,
                LauncherVersion.Channel,
                DateTimeOffset.UtcNow,
                LauncherVersion.Current);
        }

        internal static void VerifyForTest(
            ServerBundleManifest manifest,
            RSAParameters publicKey,
            string expectedKeyId,
            string expectedChannel,
            DateTimeOffset now,
            string currentLauncherVersion)
        {
            Verify(manifest, publicKey, expectedKeyId, expectedChannel, now, currentLauncherVersion);
        }

        internal static string CanonicalizeForTest(ServerBundleManifest manifest)
        {
            return Canonicalize(manifest);
        }

        private static void Verify(
            ServerBundleManifest manifest,
            RSAParameters publicKey,
            string expectedKeyId,
            string expectedChannel,
            DateTimeOffset now,
            string currentLauncherVersion)
        {
            DateTimeOffset issuedAt;
            DateTimeOffset expiresAt;
            Uri lockUri;
            if (manifest == null || manifest.BundleLock == null || manifest.Integrity == null ||
                manifest.SchemaVersion != 1 || !String.Equals(manifest.ManifestType, ManifestType, StringComparison.Ordinal) ||
                !String.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal) ||
                !SemVerPattern.IsMatch(manifest.ProductVersion ?? "") ||
                !BundlePattern.IsMatch(manifest.BundleRevision ?? "") ||
                !BundlePattern.IsMatch(manifest.ParentBundleRevision ?? "") ||
                !SemVerPattern.IsMatch(manifest.MinimumLauncherVersion ?? "") ||
                !String.Equals(manifest.ActivationMode, ActivationMode, StringComparison.Ordinal) ||
                manifest.BundleLock.SchemaVersion != 1 || !manifest.BundleLock.Immutable ||
                !String.Equals(manifest.BundleLock.Revision, manifest.BundleRevision, StringComparison.Ordinal) ||
                !ShaPattern.IsMatch(manifest.BundleLock.Sha256 ?? "") ||
                !String.Equals(manifest.BundleLock.OriginChannel, "staging", StringComparison.Ordinal) ||
                !String.Equals(manifest.Integrity.Mode, IntegrityMode, StringComparison.Ordinal) ||
                !String.Equals(manifest.Integrity.SigningKeyId, expectedKeyId, StringComparison.Ordinal) ||
                !TryUtc(manifest.IssuedAt, out issuedAt) || !TryUtc(manifest.ExpiresAt, out expiresAt) ||
                issuedAt > now.AddMinutes(5) || expiresAt <= now || expiresAt <= issuedAt ||
                expiresAt > issuedAt.AddDays(3660) ||
                !Uri.TryCreate(manifest.BundleLock.Url, UriKind.Absolute, out lockUri) ||
                lockUri.Scheme != Uri.UriSchemeHttps ||
                !String.Equals(lockUri.Host, ExpectedHost, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(lockUri.AbsolutePath,
                    "/storage/v1/object/meter-core-private/bundles/" + manifest.BundleRevision + "/bundle.lock.json",
                    StringComparison.Ordinal) || !String.IsNullOrEmpty(lockUri.Query))
                throw new InvalidOperationException("Server Bundle Manifest 계약이 올바르지 않습니다.");

            if (CompareVersions(currentLauncherVersion, manifest.MinimumLauncherVersion) < 0)
                throw new InvalidOperationException("LAUNCHER_UPDATE_REQUIRED: 이 Bundle에는 더 최신 Staging Launcher가 필요합니다.");

            byte[] signature;
            try { signature = Convert.FromBase64String(manifest.Integrity.ManifestSignature ?? ""); }
            catch (FormatException) { throw new InvalidOperationException("Server Bundle Manifest 서명 형식이 올바르지 않습니다."); }
            if (signature.Length != 384)
                throw new InvalidOperationException("Server Bundle Manifest는 RSA-3072 서명을 사용해야 합니다.");

            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.ImportParameters(publicKey);
                var canonical = Encoding.UTF8.GetBytes(Canonicalize(manifest));
                if (!rsa.VerifyData(canonical, CryptoConfig.MapNameToOID("SHA256"), signature))
                    throw new InvalidOperationException("Server Bundle Manifest RSA 서명 검증에 실패했습니다.");
            }
        }

        private static string Canonicalize(ServerBundleManifest value)
        {
            if (value == null || value.BundleLock == null || value.Integrity == null)
                throw new InvalidOperationException("Server Bundle Manifest canonicalization 대상이 비어 있습니다.");
            return String.Join("\n", new[]
            {
                SigningDomain,
                "schemaVersion=" + value.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "manifestType=" + (value.ManifestType ?? ""),
                "channel=" + (value.Channel ?? ""),
                "productVersion=" + (value.ProductVersion ?? ""),
                "bundleRevision=" + (value.BundleRevision ?? ""),
                "parentBundleRevision=" + (value.ParentBundleRevision ?? ""),
                "minimumLauncherVersion=" + (value.MinimumLauncherVersion ?? ""),
                "activationMode=" + (value.ActivationMode ?? ""),
                "bundleLock.schemaVersion=" + value.BundleLock.SchemaVersion.ToString(CultureInfo.InvariantCulture),
                "bundleLock.revision=" + (value.BundleLock.Revision ?? ""),
                "bundleLock.sha256=" + (value.BundleLock.Sha256 ?? ""),
                "bundleLock.url=" + (value.BundleLock.Url ?? ""),
                "bundleLock.immutable=" + value.BundleLock.Immutable.ToString().ToLowerInvariant(),
                "bundleLock.originChannel=" + (value.BundleLock.OriginChannel ?? ""),
                "issuedAt=" + (value.IssuedAt ?? ""),
                "expiresAt=" + (value.ExpiresAt ?? ""),
                "releaseNote=" + (value.ReleaseNote ?? ""),
                "integrity.mode=" + (value.Integrity.Mode ?? ""),
                "integrity.signingKeyId=" + (value.Integrity.SigningKeyId ?? "")
            });
        }

        private static bool TryUtc(string text, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            return !String.IsNullOrWhiteSpace(text) && text.EndsWith("Z", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value) &&
                value.Offset == TimeSpan.Zero;
        }

        private static int CompareVersions(string left, string right)
        {
            var a = (left ?? "").Split('.');
            var b = (right ?? "").Split('.');
            if (a.Length != 3 || b.Length != 3) return -1;
            for (var index = 0; index < 3; index++)
            {
                int av;
                int bv;
                if (!Int32.TryParse(a[index], NumberStyles.None, CultureInfo.InvariantCulture, out av) ||
                    !Int32.TryParse(b[index], NumberStyles.None, CultureInfo.InvariantCulture, out bv)) return -1;
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }
    }
}
