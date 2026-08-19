using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed partial class UiAssetPackInstaller
    {
        private UiAssetInstallManifest ExtractAndVerify(string packagePath, string destination, UiAssetReleaseManifest release)
        {
            Directory.CreateDirectory(destination);
            var destinationRoot = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
            var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedBytes = 0;
            var entryCount = 0;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    entryCount += 1;
                    if (entryCount > MaximumArchiveEntries) throw new InvalidOperationException("UI Asset Pack 파일 수가 허용 범위를 초과했습니다.");
                    var relative = ValidatePackageRelativePath(entry.FullName, String.IsNullOrEmpty(entry.Name));
                    if (String.IsNullOrWhiteSpace(relative)) continue;
                    var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("UI Asset Pack에 잘못된 파일 경로가 있습니다.");
                    if (String.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    if (!archivePaths.Add(relative)) throw new InvalidOperationException("UI Asset Pack에 중복 파일 경로가 있습니다.");
                    extractedBytes += entry.Length;
                    if (entry.Length < 0 || extractedBytes > MaximumExtractedBytes)
                        throw new InvalidOperationException("UI Asset Pack 압축 해제 크기가 허용 범위를 초과했습니다.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }

            var installManifestPath = Path.Combine(destination, "install-manifest.json");
            var themePath = Path.Combine(destination, "theme.json");
            if (!File.Exists(installManifestPath) || !File.Exists(themePath))
                throw new InvalidOperationException("UI Asset Pack install-manifest.json 또는 theme.json이 없습니다.");
            if (!String.Equals(Sha256(installManifestPath), release.InstallManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI Asset install manifest SHA-256이 일치하지 않습니다.");
            if (!String.Equals(Sha256(themePath), release.ThemeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI Asset theme SHA-256이 일치하지 않습니다.");

            var manifest = _json.Deserialize<UiAssetInstallManifest>(File.ReadAllText(installManifestPath, Encoding.UTF8));
            if (manifest == null || manifest.SchemaVersion != 1 || !String.Equals(manifest.PackId, UiAssetReleaseIntegrityVerifier.PackId, StringComparison.Ordinal) ||
                !String.Equals(manifest.Version, release.Version, StringComparison.Ordinal) || String.IsNullOrWhiteSpace(manifest.ThemeId) ||
                manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > MaximumArchiveEntries)
                throw new InvalidOperationException("UI Asset install manifest 계약이 release와 일치하지 않습니다.");
            var duplicates = manifest.Files.GroupBy(item => item == null ? "" : item.Path ?? "", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
            if (duplicates) throw new InvalidOperationException("UI Asset install manifest에 중복 파일이 있습니다.");
            foreach (var item in manifest.Files) VerifyManagedFile(destination, item);
            var managedPaths = new HashSet<string>(manifest.Files.Select(item => NormalizeRelativePath(item.Path)), StringComparer.OrdinalIgnoreCase)
            {
                "install-manifest.json"
            };
            var actualPaths = new HashSet<string>(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(path.Substring(destinationRoot.Length))), StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(managedPaths))
                throw new InvalidOperationException("UI Asset Pack에 manifest로 관리되지 않는 파일이 있습니다.");

            var themeObject = _json.DeserializeObject(File.ReadAllText(themePath, Encoding.UTF8)) as Dictionary<string, object>;
            if (themeObject == null || Number(themeObject, "schemaVersion") != 1 ||
                !String.Equals(Text(themeObject, "packId"), UiAssetReleaseIntegrityVerifier.PackId, StringComparison.Ordinal) ||
                !String.Equals(Text(themeObject, "version"), release.Version, StringComparison.Ordinal) ||
                !String.Equals(Text(themeObject, "themeId"), manifest.ThemeId, StringComparison.Ordinal) ||
                !String.Equals(Text(themeObject, "fallback"), "EMBEDDED_CORE", StringComparison.Ordinal))
                throw new InvalidOperationException("UI Asset theme 계약이 올바르지 않습니다.");
            return manifest;
        }

        private void VerifyInstalledFiles(ActiveUiAssetState state, UiAssetReleaseManifest release)
        {
            if (!IsActiveStateUsable(state)) throw new InvalidOperationException("설치된 UI Asset 활성 상태가 올바르지 않습니다.");
            UiAssetReleaseIntegrityVerifier.Verify(release);
            var expectedPath = Path.GetFullPath(LauncherPaths.UiAssetVersionDirectory(state.Version));
            var actualPath = Path.GetFullPath(state.InstalledPath ?? "");
            if (!String.Equals(expectedPath.TrimEnd(Path.DirectorySeparatorChar), actualPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(expectedPath))
                throw new InvalidOperationException("UI Asset 활성 슬롯 경로가 올바르지 않습니다.");
            var manifestPath = Path.Combine(expectedPath, "install-manifest.json");
            var themePath = Path.Combine(expectedPath, "theme.json");
            if (!File.Exists(manifestPath) || !File.Exists(themePath) ||
                !String.Equals(Sha256(manifestPath), release.InstallManifestSha256, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Sha256(themePath), release.ThemeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치된 UI Asset manifest/theme SHA-256이 일치하지 않습니다.");
            var manifest = _json.Deserialize<UiAssetInstallManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > MaximumArchiveEntries ||
                !String.Equals(manifest.PackId, state.PackId, StringComparison.Ordinal) || !String.Equals(manifest.Version, state.Version, StringComparison.Ordinal) ||
                !String.Equals(manifest.ThemeId, state.ThemeId, StringComparison.Ordinal))
                throw new InvalidOperationException("설치된 UI Asset manifest가 활성 상태와 일치하지 않습니다.");
            foreach (var item in manifest.Files) VerifyManagedFile(expectedPath, item);
            var expectedPaths = new HashSet<string>(manifest.Files.Select(item => NormalizeRelativePath(item.Path)), StringComparer.OrdinalIgnoreCase) { "install-manifest.json" };
            var root = Path.GetFullPath(expectedPath + Path.DirectorySeparatorChar);
            var actualPaths = new HashSet<string>(Directory.GetFiles(expectedPath, "*", SearchOption.AllDirectories).Select(path => NormalizeRelativePath(path.Substring(root.Length))), StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(expectedPaths)) throw new InvalidOperationException("설치된 UI Asset 슬롯에 비관리 파일이 있습니다.");
        }

        private static void VerifyManagedFile(string root, UiAssetInstallFile item)
        {
            if (item == null) throw new InvalidOperationException("UI Asset install manifest 파일 행이 없습니다.");
            var relative = ValidateManagedRelativePath(item.Path);
            var baseRoot = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new InvalidOperationException("UI Asset 파일이 누락됐습니다: " + relative);
            var info = new FileInfo(path);
            if (info.Length != item.Size || !String.Equals(Sha256(path), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("UI Asset 파일 무결성 검증에 실패했습니다: " + relative);
        }

        private static string ValidateManagedRelativePath(string value)
        {
            var path = NormalizeRelativePath(value);
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal) || path.Split('/').Any(part => part == "" || part == "." || part == ".."))
                throw new InvalidOperationException("UI Asset 상대 경로가 올바르지 않습니다.");
            if (path.IndexOf("area4", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("폐기된 Area4 자산은 UI Asset Pack에 포함할 수 없습니다.");
            var allowed = String.Equals(path, "theme.json", StringComparison.OrdinalIgnoreCase) ||
                (path.StartsWith("fonts/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) ||
                ((path.StartsWith("icons/status/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("icons/boss/", StringComparison.OrdinalIgnoreCase)) && path.EndSWith(".png", StringComparison.OrdinalIgnoreCase));
            if (!allowed && path.StartsWith("icons/classes/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split('/');
                allowed = parts.Length == 4 && new[] { "normal", "self", "leader", "self-leader" }.Contains(parts[2], StringComparer.OrdinalIgnoreCase);
            }
            if (!allowed) throw new InvalidOperationException("UI Asset Pack 범위를 벗어난 파일입니다: " + path);
            return path;
        }

        private static string ValidatePackageRelativePath(string value, bool directory)
        {
            var path = NormalizeRelativePath(value).TrimEnd('/');
            if (path.Length == 0) return "";
            if (path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal) || path.Split('/').Any(part => part == "" || part == "." || part == ".."))
                throw new InvalidOperationException("UI Asset ZIP 상대 경로가 올바르지 않습니다.");
            if (path.IndexOf("area4", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("폐기된 Area4 경로는 UI Asset ZIP에 포함할 수 없습니다.");
            if (!directory && !String.Equals(path, "install-manifest.json", StringComparison.OrdinalIgnoreCase)) ValidateManagedRelativePath(path);
            return path;
        }
    }
}
