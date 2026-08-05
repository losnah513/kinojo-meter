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
            if (current != null && String.Equals(current.CoreVersion, release.CoreVersion, StringComparison.Ordinal) &&
                String.Equals(current.PackageSha256, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    VerifyInstalledFiles(current, release);
                    progress?.Report(100);
                    return new CoreInstallResult { Active = current, Previous = current, Changed = false };
                }
                catch
                {
                    // A damaged slot is replaced from the immutable Server package.
                    current = null;
                }
            }

            if (Process.GetProcessesByName("KINOJO.Meter").Any(process => !process.HasExited))
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
                    SchemaVersion = 1,
                    CoreVersion = release.CoreVersion,
                    EntryPoint = release.EntryPoint,
                    InstalledPath = target,
                    ActivatedAtUtc = DateTime.UtcNow.ToString("o"),
                    PackageSha256 = release.Sha256
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
            var executable = Path.Combine(install.Active.InstalledPath, install.Active.EntryPoint);
            var envelope = _json.Serialize(new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "sessionToken", login.SessionToken },
                { "installationId", installationId ?? "" },
                { "launcherVersion", LauncherVersion.Current },
                { "coreVersion", install.Active.CoreVersion },
                { "issuedAtUtc", DateTime.UtcNow.ToString("o") },
                { "account", login.Account ?? new Dictionary<string, object>() },
                { "characters", login.Characters ?? new List<Dictionary<string, object>>() }
            });
            var encodedEnvelope = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = install.Active.InstalledPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = false
            };
            Process process = null;
            try
            {
                process = Process.Start(start);
                if (process == null) throw new InvalidOperationException("KINOJO Meter Core를 시작하지 못했습니다.");
                await process.StandardInput.WriteLineAsync("KINOJO_LAUNCHER_SESSION_V1 " + encodedEnvelope).ConfigureAwait(false);
                process.StandardInput.Close();
                var exited = await Task.Run(() => process.WaitForExit(8000)).ConfigureAwait(false);
                if (exited)
                    throw new InvalidOperationException("새 Core가 시작 직후 종료되었습니다. 종료 코드: " + process.ExitCode);
            }
            catch
            {
                try
                {
                    if (process != null && !process.HasExited) process.Kill();
                }
                catch { }
                if (install.Changed && install.Previous != null && IsActiveStateUsable(install.Previous)) WriteActiveState(install.Previous);
                else if (install.Changed)
                {
                    try { if (File.Exists(LauncherPaths.ActiveCoreFile)) File.Delete(LauncherPaths.ActiveCoreFile); }
                    catch { }
                }
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
            const long maximumExtractedBytes = 1024L * 1024L * 1024L;
            Directory.CreateDirectory(destination);
            var destinationRoot = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
            var archivePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long extractedBytes = 0;
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                foreach (var entry in archive.Entries)
                {
                    var relative = (entry.FullName ?? "").Replace('/', Path.DirectorySeparatorChar);
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
            var manifest = _json.Deserialize<CoreInstallManifest>(File.ReadAllText(installManifestPath, Encoding.UTF8));
            if (manifest == null || manifest.SchemaVersion != 1 || !String.Equals(manifest.CoreVersion, release.CoreVersion, StringComparison.Ordinal) ||
                !String.Equals(manifest.EntryPoint, release.EntryPoint, StringComparison.OrdinalIgnoreCase) || manifest.Files == null || manifest.Files.Count == 0)
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
            if (release.CodeSignatureRequired) VerifySignedBinaries(destination, manifest, release.PublisherSubject);
        }

        private void VerifyInstalledFiles(ActiveCoreState state, CoreReleaseManifest release)
        {
            var manifestPath = Path.Combine(state.InstalledPath, "install-manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException("설치된 Core manifest가 없습니다.");
            var manifest = _json.Deserialize<CoreInstallManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || manifest.Files == null || !String.Equals(manifest.CoreVersion, release.CoreVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("설치된 Core 버전 manifest가 일치하지 않습니다.");
            foreach (var item in manifest.Files) VerifyManagedFile(state.InstalledPath, item);
            var rootPath = Path.GetFullPath(state.InstalledPath + Path.DirectorySeparatorChar);
            var managedPaths = new HashSet<string>(manifest.Files.Select(item => NormalizeRelativePath(item.Path)), StringComparer.OrdinalIgnoreCase)
            {
                "install-manifest.json"
            };
            var actualPaths = new HashSet<string>(Directory.GetFiles(state.InstalledPath, "*", SearchOption.AllDirectories)
                .Select(path => NormalizeRelativePath(path.Substring(rootPath.Length))), StringComparer.OrdinalIgnoreCase);
            if (!actualPaths.SetEquals(managedPaths)) throw new InvalidOperationException("설치된 Core에 관리되지 않는 파일이 있습니다.");
            if (release.CodeSignatureRequired) VerifySignedBinaries(state.InstalledPath, manifest, release.PublisherSubject);
        }

        private static void VerifyManagedFile(string root, CoreInstallFile item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.Path) || item.Size < 0 || String.IsNullOrWhiteSpace(item.Sha256))
                throw new InvalidOperationException("Core managed file 정보가 올바르지 않습니다.");
            var rootPath = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new InvalidOperationException("Core managed file이 없거나 경로가 잘못되었습니다: " + item.Path);
            var file = new FileInfo(path);
            if (file.Length != item.Size || !String.Equals(Sha256(path), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Core managed file 무결성 검증에 실패했습니다: " + item.Path);
        }

        private static string NormalizeRelativePath(string value)
        {
            return (value ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static void VerifySignedBinaries(string root, CoreInstallManifest manifest, string publisherSubject)
        {
            var signedFiles = manifest.Files
                .Where(item => item != null && (String.Equals(Path.GetExtension(item.Path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(Path.GetExtension(item.Path), ".dll", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (signedFiles.Count == 0) throw new InvalidOperationException("Core 패키지에 서명 검증 대상이 없습니다.");
            foreach (var item in signedFiles)
                AuthenticodeVerifier.Verify(Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar)), publisherSubject);
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
            if (state == null || state.SchemaVersion != 1 || String.IsNullOrWhiteSpace(state.CoreVersion) ||
                String.IsNullOrWhiteSpace(state.EntryPoint) || String.IsNullOrWhiteSpace(state.InstalledPath)) return false;
            try
            {
                var versionRoot = Path.GetFullPath(LauncherPaths.CoreVersions + Path.DirectorySeparatorChar);
                var installed = Path.GetFullPath(state.InstalledPath + Path.DirectorySeparatorChar);
                return installed.StartsWith(versionRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(installed, state.EntryPoint));
            }
            catch { return false; }
        }

        private static void ValidateRelease(CoreReleaseManifest release, string expectedProjectHost)
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
            if (!String.Equals(release.EntryPoint, "KINOJO.Meter.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("허용되지 않은 Core 실행 파일입니다.");
            RequireApprovedDownloadUri(release.DownloadUrl, expectedProjectHost);
        }

        private static Uri RequireApprovedDownloadUri(string value, string expectedProjectHost)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                String.IsNullOrWhiteSpace(expectedProjectHost) || !String.Equals(uri.Host, expectedProjectHost, StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/storage/v1/object/sign/meter-core-private/", StringComparison.Ordinal))
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

        public static void Verify(string path, string expectedPublisherSubject)
        {
            var fileInfo = new WinTrustFileInfo(path);
            var data = new WinTrustData(fileInfo);
            try
            {
                var result = WinVerifyTrust(new IntPtr(-1), WintrustActionGenericVerifyV2, data);
                if (result != 0) throw new InvalidOperationException("Core Authenticode 서명 검증에 실패했습니다. 코드: 0x" + result.ToString("X8"));
                if (!String.IsNullOrWhiteSpace(expectedPublisherSubject))
                {
                    using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                    {
                        if (certificate.Subject.IndexOf(expectedPublisherSubject, StringComparison.OrdinalIgnoreCase) < 0)
                            throw new InvalidOperationException("Core 서명 게시자가 Server manifest와 일치하지 않습니다.");
                    }
                }
            }
            finally
            {
                data.Dispose();
                fileInfo.Dispose();
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustFileInfo : IDisposable
        {
            private readonly IntPtr _path;
            public uint StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
            public IntPtr FilePath;
            public IntPtr FileHandle = IntPtr.Zero;
            public IntPtr KnownSubject = IntPtr.Zero;

            public WinTrustFileInfo(string path)
            {
                _path = Marshal.StringToCoTaskMemUni(path);
                FilePath = _path;
            }

            public void Dispose() { if (_path != IntPtr.Zero) Marshal.FreeCoTaskMem(_path); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class WinTrustData : IDisposable
        {
            public uint StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
            public IntPtr PolicyCallbackData = IntPtr.Zero;
            public IntPtr SIPClientData = IntPtr.Zero;
            public uint UIChoice = 2;
            public uint RevocationChecks = 0;
            public uint UnionChoice = 1;
            public IntPtr FileInfoPtr;
            public uint StateAction = 0;
            public IntPtr StateData = IntPtr.Zero;
            public string URLReference = null;
            public uint ProvFlags = 0x00000020;
            public uint UIContext = 0;

            public WinTrustData(WinTrustFileInfo fileInfo)
            {
                FileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, FileInfoPtr, false);
            }

            public void Dispose() { if (FileInfoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(FileInfoPtr); }
        }
    }
}
