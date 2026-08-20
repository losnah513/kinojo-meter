using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal static class ModuleStagingInstallerTests
    {
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "k55-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                using (var signingKey = new RSACryptoServiceProvider(3072))
                {
                    signingKey.PersistKeyInCsp = false;
                    const string keyId = "fixture-module-key-v1";

                    Run("stage VERIFIED package into isolated deterministic slot", () =>
                        StageValid(root, signingKey, keyId));
                    Run("same module/version/SHA staging is idempotent", () =>
                        StageIdempotent(root, signingKey, keyId));
                    Run("reject missing VERIFIED receipt", () =>
                        RejectMissingReceipt(root, signingKey, keyId));
                    Run("reject package tampered after 5-4 verification", () =>
                        RejectTamperedAfterVerification(root, signingKey, keyId));
                    Run("reject different SHA for same module/version", () =>
                        RejectDifferentShaSibling(root, signingKey, keyId));
                    Run("staging receipt never activates bundle", () =>
                        ReceiptStaysInactive(root, signingKey, keyId));
                }

                Console.WriteLine("Module staging installer tests passed: " + _passed);
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

        private static void StageValid(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = VerifiedFixture(root, key, keyId, "payload-stage-valid");
            var stagingRoot = Path.Combine(root, "staging-valid");
            var result = ModuleStagingInstaller.StageForTest(
                new ModuleStagingInstallRequest { VerificationRequest = fixture.Request },
                stagingRoot,
                key.ExportParameters(false),
                keyId);

            if (result == null || result.InstallStatus != ModuleStagingInstaller.StagedStatus || result.AlreadyStaged)
                throw new InvalidOperationException("Valid VERIFIED package was not staged.");
            if (!File.Exists(Path.Combine(result.StagedDirectory, "KINOJO.Meter.Protocol.dll")) ||
                !File.Exists(Path.Combine(result.StagedDirectory, ModulePackageVerifier.ManifestPath)) ||
                !File.Exists(result.InstallReceiptFile))
                throw new InvalidOperationException("Staged slot is incomplete.");

            var expected = Path.GetFullPath(Path.Combine(
                stagingRoot, "protocol", "1.4.7", fixture.Sha256));
            if (!String.Equals(Path.GetFullPath(result.StagedDirectory), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staged layout is not deterministic by module/version/SHA.");
            if (Path.GetFullPath(result.StagedDirectory).StartsWith(
                Path.GetFullPath(Path.GetDirectoryName(fixture.Request.Cache.PackageFile)),
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staging slot overlaps the quarantine cache.");
        }

        private static void StageIdempotent(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = VerifiedFixture(root, key, keyId, "payload-stage-idempotent");
            var stagingRoot = Path.Combine(root, "staging-idempotent");
            var first = Stage(fixture, stagingRoot, key, keyId);
            var second = Stage(fixture, stagingRoot, key, keyId);

            if (first.AlreadyStaged || !second.AlreadyStaged ||
                !String.Equals(first.StagedDirectory, second.StagedDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Same verified package was not staged idempotently.");
        }

        private static void RejectMissingReceipt(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = VerifiedFixture(root, key, keyId, "payload-missing-receipt");
            File.Delete(Path.Combine(Path.GetDirectoryName(fixture.Request.Cache.PackageFile), ModulePackageVerifier.VerificationReceiptName));
            ExpectFailure(() => Stage(fixture, Path.Combine(root, "staging-missing-receipt"), key, keyId));
        }

        private static void RejectTamperedAfterVerification(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = VerifiedFixture(root, key, keyId, "payload-before-tamper");
            File.AppendAllText(fixture.Request.Cache.PackageFile, "tamper");
            var stagingRoot = Path.Combine(root, "staging-tampered");
            ExpectFailure(() => Stage(fixture, stagingRoot, key, keyId));

            var final = Path.Combine(stagingRoot, "protocol", "1.4.7", fixture.Sha256);
            if (Directory.Exists(final))
                throw new InvalidOperationException("Tampered package left a staged slot.");
        }

        private static void RejectDifferentShaSibling(string root, RSACryptoServiceProvider key, string keyId)
        {
            var stagingRoot = Path.Combine(root, "staging-sha-conflict");
            var first = VerifiedFixture(root, key, keyId, "payload-sha-a");
            var second = VerifiedFixture(root, key, keyId, "payload-sha-b");
            if (String.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture SHA values unexpectedly match.");

            Stage(first, stagingRoot, key, keyId);
            ExpectFailure(() => Stage(second, stagingRoot, key, keyId));
        }

        private static void ReceiptStaysInactive(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = VerifiedFixture(root, key, keyId, "payload-inactive");
            var result = Stage(fixture, Path.Combine(root, "staging-inactive"), key, keyId);
            var receipt = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(result.InstallReceiptFile))
                as IDictionary<string, object>;
            if (receipt == null ||
                !String.Equals(Convert.ToString(receipt["installStatus"]), "STAGED", StringComparison.Ordinal) ||
                Convert.ToBoolean(receipt["activationAllowed"]) ||
                Convert.ToBoolean(receipt["activeBundleChanged"]))
                throw new InvalidOperationException("5-5 staging receipt crossed activation boundary.");
        }

        private static ModuleStagingInstallResult Stage(
            Fixture fixture,
            string stagingRoot,
            RSACryptoServiceProvider key,
            string keyId)
        {
            return ModuleStagingInstaller.StageForTest(
                new ModuleStagingInstallRequest { VerificationRequest = fixture.Request },
                stagingRoot,
                key.ExportParameters(false),
                keyId);
        }

        private static Fixture VerifiedFixture(
            string root,
            RSACryptoServiceProvider key,
            string keyId,
            string payloadText)
        {
            var package = BuildPackage(key, keyId, payloadText);
            var sha = Sha256(package);
            var cacheRoot = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ModulePackageCacheResult cached;
            using (var handler = new ByteArrayHandler(package))
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                cached = cache.DownloadAsync(
                    new ModulePackageDownloadRequest
                    {
                        ModuleId = "protocol",
                        ModuleVersion = "1.4.7",
                        PackagePath = "modules/protocol/1.4.7/KINOJO.Meter.Protocol.1.4.7.zip",
                        ExpectedSha256 = sha,
                        DownloadUri = new Uri("https://fixture.example.invalid/modules/protocol/1.4.7/package.zip")
                    },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            var request = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = "protocol",
                ModuleVersion = "1.4.7",
                BundlePackagePath = "modules/protocol/1.4.7/KINOJO.Meter.Protocol.1.4.7.zip",
                ExpectedSha256 = sha,
                ContractSetVersion = 1,
                StateSchemaVersion = 0
            };
            ModulePackageVerifier.VerifyForTest(request, key.ExportParameters(false), keyId);
            return new Fixture { Request = request, Sha256 = sha };
        }

        private static byte[] BuildPackage(
            RSACryptoServiceProvider signingKey,
            string keyId,
            string payloadText)
        {
            var payload = Encoding.UTF8.GetBytes(payloadText);
            var manifest = new ModulePackageManifest
            {
                SchemaVersion = 1,
                ManifestType = ModulePackageVerifier.ManifestType,
                ModuleId = "protocol",
                ModuleVersion = "1.4.7",
                SourceCommit = new String('a', 40),
                TargetPlatform = ModulePackageVerifier.TargetPlatform,
                PrimaryArtifact = new ModulePackagePrimaryArtifact
                {
                    Path = "KINOJO.Meter.Protocol.dll",
                    Kind = "DLL",
                    LoadTarget = "ENGINE_HOST_PROCESS"
                },
                DependencyModuleIds = new List<string> { "contracts", "capture" },
                ContractSetVersion = 1,
                State = new ModulePackageState
                {
                    Mode = "NONE",
                    StateSchemaVersion = 0,
                    MinimumReadableSchema = 0,
                    RollbackReadableByPrevious = true,
                    MigrationRequired = false
                },
                Files = new List<ModulePackageFile>
                {
                    new ModulePackageFile
                    {
                        Path = "KINOJO.Meter.Protocol.dll",
                        Size = payload.Length,
                        Sha256 = Sha256(payload),
                        Role = "PRIMARY"
                    }
                },
                Integrity = new ModulePackageIntegrity
                {
                    Mode = ModulePackageVerifier.IntegrityMode,
                    SigningKeyId = keyId,
                    ManifestSignature = "pending"
                }
            };
            var canonical = ModulePackageVerifier.CanonicalizeForTest(manifest);
            manifest.Integrity.ManifestSignature = Convert.ToBase64String(
                signingKey.SignData(Encoding.UTF8.GetBytes(canonical), CryptoConfig.MapNameToOID("SHA256")));

            var manifestJson = SerializeManifest(manifest);
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    WriteEntry(archive, "KINOJO.Meter.Protocol.dll", payload);
                    WriteEntry(archive, ModulePackageVerifier.ManifestPath, Encoding.UTF8.GetBytes(manifestJson));
                }
                return output.ToArray();
            }
        }

        private static string SerializeManifest(ModulePackageManifest manifest)
        {
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", manifest.SchemaVersion },
                { "manifestType", manifest.ManifestType },
                { "moduleId", manifest.ModuleId },
                { "moduleVersion", manifest.ModuleVersion },
                { "sourceCommit", manifest.SourceCommit },
                { "targetPlatform", manifest.TargetPlatform },
                { "primaryArtifact", new Dictionary<string, object>
                    {
                        { "path", manifest.PrimaryArtifact.Path },
                        { "kind", manifest.PrimaryArtifact.Kind },
                        { "loadTarget", manifest.PrimaryArtifact.LoadTarget }
                    }
                },
                { "dependencyModuleIds", manifest.DependencyModuleIds.ToArray() },
                { "contractSetVersion", manifest.ContractSetVersion },
                { "state", new Dictionary<string, object>
                    {
                        { "mode", manifest.State.Mode },
                        { "stateSchemaVersion", manifest.State.StateSchemaVersion },
                        { "minimumReadableSchema", manifest.State.MinimumReadableSchema },
                        { "rollbackReadableByPrevious", manifest.State.RollbackReadableByPrevious },
                        { "migrationRequired", manifest.State.MigrationRequired }
                    }
                },
                { "files", manifest.Files.Select(file => (object)new Dictionary<string, object>
                    {
                        { "path", file.Path },
                        { "size", file.Size },
                        { "sha256", file.Sha256 },
                        { "role", file.Role }
                    }).ToArray()
                },
                { "integrity", new Dictionary<string, object>
                    {
                        { "mode", manifest.Integrity.Mode },
                        { "signingKeyId", manifest.Integrity.SigningKeyId },
                        { "manifestSignature", manifest.Integrity.ManifestSignature }
                    }
                }
            });
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open()) stream.Write(bytes, 0, bytes.Length);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private static void ExpectFailure(Action action)
        {
            var failed = false;
            try { action(); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Expected staging failure did not occur.");
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private sealed class Fixture
        {
            public ModulePackageVerificationRequest Request { get; set; }
            public string Sha256 { get; set; }
        }

        private sealed class ByteArrayHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;

            public ByteArrayHandler(byte[] bytes)
            {
                _bytes = bytes;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(_bytes)
                };
                response.Content.Headers.ContentLength = _bytes.Length;
                return Task.FromResult(response);
            }
        }
    }
}
