using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace KinojoMeterLauncher
{
    internal sealed class CorePackageInstaller : IDisposable
    {
        private const long MaximumPackageBytes = 64L * 1024L * 1024L;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public ActiveCoreState ReadActiveState()
        {
            try
            {
                if (!File.Exists(LauncherPaths.ActiveCoreFile)) return null;
                var value = _json.Deserialize<ActiveCoreState>(File.ReadAllText(LauncherPaths.ActiveCoreFile, Encoding.UTF8));
                return IsActiveStateUsable(value) ? value : null;
            }
            catch { return null; }
        }

        public async Task<CoreInstallResult> EnsureInstalledAsync(
            CoreReleaseManifest release,
            string expectedProjectHost,
            IProgress<int> progress,
            CancellationToken cancellationToken)
        {
            ValidateRelease(release, expectedProjectHost);
            LauncherPaths.EnsureDirectories();
            var current = ReadActiveState();
            if (current != null)
            {
                try
                {
                    var currentRelease = ReleaseFromState(current);
                    var matchesRelease = String.Equals(current.CoreVersion, release.CoreVersion, StringComparison.Ordinal) &&
                        String.Equals(current.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(current.ManifestSignature, release.ManifestSignature, StringComparison.Ordinal);
                    VerifyInstalledFiles(current, matchesRelease ? release : currentRelease);
                    if (matchesRelease)
                    {
                        progress?.Report(100);
                        return new CoreInstallResult { Active = current, Previous = current, Changed = false };
                    }
                }
                catch
                {
                    // A damaged or untrusted slot must never be retained as a rollback target.
                    current = null;
                }
            }

            if (Process.GetProcessesByName(LauncherBuildProfile.CoreProcessName).Any(process => !process.HasExited))
                throw new InvalidOperationException("실행 중인 KINOJO Meter를 종료한 뒤 업데이트해 주세요.");

            var transactionId = Guid.NewGuid().ToString("N");
            var transactionRoot = Path.Combine(LauncherPaths.CoreStaging, transactionId);
            var packagePath = Path.Combine(transactionRoot, release.FileName);
            var extractedPath = Path.Combine(transactionRoot, "extracted");
            Directory.CreateDirectory(transactionRoot);
            try
            {
                await DownloadAsync(release, expectedProjectHost, packagePath, progress, cancellationToken).ConfigureAwait(false);
                var target = LauncherPaths.VersionDirectory(release.CoreVersion);
                ExtractAndVerify(packagePath, extractedPath, release);
                // Stable versions are immutable. A slot that is not already linked to the
                // current Server package hash is stale or untrusted and is never merged.
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(extractedPath, target);

                var active = new ActiveCoreState
                {
                    SchemaVersion = 2,
                    Channel = release.Channel,
                    CoreVersion = release.CoreVersion,
                    MinimumCoreVersion = release.MinimumCoreVersion,
                    MinimumLauncherVersion = release.MinimumLauncherVersion,
                    PackageId = release.PackageId,
                    FileName = release.FileName,
                    FileSize = release.FileSize,
                    EntryPoint = release.EntryPoint,
                    InstalledPath = target,
                    ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
                    PackageSha256 = release.Sha256,
                    InstallManifestSha256 = release.InstallManifestSha256,
                    Mandatory = release.Mandatory,
                    IntegrityMode = release.IntegrityMode,
                    SigningKeyId = release.SigningKeyId,
                    ManifestSignature = release.ManifestSignature
                };
                WriteActiveState(active);
                progress?.Report(100);
                return new CoreInstallResult { Active = active, Previous = current, Changed = true };
            }
            finally
            {
                try { if (Directory.Exists(transactionRoot)) Directory.Delete(transactionRoot, true); }
                catch { }
            }
        }

        public async Task LaunchAndVerifyAsync(CoreInstallResult install, LauncherLoginResult login, string installationId)
        {
            if (install == null || !IsActiveStateUsable(install.Active)) throw new InvalidOperationException("실행할 Core가 준비되지 않았습니다.");
            if (login == null || String.IsNullOrWhiteSpace(login.SessionToken)) throw new InvalidOperationException("Core에 전달할 Server 세션이 없습니다.");
            try
            {
                await StartCoreAndWaitForReadyAsync(install.Active, login, installationId).ConfigureAwait(false);
            }
            catch
            {
                if (!install.Changed || install.Previous == null || !IsActiveStateUsable(install.Previous))
                {
                    if (install.Changed)
                    {
                        try { if (File.Exists(LauncherPaths.ActiveCoreFile)) File.Delete(LauncherPaths.ActiveCoreFile); }
                        catch { }
                    }
                    throw;
                }

                WriteActiveState(install.Previous);
                try
                {
                    await StartCoreAndWaitForReadyAsync(install.Previous, login, installationId).ConfigureAwait(false);
                    install.Active = install.Previous;
                    install.Changed = false;
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException("새 Core 시작과 이전 버전 자동 복구가 모두 실패했습니다.", rollbackError);
                }
            }
        }

        private async Task StartCoreAndWaitForReadyAsync(ActiveCoreState state, LauncherLoginResult login, string installationId)
        {
            // Re-check the signed release contract and every installed file immediately
            // before both normal startup and an automatic rollback startup.
            VerifyInstalledFiles(state, ReleaseFromState(state));
            var executable = Path.Combine(state.InstalledPath, state.EntryPoint);
            var envelope = _json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "sessionToken", login.SessionToken },
                { "installationId", installationId ?? "" },
                { "launcherVersion", LauncherVersion.Current },
                { "coreVersion", state.CoreVersion },
                { "channel", LauncherVersion.Channel },
                { "apiEndpoint", "https://josvoltpktvwysrasffq.supabase.co/functions/v1/" + LauncherBuildProfile.FunctionName },
                { "issuedAtUtc", DateTime.UtcNow.ToString("o") },
                { "account", login.Account ?? new Dictionary<string, object>() },
                { "characters", login.Characters ?? new List<Dictionary<string, object>>() }
            });
            var encodedEnvelope = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = state.InstalledPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = false
            };
            Process process = null;
            try
            {
                process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("KINOJO Meter Core를 시작하지 못했습니다.");
                await process.StandardInput.WriteLineAsync("KINOJO_LAUNCHER_SESSION_V1 " + encodedEnvelope).ConfigureAwait(false);
                process.StandardInput.Close();
                var readyTask = process.StandardOutput.ReadLineAsync();
                var completed = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(12))).ConfigureAwait(false);
                if (completed != readyTask)
                    throw new InvalidOperationException("Core가 제한 시간 안에 시작 준비를 완료하지 못했습니다.");
                var ready = await readyTask.ConfigureAwait(false);
                if (!String.Equals(ready, "KINOJO_CORE_READY_V1 " + state.CoreVersion, StringComparison.Ordinal))
                {
                    var detail = process.HasExited ? " 종료 코드: " + process.ExitCode : "";
                    throw new InvalidOperationException("Core 시작 준비 신호가 올바르지 않습니다." + detail);
                }
            }
            catch
            {
                try
                {
                    if (process != null && !process.HasExited) process.Kill();
                }
                catch { }
                throw;
            }
            finally
            {
                if (process != null) process.Dispose();
            }
        }

        private async Task DownloadAsync(CoreReleaseManifest release, string expectedProjectHost, string target, IProgress<int> progress, CancellationToken token)
        {
            var uri = RequireApprovedDownloadUri(release.DownloadUrl, expectedProjectHost);
            using (var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage == null ? uri : response.RequestMessage.RequestUri;
                if (finalUri == null || !String.Equals(finalUri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Core 다운로드가 허용되지 않은 호스트로 이동했습니다.");
                var announced = response.Content.Headers.ContentLength;
                if (announced.HasValue && announced.Value != release.FileSize)
                    throw new InvalidOperationException("Core 패키지 응답 크기가 Server manifest와 다릅니다.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                        if (read <= 0) break;
                        total += read;
                        if (total > release.FileSize || total > MaximumPackageBytes)
                            throw new InvalidOperationException("Core 패키지가 허용 크기를 초과했습니다.");
                        await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                        progress?.Report((int)Math.Min(95, total * 95L / Math.Max(1, release.FileSize)));
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (total != release.FileSize) throw new InvalidOperationException("Core 패키지 다운로드 크기가 일치하지 않습니다.");
                }
            }
            var hash = Sha256(target);
            if (!String.Equals(hash, release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Core 패키지 SHA-256 검증에 실패했습니다.");
        }

        private void ExtractAndVerify(string packagePath, string destination, CoreReleaseManifest release)
        {
            const long maximumExtractedBytes = 512L * 1024L * 1024L;
            const int maximumArchiveEntries = 2048;
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
                    if (entryCount > maximumArchiveEntries)
                        throw new InvalidOperationException("Core 패키지 파일 수가 허용 범위를 초과했습니다.");
                    var relative = ValidatePackageRelativePath(entry.FullName, String.IsNullOrEmpty(entry.Name));
                    if (String.IsNullOrWhiteSpace(relative)) continue;
                    var target = Path.GetFullPath(Path.Combine(destination, relative));
                    if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Core 패키지에 잘못된 파일 경로가 있습니다.");
                    if (String.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    if (!archivePaths.Add(relative)) throw new InvalidOperationException("Core 패키지에 중복 파일 경로가 있습니다.");
                    extractedBytes += entry.Length;
                    if (entry.Length < 0 || extractedBytes > maximumExtractedBytes)
                        throw new InvalidOperationException("Core 패키지 압축 해제 크기가 허용 범위를 초과했습니다.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }

            var installManifestPath = Path.Combine(destination, "install-manifest.json");
            if (!File.Exists(installManifestPath)) throw new InvalidOperationException("Core install-manifest.json이 없습니다.");
            if (!String.Equals(Sha256(installManifestPath), release.InstallManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Core install manifest의 서명된 SHA-256이 일치하지 않습니다.");
            var manifest = _json.Deserialize<CoreInstallManifest>(File.ReadAllText(installManifestPath, Encoding.UTF8));
            if (manifest == null || manifest.SchemaVersion != 1 || !String.Equals(manifest.CoreVersion, release.CoreVersion, StringComparison.Ordinal) ||
                !String.Equals(manifest.EntryPoint, release.EntryPoint, StringComparison.OrdinalIgnoreCase) || manifest.Files == null ||
                manifest.Files.Count == 0 || manifest.Files.Count > maximumArchiveEntries)
                throw new InvalidOperationException("Core install manifest 계약이 Server release와 일치하지 않습니다.");
            var duplicates = manifest.Files.GroupBy(item => item.Path ?? "", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
            if (duplicates) throw new InvalidOperationException("Core install manifest에 중복 파일이 있습니다.");
            foreach (var item in manifest.Files) VerifyManagedFile(destination, item);
            var managedPaths = new HashSet<string>(manifest.Files.Select(item => NormalizeRelativePath(item.Path)), StringComparer.OrdinalIgnoreCase)
            {
                "install-manifest.json"
            };
            var actualPaths = new HashSet<string>(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(path.Substring(destinationRoot.Length))), StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(managedPaths))
                throw new InvalidOperationException("Core 패키지에 manifest로 관리되지 않는 파일이 있습니다.");
            var executable = Path.Combine(destination, release.EntryPoint);
            if (!File.Exists(executable)) throw new InvalidOperationException("Core 실행 파일이 없습니다.");
            if (!managedPaths.Contains(NormalizeRelativePath(release.EntryPoint)))
                throw new InvalidOperationException("Core 실행 파일이 install manifest에 등록되지 않았습니다.");
            VerifyBundledDriverSignatures(destination, manifest);
        }

        internal void ExtractAndVerifyForTest(string packagePath, string destination, CoreReleaseManifest release)
        {
            ExtractAndVerify(packagePath, destination, release);
        }

        private void VerifyInstalledFiles(ActiveCoreState state, CoreReleaseManifest release)
        {
            if (!IsActiveStateUsable(state)) throw new InvalidOperationException("설치된 Core 활성 상태가 올바르지 않습니다.");
            if (release == null) release = ReleaseFromState(state);
            CoreReleaseIntegrityVerifier.Verify(release);
            var manifestPath = Path.Combine(state.InstalledPath, "install-manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("설치된 Core manifest가 없습니다.");
            if (!String.Equals(Sha256(manifestPath), release.InstallManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치된 Core manifest의 서명된 SHA-256이 일치하지 않습니다.");
            var manifest = _json.Deserialize<CoreInstallManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            var expectedVersion = release == null ? state.CoreVersion : release.CoreVersion;
            var expectedEntryPoint = release == null ? state.EntryPoint : release.EntryPoint;
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > 2048 ||
                !String.Equals(state.CoreVersion, expectedVersion, StringComparison.Ordinal) ||
                !String.Equals(state.EntryPoint, expectedEntryPoint, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(manifest.CoreVersion, expectedVersion, StringComparison.Ordinal) ||
                !String.Equals(manifest.EntryPoint, expectedEntryPoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치된 Core 버전 manifest가 일치하지 않습니다.");
            var duplicates = manifest.Files.GroupBy(item => item == null ? "" : item.Path ?? "", StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
            if (duplicates) throw new InvalidOperationException("설치된 Core manifest에 중복 파일이 있습니다.");
            foreach (var item in manifest.Files) VerifyManagedFile(state.InstalledPath, item);
            var rootPath = Path.GetFullPath(state.InstalledPath + Path.DirectorySeparatorChar);
            var managedPaths = new HashSet<string>(manifest.Files.Select(item => NormalizeRelativePath(item.Path)), StringComparer.OrdinalIgnoreCase)
            {
                "install-manifest.json"
            };
            if (!managedPaths.Contains(NormalizeRelativePath(expectedEntryPoint)))
                throw new InvalidOperationException("설치된 Core 실행 파일이 manifest에 등록되지 않았습니다.");
            var actualPaths = new HashSet<string>(Directory.GetFiles(state.InstalledPath, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(path.Substring(rootPath.Length))), StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(managedPaths)) throw new InvalidOperationException("설치된 Core에 관리되지 않는 파일이 있습니다.");
            VerifyBundledDriverSignatures(state.InstalledPath, manifest);
        }

        private static void VerifyManagedFile(string root, CoreInstallFile item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.Path) || item.Size < 0 || String.IsNullOrWhiteSpace(item.Sha256))
                throw new InvalidOperationException("Core managed file 정보가 올바르지 않습니다.");
            var rootPath = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            var relative = ValidatePackageRelativePath(item.Path, false);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("Core managed file이 없거나 경로가 잘못되었습니다: " + item.Path);
            var file = new FileInfo(path);
            if (file.Length != item.Size || !String.Equals(Sha256(path), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Core managed file 무결성 검증에 실패했습니다: " + item.Path);
        }

        private static string NormalizeRelativePath(string value)
        {
            return ValidatePackageRelativePath(value, false).Replace('\\', '/');
        }

        internal static string ValidatePackageRelativePath(string value, bool directory)
        {
            var raw = value ?? "";
            if (raw.IndexOf('\0') >= 0 || raw.IndexOf(':') >= 0 || raw.Length > 240 ||
                raw.StartsWith("/", StringComparison.Ordinal) || raw.StartsWith("\\", StringComparison.Ordinal) ||
                Path.IsPathRooted(raw))
                throw new InvalidOperationException("Core 패키지에 허용되지 않은 파일 경로가 있습니다.");

            var normalized = raw.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (directory) normalized = normalized.TrimEnd(Path.DirectorySeparatorChar);
            if (String.IsNullOrWhiteSpace(normalized)) return "";
            var parts = normalized.Split(Path.DirectorySeparatorChar);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            foreach (var part in parts)
            {
                if (String.IsNullOrWhiteSpace(part) || part == "." || part == ".." || part.Length > 120 ||
                    part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) ||
                    reserved.Contains(Path.GetFileNameWithoutExtension(part)))
                    throw new InvalidOperationException("Core 패키지에 허용되지 않은 파일 경로가 있습니다.");
            }
            return normalized;
        }

        private static void VerifyBundledDriverSignatures(string root, CoreInstallManifest manifest)
        {
            foreach (var item in manifest.Files.Where(item => item != null && String.Equals(Path.GetExtension(item.Path), ".sys", StringComparison.OrdinalIgnoreCase)))
                AuthenticodeVerifier.Verify(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)), "");
        }

        private static CoreReleaseManifest ReleaseFromState(ActiveCoreState state)
        {
            if (state == null) return null;
            return new CoreReleaseManifest
            {
                SchemaVersion = 1,
                Channel = state.Channel,
                CoreVersion = state.CoreVersion,
                MinimumCoreVersion = state.MinimumCoreVersion,
                MinimumLauncherVersion = state.MinimumLauncherVersion,
                PackageId = state.PackageId,
                FileName = state.FileName,
                FileSize = state.FileSize,
                Sha256 = state.PackageSha256,
                InstallManifestSha256 = state.InstallManifestSha256,
                EntryPoint = state.EntryPoint,
                Mandatory = state.Mandatory,
                IntegrityMode = state.IntegrityMode,
                SigningKeyId = state.SigningKeyId,
                ManifestSignature = state.ManifestSignature,
                CodeSignatureRequired = false,
                PublisherSubject = ""
            };
        }

        private void WriteActiveState(ActiveCoreState state)
        {
            LauncherPaths.EnsureDirectories();
            var temporary = LauncherPaths.ActiveCoreFile + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, _json.Serialize(state), new UTF8Encoding(false));
            if (File.Exists(LauncherPaths.ActiveCoreFile)) File.Replace(temporary, LauncherPaths.ActiveCoreFile, null);
            else File.Move(temporary, LauncherPaths.ActiveCoreFile);
        }

        private static bool IsActiveStateUsable(ActiveCoreState state)
        {
            if (state == null || state.SchemaVersion != 2 || String.IsNullOrWhiteSpace(state.Channel) ||
                String.IsNullOrWhiteSpace(state.CoreVersion) || String.IsNullOrWhiteSpace(state.EntryPoint) ||
                String.IsNullOrWhiteSpace(state.InstalledPath) || String.IsNullOrWhiteSpace(state.PackageSha256) ||
                String.IsNullOrWhiteSpace(state.InstallManifestSha256) || String.IsNullOrWhiteSpace(state.ManifestSignature)) return false;
            try
            {
                if (!String.Equals(state.EntryPoint, LauncherBuildProfile.CoreEntryPoint, StringComparison.OrdinalIgnoreCase)) return false;
                var expected = Path.GetFullPath(LauncherPaths.VersionDirectory(state.CoreVersion) + Path.DirectorySeparatorChar);
                var installed = Path.GetFullPath(state.InstalledPath + Path.DirectorySeparatorChar);
                var executable = Path.GetFullPath(Path.Combine(installed, state.EntryPoint));
                return String.Equals(installed, expected, StringComparison.OrdinalIgnoreCase) &&
                    executable.StartsWith(installed, StringComparison.OrdinalIgnoreCase) && File.Exists(executable);
            }
            catch { return false; }
        }

        private static void ValidateRelease(CoreReleaseManifest release, string expectedProjectHost)
        {
            ValidateReleaseFields(release, expectedProjectHost);
            CoreReleaseIntegrityVerifier.Verify(release);
        }

        private static void ValidateReleaseFields(CoreReleaseManifest release, string expectedProjectHost)
        {
            if (release == null || release.SchemaVersion != 1) throw new InvalidOperationException("지원하지 않는 Core release manifest입니다.");
            LauncherPaths.VersionDirectory(release.CoreVersion);
            if (!String.Equals(release.Channel, LauncherVersion.Channel, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Core release 채널이 다릅니다.");
            if (release.FileSize <= 0 || release.FileSize > MaximumPackageBytes) throw new InvalidOperationException("Core 패키지 크기가 허용 범위를 벗어났습니다.");
            if (String.IsNullOrWhiteSpace(release.Sha256) || !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$"))
                throw new InvalidOperationException("Core SHA-256 형식이 올바르지 않습니다.");
            if (release.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(5)) throw new InvalidOperationException("Core 다운로드 승인이 만료되었습니다. 다시 시도해 주세요.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(release.FileName ?? "", "^KinojoMeterCore_[0-9]+\\.[0-9]+\\.[0-9]+_x64\\.zip$"))
                throw new InvalidOperationException("Core 패키지 파일명이 올바르지 않습니다.");
            if (!String.Equals(release.FileName, "KinojoMeterCore_" + release.CoreVersion + "_x64.zip", StringComparison.Ordinal))
                throw new InvalidOperationException("Core 패키지 파일명과 Core 버전이 일치하지 않습니다.");
            if (!String.Equals(release.EntryPoint, LauncherBuildProfile.CoreEntryPoint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("허용되지 않은 Core 실행 파일입니다.");
            if (release.CodeSignatureRequired || !String.IsNullOrWhiteSpace(release.PublisherSubject))
                throw new InvalidOperationException("무료 개인 배포 Core는 Windows 게시자 코드서명을 요구하지 않아야 합니다.");
            if (String.IsNullOrWhiteSpace(release.InstallManifestSha256) ||
                !System.Text.RegularExpressions.Regex.IsMatch(release.InstallManifestSha256, "^[0-9a-f]{64}$"))
                throw new InvalidOperationException("Core install manifest SHA-256 형식이 올바르지 않습니다.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(release.MinimumCoreVersion ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(release.MinimumLauncherVersion ?? "", "^[0-9]+\\.[0-9]+\\.[0-9]+$"))
                throw new InvalidOperationException("Core 최소 버전 계약이 올바르지 않습니다.");
            var expectedPackageId = release.Channel.ToLowerInvariant() + ":" + release.CoreVersion + ":" + release.Sha256.Substring(0, 16);
            if (!String.Equals(release.PackageId, expectedPackageId, StringComparison.Ordinal))
                throw new InvalidOperationException("Core packageId가 서명 대상 파일과 일치하지 않습니다.");
            RequireApprovedDownloadUri(release.DownloadUrl, expectedProjectHost);
        }

        internal static void ValidateReleaseForTest(CoreReleaseManifest release, string expectedProjectHost)
        {
            ValidateRelease(release, expectedProjectHost);
        }

        internal static void ValidateReleaseForTest(CoreReleaseManifest release, string expectedProjectHost, RSAParameters publicKey, string expectedKeyId)
        {
            ValidateReleaseFields(release, expectedProjectHost);
            CoreReleaseIntegrityVerifier.VerifyForTest(release, publicKey, expectedKeyId);
        }

        private static Uri RequireApprovedDownloadUri(string value, string expectedProjectHost)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/storage/v1/object/sign/meter-core-private/" + LauncherVersion.Channel + "/", StringComparison.Ordinal))
                throw new InvalidOperationException("허용되지 않은 Core 다운로드 주소입니다.");
            return uri;
        }

        private static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return String.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        public void Dispose() { _http.Dispose(); }
    }

    internal static class AuthenticodeVerifier
    {
        private static readonly Guid WintrustActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;

        public static void Verify(string path, string expectedPublisherSubject)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Core Authenticode 검증 대상 파일이 없습니다.");

            var actionId = WintrustActionGenericVerifyV2;
            var pathPointer = IntPtr.Zero;
            var fileInfoPointer = IntPtr.Zero;
            var data = new WinTrustData();
            try
            {
                pathPointer = Marshal.StringToCoTaskMemUni(path);
                var fileInfo = new WinTrustFileInfo
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                    FilePath = pathPointer,
                    FileHandle = IntPtr.Zero,
                    KnownSubject = IntPtr.Zero
                };
                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                data = new WinTrustData
                {
                    StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    PolicyCallbackData = IntPtr.Zero,
                    SIPClientData = IntPtr.Zero,
                    UIChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    FileInfoPtr = fileInfoPointer,
                    StateAction = WtdStateActionVerify,
                    StateData = IntPtr.Zero,
                    URLReference = IntPtr.Zero,
                    ProvFlags = 0,
                    UIContext = 0
                };

                var result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
                if (result != 0)
                    throw new InvalidOperationException("Core Authenticode 서명 검증에 실패했습니다. 코드: 0x" + unchecked((uint)result).ToString("X8"));
                if (!String.IsNullOrWhiteSpace(expectedPublisherSubject))
                {
                    using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    {
                        var signerName = certificate.GetNameInfo(X509NameType.SimpleName, false);
                        if (!String.Equals(signerName, expectedPublisherSubject, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Core 서명 게시자가 Server manifest와 일치하지 않습니다.");
                    }
                }
            }
            finally
            {
                if (data.StateData != IntPtr.Zero)
                {
                    data.StateAction = WtdStateActionClose;
                    WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
                }
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
                if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SIPClientData;
            public uint UIChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfoPtr;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr URLReference;
            public uint ProvFlags;
            public uint UIContext;
        }
    }
}
