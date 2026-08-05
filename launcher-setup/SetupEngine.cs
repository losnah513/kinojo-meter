using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KinojoMeterLauncherSetup
{
    internal static class LauncherSetupEngine
    {
        private const string PayloadResource = "KINOJO.Meter.Launcher.Payload";
        private static readonly string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + SetupBuildProfile.UninstallKeyName;
        private const int MoveFileDelayUntilReboot = 0x4;

        private static string InstallDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", SetupBuildProfile.InstallFolderName); }
        }

        private static string LauncherPath { get { return Path.Combine(InstallDirectory, SetupBuildProfile.LauncherFileName); } }
        private static string SetupPath { get { return Path.Combine(InstallDirectory, SetupBuildProfile.SetupFileName); } }
        private static string StartMenuDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", SetupBuildProfile.ShortcutName); }
        }
        private static string StartMenuShortcut { get { return Path.Combine(StartMenuDirectory, SetupBuildProfile.ShortcutName + ".lnk"); } }
        private static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SetupBuildProfile.ShortcutName + ".lnk"); }
        }

        public static void Install(bool silent)
        {
            if (!silent && MessageBox.Show(
                "KINOJO Meter Launcher를 이 Windows 사용자 계정에 설치합니다.\n\n" +
                "이 프로그램은 개인 취미 프로젝트라 유료 Windows 게시자 인증서를 사용하지 않습니다. " +
                "Windows에 '알 수 없는 게시자' 경고가 표시될 수 있습니다.\n\n" +
                "Core 업데이트는 RSA 전자서명과 SHA-256으로 별도 검증합니다. 계속할까요?",
                SetupBuildProfile.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            StopProcesses(SetupBuildProfile.ProcessName);
            var transaction = Path.Combine(Path.GetTempPath(), "kinojo-launcher-setup-" + Guid.NewGuid().ToString("N"));
            var stagedLauncher = Path.Combine(transaction, SetupBuildProfile.LauncherFileName);
            var backupLauncher = Path.Combine(transaction, "previous-launcher.exe");
            Directory.CreateDirectory(transaction);
            try
            {
                ExtractPayload(stagedLauncher);
                ValidatePayloadVersion(stagedLauncher);
                Directory.CreateDirectory(InstallDirectory);

                if (File.Exists(LauncherPath))
                {
                    File.Replace(stagedLauncher, LauncherPath, backupLauncher, true);
                }
                else
                {
                    File.Move(stagedLauncher, LauncherPath);
                }

                if (!SamePath(Assembly.GetExecutingAssembly().Location, SetupPath))
                    File.Copy(Assembly.GetExecutingAssembly().Location, SetupPath, true);
                CreateShortcut(StartMenuShortcut, LauncherPath);
                CreateShortcut(DesktopShortcut, LauncherPath);
                RegisterUninstall();
                ValidatePayloadVersion(LauncherPath);

                Process.Start(new ProcessStartInfo(LauncherPath) { WorkingDirectory = InstallDirectory, UseShellExecute = true });
                if (!silent) MessageBox.Show("설치가 완료되었습니다.\n\nPASS KEY를 입력하면 최신 Core를 확인한 뒤 미터기가 실행됩니다.", SetupBuildProfile.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch
            {
                try
                {
                    if (File.Exists(backupLauncher)) File.Copy(backupLauncher, LauncherPath, true);
                }
                catch { }
                throw;
            }
            finally
            {
                try { if (Directory.Exists(transaction)) Directory.Delete(transaction, true); }
                catch { }
            }
        }

        public static void Uninstall(bool silent)
        {
            if (!silent && MessageBox.Show(
                "KINOJO Meter Launcher와 설치된 Core를 제거합니다.\n\n계속할까요?",
                SetupBuildProfile.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            StopProcesses(SetupBuildProfile.ProcessName, SetupBuildProfile.CoreProcessName);
            DeleteShortcut(DesktopShortcut);
            DeleteShortcut(StartMenuShortcut);
            try { if (Directory.Exists(StartMenuDirectory) && Directory.GetFileSystemEntries(StartMenuDirectory).Length == 0) Directory.Delete(StartMenuDirectory); }
            catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }
            catch { }

            var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SetupBuildProfile.DataFolderName);
            try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }
            catch { }
            try { if (File.Exists(LauncherPath)) File.Delete(LauncherPath); }
            catch { MoveFileEx(LauncherPath, null, MoveFileDelayUntilReboot); }
            try { if (File.Exists(SetupPath) && !SamePath(SetupPath, Assembly.GetExecutingAssembly().Location)) File.Delete(SetupPath); }
            catch { MoveFileEx(SetupPath, null, MoveFileDelayUntilReboot); }
            try
            {
                if (SamePath(SetupPath, Assembly.GetExecutingAssembly().Location)) ScheduleSelfDelete(SetupPath, InstallDirectory);
            }
            catch { }
            try { if (Directory.Exists(InstallDirectory) && Directory.GetFileSystemEntries(InstallDirectory).Length == 0) Directory.Delete(InstallDirectory); }
            catch { }

            if (!silent) MessageBox.Show(SetupBuildProfile.ProductName + " 제거를 완료했습니다.", SetupBuildProfile.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void ExtractPayload(string target)
        {
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (resource == null || resource.Length <= 0 || resource.Length > 10L * 1024L * 1024L)
                    throw new InvalidOperationException("Launcher 설치 파일 내부 payload가 올바르지 않습니다.");
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
            }
        }

        private static void ValidatePayloadVersion(string path)
        {
            if (!File.Exists(path)) throw new InvalidOperationException("Launcher 실행 파일이 없습니다.");
            var expected = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
            var actual = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (String.IsNullOrWhiteSpace(expected) || !String.Equals(expected, actual, StringComparison.Ordinal))
                throw new InvalidOperationException("Launcher 설치기와 실행 파일 버전이 일치하지 않습니다.");
        }

        private static void RegisterUninstall()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                if (key == null) throw new InvalidOperationException("Windows 앱 제거 정보를 등록하지 못했습니다.");
                var version = FileVersionInfo.GetVersionInfo(LauncherPath).ProductVersion ?? "";
                key.SetValue("DisplayName", SetupBuildProfile.ProductName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "KINOJO INFO");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("DisplayIcon", LauncherPath);
                key.SetValue("UninstallString", "\"" + SetupPath + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + SetupPath + "\" /uninstall /silent");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private static void CreateShortcut(string shortcutPath, string targetPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows 바로가기 기능을 사용할 수 없습니다.");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.WorkingDirectory = InstallDirectory;
            shortcut.Description = SetupBuildProfile.ProductName;
            shortcut.Save();
        }

        private static void DeleteShortcut(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        private static void StopProcesses(params string[] names)
        {
            foreach (var name in names ?? new string[0])
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id) continue;
                        process.CloseMainWindow();
                        if (!process.WaitForExit(2500)) process.Kill();
                    }
                    catch { }
                    finally { process.Dispose(); }
                }
            }
        }

        private static bool SamePath(string left, string right)
        {
            try { return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void ScheduleSelfDelete(string file, string directory)
        {
            var escapedFile = (file ?? "").Replace("'", "''");
            var escapedDirectory = (directory ?? "").Replace("'", "''");
            var script = "$f='" + escapedFile + "';$d='" + escapedDirectory +
                "';Start-Sleep -Milliseconds 900;Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue;" +
                "Remove-Item -LiteralPath $d -Force -ErrorAction SilentlyContinue";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch
            {
                MoveFileEx(file, null, MoveFileDelayUntilReboot);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
    }
}
