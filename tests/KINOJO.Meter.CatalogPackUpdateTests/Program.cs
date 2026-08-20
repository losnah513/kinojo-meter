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
    internal static class LauncherVersion
    {
        public const string Channel = LauncherBuildProfile.Channel;
        public const string Current = "1.1.5";
    }

    internal static class CatalogPackUpdateTests
    {
        private const string Host = "josvoltpktvwysrasffq.supabase.co";
        private const string KeyId = "fixture-rsa-3072";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static int _passed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "kcp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            try
            {
                using (var key = new RSACryptoServiceProvider(3072))
                {
                    key.PersistKeyInCsp = false;
                    Run("changed Pack downloads and activates independently", () => ChangedPackOnly(root, key));
                    Run("same approved Pack revalidates without download", () => IdempotentRevalidation(root, key));
                    Run("same version with different SHA fails closed", () => SameVersionDifferentSha(root, key));
                    Run("bad signature leaves no active state", () => BadSignature(root, key));
                    Run("tampered package leaves no active state", () => TamperedPackage(root, key));
                    Run("unrelated active pointer stays byte-identical", () => UnrelatedPointerUnchanged(root, key));
                    Run("extra ZIP entry is rejected", () => ExtraEntryRejected(root, key));
                    Run("lookalike signed URL token is rejected", () => LookalikeTokenRejected(root, key));
                    Run("server order cannot change fixed Pack order", () => FixedPackOrder(root, key));
                }
                Console.WriteLine("Catalog Pack individual update tests passed: " + _passed);
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

        private static void ChangedPackOnly(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "changed");
            var fixture = CreateFixture(CatalogPackInstaller.ClassSkillPackId, "CLASS_SKILL_CATALOG_20260820_01", key, "one", false);
            var handler = new FixtureHandler(fixture.PackageBytes);
            using (var installer = NewInstaller(handler, testRoot, key))
            {
                var result = installer.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
                if (!result.Changed || !result.Downloaded || handler.RequestCount != 1 ||
                    installer.ReadVerifiedActiveState(fixture.Release.PackId) == null)
                    throw new InvalidOperationException("Changed Catalog Pack was not independently activated.");
            }
        }

        private static void IdempotentRevalidation(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "idempotent");
            var fixture = CreateFixture(CatalogPackInstaller.DungeonBossPackId, "METER_CATALOG_20260820_01", key, "one", false);
            using (var first = NewInstaller(new FixtureHandler(fixture.PackageBytes), testRoot, key))
                first.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
            var noDownload = new FixtureHandler((byte[])null);
            using (var second = NewInstaller(noDownload, testRoot, key))
            {
                var result = second.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
                if (result.Changed || result.Downloaded || noDownload.RequestCount != 0)
                    throw new InvalidOperationException("Exact active Catalog Pack was downloaded again.");
            }
        }

        private static void SameVersionDifferentSha(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "conflict");
            var first = CreateFixture(CatalogPackInstaller.BossHpPackId, "BOSS_HP_FINGERPRINT_20260820_01", key, "one", false);
            var conflicting = CreateFixture(CatalogPackInstaller.BossHpPackId, "BOSS_HP_FINGERPRINT_20260820_01", key, "two", false);
            using (var installer = NewInstaller(new FixtureHandler(first.PackageBytes), testRoot, key))
                installer.EnsureInstalledAsync(first.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
            var activeFile = Path.Combine(testRoot, first.Release.PackId, "active.json");
            var before = File.ReadAllBytes(activeFile);
            var handler = new FixtureHandler(conflicting.PackageBytes);
            using (var installer = NewInstaller(handler, testRoot, key))
                ExpectFailure(() => installer.EnsureInstalledAsync(conflicting.Release, Host, CancellationToken.None).GetAwaiter().GetResult(), CatalogPackInstaller.VersionShaConflictCode);
            if (handler.RequestCount != 0 || !before.SequenceEqual(File.ReadAllBytes(activeFile)))
                throw new InvalidOperationException("Version/SHA conflict changed the active pointer or reached the network.");
        }

        private static void BadSignature(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "signature");
            var fixture = CreateFixture(CatalogPackInstaller.ClassSkillPackId, "CLASS_SKILL_CATALOG_20260820_02", key, "one", false);
            fixture.Release.ManifestSignature = Convert.ToBase64String(new byte[384]);
            var handler = new FixtureHandler(fixture.PackageBytes);
            using (var installer = NewInstaller(handler, testRoot, key))
                ExpectFailure(() => installer.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult(), "서명");
            AssertNoActive(testRoot, fixture.Release.PackId);
            if (handler.RequestCount != 0) throw new InvalidOperationException("Bad signature reached the download boundary.");
        }

        private static void TamperedPackage(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "tamper");
            var fixture = CreateFixture(CatalogPackInstaller.DungeonBossPackId, "METER_CATALOG_20260820_02", key, "one", false);
            var tampered = (byte[])fixture.PackageBytes.Clone();
            tampered[tampered.Length / 2] ^= 0x55;
            using (var installer = NewInstaller(new FixtureHandler(tampered), testRoot, key))
                ExpectFailure(() => installer.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult(), "SHA-256");
            AssertNoActive(testRoot, fixture.Release.PackId);
        }

        private static void UnrelatedPointerUnchanged(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "unrelated");
            var dungeon = CreateFixture(CatalogPackInstaller.DungeonBossPackId, "METER_CATALOG_20260820_03", key, "one", false);
            var skill = CreateFixture(CatalogPackInstaller.ClassSkillPackId, "CLASS_SKILL_CATALOG_20260820_03", key, "one", false);
            using (var installer = NewInstaller(new FixtureHandler(dungeon.PackageBytes), testRoot, key))
                installer.EnsureInstalledAsync(dungeon.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
            var dungeonPointer = Path.Combine(testRoot, dungeon.Release.PackId, "active.json");
            var before = File.ReadAllBytes(dungeonPointer);
            using (var installer = NewInstaller(new FixtureHandler(skill.PackageBytes), testRoot, key))
                installer.EnsureInstalledAsync(skill.Release, Host, CancellationToken.None).GetAwaiter().GetResult();
            if (!before.SequenceEqual(File.ReadAllBytes(dungeonPointer)))
                throw new InvalidOperationException("Updating one Pack rewrote another Pack pointer.");
        }

        private static void ExtraEntryRejected(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "extra");
            var fixture = CreateFixture(CatalogPackInstaller.BossHpPackId, "BOSS_HP_FINGERPRINT_20260820_02", key, "one", true);
            using (var installer = NewInstaller(new FixtureHandler(fixture.PackageBytes), testRoot, key))
                ExpectFailure(() => installer.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult(), "파일 집합");
            AssertNoActive(testRoot, fixture.Release.PackId);
        }

        private static void FixedPackOrder(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "order");
            var fixtures = new[]
            {
                CreateFixture(CatalogPackInstaller.BossHpPackId, "BOSS_HP_FINGERPRINT_20260820_03", key, "one", false),
                CreateFixture(CatalogPackInstaller.ClassSkillPackId, "CLASS_SKILL_CATALOG_20260820_04", key, "one", false),
                CreateFixture(CatalogPackInstaller.DungeonBossPackId, "METER_CATALOG_20260820_04", key, "one", false)
            };
            var packages = fixtures.ToDictionary(value => new Uri(value.Release.DownloadUrl).AbsolutePath, value => value.PackageBytes, StringComparer.Ordinal);
            var handler = new FixtureHandler(packages);
            using (var installer = NewInstaller(handler, testRoot, key))
            {
                var authorization = new CatalogPackUpdateAuthorization
                {
                    Authorized = true,
                    Releases = fixtures.Select(value => value.Release).ToList()
                };
                var results = CatalogPackUpdateCoordinator.ApplyAsync(installer, authorization, Host, CancellationToken.None).GetAwaiter().GetResult();
                if (results.Count != 3) throw new InvalidOperationException("Not all approved Catalog Packs were applied.");
            }
            var expected = CatalogPackUpdateCoordinator.PackOrder.Select(value => "/" + value + "/").ToArray();
            if (handler.Paths.Count != 3 || expected.Where((value, index) => handler.Paths[index].IndexOf(value, StringComparison.Ordinal) < 0).Any())
                throw new InvalidOperationException("Server response order changed the fixed Catalog Pack install order.");
        }

        private static void LookalikeTokenRejected(string root, RSACryptoServiceProvider key)
        {
            var testRoot = NewRoot(root, "token");
            var fixture = CreateFixture(CatalogPackInstaller.DungeonBossPackId, "METER_CATALOG_20260820_05", key, "one", false);
            fixture.Release.DownloadUrl = fixture.Release.DownloadUrl.Replace("?token=fixture", "?nottoken=fixture");
            var handler = new FixtureHandler(fixture.PackageBytes);
            using (var installer = NewInstaller(handler, testRoot, key))
                ExpectFailure(() => installer.EnsureInstalledAsync(fixture.Release, Host, CancellationToken.None).GetAwaiter().GetResult(), "signed URL");
            AssertNoActive(testRoot, fixture.Release.PackId);
            if (handler.RequestCount != 0) throw new InvalidOperationException("Lookalike token reached the download boundary.");
        }

        private static CatalogPackInstaller NewInstaller(FixtureHandler handler, string root, RSACryptoServiceProvider key)
        {
            return new CatalogPackInstaller(handler, root, key.ExportParameters(false), KeyId);
        }

        private static Fixture CreateFixture(string packId, string version, RSACryptoServiceProvider key, string marker, bool extraEntry)
        {
            var catalogBytes = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "packId", packId }, { "catalogVersion", version }, { "marker", marker }
            }));
            var catalogSha = Sha256(catalogBytes);
            var installBytes = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 }, { "packId", packId }, { "catalogVersion", version },
                { "files", new[] { new Dictionary<string, object> { { "path", "catalog.json" }, { "size", catalogBytes.Length }, { "sha256", catalogSha } } } }
            }));
            byte[] package;
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    WriteEntry(archive, "catalog.json", catalogBytes);
                    WriteEntry(archive, "install-manifest.json", installBytes);
                    if (extraEntry) WriteEntry(archive, "unexpected.txt", Encoding.UTF8.GetBytes("blocked"));
                }
                package = output.ToArray();
            }
            var sha = Sha256(package);
            var fileName = ExpectedFileName(packId, version);
            var release = new CatalogPackReleaseManifest
            {
                SchemaVersion = 1,
                Channel = LauncherVersion.Channel,
                PackId = packId,
                CatalogVersion = version,
                MinimumLauncherVersion = "1.0.0",
                PackageId = LauncherVersion.Channel + ":" + packId + ":" + version + ":" + sha.Substring(0, 16),
                FileName = fileName,
                FileSize = package.Length,
                Sha256 = sha,
                InstallManifestSha256 = Sha256(installBytes),
                CatalogSha256 = catalogSha,
                DownloadUrl = "https://" + Host + "/storage/v1/object/sign/meter-core-private/catalog-packs/" + LauncherVersion.Channel + "/" + packId + "/" + version + "/" + fileName + "?token=fixture",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                IntegrityMode = CatalogPackReleaseIntegrityVerifier.IntegrityMode,
                SigningKeyId = KeyId,
                ReleaseNote = "fixture"
            };
            release.ManifestSignature = Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(CatalogPackReleaseIntegrityVerifier.Canonicalize(release)),
                CryptoConfig.MapNameToOID("SHA256")));
            return new Fixture { Release = release, PackageBytes = package };
        }

        private static string ExpectedFileName(string packId, string version)
        {
            if (packId == CatalogPackInstaller.DungeonBossPackId) return "KinojoDungeonBossCatalog_" + version + ".zip";
            if (packId == CatalogPackInstaller.ClassSkillPackId) return "KinojoClassSkillCatalog_" + version + ".zip";
            return "KinojoBossHpFingerprint_" + version + ".zip";
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using (var output = entry.Open()) output.Write(content, 0, content.Length);
        }

        private static string Sha256(byte[] value)
        {
            using (var hash = SHA256.Create())
                return String.Concat(hash.ComputeHash(value).Select(item => item.ToString("x2")));
        }

        private static string NewRoot(string root, string name)
        {
            var value = Path.Combine(root, name);
            Directory.CreateDirectory(value);
            return value;
        }

        private static void AssertNoActive(string root, string packId)
        {
            if (File.Exists(Path.Combine(root, packId, "active.json")))
                throw new InvalidOperationException("Failed Catalog Pack changed active state.");
        }

        private static void ExpectFailure(Action action, string expected)
        {
            try { action(); }
            catch (Exception error)
            {
                if (error.ToString().IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0) return;
                throw new InvalidOperationException("Expected failure token was not found: " + expected, error);
            }
            throw new InvalidOperationException("Expected Catalog Pack failure did not occur: " + expected);
        }

        private static void Run(string name, Action action)
        {
            action();
            _passed++;
            Console.WriteLine("PASS " + name);
        }

        private sealed class Fixture
        {
            public CatalogPackReleaseManifest Release { get; set; }
            public byte[] PackageBytes { get; set; }
        }

        private sealed class FixtureHandler : HttpMessageHandler
        {
            private readonly byte[] _single;
            private readonly Dictionary<string, byte[]> _packages;
            public int RequestCount { get; private set; }
            public List<string> Paths { get; private set; }

            public FixtureHandler(byte[] package)
            {
                _single = package;
                Paths = new List<string>();
            }

            public FixtureHandler(Dictionary<string, byte[]> packages)
            {
                _packages = packages;
                Paths = new List<string>();
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                var path = request.RequestUri.AbsolutePath;
                Paths.Add(path);
                byte[] package;
                if (_packages != null) _packages.TryGetValue(path, out package);
                else package = _single;
                if (package == null) throw new InvalidOperationException("Unexpected Catalog Pack network request.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(package)
                });
            }
        }
    }
}
