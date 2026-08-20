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
    internal static class ModuleDamagedPackageRepairTests
    {
        private static int _passed;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "k59-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                using (var key = new RSACryptoServiceProvider(3072))
                {
                    key.PersistKeyInCsp = false;
                    const string keyId = "fixture-module-key-v1";
                    Run("same-length damaged cache is purged and downloaded fresh", () => DamagedCache(root, key, keyId));
                    Run("damaged staging and stale self-test are replaced", () => DamagedStaging(root, key, keyId));
                    Run("active exact module requires Stage 5-8 rollback first", () => ActiveBlocked(root, key, keyId));
                    Run("unrelated active bundle pointer stays byte-identical", () => ActivePreserved(root, key, keyId));
                    Run("different SHA sibling slot is never deleted", () => SiblingPreserved(root, key, keyId));
                    Run("bad redownload fails closed without partial repaired slot", () => BadDownload(root, key, keyId));
                    Run("repair receipt stays inactive and never changes release pointer", () => ReceiptBoundary(root, key, keyId));
                }
                Console.WriteLine("Module damaged package repair tests passed: " + _passed);
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void DamagedCache(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "cache", key, keyId, true);
            var cacheFile = CachePackage(f.Root, f.Request.Download);
            var len = checked((int)new FileInfo(cacheFile).Length);
            File.WriteAllBytes(cacheFile, Enumerable.Repeat((byte)0x5a, len).ToArray());
            var h = new CountingHandler(f.Package);
            AssertSuccess(Repair(f, h, key, keyId), f.Request);
            if (h.Calls != 1 || !File.ReadAllBytes(cacheFile).SequenceEqual(f.Package))
                throw new InvalidOperationException("Damaged cache was not freshly replaced.");
        }

        private static void DamagedStaging(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "staging", key, keyId, true);
            var primary = Path.Combine(StageSlot(f.Root, f.Request.Download), "KINOJO.Meter.Contracts.dll");
            File.AppendAllText(primary, "damage");
            var selfReceipt = SelfReceipt(f.Root, f.Request.Download);
            File.WriteAllText(selfReceipt, "{}");
            var h = new CountingHandler(f.Package);
            AssertSuccess(Repair(f, h, key, keyId), f.Request);
            var receipt = Json.DeserializeObject(File.ReadAllText(selfReceipt)) as IDictionary<string, object>;
            if (h.Calls != 1 || receipt == null || Convert.ToString(receipt["status"]) != ModuleStagingSelfTest.PassedStatus)
                throw new InvalidOperationException("Damaged staging/self-test were not rebuilt.");
        }

        private static void ActiveBlocked(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "active-block", key, keyId, true);
            WriteActive(f.Root, f.Request.Download.ModuleVersion, f.Request.Download.ExpectedSha256);
            var before = ShaFile(CachePackage(f.Root, f.Request.Download));
            var h = new CountingHandler(f.Package);
            ExpectContains(() => Repair(f, h, key, keyId), ModuleDamagedPackageRepair.ActiveModuleRepairBlockedCode);
            if (h.Calls != 0 || before != ShaFile(CachePackage(f.Root, f.Request.Download)))
                throw new InvalidOperationException("Active target was touched before rollback.");
        }

        private static void ActivePreserved(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "active-preserve", key, keyId, false);
            WriteActive(f.Root, "9.9.9", new String('b', 64));
            var active = Path.Combine(f.Root, "active-bundle.json");
            var before = ShaFile(active);
            AssertSuccess(Repair(f, new CountingHandler(f.Package), key, keyId), f.Request);
            if (before != ShaFile(active)) throw new InvalidOperationException("Active pointer changed during repair.");
        }

        private static void SiblingPreserved(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "sibling", key, keyId, false);
            var siblingSha = f.Request.Download.ExpectedSha256.StartsWith("b") ? new String('c', 64) : new String('b', 64);
            var sibling = Path.Combine(f.Root, "cache", "contracts", "1.0.0", siblingSha);
            Directory.CreateDirectory(sibling);
            var marker = Path.Combine(sibling, "keep.txt");
            File.WriteAllText(marker, "keep");
            AssertSuccess(Repair(f, new CountingHandler(f.Package), key, keyId), f.Request);
            if (!File.Exists(marker) || File.ReadAllText(marker) != "keep")
                throw new InvalidOperationException("Different SHA sibling was deleted.");
        }

        private static void BadDownload(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "bad", key, keyId, false);
            var h = new CountingHandler(Encoding.UTF8.GetBytes("not-approved"));
            Expect(() => Repair(f, h, key, keyId));
            if (h.Calls != 1 || Directory.Exists(StageSlot(f.Root, f.Request.Download)) ||
                Directory.Exists(SelfSlot(f.Root, f.Request.Download)) || File.Exists(RepairReceipt(f.Root, f.Request.Download)) ||
                Directory.Exists(Path.GetDirectoryName(CachePackage(f.Root, f.Request.Download))))
                throw new InvalidOperationException("Bad download left partial trusted state.");
        }

        private static void ReceiptBoundary(string root, RSACryptoServiceProvider key, string keyId)
        {
            var f = Fixture(root, "receipt", key, keyId, false);
            var r = Repair(f, new CountingHandler(f.Package), key, keyId);
            AssertSuccess(r, f.Request);
            var x = Json.DeserializeObject(File.ReadAllText(r.RepairReceiptFile)) as IDictionary<string, object>;
            if (x == null || Convert.ToString(x["status"]) != ModuleDamagedPackageRepair.RepairedStatus ||
                !Convert.ToBoolean(x["downloadedFresh"]) || Convert.ToBoolean(x["activeBundleChanged"]) ||
                Convert.ToBoolean(x["releasePointerChanged"]) || Convert.ToString(x["verificationStatus"]) != ModulePackageVerifier.VerifiedStatus ||
                Convert.ToString(x["stagingStatus"]) != ModuleStagingInstaller.StagedStatus || Convert.ToString(x["selfTestStatus"]) != ModuleStagingSelfTest.PassedStatus)
                throw new InvalidOperationException("Repair receipt crossed activation/release boundary.");
        }

        private static Fixture Fixture(string root, string name, RSACryptoServiceProvider key, string keyId, bool prime)
        {
            var r = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(r);
            var package = BuildPackage(key, keyId);
            var request = Request(package);
            var f = new Fixture { Root = r, Package = package, Request = request };
            if (prime) Prime(f, key, keyId);
            return f;
        }

        private static ModuleDamagedPackageRepairRequest Request(byte[] package)
        {
            return new ModuleDamagedPackageRepairRequest
            {
                BundleRevision = "B000123", BundleLockSha256 = new String('a', 64), Channel = "staging",
                Download = new ModulePackageDownloadRequest
                {
                    ModuleId = "contracts", ModuleVersion = "1.0.0",
                    PackagePath = "modules/contracts/1.0.0/KINOJO.Meter.Contracts.1.0.0.zip",
                    ExpectedSha256 = Sha(package), DownloadUri = new Uri("https://fixture.example.invalid/modules/contracts/1.0.0/package.zip")
                },
                ContractSetVersion = 1, StateSchemaVersion = 0, Dependencies = new List<ModuleSelfTestDependency>(), ReasonCode = "STAGING_FILE_MISMATCH"
            };
        }

        private static void Prime(Fixture f, RSACryptoServiceProvider key, string keyId)
        {
            ModulePackageCacheResult cacheResult;
            using (var cache = new ModulePackageDownloadCache(new CountingHandler(f.Package), Path.Combine(f.Root, "cache")))
                cacheResult = cache.DownloadAsync(f.Request.Download, null, CancellationToken.None).GetAwaiter().GetResult();
            var v = Verification(f.Request, cacheResult);
            ModulePackageVerifier.VerifyForTest(v, key.ExportParameters(false), keyId);
            var s = ModuleStagingInstaller.StageForTest(new ModuleStagingInstallRequest { VerificationRequest = v }, Path.Combine(f.Root, "staging"), key.ExportParameters(false), keyId);
            ModuleStagingSelfTest.RunForTest(new ModuleSelfTestRequest { Target = s, Dependencies = new List<ModuleSelfTestDependency>() }, Path.Combine(f.Root, "staging"), Path.Combine(f.Root, "self-tests"));
        }

        private static ModulePackageVerificationRequest Verification(ModuleDamagedPackageRepairRequest r, ModulePackageCacheResult c)
        {
            return new ModulePackageVerificationRequest
            {
                Cache = c, ModuleId = r.Download.ModuleId, ModuleVersion = r.Download.ModuleVersion,
                BundlePackagePath = r.Download.PackagePath, ExpectedSha256 = r.Download.ExpectedSha256,
                ContractSetVersion = r.ContractSetVersion, StateSchemaVersion = r.StateSchemaVersion
            };
        }

        private static ModuleDamagedPackageRepairResult Repair(Fixture f, CountingHandler h, RSACryptoServiceProvider key, string keyId)
        {
            return ModuleDamagedPackageRepair.RepairForTestAsync(f.Request, null, CancellationToken.None, f.Root, h, key.ExportParameters(false), keyId).GetAwaiter().GetResult();
        }

        private static byte[] BuildPackage(RSACryptoServiceProvider key, string keyId)
        {
            var payload = File.ReadAllBytes(typeof(ModuleDamagedPackageRepairTests).Assembly.Location);
            var m = new ModulePackageManifest
            {
                SchemaVersion = 1, ManifestType = ModulePackageVerifier.ManifestType, ModuleId = "contracts", ModuleVersion = "1.0.0",
                SourceCommit = new String('a', 40), TargetPlatform = ModulePackageVerifier.TargetPlatform,
                PrimaryArtifact = new ModulePackagePrimaryArtifact { Path = "KINOJO.Meter.Contracts.dll", Kind = "DLL", LoadTarget = "SHARED_RUNTIME" },
                DependencyModuleIds = new List<string>(), ContractSetVersion = 1,
                State = new ModulePackageState { Mode = "NONE", StateSchemaVersion = 0, MinimumReadableSchema = 0, RollbackReadableByPrevious = true, MigrationRequired = false },
                Files = new List<ModulePackageFile> { new ModulePackageFile { Path = "KINOJO.Meter.Contracts.dll", Size = payload.Length, Sha256 = Sha(payload), Role = "PRIMARY" } },
                Integrity = new ModulePackageIntegrity { Mode = ModulePackageVerifier.IntegrityMode, SigningKeyId = keyId, ManifestSignature = "pending" }
            };
            m.Integrity.ManifestSignature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(ModulePackageVerifier.CanonicalizeForTest(m)), CryptoConfig.MapNameToOID("SHA256")));
            var manifest = ManifestJson(m);
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    Entry(zip, "KINOJO.Meter.Contracts.dll", payload);
                    Entry(zip, ModulePackageVerifier.ManifestPath, Encoding.UTF8.GetBytes(manifest));
                }
                return output.ToArray();
            }
        }

        private static string ManifestJson(ModulePackageManifest m)
        {
            return Json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", m.SchemaVersion }, { "manifestType", m.ManifestType }, { "moduleId", m.ModuleId }, { "moduleVersion", m.ModuleVersion },
                { "sourceCommit", m.SourceCommit }, { "targetPlatform", m.TargetPlatform },
                { "primaryArtifact", new Dictionary<string, object> { { "path", m.PrimaryArtifact.Path }, { "kind", m.PrimaryArtifact.Kind }, { "loadTarget", m.PrimaryArtifact.LoadTarget } } },
                { "dependencyModuleIds", m.DependencyModuleIds.ToArray() }, { "contractSetVersion", m.ContractSetVersion },
                { "state", new Dictionary<string, object> { { "mode", m.State.Mode }, { "stateSchemaVersion", m.State.StateSchemaVersion }, { "minimumReadableSchema", m.State.MinimumReadableSchema }, { "rollbackReadableByPrevious", m.State.RollbackReadableByPrevious }, { "migrationRequired", m.State.MigrationRequired } } },
                { "files", m.Files.Select(x => (object)new Dictionary<string, object> { { "path", x.Path }, { "size", x.Size }, { "sha256", x.Sha256 }, { "role", x.Role } }).ToArray() },
                { "integrity", new Dictionary<string, object> { { "mode", m.Integrity.Mode }, { "signingKeyId", m.Integrity.SigningKeyId }, { "manifestSignature", m.Integrity.ManifestSignature } } }
            });
        }

        private static void WriteActive(string root, string contractsVersion, string contractsSha)
        {
            var ids = new[] { "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell" };
            var modules = ids.Select(id => new ActiveModuleBundleEntry
            {
                ModuleId = id, ModuleVersion = id == "contracts" ? contractsVersion : "9.9.9",
                ArchiveSha256 = id == "contracts" ? contractsSha : Sha(Encoding.UTF8.GetBytes("active-" + id)), StateSchemaVersion = 0,
                PackagePath = "modules/" + id + "/9.9.9/fixture.zip", StagedDirectory = Path.Combine(root, "staging", id),
                SelfTestReceiptSha256 = new String('c', 64), ManifestSha256 = new String('d', 64)
            }).ToList();
            var state = new ActiveModuleBundleState
            {
                SchemaVersion = 1, Status = ModuleBundleActivator.ActiveStatus, Channel = "staging", ProductVersion = "0.3.0",
                BundleRevision = "B000122", ParentBundleRevision = "B000121", SourceCommit = new String('a', 40), ContractSetVersion = 1,
                ModuleSetHash = new String('e', 64), BundleLockSha256 = new String('f', 64), ActivatedAtUtc = DateTime.UtcNow.ToString("o"), ActivationAtomic = true, Modules = modules
            };
            File.WriteAllText(Path.Combine(root, "active-bundle.json"), Json.Serialize(state));
        }

        private static void AssertSuccess(ModuleDamagedPackageRepairResult r, ModuleDamagedPackageRepairRequest q)
        {
            if (r == null || r.Status != ModuleDamagedPackageRepair.RepairedStatus || r.ModuleId != q.Download.ModuleId || r.ModuleVersion != q.Download.ModuleVersion ||
                r.ArchiveSha256 != q.Download.ExpectedSha256 || !r.DownloadedFresh || r.ActiveBundleChanged || r.ReleasePointerChanged ||
                !File.Exists(r.RepairReceiptFile) || !File.Exists(r.SelfTestReceiptFile))
                throw new InvalidOperationException("Repair result invalid.");
        }

        private static string CachePackage(string root, ModulePackageDownloadRequest r) { return Path.Combine(root, "cache", r.ModuleId, r.ModuleVersion, r.ExpectedSha256, "package.zip"); }
        private static string StageSlot(string root, ModulePackageDownloadRequest r) { return Path.Combine(root, "staging", r.ModuleId, r.ModuleVersion, r.ExpectedSha256); }
        private static string SelfSlot(string root, ModulePackageDownloadRequest r) { return Path.Combine(root, "self-tests", r.ModuleId, r.ModuleVersion, r.ExpectedSha256); }
        private static string SelfReceipt(string root, ModulePackageDownloadRequest r) { return Path.Combine(SelfSlot(root, r), ModuleStagingSelfTest.ReceiptName); }
        private static string RepairReceipt(string root, ModulePackageDownloadRequest r) { return Path.Combine(root, "repairs", r.ModuleId, r.ModuleVersion, r.ExpectedSha256, ModuleDamagedPackageRepair.ReceiptName); }
        private static void Entry(ZipArchive z, string n, byte[] b) { var e = z.CreateEntry(n, CompressionLevel.Optimal); using (var s = e.Open()) s.Write(b, 0, b.Length); }
        private static string Sha(byte[] b) { using (var h = SHA256.Create()) return String.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }
        private static string ShaFile(string p) { return Sha(File.ReadAllBytes(p)); }
        private static void Run(string n, Action a) { a(); _passed++; Console.WriteLine("PASS " + n); }
        private static void Expect(Action a) { try { a(); } catch { return; } throw new InvalidOperationException("Expected failure."); }
        private static void ExpectContains(Action a, string s) { try { a(); } catch (Exception e) { if (e.ToString().IndexOf(s, StringComparison.Ordinal) >= 0) return; throw; } throw new InvalidOperationException("Expected failure: " + s); }

        private sealed class Fixture { public string Root; public byte[] Package; public ModuleDamagedPackageRepairRequest Request; }
        internal sealed class CountingHandler : HttpMessageHandler
        {
            private readonly byte[] _bytes;
            public CountingHandler(byte[] bytes) { _bytes = bytes ?? new byte[0]; }
            public int Calls { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(_bytes) };
                response.Content.Headers.ContentLength = _bytes.Length;
                return Task.FromResult(response);
            }
        }
    }
}
