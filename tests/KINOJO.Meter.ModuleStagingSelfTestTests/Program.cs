using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal static class ModuleStagingSelfTestTests
    {
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "k56-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                using (var key = new RSACryptoServiceProvider(3072))
                {
                    key.PersistKeyInCsp = false;
                    const string keyId = "fixture-module-key-v1";

                    Run("contracts staged module passes isolated self-test", () => PassContracts(root, key, keyId));
                    Run("self-test receipt is idempotent and inactive", () => IdempotentInactive(root, key, keyId));
                    Run("capture requires exact staged contracts dependency", () => PassCaptureDependency(root, key, keyId));
                    Run("missing dependency fails closed", () => RejectMissingDependency(root, key, keyId));
                    Run("tampered staged file fails closed", () => RejectTamperedStagedFile(root, key, keyId));
                    Run("unmanaged primary artifact fails metadata load", () => RejectUnmanagedPrimary(root, key, keyId));
                }

                Console.WriteLine("Module staging self-test tests passed: " + _passed);
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

        private static void PassContracts(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = Stage(root, key, keyId, "contracts", new string[0], ManagedPayload());
            var selfRoot = Path.Combine(root, "self-contracts");
            var result = ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = fixture.Stage, Dependencies = new List<ModuleSelfTestDependency>() },
                fixture.StagingRoot,
                selfRoot);

            if (result == null || result.Status != ModuleStagingSelfTest.PassedStatus || result.AlreadyPassed || !File.Exists(result.ReceiptFile))
                throw new InvalidOperationException("Valid contracts staging did not pass self-test.");
            if (File.Exists(Path.Combine(fixture.Stage.StagedDirectory, ModuleStagingSelfTest.ReceiptName)))
                throw new InvalidOperationException("Self-test receipt leaked into staging slot.");
        }

        private static void IdempotentInactive(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = Stage(root, key, keyId, "contracts", new string[0], ManagedPayload());
            var selfRoot = Path.Combine(root, "self-idempotent");
            var request = new ModuleSelfTestRequest { Target = fixture.Stage, Dependencies = new List<ModuleSelfTestDependency>() };
            var first = ModuleStagingSelfTest.RunForTest(request, fixture.StagingRoot, selfRoot);
            var second = ModuleStagingSelfTest.RunForTest(request, fixture.StagingRoot, selfRoot);
            if (first.AlreadyPassed || !second.AlreadyPassed || !String.Equals(first.ReceiptFile, second.ReceiptFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Self-test receipt is not idempotent.");

            var receipt = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(second.ReceiptFile)) as IDictionary<string, object>;
            if (receipt == null || Convert.ToBoolean(receipt["activationAllowed"]) || Convert.ToBoolean(receipt["activeBundleChanged"]))
                throw new InvalidOperationException("5-6 self-test crossed activation boundary.");
        }

        private static void PassCaptureDependency(string root, RSACryptoServiceProvider key, string keyId)
        {
            var stagingRoot = Path.Combine(root, "staging-deps");
            var contracts = StageAt(stagingRoot, root, key, keyId, "contracts", new string[0], ManagedPayload());
            var capture = StageAt(stagingRoot, root, key, keyId, "capture", new[] { "contracts" }, ManagedPayload());
            var dependency = ToDependency(contracts.Stage);

            var result = ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest
                {
                    Target = capture.Stage,
                    Dependencies = new List<ModuleSelfTestDependency> { dependency }
                },
                stagingRoot,
                Path.Combine(root, "self-deps"));
            if (result.Status != ModuleStagingSelfTest.PassedStatus)
                throw new InvalidOperationException("Valid capture dependency did not pass self-test.");
        }

        private static void RejectMissingDependency(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = Stage(root, key, keyId, "capture", new[] { "contracts" }, ManagedPayload());
            ExpectFailure(() => ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = fixture.Stage, Dependencies = new List<ModuleSelfTestDependency>() },
                fixture.StagingRoot,
                Path.Combine(root, "self-missing-dep")));
        }

        private static void RejectTamperedStagedFile(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = Stage(root, key, keyId, "contracts", new string[0], ManagedPayload());
            File.AppendAllText(Path.Combine(fixture.Stage.StagedDirectory, "KINOJO.Meter.Contracts.dll"), "tamper");
            ExpectFailure(() => ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = fixture.Stage, Dependencies = new List<ModuleSelfTestDependency>() },
                fixture.StagingRoot,
                Path.Combine(root, "self-tamper")));
        }

        private static void RejectUnmanagedPrimary(string root, RSACryptoServiceProvider key, string keyId)
        {
            var fixture = Stage(root, key, keyId, "contracts", new string[0], Encoding.UTF8.GetBytes("not-a-managed-assembly"));
            ExpectFailure(() => ModuleStagingSelfTest.RunForTest(
                new ModuleSelfTestRequest { Target = fixture.Stage, Dependencies = new List<ModuleSelfTestDependency>() },
                fixture.StagingRoot,
                Path.Combine(root, "self-unmanaged")));
        }

        private static Fixture Stage(string root, RSACryptoServiceProvider key, string keyId, string moduleId, string[] dependencies, byte[] payload)
        {
            return StageAt(Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N").Substring(0, 8)), root, key, keyId, moduleId, dependencies, payload);
        }

        private static Fixture StageAt(string stagingRoot, string root, RSACryptoServiceProvider key, string keyId, string moduleId, string[] dependencies, byte[] payload)
        {
            var version = "1.4.7";
            var package = BuildPackage(key, keyId, moduleId, version, dependencies, payload);
            var sha = Sha256(package);
            var cacheRoot = Path.Combine(root, "cache-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ModulePackageCacheResult cached;
            using (var handler = new ByteArrayHandler(package))
            using (var cache = new ModulePackageDownloadCache(handler, cacheRoot))
            {
                cached = cache.DownloadAsync(new ModulePackageDownloadRequest
                {
                    ModuleId = moduleId,
                    ModuleVersion = version,
                    PackagePath = "modules/" + moduleId + "/" + version + "/KINOJO.Meter." + Capitalize(moduleId) + "." + version + ".zip",
                    ExpectedSha256 = sha,
                    DownloadUri = new Uri("https://fixture.example.invalid/modules/" + moduleId + "/package.zip")
                }, null, CancellationToken.None).GetAwaiter().GetResult();
            }

            var verification = new ModulePackageVerificationRequest
            {
                Cache = cached,
                ModuleId = moduleId,
                ModuleVersion = version,
                BundlePackagePath = "modules/" + moduleId + "/" + version + "/KINOJO.Meter." + Capitalize(moduleId) + "." + version + ".zip",
                ExpectedSha256 = sha,
                ContractSetVersion = 1,
                StateSchemaVersion = 0
            };
            ModulePackageVerifier.VerifyForTest(verification, key.ExportParameters(false), keyId);
            var staged = ModuleStagingInstaller.StageForTest(
                new ModuleStagingInstallRequest { VerificationRequest = verification },
                stagingRoot,
                key.ExportParameters(false),
                keyId);
            return new Fixture { Stage = staged, StagingRoot = stagingRoot };
        }

        private static byte[] BuildPackage(RSACryptoServiceProvider key, string keyId, string moduleId, string version, string[] dependencies, byte[] payload)
        {
            var primary = Primary(moduleId);
            var manifest = new ModulePackageManifest
            {
                SchemaVersion = 1,
                ManifestType = ModulePackageVerifier.ManifestType,
                ModuleId = moduleId,
                ModuleVersion = version,
                SourceCommit = new String('a', 40),
                TargetPlatform = ModulePackageVerifier.TargetPlatform,
                PrimaryArtifact = new ModulePackagePrimaryArtifact
                {
                    Path = primary,
                    Kind = moduleId == "shell" ? "EXE" : "DLL",
                    LoadTarget = moduleId == "contracts" ? "SHARED_RUNTIME" : (moduleId == "shell" ? "SHELL_PROCESS" : "ENGINE_HOST_PROCESS")
                },
                DependencyModuleIds = dependencies.ToList(),
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
                    new ModulePackageFile { Path = primary, Size = payload.Length, Sha256 = Sha256(payload), Role = "PRIMARY" }
                },
                Integrity = new ModulePackageIntegrity
                {
                    Mode = ModulePackageVerifier.IntegrityMode,
                    SigningKeyId = keyId,
                    ManifestSignature = "pending"
                }
            };
            var canonical = ModulePackageVerifier.CanonicalizeForTest(manifest);
            manifest.Integrity.ManifestSignature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(canonical), CryptoConfig.MapNameToOID("SHA256")));
            var json = SerializeManifest(manifest);

            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    WriteEntry(archive, primary, payload);
                    WriteEntry(archive, ModulePackageVerifier.ManifestPath, Encoding.UTF8.GetBytes(json));
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
                        { "path", manifest.PrimaryArtifact.Path }, { "kind", manifest.PrimaryArtifact.Kind }, { "loadTarget", manifest.PrimaryArtifact.LoadTarget }
                    }
                },
                { "dependencyModuleIds", manifest.DependencyModuleIds.ToArray() },
                { "contractSetVersion", manifest.ContractSetVersion },
                { "state", new Dictionary<string, object>
                    {
                        { "mode", manifest.State.Mode }, { "stateSchemaVersion", manifest.State.StateSchemaVersion },
                        { "minimumReadableSchema", manifest.State.MinimumReadableSchema }, { "rollbackReadableByPrevious", manifest.State.RollbackReadableByPrevious },
                        { "migrationRequired", manifest.State.MigrationRequired }
                    }
                },
                { "files", manifest.Files.Select(file => (object)new Dictionary<string, object>
                    {
                        { "path", file.Path }, { "size", file.Size }, { "sha256", file.Sha256 }, { "role", file.Role }
                    }).ToArray()
                },
                { "integrity", new Dictionary<string, object>
                    {
                        { "mode", manifest.Integrity.Mode }, { "signingKeyId", manifest.Integrity.SigningKeyId }, { "manifestSignature", manifest.Integrity.ManifestSignature }
                    }
                }
            });
        }

        private static byte[] ManagedPayload()
        {
            return File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
        }

        private static ModuleSelfTestDependency ToDependency(ModuleStagingInstallResult stage)
        {
            return new ModuleSelfTestDependency
            {
                ModuleId = stage.ModuleId,
                ModuleVersion = stage.ModuleVersion,
                ArchiveSha256 = stage.ArchiveSha256,
                StagedDirectory = stage.StagedDirectory
            };
        }

        private static string Primary(string moduleId)
        {
            switch (moduleId)
            {
                case "contracts": return "KINOJO.Meter.Contracts.dll";
                case "capture": return "KINOJO.Meter.Capture.dll";
                case "protocol": return "KINOJO.Meter.Protocol.dll";
                case "combat": return "KINOJO.Meter.Combat.dll";
                case "encounter": return "KINOJO.Meter.Encounter.dll";
                case "sync": return "KINOJO.Meter.Sync.dll";
                case "shell": return "KINOJO.Meter.Shell.exe";
                default: throw new InvalidOperationException("Unknown module fixture.");
            }
        }

        private static string Capitalize(string value)
        {
            return Char.ToUpperInvariant(value[0]) + value.Substring(1);
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
            if (!failed) throw new InvalidOperationException("Expected self-test failure did not occur.");
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private sealed class Fixture
        {
            public ModuleStagingInstallResult Stage { get; set; }
            public string StagingRoot { get; set; }
        }

        private sealed class ByteArrayHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;
            public ByteArrayHandler(byte[] bytes) { _bytes = bytes; }
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
