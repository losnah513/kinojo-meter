using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KinojoMeterLauncher
{
    internal static class LauncherPaths
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LauncherBuildProfile.DataFolderName);
        public static readonly string LauncherData = Path.Combine(Root, "launcher");
        public static readonly string CoreRoot = Path.Combine(Root, "core");
        public static readonly string CoreVersions = Path.Combine(CoreRoot, "versions");
        public static readonly string CoreStaging = Path.Combine(CoreRoot, "staging");
        public static readonly string ActiveCoreFile = Path.Combine(CoreRoot, "active.json");
        public static readonly string UiAssetRoot = Path.Combine(Root, "ui-assets");
        public static readonly string UiAssetVersions = Path.Combine(UiAssetRoot, "versions");
        public static readonly string UiAssetStaging = Path.Combine(UiAssetRoot, "staging");
        public static readonly string ActiveUiAssetFile = Path.Combine(UiAssetRoot, "active.json");
        public static readonly string ModuleRoot = Path.Combine(Root, "modules");
        public static readonly string ModulePackageCache = Path.Combine(ModuleRoot, "cache");
        public static readonly string ModuleStaging = Path.Combine(ModuleRoot, "staging");
        public static readonly string ModuleSelfTests = Path.Combine(ModuleRoot, "self-tests");
        public static readonly string DeviceIdFile = Path.Combine(LauncherData, "device.dat");
        public static readonly string LauncherContentCacheFile = Path.Combine(LauncherData, "content-cache.json");
        public static readonly string LauncherContentReadFile = Path.Combine(LauncherData, "content-read.json");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(LauncherData);
            Directory.CreateDirectory(CoreVersions);
            Directory.CreateDirectory(CoreStaging);
            Directory.CreateDirectory(UiAssetVersions);
            Directory.CreateDirectory(UiAssetStaging);
            Directory.CreateDirectory(ModulePackageCache);
            Directory.CreateDirectory(ModuleStaging);
            Directory.CreateDirectory(ModuleSelfTests);
        }

        public static string GetOrCreateInstallationId()
        {
            EnsureDirectories();
            if (File.Exists(DeviceIdFile))
            {
                try
                {
                    var protectedBytes = File.ReadAllBytes(DeviceIdFile);
                    var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    var existing = Encoding.UTF8.GetString(bytes).Trim();
                    Guid parsed;
                    if (Guid.TryParse(existing, out parsed)) return parsed.ToString("N");
                }
                catch { }
            }

            var created = Guid.NewGuid().ToString("N");
            var raw = Encoding.UTF8.GetBytes(created);
            var encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            var temporary = DeviceIdFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(DeviceIdFile)) File.Replace(temporary, DeviceIdFile, null);
            else File.Move(temporary, DeviceIdFile);
            return created;
        }

        public static string VersionDirectory(string version)
        {
            if (String.IsNullOrWhiteSpace(version) || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$"))
                throw new InvalidOperationException("Core version 형식이 올바르지 않습니다.");
            return Path.Combine(CoreVersions, version);
        }

        public static string UiAssetVersionDirectory(string version)
        {
            if (String.IsNullOrWhiteSpace(version) || !System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$"))
                throw new InvalidOperationException("UI Asset Pack version 형식이 올바르지 않습니다.");
            return Path.Combine(UiAssetVersions, version);
        }
    }
}
