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
    internal static class ModuleBundleRollbackTests
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
            var root = Path.Combine(Path.GetTempPath(), "k58-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                Run("successful readiness keeps new Bundle and durable previous snapshot", () => SuccessfulReadinessKeepsNewBundle(root));
                Run("readiness failure restores exact previous Bundle automatically", () => ReadinessFailureRestoresPrevious(root));
                Run("delayed runtime failure rolls back through durable plan", () => DelayedRuntimeFailureRollsBack(root));
                Run("tampered previous snapshot fails closed", () => TamperedPreviousSnapshotFailsClosed(root));
                Run("first modular activation failure leaves no active module pointer", () => FirstActivationFailureClearsPointer(root));
                Run("stale failed revision cannot roll back unrelated active Bundle", () => StaleRollbackRequestRejected(root));
                Run("tampered previous staged file blocks rollback and clears failed active pointer", () => TamperedPreviousStageFailsClosed(root));
                Console.WriteLine("Module Bundle rollback tests passed: " + _passed);
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

        private static void SuccessfulReadinessKeepsNewBundle(string root)
        {
            var testRoot = NewRoot(root, "success");
            var moduleRoot = Path.Combine(testRoot, "modules");
            ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");

            var result = ModuleBundleRollback.ActivateAndVerifyForTest(
                Request(next, "B000048"),
                state =>
                {
                    if (state == null || state.BundleRevision != "B000049")
                        throw new InvalidOperationException("Expected B000049 readiness target.");
                },
                moduleRoot);

            if (result == null || result.RolledBack || result.ActiveBundleRevision != "B000049")
                throw new InvalidOperationException("Successful readiness did not keep the new Bundle active.");
            if (ReadActive(moduleRoot).BundleRevision != "B000049")
                throw new InvalidOperationException("New Bundle active pointer readback mismatch.");

            var previous = Json.Deserialize<ActiveModuleBundleState>(File.ReadAllText(Path.Combine(moduleRoot, "rollback", "previous-bundle.json")));
            if (previous == null || previous.BundleRevision != "B000048")
                throw new InvalidOperationException("Durable previous Bundle snapshot was not preserved.");
            var plan = Json.Deserialize<ModuleBundleRollbackPlan>(File.ReadAllText(Path.Combine(moduleRoot, "rollback", "rollback-plan.json")));
            if (plan == null || plan.CandidateBundleRevision != "B000049" || plan.PreviousBundleRevision != "B000048" || !plan.PreviousAvailable || plan.ReleasePointerChanged)
                throw new InvalidOperationException("Rollback plan does not bind B000049 to B000048.");
        }

        private static void ReadinessFailureRestoresPrevious(string root)
        {
            var testRoot = NewRoot(root, "readyfail");
            var moduleRoot = Path.Combine(testRoot, "modules");
            ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");

            var result = ModuleBundleRollback.ActivateAndVerifyForTest(
                Request(next, "B000048"),
                state =>
                {
                    if (state.BundleRevision == "B000049")
                        throw new InvalidOperationException("synthetic readiness failure");
                    if (state.BundleRevision != "B000048")
                        throw new InvalidOperationException("Unexpected rollback readiness target.");
                },
                moduleRoot);

            if (result == null || !result.RolledBack || result.Status != ModuleBundleRollback.RolledBackStatus || result.ActiveBundleRevision != "B000048")
                throw new InvalidOperationException("Readiness failure did not return a successful rollback result.");
            if (ReadActive(moduleRoot).BundleRevision != "B000048")
                throw new InvalidOperationException("Previous Bundle was not restored to active-bundle.json.");

            var receipt = Json.Deserialize<ModuleBundleRollbackReceipt>(File.ReadAllText(Path.Combine(moduleRoot, "rollback", "last-rollback.json")));
            if (receipt == null || receipt.Status != ModuleBundleRollback.RolledBackStatus || receipt.FailedBundleRevision != "B000049" ||
                receipt.RestoredBundleRevision != "B000048" || receipt.ReleasePointerChanged || !receipt.ActiveBundleChanged)
                throw new InvalidOperationException("Rollback receipt does not record the exact previous Bundle restoration.");
        }

        private static void DelayedRuntimeFailureRollsBack(string root)
        {
            var testRoot = NewRoot(root, "delayed");
            var moduleRoot = Path.Combine(testRoot, "modules");
            ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");
            ModuleBundleRollback.ActivateAndVerifyForTest(Request(next, "B000048"), state => { }, moduleRoot);

            var result = ModuleBundleRollback.RollbackCurrentToPreviousForTest(moduleRoot, "B000049", "RUNTIME_CRASH");
            if (result == null || !result.RolledBack || result.ActiveBundleRevision != "B000048" || ReadActive(moduleRoot).BundleRevision != "B000048")
                throw new InvalidOperationException("Delayed runtime failure did not restore B000048.");
        }

        private static void TamperedPreviousSnapshotFailsClosed(string root)
        {
            var testRoot = NewRoot(root, "snapshot-tamper");
            var moduleRoot = Path.Combine(testRoot, "modules");
            ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");

            ExpectFailure(() => ModuleBundleRollback.ActivateAndVerifyForTest(
                Request(next, "B000048"),
                state =>
                {
                    if (state.BundleRevision == "B000049")
                    {
                        File.AppendAllText(Path.Combine(moduleRoot, "rollback", "previous-bundle.json"), " ", Encoding.UTF8);
                        throw new InvalidOperationException("synthetic readiness failure after snapshot tamper");
                    }
                },
                moduleRoot), "자동 복구가 모두 실패");

            if (File.Exists(Path.Combine(moduleRoot, "active-bundle.json")))
                throw new InvalidOperationException("Failed new Bundle remained active after rollback snapshot tamper.");
        }

        private static void FirstActivationFailureClearsPointer(string root)
        {
            var testRoot = NewRoot(root, "first");
            var moduleRoot = Path.Combine(testRoot, "modules");
            var first = CreateFixture(testRoot, "B000048", "B000047", "0.3.0");

            ExpectFailure(() => ModuleBundleRollback.ActivateAndVerifyForTest(
                Request(first, "B000047"),
                state => { throw new InvalidOperationException("synthetic first activation failure"); },
                moduleRoot), "자동 복구가 모두 실패");

            if (File.Exists(Path.Combine(moduleRoot, "active-bundle.json")))
                throw new InvalidOperationException("First failed modular activation left an active Bundle pointer.");
            var receipt = Json.Deserialize<ModuleBundleRollbackReceipt>(File.ReadAllText(Path.Combine(moduleRoot, "rollback", "last-rollback.json")));
            if (receipt == null || receipt.Status != ModuleBundleRollback.RollbackUnavailableStatus || receipt.ReleasePointerChanged)
                throw new InvalidOperationException("First activation failure did not record rollback-unavailable state.");
        }

        private static void StaleRollbackRequestRejected(string root)
        {
            var testRoot = NewRoot(root, "stale");
            var moduleRoot = Path.Combine(testRoot, "modules");
            ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");
            ModuleBundleRollback.ActivateAndVerifyForTest(Request(next, "B000048"), state => { }, moduleRoot);
            var before = File.ReadAllBytes(Path.Combine(moduleRoot, "active-bundle.json"));

            ExpectFailure(
                () => ModuleBundleRollback.RollbackCurrentToPreviousForTest(moduleRoot, "B000050", "RUNTIME_CRASH"),
                "rollback plan");
            if (!before.SequenceEqual(File.ReadAllBytes(Path.Combine(moduleRoot, "active-bundle.json"))))
                throw new InvalidOperationException("Stale rollback request changed the active Bundle.");
        }

        private static void TamperedPreviousStageFailsClosed(string root)
        {
            var testRoot = NewRoot(root, "stage-tamper");
            var moduleRoot = Path.Combine(testRoot, "modules");
            var previous = ActivateInitial(testRoot);
            var next = CreateFixture(testRoot, "B000049", "B000048", "0.3.1");
            ModuleBundleRollback.ActivateAndVerifyForTest(Request(next, "B000048"), state => { }, moduleRoot);

            var previousCombat = previous.Modules.First(value => value.ModuleId == "combat");
            File.AppendAllText(Path.Combine(StageDirectory(testRoot, previousCombat), Primary("combat")), "tamper", Encoding.UTF8);
            ExpectFailure(
                () => ModuleBundleRollback.RollbackCurrentToPreviousForTest(moduleRoot, "B000049", "RUNTIME_CRASH"),
                "무결성");
            if (File.Exists(Path.Combine(moduleRoot, "active-bundle.json")))
                throw new InvalidOperationException("Failed active Bundle pointer survived an invalid rollback target.");
        }

        private static Fixture ActivateInitial(string testRoot)
        {
            var fixture = CreateFixture(testRoot, "B000048", "B000047", "0.3.0");
            var result = ModuleBundleActivator.ActivateForTest(Request(fixture, "B000047"), Path.Combine(testRoot, "modules"));
            if (result == null || !result.Changed || result.BundleRevision != "B000048")
                throw new InvalidOperationException("Initial B000048 activation fixture failed.");
            return fixture;
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

        private static Fixture CreateFixture(string root, string revision, string parent, string version)
        {
            var moduleRoot = Path.Combine(root, "modules");
            Directory.CreateDirectory(Path.Combine(moduleRoot, "staging"));
            Directory.CreateDirectory(Path.Combine(moduleRoot, "self-tests"));

            var modules = new List<ModuleBundleLockEntry>();
            foreach (var id in ModuleIds)
            {
                var stateSchema = (id == "combat" || id == "encounter" || id == "sync" || id == "shell") ? 1 : 0;
                modules.Add(new ModuleBundleLockEntry
                {
                    ModuleId = id,
                    ModuleVersion = version,
                    Sha256 = Sha256(Encoding.UTF8.GetBytes("archive:" + id + ":" + version)),
                    ContractSetVersion = 1,
                    StateSchemaVersion = stateSchema,
                    PackagePath = "modules/" + id + "/" + version + "/KINOJO.Meter." + Capitalize(id) + "." + version + ".zip"
                });
            }

            var byId = modules.ToDictionary(value => value.ModuleId, StringComparer.Ordinal);
            foreach (var module in modules)
                WriteStagedAndSelfTest(root, module, byId);

            var lockPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "productVersion", version },
                { "channel", "staging" },
                { "bundleRevision", revision },
                { "parentBundleRevision", parent },
                { "sourceCommit", new String('a', 40) },
                { "contractSetVersion", 1 },
                { "moduleSetHash", ModuleBundleActivator.ComputeModuleSetHashForTest(modules) },
                { "activationMode", ModuleBundleActivator.RequiredActivationMode },
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
            var primary = Primary(module.ModuleId);
            var primaryBytes = Encoding.UTF8.GetBytes("fixture:" + module.ModuleId + ":" + module.ModuleVersion);
            File.WriteAllBytes(Path.Combine(staged, primary), primaryBytes);

            var dependencies = Dependencies[module.ModuleId];
            var manifestPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "manifestType", ModulePackageVerifier.ManifestType },
                { "moduleId", module.ModuleId },
                { "moduleVersion", module.ModuleVersion },
                { "sourceCommit", new String('b', 40) },
                { "targetPlatform", ModulePackageVerifier.TargetPlatform },
                { "primaryArtifact", new Dictionary<string, object>
                    {
                        { "path", primary },
                        { "kind", module.ModuleId == "shell" ? "EXE" : "DLL" },
                        { "loadTarget", "TEST" }
                    }
                },
                { "dependencyModuleIds", dependencies },
                { "contractSetVersion", module.ContractSetVersion },
                { "state", new Dictionary<string, object>
                    {
                        { "mode", module.StateSchemaVersion == 0 ? "NONE" : "OWNED" },
                        { "stateSchemaVersion", module.StateSchemaVersion },
                        { "minimumReadableSchema", 0 },
                        { "rollbackReadableByPrevious", true },
                        { "migrationRequired", false }
                    }
                },
                { "files", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "path", primary },
                            { "size", primaryBytes.Length },
                            { "sha256", Sha256(primaryBytes) },
                            { "role", "PRIMARY" }
                        }
                    }
                },
                { "integrity", new Dictionary<string, object>
                    {
                        { "mode", "RSA_SHA256" }, { "signingKeyId", "fixture" }, { "manifestSignature", "fixture" }
                    }
                }
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
                { "verificationReceiptSha256", Sha256(Encoding.UTF8.GetBytes("verification:" + module.ModuleId + ":" + module.ModuleVersion)) },
                { "stagedAtUtc", "2026-08-20T00:00:00.0000000Z" }, { "activationAllowed", false }, { "activeBundleChanged", false }
            };
            var stageFile = Path.Combine(staged, ModuleStagingInstaller.InstallReceiptName);
            File.WriteAllText(stageFile, Json.Serialize(stagePayload), new UTF8Encoding(false));
            var stageSha = Sha256(File.ReadAllBytes(stageFile));

            var selfFile = SelfReceipt(root, module);
            Directory.CreateDirectory(Path.GetDirectoryName(selfFile));
            var selfPayload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "status", ModuleStagingSelfTest.PassedStatus },
                { "moduleId", module.ModuleId }, { "moduleVersion", module.ModuleVersion }, { "archiveSha256", module.Sha256 },
                { "contractSetVersion", module.ContractSetVersion }, { "stateSchemaVersion", module.StateSchemaVersion },
                { "stageReceiptSha256", stageSha }, { "manifestSha256", manifestSha },
                { "dependencyFingerprint", DependencyFingerprint(dependencies, byId) }, { "dependencyCount", dependencies.Length },
                { "assemblyMetadataLoad", true }, { "testedAtUtc", "2026-08-20T00:00:01.0000000Z" },
                { "activationAllowed", false }, { "activeBundleChanged", false }
            };
            File.WriteAllText(selfFile, Json.Serialize(selfPayload), new UTF8Encoding(false));
        }

        private static ActiveModuleBundleState ReadActive(string moduleRoot)
        {
            var path = Path.Combine(moduleRoot, "active-bundle.json");
            if (!File.Exists(path)) return null;
            return Json.Deserialize<ActiveModuleBundleState>(File.ReadAllText(path));
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
            var value = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(value);
            return value;
        }

        private static void ExpectFailure(Action action, string expectedText)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                if (error.ToString().IndexOf(expectedText, StringComparison.OrdinalIgnoreCase) >= 0) return;
                throw new InvalidOperationException("Expected failure text was not found: " + expectedText + " / " + error.Message, error);
            }
            throw new InvalidOperationException("Expected rollback failure did not occur: " + expectedText);
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
