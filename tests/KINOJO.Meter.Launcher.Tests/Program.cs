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
        public const string Channel = "stable";
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
                Run("valid package", () => VerifyPackage(root, false, false));
                Run("reject unmanaged file", () => ExpectFailure(() => VerifyPackage(root, true, false)));
                Run("reject duplicate archive path", () => ExpectFailure(() => VerifyPackage(root, false, true)));
                Run("reject traversal path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("../outside.txt", false)));
                Run("reject Windows ADS path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("KINOJO.Meter.exe:payload", false)));
                Run("reject rooted path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("C:\\Windows\\system32.dll", false)));
                Run("reject reserved device path", () => ExpectFailure(() => CorePackageInstaller.ValidatePackageRelativePath("NUL.txt", false)));
                Run("accept signed release contract", () => VerifyReleaseContract(true, "KINOJO INFO"));
                Run("reject unsigned release contract", () => ExpectFailure(() => VerifyReleaseContract(false, "KINOJO INFO")));
                Run("reject wrong release publisher", () => ExpectFailure(() => VerifyReleaseContract(true, "NOT KINOJO INFO")));
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

        private static void VerifyPackage(string root, bool unmanaged, bool duplicate)
        {
            var id = Guid.NewGuid().ToString("N");
            var package = Path.Combine(root, id + ".zip");
            var destination = Path.Combine(root, id);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "KINOJO.Meter.exe", Encoding.UTF8.GetBytes("test-core") },
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
                EntryPoint = "KINOJO.Meter.exe",
                Files = managed
            };
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (var pair in files) WriteEntry(archive, pair.Key, pair.Value);
                WriteEntry(archive, "install-manifest.json", Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(installManifest)));
                if (unmanaged) WriteEntry(archive, "unmanaged.dll", new byte[] { 1, 2, 3 });
                if (duplicate) WriteEntry(archive, "version.json", Encoding.UTF8.GetBytes("duplicate"));
            }
            var release = new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = "stable",
                CoreVersion = "0.2.38",
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                EntryPoint = "KINOJO.Meter.exe",
                CodeSignatureRequired = false
            };
            using (var installer = new CorePackageInstaller()) installer.ExtractAndVerifyForTest(package, destination, release);
        }

        private static void VerifyReleaseContract(bool codeSignatureRequired, string publisherSubject)
        {
            CorePackageInstaller.ValidateReleaseForTest(new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = "stable",
                CoreVersion = "0.2.38",
                FileName = "KinojoMeterCore_0.2.38_x64.zip",
                FileSize = 1,
                Sha256 = new String('a', 64),
                DownloadUrl = "https://josvoltpktvwysrasffq.supabase.co/storage/v1/object/sign/meter-core-private/stable/0.2.38/package?token=test",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
                EntryPoint = "KINOJO.Meter.exe",
                CodeSignatureRequired = codeSignatureRequired,
                PublisherSubject = publisherSubject
            }, "josvoltpktvwysrasffq.supabase.co");
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
