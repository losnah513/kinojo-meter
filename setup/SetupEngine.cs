using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace KinojoMeterSetup
{
    internal enum SetupMode
    {
        NewInstall,
        Update,
        Repair,
        DowngradeBlocked
    }

    internal sealed class ManagedFileRecord
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class InstallManifest
    {
        public int SchemaVersion { get; set; }
        public string Product { get; set; }
        public string Version { get; set; }
        public string FileVersion { get; set; }
        public string InstalledAtUtc { get; set; }
        public List<ManagedFileRecord> ManagedFiles { get; set; }
    }

    internal sealed class InstallationSnapshot
    {
        public bool Exists { get; set; }
        public string InstallPath { get; set; }
        public string Version { get; set; }
        public string FileVersion { get; set; }
        public bool DesktopShortcutExists { get; set; }
        public bool StartMenuShortcutExists { get; set; }

        public static InstallationSnapshot Detect(string explicitPath)
        {
            var installPath = ResolveInstallPath(explicitPath);
            var executable = String.IsNullOrWhiteSpace(installPath)
                ? null
                : Path.Combine(installPath, "KINOJO.Meter.exe");
            var registered = IsRegisteredInstallation(installPath);
            var hasInstallMarkers = !String.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath) &&
                (File.Exists(executable) ||
                 File.Exists(Path.Combine(installPath, "version.json")) ||
                 File.Exists(Path.Combine(installPath, "KINOJO.Meter.Setup.exe")) ||
                 registered);
            var exists = hasInstallMarkers;

            var snapshot = new InstallationSnapshot
            {
                Exists = exists,
                InstallPath = installPath,
                Version = exists ? ReadInstalledVersion(installPath) : "",
                FileVersion = exists ? ReadInstalledFileVersion(executable) : "",
                DesktopShortcutExists = ShortcutManager.DesktopShortcutExists(),
                StartMenuShortcutExists = ShortcutManager.StartMenuShortcutExists()
            };
            return snapshot;
        }


        private static bool IsRegisteredInstallation(string installPath)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(InstallerRegistry.UninstallKeyPath, false))
                {
                    if (key == null) return false;
                    var registeredPath = Convert.ToString(key.GetValue("InstallLocation"));
                    if (String.IsNullOrWhiteSpace(registeredPath) || String.IsNullOrWhiteSpace(installPath)) return false;
                    return String.Equals(
                        Path.GetFullPath(registeredPath.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(installPath.Trim()).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static string ResolveInstallPath(string explicitPath)
        {
            if (!String.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath.Trim());

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(InstallerRegistry.UninstallKeyPath, false))
                {
                    var registered = key == null ? null : Convert.ToString(key.GetValue("InstallLocation"));
                    if (!String.IsNullOrWhiteSpace(registered)) return Path.GetFullPath(registered.Trim());
                }
            }
            catch { }

            var defaultPath = InstallerPaths.DefaultInstallPath;
            if (File.Exists(Path.Combine(defaultPath, "KINOJO.Meter.exe"))) return defaultPath;
            return defaultPath;
        }

        private static string ReadInstalledVersion(string installPath)
        {
            try
            {
                var manifestPath = Path.Combine(installPath, "version.json");
                if (File.Exists(manifestPath))
                {
                    var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifestPath));
                    object raw;
                    if (data != null && data.TryGetValue("version", out raw))
                    {
                        var value = Convert.ToString(raw);
                        if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
                    }
                }
            }
            catch { }

            try
            {
                var executable = Path.Combine(installPath, "KINOJO.Meter.exe");
                var fileVersion = FileVersionInfo.GetVersionInfo(executable).FileVersion;
                System.Version parsed;
                if (System.Version.TryParse(fileVersion, out parsed))
                    return parsed.Major + "." + parsed.Minor + "." + Math.Max(0, parsed.Build);
            }
            catch { }
            return "";
        }

        private static string ReadInstalledFileVersion(string executable)
        {
            try
            {
                var value = FileVersionInfo.GetVersionInfo(executable).FileVersion;
                return String.IsNullOrWhiteSpace(value) ? "" : value.Trim();
            }
            catch { return ""; }
        }
    }

    internal static class InstallerPaths
    {
        public static string DefaultInstallPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KINOJO Meter"); }
        }

        public static string InstallerDataRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KINOJO Meter", "Installer"); }
        }

        public static string LegacyInstallPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KINOJO Meter Test"); }
        }
    }

    internal static class ShortcutManager
    {
        public static string CommonDesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "KINOJO Meter.lnk"); }
        }

        public static string UserDesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KINOJO Meter.lnk"); }
        }

        public static string CommonStartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "KINOJO Meter", "KINOJO Meter.lnk"); }
        }

        public static string UserStartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KINOJO Meter", "KINOJO Meter.lnk"); }
        }

        public static bool DesktopShortcutExists()
        {
            return File.Exists(CommonDesktopShortcut) || File.Exists(UserDesktopShortcut) ||
                   File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KINOJO Meter Test.lnk"));
        }

        public static bool StartMenuShortcutExists()
        {
            return File.Exists(CommonStartMenuShortcut) || File.Exists(UserStartMenuShortcut) ||
                   File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KINOJO Meter Test.lnk"));
        }

        public static void Apply(string installPath, bool desktopShortcut, bool startMenuShortcut)
        {
            DeleteKnownShortcuts();
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType);
            if (startMenuShortcut) Create(shell, CommonStartMenuShortcut, installPath);
            if (desktopShortcut) Create(shell, CommonDesktopShortcut, installPath);
        }

        public static void DeleteKnownShortcuts()
        {
            DeleteFile(CommonDesktopShortcut);
            DeleteFile(UserDesktopShortcut);
            DeleteFile(CommonStartMenuShortcut);
            DeleteFile(UserStartMenuShortcut);
            DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KINOJO Meter Test.lnk"));
            DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "KINOJO Meter Test.lnk"));
            DeleteEmptyDirectory(Path.GetDirectoryName(CommonStartMenuShortcut));
            DeleteEmptyDirectory(Path.GetDirectoryName(UserStartMenuShortcut));
        }

        private static void Create(dynamic shell, string shortcutPath, string installPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            var executablePath = Path.Combine(installPath, "KINOJO.Meter.exe");
            shortcut.TargetPath = executablePath;
            shortcut.IconLocation = executablePath + ",0";
            shortcut.WorkingDirectory = installPath;
            shortcut.Description = "KINOJO Meter";
            shortcut.Save();
        }

        private static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void DeleteEmptyDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
            }
            catch { }
        }
    }

    internal static class InstallerRegistry
    {
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KINOJO Meter";

        public static void Register(string installPath, string displayVersion)
        {
            var setupPath = Path.Combine(installPath, "KINOJO.Meter.Setup.exe");
            using (var key = Registry.LocalMachine.CreateSubKey(UninstallKeyPath))
            {
                if (key == null) return;
                key.SetValue("DisplayName", "KINOJO Meter");
                key.SetValue("DisplayVersion", displayVersion ?? "");
                key.SetValue("Publisher", "KINOJO INFO");
                key.SetValue("InstallLocation", installPath);
                key.SetValue("DisplayIcon", Path.Combine(installPath, "KINOJO.Meter.exe"));
                key.SetValue("UninstallString", "\"" + setupPath + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + setupPath + "\" /uninstall /silent");
                key.SetValue("ModifyPath", "\"" + setupPath + "\" /repair");
                key.SetValue("NoModify", 0, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.UtcNow.ToString("yyyyMMdd"));
                var size = Directory.Exists(installPath)
                    ? Directory.GetFiles(installPath, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path).Length).Sum()
                    : 0L;
                key.SetValue("EstimatedSize", (int)Math.Max(1L, Math.Min(Int32.MaxValue, size / 1024L)), RegistryValueKind.DWord);
            }
        }

        public static void Remove()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, false); }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KINOJO Meter Test", false); }
            catch { }
        }
    }

    internal sealed class SetupEngine
    {
        private readonly SetupOptions _options;
        private readonly Action<string> _status;
        private readonly InstallationSnapshot _existing;

        public SetupEngine(SetupOptions options, Action<string> status)
        {
            _options = options ?? new SetupOptions();
            _status = status ?? delegate { };
            _existing = InstallationSnapshot.Detect(_options.PathSpecified ? _options.InstallPath : null);
            InstallPath = _existing.InstallPath;
            Mode = DetermineMode();
        }

        public SetupMode Mode { get; private set; }
        public string InstallPath { get; private set; }
        public string ExistingVersion { get { return _existing.Version; } }
        public bool ExistingDesktopShortcut { get { return _existing.DesktopShortcutExists; } }
        public bool ExistingStartMenuShortcut { get { return _existing.StartMenuShortcutExists; } }

        public string ModeLabel
        {
            get
            {
                if (Mode == SetupMode.NewInstall) return "신규 설치";
                if (Mode == SetupMode.Update) return "업데이트";
                if (Mode == SetupMode.Repair) return "복구 설치";
                return "설치 불가";
            }
        }

        public void Install(bool desktopShortcut, bool launch)
        {
            if (Mode == SetupMode.DowngradeBlocked)
                throw new InvalidOperationException("현재 설치된 버전이 더 최신입니다. 최신 설치기를 사용해 주세요.");

            ValidateInstallTarget();
            WaitForRequestedProcess();
            StopRunningMeter();

            var transactionRoot = Path.Combine(InstallerPaths.InstallerDataRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            var stagingPath = Path.Combine(transactionRoot, "staging");
            var backupPath = Path.Combine(transactionRoot, "backup");
            var backupCreated = false;
            var preserveTransactionForRecovery = false;
            var oldVersion = _existing.Version;
            var oldDesktop = _existing.DesktopShortcutExists;
            var oldStartMenu = _existing.StartMenuShortcutExists;

            try
            {
                Directory.CreateDirectory(stagingPath);
                _status("새 설치 파일을 준비하고 있습니다...");
                ExtractPayload(stagingPath);
                var stagedManifest = ValidateAndBuildManifest(stagingPath);

                if (_existing.Exists && Directory.Exists(InstallPath))
                {
                    _status("기존 설치를 안전하게 백업하고 있습니다...");
                    CopyDirectory(InstallPath, backupPath);
                    backupCreated = true;
                }

                _status(Mode == SetupMode.Repair ? "프로그램 파일을 복구하고 있습니다..." : "새 버전으로 교체하고 있습니다...");
                ReplaceInstallDirectory(stagingPath, InstallPath);
                CopySetupExecutable(InstallPath);
                WriteInstallManifest(InstallPath, stagedManifest);
                ValidateInstalledFiles(InstallPath, stagedManifest);

                var startMenuShortcut = _existing.Exists ? oldStartMenu : true;
                ShortcutManager.Apply(InstallPath, desktopShortcut, startMenuShortcut);
                InstallerRegistry.Register(InstallPath, SetupVersionInfo.Current);

                if (launch)
                {
                    _status("새 버전 실행을 확인하고 있습니다...");
                    VerifyApplicationLaunch(InstallPath);
                }

                CleanupLegacyInstall(InstallPath);
                _status(Mode == SetupMode.NewInstall ? "설치를 완료했습니다." : (Mode == SetupMode.Repair ? "복구 설치를 완료했습니다." : "업데이트를 완료했습니다."));
            }
            catch (Exception installError)
            {
                try
                {
                    _status("설치에 실패해 이전 상태로 복구하고 있습니다...");
                    StopRunningMeter();
                    if (backupCreated && Directory.Exists(backupPath))
                    {
                        ReplaceInstallDirectory(backupPath, InstallPath);
                        ShortcutManager.Apply(InstallPath, oldDesktop, oldStartMenu);
                        InstallerRegistry.Register(InstallPath, String.IsNullOrWhiteSpace(oldVersion) ? "0.0.0" : oldVersion);
                        if (launch) TryLaunchWithoutValidation(InstallPath);
                    }
                    else
                    {
                        DeleteDirectoryRobust(InstallPath);
                        ShortcutManager.DeleteKnownShortcuts();
                        InstallerRegistry.Remove();
                    }
                }
                catch (Exception rollbackError)
                {
                    preserveTransactionForRecovery = true;
                    throw new InvalidOperationException("설치와 자동 복구에 실패했습니다. 설치 오류: " + installError.Message + " / 복구 오류: " + rollbackError.Message + " / 복구 자료: " + transactionRoot, installError);
                }
                throw new InvalidOperationException("설치에 실패해 이전 버전으로 복구했습니다. " + installError.Message, installError);
            }
            finally
            {
                if (!preserveTransactionForRecovery)
                {
                    try { DeleteDirectoryRobust(transactionRoot); }
                    catch { }
                }
            }
        }

        private void ValidateInstallTarget()
        {
            AssertSafeManagedDirectory(InstallPath);
            if (Mode != SetupMode.NewInstall || !Directory.Exists(InstallPath)) return;
            if (Directory.GetFileSystemEntries(InstallPath).Length > 0)
                throw new InvalidOperationException("신규 설치 위치는 비어 있는 폴더여야 합니다. 다른 파일이 있는 폴더를 선택하지 마세요.");
        }

        public static void ValidateManagedDirectory(string path)
        {
            AssertSafeManagedDirectory(path);
        }

        private static void AssertSafeManagedDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("설치 경로가 비어 있습니다.");
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(full);
            if (String.IsNullOrWhiteSpace(root) || String.Equals(full, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("드라이브 루트에는 설치하거나 제거할 수 없습니다.");

            foreach (var protectedPath in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            })
            {
                if (String.IsNullOrWhiteSpace(protectedPath)) continue;
                var protectedFull = Path.GetFullPath(protectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (String.Equals(full, protectedFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows 또는 사용자 기준 폴더 자체에는 설치하거나 제거할 수 없습니다.");
            }
        }

        private SetupMode DetermineMode()
        {
            if (!_existing.Exists) return SetupMode.NewInstall;
            if (_options.Repair) return SetupMode.Repair;

            System.Version current;
            System.Version target;
            if (!System.Version.TryParse(_existing.Version, out current) || !System.Version.TryParse(SetupVersionInfo.Current, out target))
                return SetupMode.Repair;
            if (target < current && !_options.AllowDowngrade) return SetupMode.DowngradeBlocked;
            if (target == current) return SetupMode.Repair;
            return SetupMode.Update;
        }

        private void WaitForRequestedProcess()
        {
            if (_options.WaitProcessId <= 0) return;
            try
            {
                using (var process = Process.GetProcessById(_options.WaitProcessId)) process.WaitForExit(20000);
            }
            catch { }
        }

        public static void StopRunningMeter()
        {
            foreach (var name in new[] { "KINOJO.Meter", "KINOJO.Meter.Test" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.CloseMainWindow();
                        if (!process.WaitForExit(3000)) process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
        }

        private static void ExtractPayload(string destinationRoot)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KinojoMeterPayload"))
            {
                if (stream == null) throw new InvalidOperationException("설치 데이터가 없습니다.");
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    foreach (var entry in archive.Entries)
                    {
                        var destination = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("설치 데이터에 잘못된 경로가 포함되어 있습니다.");
                        if (String.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                    }
                }
            }
        }

        private static InstallManifest ValidateAndBuildManifest(string stagingPath)
        {
            foreach (var required in new[]
            {
                "KINOJO.Meter.exe", "KINOJO.Meter.exe.config", "version.json", "SharpPcap.dll",
                "PacketDotNet.dll", "WinDivert.dll", "WinDivert64.sys"
            })
            {
                if (!File.Exists(Path.Combine(stagingPath, required)))
                    throw new InvalidOperationException("필수 설치 파일이 없습니다: " + required);
            }

            var release = ReadReleaseManifest(Path.Combine(stagingPath, "version.json"));
            if (!String.Equals(release.Item1, SetupVersionInfo.Current, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(release.Item2, SetupVersionInfo.FileVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치기와 Payload 버전이 일치하지 않습니다.");

            var executableVersion = FileVersionInfo.GetVersionInfo(Path.Combine(stagingPath, "KINOJO.Meter.exe")).FileVersion;
            if (!String.Equals(executableVersion, SetupVersionInfo.FileVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치기와 프로그램 파일 버전이 일치하지 않습니다.");

            var records = Directory.GetFiles(stagingPath, "*", SearchOption.AllDirectories)
                .Select(path => BuildFileRecord(stagingPath, path))
                .OrderBy(record => record.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new InstallManifest
            {
                SchemaVersion = 1,
                Product = "KINOJO Meter",
                Version = SetupVersionInfo.Current,
                FileVersion = SetupVersionInfo.FileVersion,
                InstalledAtUtc = DateTime.UtcNow.ToString("o"),
                ManagedFiles = records
            };
        }

        private static Tuple<string, string> ReadReleaseManifest(string path)
        {
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                object version;
                object fileVersion;
                if (data == null || !data.TryGetValue("version", out version) || !data.TryGetValue("fileVersion", out fileVersion))
                    throw new InvalidOperationException("version.json의 필수 정보가 없습니다.");
                return Tuple.Create(Convert.ToString(version).Trim(), Convert.ToString(fileVersion).Trim());
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException("version.json을 읽을 수 없습니다.", ex);
            }
        }

        private static ManagedFileRecord BuildFileRecord(string root, string path)
        {
            return new ManagedFileRecord
            {
                Path = MakeRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                Size = new FileInfo(path).Length,
                Sha256 = ComputeSha256(path)
            };
        }

        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void ReplaceInstallDirectory(string sourcePath, string installPath)
        {
            DeleteDirectoryRobust(installPath);
            Directory.CreateDirectory(installPath);
            CopyDirectory(sourcePath, installPath);
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = MakeRelativePath(sourcePath, directory);
                Directory.CreateDirectory(Path.Combine(destinationPath, relative));
            }
            foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                var relative = MakeRelativePath(sourcePath, file);
                var destination = Path.Combine(destinationPath, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(file, destination, true);
            }
        }

        private static void CopySetupExecutable(string installPath)
        {
            var source = Assembly.GetExecutingAssembly().Location;
            var destination = Path.Combine(installPath, "KINOJO.Meter.Setup.exe");
            File.Copy(source, destination, true);
        }

        private static void WriteInstallManifest(string installPath, InstallManifest manifest)
        {
            var json = new JavaScriptSerializer().Serialize(manifest);
            File.WriteAllText(Path.Combine(installPath, "install-manifest.json"), json);
        }

        private static void ValidateInstalledFiles(string installPath, InstallManifest manifest)
        {
            foreach (var record in manifest.ManagedFiles)
            {
                var installed = Path.Combine(installPath, record.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(installed)) throw new InvalidOperationException("설치 검증 중 파일이 누락되었습니다: " + record.Path);
                var info = new FileInfo(installed);
                if (info.Length != record.Size) throw new InvalidOperationException("설치 검증 중 파일 크기가 일치하지 않습니다: " + record.Path);
                if (!String.Equals(ComputeSha256(installed), record.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("설치 검증 중 파일 무결성이 일치하지 않습니다: " + record.Path);
            }

            var release = ReadReleaseManifest(Path.Combine(installPath, "version.json"));
            if (!String.Equals(release.Item1, SetupVersionInfo.Current, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(release.Item2, SetupVersionInfo.FileVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("설치된 version.json 검증에 실패했습니다.");
        }

        private static void VerifyApplicationLaunch(string installPath)
        {
            var process = Process.Start(new ProcessStartInfo(Path.Combine(installPath, "KINOJO.Meter.exe"))
            {
                UseShellExecute = true,
                WorkingDirectory = installPath
            });
            if (process == null) throw new InvalidOperationException("새 버전을 실행하지 못했습니다.");
            try
            {
                if (process.WaitForExit(5000))
                    throw new InvalidOperationException("새 버전이 시작 직후 종료되었습니다. 종료 코드: " + process.ExitCode);
            }
            finally { process.Dispose(); }
        }

        private static void TryLaunchWithoutValidation(string installPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(installPath, "KINOJO.Meter.exe"))
                {
                    UseShellExecute = true,
                    WorkingDirectory = installPath
                });
            }
            catch { }
        }

        private static void CleanupLegacyInstall(string newInstallPath)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KINOJO Meter Test", false); }
            catch { }
            if (!String.Equals(Path.GetFullPath(InstallerPaths.LegacyInstallPath), Path.GetFullPath(newInstallPath), StringComparison.OrdinalIgnoreCase))
                DeleteDirectoryRobust(InstallerPaths.LegacyInstallPath);
        }

        public static void DeleteDirectoryRobust(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            AssertSafeManagedDirectory(path);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); }
                        catch { }
                    }
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    if (attempt == 3) throw;
                    Thread.Sleep(500 * (attempt + 1));
                }
            }
        }
    }
}
