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
    internal static class ModulePackageVerifierTests
    {
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "k54-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                using (var signingKey = new RSACryptoServiceProvider(3072))
                using (var wrongKey = new RSACryptoServiceProvider(3072))
                {
                    signingKey.PersistKeyInCsp = false;
                    wrongKey.PersistKeyInCsp = false;
                    const string keyId = "fixture-module-key-v1";

                    Run("verify archive SHA, RSA manifest, Contract and internal file hashes", () =>
                        VerifyValid(root, signingKey, keyId));
                    Run("reject Bundle Lock archive SHA mismatch", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions(), true));
                    Run("reject tampered signed manifest", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { TamperAfterSigning = true }, false));
                    Run("reject unsupported Contract Set", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { ContractSetVersion = 2 }, false));
                    Run("reject state schema mismatch", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { StateSchemaVersion = 1 }, false));
                    Run("reject dependency topology mismatch", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { DependencyMismatch = true }, false));
                    Run("reject inner file SHA mismatch", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { InnerHashMismatch = true }, false));
                    Run("reject undeclared archive file", () =>
                        ExpectVerificationFailure(root, signingKey, signingKey, keyId, new PackageOptions { ExtraFile = true }, false));
                    Run("reject package signed by wrong key", () =>
                        ExpectVerificationFailure(root, wrongKey, signingKey, keyId, new PackageOptions(), false));
                }

                Console.WriteLine("Module package verifier tests passed: " + _passed);
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

        private static void VerifyValid(string root, RSACryptoServiceProvider signingKey, string keyId)
        {
            var package = BuildPackage(signingKey, keyId, new PackageOptions());
            var packageSha = Sha256(package);
            var cached = Cache(root, package, packageSha);
            var request = VerificationRequest(cached, packageSha, 1, 0);
            var result = ModulePackageVerifier.VerifyForTest(request, signingKey.ExportParameters(false), keyId);

            if (result == null || result.VerificationStatus != ModulePackageVerifier.VerifiedStatus ||
                result.ModuleId != "protocol" || result.ModuleVersion != "1.4.7" ||
                result.ArchiveSha256 != packageSha || result.ContractSetVersion != 1 ||
                result.StateSchemaVersion != 0 || !File.Exists(result.VerificationReceiptFile))
                throw new InvalidOperationException("Valid module package did not produce a VERIFIED result.");

            var receipt = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(result.VerificationReceiptFile)) as IDictionary<string, object>;
            if (receipt == null || !String.Equals(Convert.ToString(receipt["verificationStatus"]), "VERIFIED", StringComparison.Ordinal) ||
                Convert.ToBoolean(receipt["installAllowed"]) || Convert.ToBoolean(receipt["activationAllowed"]))
                throw new InvalidOperationException("Verification receipt crossed the 5-5 install/activation boundary.");

            if (!cached.RequiresVerification || cached.VerificationStatus != "UNVERIFIED")
                throw new InvalidOperationException("5-3 download layer state was mutated by 5-4 verification.");
        }

        private static void ExpectVerificationFailure(
            string root,
            RSACryptoServiceProvider packageSigningKey,
            RSACryptoServiceProvider verificationKey,
            string keyId,
            PackageOptions options,
            bool archiveShaMismatch)
        {
            var package = BuildPackage(packageSigningKey, keyId, options);
            var expectedSha = archiveShaMismatch ? new String('f', 64) : Sha256(package);
            var cached = Cache(root, package, expectedSha);
            var request = VerificationRequest(cached, expectedSha, 1, 0);
            var receipt = Path.Combine(Path.GetDirectoryName(cached.PackageFile), ModulePackageVerifier.VerificationReceiptName);

            var failed = false;
            try
            {
                ModulePackageVerifier.VerifyForTest(request, verificationKey.ExportParameters(false), keyId);
            }
            catch (InvalidOperationException)
            {
                failed = true;
            }
            if (!failed) throw new InvalidOperationException("Invalid module package was accepted.");
            if (File.Exists(receipt)) throw new InvalidOperationException("Failed verification left a VERIFIED receipt behind.");
        }

        private static ModulePackageCacheResult Cache(string root, byte[] package, string expectedSha)
        {
            var cacheRoot = Path.Combine(root, "c-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (var handler = new ByteArrayHandler(package))
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                var request = new ModulePackageDownloadRequest
                {
                    ModuleId = "protocol",
                    ModuleVersion = "1.4.7",
                    PackagePath = "modules/protocol/1.4.7/KINOJO.Meter.Protocol.1.4.7.zip",
                    ExpectedSha256 = expectedSha,
                    DownloadUri = new Uri("https://fixture.example.invalid/modules/protocol/1.4.7/package.zip")
                };
                return cache.DownloadAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private static ModulePackageVerificationRequest VerificationRequest(
            ModulePackageCacheResult cached,
            string expectedSha,
            int contractSetVersion,
            int stateSchemaVersion)
        {
            return new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = "protocol",
                ModuleVersion = "1.4.7",
                BundlePackagePath = "modules/protocol/1.4.7/KINOJO.Meter.Protocol.1.4.7.zip",
                ExpectedSha256 = expectedSha,
                ContractSetVersion = contractSetVersion,
                StateSchemaVersion = stateSchemaVersion
            };
        }

        private static byte[] BuildPackage(RSACryptoServiceProvider signingKey, string keyId, PackageOptions options)
        {
            var payload = Encoding.UTF8.GetBytes("protocol-fixture-payload-v1");
            var fileSha = options.InnerHashMismatch ? new String('0', 64) : Sha256(payload);
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
                DependencyModuleIds = options.DependencyMismatch
                    ? new List<string> { "contracts" }
                    : new List<string> { "contracts", "capture" },
                ContractSetVersion = options.ContractSetVersion == 0 ? 1 : options.ContractSetVersion,
                State = new ModulePackageState
                {
                    Mode = options.StateSchemaVersion == 0 ? "NONE" : "OWNED",
                    StateSchemaVersion = options.StateSchemaVersion,
                    MinimumReadableSchema = options.StateSchemaVersion == 0 ? 0 : 1,
                    RollbackReadableByPrevious = true,
                    MigrationRequired = false
                },
                Files = new List<ModulePackageFile>
                {
                    new ModulePackageFile
                    {
                        Path = "KINOJO.Meter.Protocol.dll",
                        Size = payload.Length,
                        Sha256 = fileSha,
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
            if (options.TamperAfterSigning) manifest.SourceCommit = new String('b', 40);

            var manifestJson = SerializeManifest(manifest);
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    WriteEntry(archive, "KINOJO.Meter.Protocol.dll", payload);
                    WriteEntry(archive, ModulePackageVerifier.ManifestPath, Encoding.UTF8.GetBytes(manifestJson));
                    if (options.ExtraFile) WriteEntry(archive, "extra.bin", new byte[] { 1, 2, 3 });
                }
                return output.ToArray();
            }
        }

        private static string SerializeManifest(ModulePackageManifest manifest)
        {
            var files = manifest.Files.Select(file => (object)new Dictionary<string, object>
            {
                { "path", file.Path },
                { "size", file.Size },
                { "sha256", file.Sha256 },
                { "role", file.Role }
            }).ToArray();

            var payload = new Dictionary<string, object>
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
                { "files", files },
                { "integrity", new Dictionary<string, object>
                    {
                        { "mode", manifest.Integrity.Mode },
                        { "signingKeyId", manifest.Integrity.SigningKeyId },
                        { "manifestSignature", manifest.Integrity.ManifestSignature }
                    }
                }
            };
            return new JavaScriptSerializer().Serialize(payload);
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

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private sealed class PackageOptions
        {
            public bool TamperAfterSigning { get; set; }
            public bool DependencyMismatch { get; set; }
            public bool InnerHashMismatch { get; set; }
            public bool ExtraFile { get; set; }
            public int ContractSetVersion { get; set; }
            public int StateSchemaVersion { get; set; }
        }

        private sealed class ByteArrayHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;

            public ByteArrayHandler(byte[] bytes)
            {
                _bytes = bytes;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
