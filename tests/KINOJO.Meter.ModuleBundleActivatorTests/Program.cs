using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal static class ModuleBundleActivatorTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private static readonly string[] ModuleIds = { "contracts", "capture", "protocol", "combat", "encounter", "sync", "shell" };
        private static readonly Dictionary<string, string[]> Dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "contracts", new string[0] },
            { "capture", new[] { "contracts" } },
            { "protocol", new[] { "contracts", "capture" } },
            { "combat", new[] { "contracts", "protocol" } },
            { "encounter", new[] { "contracts" } },
            { "sync", new[] { "contracts", "capture", "protocol", "combat" } },
            { "shell", new[] { "contracts", "capture", "protocol", "combat", "encounter", "sync" } }
        };

        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "k57-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Run("governance moduleSetHash canonicalization matches private authority", GovernanceHashMatches);
                Run("valid seven-module bundle replaces one active pointer atomically", () => ValidAtomicActivation(root));
                Run("same exact active bundle is idempotent", () => IdempotentActivation(root));
                Run("shadow compare bundle cannot become active", () => RejectShadowBundle(root));
                Run("stale bundle parent fails closed", () => RejectStaleParent(root));
                Run("missing self-test leaves previous active bundle unchanged", () => MissingSelfTestKeepsPrevious(root));
                Run("post-self-test staging tamper leaves previous active bundle unchanged", () => TamperedStageKeepsPrevious(root));
                Run("wrong bundle lock SHA fails before active pointer change", () => WrongBundleShaKeepsPrevious(root));
                Console.WriteLine("Module active bundle tests passed: " + _passed);
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

        private static void GovernanceHashMatches()
        {
            var modules = new List<ModuleBundleLockEntry>
            {
                Entry("contracts", "0.2.80", "03cd1c0e6c398af834500569a2e3571389f8b1872b3d3d358f2640173e0c2f36", 0, "modules/contracts/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("capture", "0.2.80", "68543303dcc5ec2a94da7355f2186dd4f729d57feedeb5133d772cb925d9e676", 0, "modules/capture/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("protocol", "0.2.80", "3b0f5fd12f971c09f53b15a99cdbb146fd124528fe5c172aec235130e1b812de", 0, "modules/protocol/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("combat", "0.2.80", "212f314e12af810d8a03e8ec50a8526723f3515c61d51579993d3ea2e4d2b246", 1, "modules/combat/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("encounter", "0.2.80", "11afa43f094bcd3b9fd9a498b9b726665860997b3651b5213334ac9d213a5319", 1, "modules/encounter/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("sync", "0.2.80", "8a75840a98f0bc56777fa949b2054ba2d874b203c50daf5396d90cbb2a74c33d", 1, "modules/sync/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip"),
                Entry("shell", "0.2.80", "08694c7d6dacd3d96c56abb11a8f585d09cec4da65f9705500ec3c6ed3a2dd9a", 1, "modules/shell/0.2.80/MONOLITHIC_SHADOW_KinojoMeterCore_0.2.80_x64.zip")
            };
            var actual = ModuleBundleActivator.ComputeModuleSetHashForTest(modules);
            const string expected = "dad05fa5052b3bc5394d1f55666a120d204af4d21b9233c59da66bfc6546f73a";
            if (!String.Equals(actual, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("moduleSetHash canonicalization diverged from private Bundle authority: " + actual);
        }

        private static ModuleBundleLockEntry Entry(string id, string version, string sha, int stateSchema, string packagePath)
        {
            return new ModuleBundleLockEntry
            {
                ModuleId = id,
                ModuleVersion = version,
                Sha256 = sha,
                ContractSetVersion = 1,
                StateSchemaVersion = stateSchema,
                PackagePath = packagePath
            };
        }

        private static void ValidAtomicActivation(string root)
        {
            var testRoot = NewRoot(root, "valid");
            var fixture = CreateFixture(testRoot, "B000048", "B000047", ModuleBundleActivator.RequiredActivationMode);
            var result = Activate(fixture, "B000047");
            if (result == null || !result.Changed || result.Status != ModuleBundleActivator.ActiveStatus)
                throw new InvalidOperationException("Valid atomic Bundle was not activated.");

            var activeFile = Path.Combine(testRoot, "modules", "active-bundle.json");
            var active = Json.Deserialize<ActiveModuleBundleState>(File.ReadAllText(activeFile));
            if (active == null || active.BundleRevision != "B000048" || !active.ActivationAtomic || active.Modules == null || active.Modules.Count != 7)
                throw new InvalidOperationException("Active Bundle readback does not contain exact seven-module atomic state.");
            if (Directory.GetFiles(Path.Combine(testRoot, "modules"), "active-bundle.json.tmp-*", SearchOption.TopDirectoryOnly).Length != 0)
                throw new InvalidOperationException("Atomic activation left temporary pointer files.");
            foreach (var id in ModuleIds)
            {
                if (File.Exists(Path.Combine(testRoot, "modules", id + ".active.json")))
                    throw new InvalidOperationException("Per-module active pointer leaked: " + id);
            }
        }

        private static void IdempotentActivation(string root)
        {
            var testRoot = NewRoot(root, "idem");
            var fixture = CreateFixture(testRoot, "B000048", "B000047", ModuleBundleActivator.RequiredActivationMode);
            var first = Activate(fixture, "B000047");
            var firstBytes = File.ReadAllBytes(first.ActiveBundleFile);
            var second = Activate(fixture, "B000047");
            if (!first.Changed || second.Changed || !firstBytes.SequenceEqual(File.ReadAllBytes(second.ActiveBundleFile)))
                throw new InvalidOperationException("Exact Bundle activation is not idempotent.");
        }

        private static void RejectShadowBundle(string root)
        {
            var testRoot = NewRoot(root, "shadow");
            var fixture = CreateFixture(testRoot, "B000048", "B000047", "SHADOW_COMPARE");
            ExpectFailure(() => Activate(fixture, "B000047"), "ATOMIC_BUNDLE");
            if (File.Exists(Path.Combine(testRoot, "modules", "active-bundle.json")))
                throw new InvalidOperationException("SHADOW_COMPARE Bundle changed active pointer.");
        }

        private static void RejectStaleParent(string root)
        {
            var testRoot = NewRoot(root, "stale");
            var fixture = CreateFixture(testRoot, "B000048", "B000046", ModuleBundleActivator.RequiredActivationMode);
            ExpectFailure(() => Activate(fixture, "B000047"), ModuleBundleActivator.StaleBundleBaseCode);
            if (File.Exists(Path.Combine(testRoot, "modules", "active-bundle.json")))
                throw new InvalidOperationException("Stale Bundle changed active pointer.");
        }

        private static void MissingSelfTestKeepsPrevious(string root)
        {
            var testRoot = NewRoot(root, "missing");
            var first = CreateFixture(testRoot, "B000048", "B000047", ModuleBundleActivator.RequiredActivationMode);
            Activate(first, "B000047");
            var activeFile = Path.Combine(testRoot, "modules", "active-bundle.json");
            var before = File.ReadAllBytes(activeFile);

            var next = CreateFixture(testRoot, "B000049", "B000048", ModuleBundleActivator.RequiredActivationMode);
            var bad = next.Modules.First(value => value.ModuleId == "combat");
            var receipt = SelfReceipt(testRoot, bad);
            File.Delete(receipt);
            ExpectFailure(() => Activate(next, "B000048"), "self-test.json");
            if (!before.SequenceEqual(File.ReadAllBytes(activeFile)))
                throw new InvalidOperationException("Failed whole-Bundle validation changed previous active pointer.");
        }

        private static void TamperedStageKeepsPrevious(string root)
        {
            var testRoot = NewRoot(root, "tamper");
            var first = CreateFixture(testRoot, "B000048", "B000047", ModuleBundleActivator.RequiredActivationMode);
            Activate(first, "B000047");
            var activeFile = Path.Combine(testRoot, "modules", "active-bundle.json");
            var before = File.ReadAllBytes(activeFile);

            var next = CreateFixture(testRoot, "B000049", "B000048", ModuleBundleActivator.RequiredActivationMode);
            var bad = next.Modules.First(value => value.ModuleId == "protocol");
            File.AppendAllText(StageReceipt(testRoot, bad), " ", Encoding.UTF8);
            ExpectFailure(() => Activate(next, "B000048"), "SELF_TEST_PASSED");
            if (!before.SequenceEqual(File.ReadAllBytes(activeFile)))
                throw new InvalidOperationException("Post-self-test staging tamper changed previous active pointer.");
        }

        private static void WrongBundleShaKeepsPrevious(string root)
        {
            var testRoot = NewRoot(root, "locksha");
            var first = CreateFixture(testRoot, "B000048", "B000047", ModuleBundleActivator.RequiredActivationMode);
            Activate(first, "B000047");
            var activeFile = Path.Combine(testRoot, "modules", "active-bundle.json");
            var before = File.ReadAllBytes(activeFile);

            var next = CreateFixture(testRoot, "B000049", "B000048", ModuleBundleActivator.RequiredActivationMode);
            var request = Request(next, "B000048");
            request.ExpectedBundleLockSha256 = new String('0', 64);
            ExpectFailure(() => ModuleBundleActivator.ActivateForTest(request, Path.Combine(testRoot, "modules")), "Bundle Lock SHA-256");
            if (!before.SequenceEqual(File.ReadAllBytes(activeFile)))
                throw new InvalidOperationException("Wrong Bundle Lock SHA changed previous active pointer.");
        }

        private static ModuleBundleActivationResult Activate(Fixture fixture, string expectedCurrent)
        {
            return ModuleBundleActivator.ActivateForTest(Request(fixture, expectedCurrent), Path.Combine(fixture.Root, "modules"));
        }

        private static ModuleBundleActivationRequest Request(Fixture fixture, string expectedCurrent)
        {
            return new ModuleBundleActivationRequest
            {
                BundleLockFile = fixture.BundleLockFile,
                ExpectedBundleLockSha256 = fixture.BundleLockSha256,
                ExpectedChannel = "staging",
                ExpectedCurrentBundleRevision = expectedCurrent
            };
        }

        private static Fixture CreateFixture(string root, string revision, string parent, string activationMode)
        {
            var moduleRoot = Path.Combine(root, "modules");
            var stagingRoot = Path.Combine(moduleRoot, "staging");
            var selfRoot = Path.Combine(moduleRoot, "self-tests");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(selfRoot);

            var version = String.Equals(revision, "B000049", StringComparison.Ordinal) ? "0.3.1" : "0.3.0";
            var modules = new List<ModuleBundleLockEntry>();
            foreach (var id in ModuleIds)
            {
                var stateSchema = (id == "combat" || id == "encounter" || id == "sync" || id == "shell") ? 1 : 0;
                modules.Add(Entry(
                    id,
                    version,
                    Sha256(Encoding.UTF8.GetBytes("archive:" + id + ":" + version)),
                    stateSchema,
                    "modules/" + id + "/" + version + "/KINOJO.Meter." + Capitalize(id) + "." + version + ".zip"));
            }

            var byId = modules.ToDictionary(value => value.ModuleId, StringComparer.Ordinal);
            foreach (var module in modules)
                WriteStagedAndSelfTest(root, module, byId);

            var moduleSetHash = ModuleBundleActivator.ComputeModuleSetHashForTest(modules);
            var lockPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "productVersion", version },
                { "channel", "staging" },
                { "bundleRevision", revision },
                { "parentBundleRevision", parent },
                { "sourceCommit", new String('a', 40) },
                { "contractSetVersion", 1 },
                { "moduleSetHash", moduleSetHash },
                { "activationMode", activationMode },
                { "immutable", true },
                { "modules", modules.Select(module => (object)new Dictionary<string, object>
                    {
                        { "moduleId", module.ModuleId }, { "moduleVersion", module.ModuleVersion }, { "sha256", module.Sha256 },
                        { "contractSetVersion", module.ContractSetVersion }, { "stateSchemaVersion", module.StateSchemaVersion }, { "packagePath", module.PackagePath }
                    }).ToArray()
                }
            };
            var bundleLockFile = Path.Combine(root, revision + ".bundle.lock.json");
            File.WriteAllText(bundleLockFile, Json.Serialize(lockPayload), new UTF8Encoding(false));
            return new Fixture
            {
                Root = root,
                BundleLockFile = bundleLockFile,
                BundleLockSha256 = Sha256(File.ReadAllBytes(bundleLockFile)),
                Modules = modules
            };
        }

        private static void WriteStagedAndSelfTest(
            string root,
            ModuleBundleLockEntry module,
            IDictionary<string, ModuleBundleLockEntry> byId)
        {
            var staged = StageDirectory(root, module);
            Directory.CreateDirectory(staged);
            var dependencies = Dependencies[module.ModuleId];
            var manifestPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "manifestType", ModulePackageVerifier.ManifestType },
                { "moduleId", module.ModuleId },
                { "moduleVersion", module.ModuleVersion },
                { "sourceCommit", new String('b', 40) },
                { "targetPlatform", ModulePackageVerifier.TargetPlatform },
                { "primaryArtifact", new Dictionary<string, object> { { "path", Primary(module.ModuleId) }, { "kind", module.ModuleId == "shell" ? "EXE" : "DLL" }, { "loadTarget", "TEST" } } },
                { "dependencyModuleIds", dependencies },
                { "contractSetVersion", module.ContractSetVersion },
                { "state", new Dictionary<string, object>
                    {
                        { "mode", module.StateSchemaVersion == 0 ? "NONE" : "OWNED" }, { "stateSchemaVersion", module.StateSchemaVersion },
                        { "minimumReadableSchema", 0 }, { "rollbackReadableByPrevious", true }, { "migrationRequired", false }
                    }
                },
                { "files", new object[0] },
                { "integrity", new Dictionary<string, object> { { "mode", "RSA_SHA256" }, { "signingKeyId", "fixture" }, { "manifestSignature", "fixture" } } }
            };
            var manifestFile = Path.Combine(staged, ModulePackageVerifier.ManifestPath);
            File.WriteAllText(manifestFile, Json.Serialize(manifestPayload), new UTF8Encoding(false));
            var manifestSha = Sha256(File.ReadAllBytes(manifestFile));

            var stagePayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "installStatus", ModuleStagingInstaller.StagedStatus },
                { "moduleId", module.ModuleId }, { "moduleVersion", module.ModuleVersion }, { "bundlePackagePath", module.PackagePath },
                { "archiveSha256", module.Sha256 }, { "manifestSha256", manifestSha }, { "contractSetVersion", module.ContractSetVersion },
                { "stateSchemaVersion", module.StateSchemaVersion }, { "signingKeyId", "fixture-module-key-v1" },
                { "verificationReceiptSha256", Sha256(Encoding.UTF8.GetBytes("verification:" + module.ModuleId)) },
                { "stagedAtUtc", "2026-08-20T00:00:00.0000000Z" }, { "activationAllowed", false }, { "activeBundleChanged", false }
            };
            var stageFile = Path.Combine(staged, ModuleStagingInstaller.InstallReceiptName);
            File.WriteAllText(stageFile, Json.Serialize(stagePayload), new UTF8Encoding(false));
            var stageSha = Sha256(File.ReadAllBytes(stageFile));

            var dependencyFingerprint = DependencyFingerprint(dependencies, byId);
            var selfFile = SelfReceipt(root, module);
            Directory.CreateDirectory(Path.GetDirectoryName(selfFile));
            var selfPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "status", ModuleStagingSelfTest.PassedStatus },
                { "moduleId", module.ModuleId }, { "moduleVersion", module.ModuleVersion }, { "archiveSha256", module.Sha256 },
                { "contractSetVersion", module.ContractSetVersion }, { "stateSchemaVersion", module.StateSchemaVersion },
                { "stageReceiptSha256", stageSha }, { "manifestSha256", manifestSha }, { "dependencyFingerprint", dependencyFingerprint },
                { "dependencyCount", dependencies.Length }, { "assemblyMetadataLoad", true },
                { "testedAtUtc", "2026-08-20T00:00:01.0000000Z" }, { "activationAllowed", false }, { "activeBundleChanged", false }
            };
            File.WriteAllText(selfFile, Json.Serialize(selfPayload), new UTF8Encoding(false));
        }

        private static string DependencyFingerprint(string[] ids, IDictionary<string, ModuleBundleLockEntry> byId)
        {
            var text = String.Join("\n", ids.OrderBy(value => value, StringComparer.Ordinal).Select(id =>
            {
                var item = byId[id];
                return item.ModuleId + "=" + item.ModuleVersion + "@" + item.Sha256;
            }));
            return Sha256(Encoding.UTF8.GetBytes(text));
        }

        private static string StageDirectory(string root, ModuleBundleLockEntry module)
        {
            return Path.Combine(root, "modules", "staging", module.ModuleId, module.ModuleVersion, module.Sha256);
        }

        private static string StageReceipt(string root, ModuleBundleLockEntry module)
        {
            return Path.Combine(StageDirectory(root, module), ModuleStagingInstaller.InstallReceiptName);
        }

        private static string SelfReceipt(string root, ModuleBundleLockEntry module)
        {
            return Path.Combine(root, "modules", "self-tests", module.ModuleId, module.ModuleVersion, module.Sha256, ModuleStagingSelfTest.ReceiptName);
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
                default: throw new InvalidOperationException("Unknown module fixture: " + moduleId);
            }
        }

        private static string Capitalize(string value)
        {
            return Char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static string NewRoot(string root, string name)
        {
            var result = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(result);
            return result;
        }

        private static void ExpectFailure(Action action, string expectedText)
        {
            try
            {
                action();
            }
            catch (InvalidOperationException error)
            {
                if (error.ToString().IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0) return;
                throw new InvalidOperationException("Expected failure text was not found: " + expectedText + " / " + error.Message, error);
            }
            throw new InvalidOperationException("Expected activation failure did not occur: " + expectedText);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private sealed class Fixture
        {
            public string Root { get; set; }
            public string BundleLockFile { get; set; }
            public string BundleLockSha256 { get; set; }
            public List<ModuleBundleLockEntry> Modules { get; set; }
        }
    }
}
