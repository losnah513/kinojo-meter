using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal static class LauncherVersion
    {
        public const string Channel = LauncherBuildProfile.Channel;
        public const string Current = "1.0.0";
    }

    internal static class LauncherPackageTests
    {
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "kinojo-launcher-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("channel profile is compile-time bound", VerifyChannelProfile);
                Run("valid package", () => VerifyPackage(root, false, false, false));
                Run("reject unmanaged file", () => ExpectFailure(() => VerifyPackage(root, true, false, false)));
                Run("reject duplicate archive path", () => ExpectFailure(() => VerifyPackage(root, false, true, false)));
                Run("reject tampered install manifest hash", () => ExpectFailure(() => VerifyPackage(root, false, false, true)));
                Run("reject traversal path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("../outside.txt", false)));
                Run("reject Windows ADS path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("KINOJO.Meter.exe:payload", false)));
                Run("reject rooted path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("C:\\Windows\\system32.dll", false)));
                Run("reject reserved device path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("NUL.txt", false)));
                using (var signingKey = new RSACryptoServiceProvider(3072))
                {
                    signingKey.PersistKeyInCsp = false;
                    Run("accept RSA-signed hobby release", () => VerifyReleaseContract(signingKey, null));
                    Run("reject tampered package hash", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.Sha256 = new String('b', 64))));
                    Run("reject tampered install manifest hash", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.InstallManifestSha256 = new String('c', 64))));
                    Run("reject missing manifest signature", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.ManifestSignature = "")));
                    Run("reject wrong signing key id", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.SigningKeyId = "wrong-key")));
                    Run("reject Authenticode-required hobby release", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.CodeSignatureRequired = true)));
                    Run("reject cross-channel signed URL", () => ExpectFailure(() => VerifyReleaseContract(signingKey, value => value.DownloadUrl = value.DownloadUrl.Replace("/" + LauncherVersion.Channel + "/", "/" + (LauncherVersion.Channel == "staging" ? "stable" : "staging") + "/"))));
                }
                Console.WriteLine("Launcher package tests passed: " + _passed);
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

        private static void VerifyChannelProfile()
        {
            var expectedFunction = LauncherVersion.Channel == "staging" ? "meter-staging-ingest" : "meter-ingest";
            var expectedFolder = LauncherVersion.Channel == "staging" ? "KINOJO Meter Staging" : "KINOJO Meter";
            if (!String.Equals(LauncherBuildProfile.FunctionName, expectedFunction, StringComparison.Ordinal) ||
                !String.Equals(LauncherBuildProfile.DataFolderName, expectedFolder, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher channel profile is not compile-time bound.");
        }

        private static void VerifyPackage(string root, bool unmanaged, bool duplicate, bool wrongManifestHash)
        {
            var id = Guid.NewGuid().ToString("N");
            var package = Path.Combine(root, id + ".zip");
            var destination = Path.Combine(root, id);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                { LauncherBuildProfile.CoreEntryPoint, Encoding.UTF8.GetBytes("test-core") },
                { "version.json", Encoding.UTF8.GetBytes("{\"version\":\"0.2.38\"}") }
            };
            var managed = files.Select(pair => new CoreInstallFile
            {
                Path = pair.Key,
                Size = pair.Value.Length,
                Sha256 = Hash(pair.Value)
            }).ToList();
            var installManifest = new CoreInstallManifest
            {
                SchemaVersion = 1,
                CoreVersion = "0.2.38",
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                Files = managed
            };
            var installManifestBytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(installManifest));
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (var pair in files) WriteEntry(archive, pair.Key, pair.Value);
                WriteEntry(archive, "install-manifest.json", installManifestBytes);
                if (unmanaged) WriteEntry(archive, "unmanaged.dll", new byte[] { 1, 2, 3 });
                if (duplicate) WriteEntry(archive, "version.json", Encoding.UTF8.GetBytes("duplicate"));
            }
            var release = new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                CoreVersion = "0.2.38",
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                InstallManifestSha256 = wrongManifestHash ? new String('0', 64) : Hash(installManifestBytes),
                CodeSignatureRequired = false,
                PublisherSubject = ""
            };
            using (var installer = new CorePackageInstaller()) installer.ExtractAndVerifyForTest(package, destination, release);
        }

        private static void VerifyReleaseContract(RSACryptoServiceProvider signingKey, Action<CoreReleaseManifest> mutateAfterSigning)
        {
            const string keyId = "launcher-test-key";
            var release = new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                CoreVersion = "0.2.38",
                MinimumCoreVersion = "0.2.38",
                MinimumLauncherVersion = "1.0.0",
                PackageId = LauncherVersion.Channel + ":0.2.38:" + new String('a', 16),
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                FileSize = 1,
                Sha256 = new String('a', 64),
                InstallManifestSha256 = new String('d', 64),
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/" + LauncherVersion.Channel + "/0.2.38/package?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                EntryPoint = LauncherBuildProfile.CoreEntryPoint,
                Mandatory = true,
                CodeSignatureRequired = false,
                PublisherSubject = "",
                IntegrityMode = CoreReleaseIntegrityVerifier.IntegrityMode,
                SigningKeyId = keyId
            };
            release.ManifestSignature = Convert.ToBase64String(signingKey.SignData(
                Encoding.UTF8.GetBytes(CoreReleaseIntegrityVerifier.Canonicalize(release)),
                CryptoConfig.MapNameToOID("SHA256")));
            if (mutateAfterSigning != null) mutateAfterSigning(release);
            CorePackageInstaller.ValidateReleaseForTest(
                release,
                "josvoltpktvwysrasffq.supabase.co",
                signingKey.ExportParameters(false),
                keyId);
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var output = entry.Open()) output.Write(content, 0, content.Length);
        }

        private static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create()) return String.Concat(sha.ComputeHash(value).Select(item => item.ToString("x2")));
        }

        private static void ExpectFailure(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Expected package validation failure did not occur.");
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed += 1;
            Console.WriteLine("PASS " + name);
        }
    }
}
