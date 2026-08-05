using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using KinojoMeterShared;

namespace KinojoMeterSetup
{
    internal static class SetupVersionInfo
    {
        private static readonly Assembly ExecutingAssembly = Assembly.GetExecutingAssembly();

        public static string Current
        {
            get
            {
                var version = ExecutingAssembly.GetName().Version;
                if (version == null) return "0.0.0";
                return version.Major + "." + version.Minor + "." + Math.Max(0, version.Build);
            }
        }

        public static string FileVersion
        {
            get
            {
                try
                {
                    var value = FileVersionInfo.GetVersionInfo(ExecutingAssembly.Location).FileVersion;
                    if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
                return Current + ".0";
            }
        }
    }

    internal sealed class SetupOptions
    {
        public bool Silent { get; set; }
        public bool Update { get; set; }
        public bool Repair { get; set; }
        public bool Launch { get; set; }
        public bool Uninstall { get; set; }
        public bool AllowDowngrade { get; set; }
        public bool Relocated { get; set; }
        public int WaitProcessId { get; set; }
        public string InstallPath { get; set; }
        public bool PathSpecified { get; set; }

        public static SetupOptions Parse(string[] args)
        {
            var value = new SetupOptions { InstallPath = InstallerPaths.DefaultInstallPath };
            args = args ?? new string[0];
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index] ?? "";
                if (String.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase)) value.Silent = true;
                else if (String.Equals(arg, "/update", StringComparison.OrdinalIgnoreCase)) value.Update = true;
                else if (String.Equals(arg, "/repair", StringComparison.OrdinalIgnoreCase)) value.Repair = true;
                else if (String.Equals(arg, "/launch", StringComparison.OrdinalIgnoreCase)) value.Launch = true;
                else if (String.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase)) value.Uninstall = true;
                else if (String.Equals(arg, "/allowdowngrade", StringComparison.OrdinalIgnoreCase)) value.AllowDowngrade = true;
                else if (String.Equals(arg, "/relocated", StringComparison.OrdinalIgnoreCase)) value.Relocated = true;
                else if (String.Equals(arg, "/path", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    value.InstallPath = args[++index];
                    value.PathSpecified = true;
                }
                else if (String.Equals(arg, "/waitpid", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                {
                    int parsed;
                    if (Int32.TryParse(args[++index], out parsed)) value.WaitProcessId = parsed;
                }
            }
            return value;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly SetupOptions _options;
        private SetupEngine _engine;
        private readonly Label _mode;
        private readonly Label _status;
        private readonly Button _install;
        private readonly TextBox _path;
        private readonly Button _browse;
        private readonly CheckBox _desktopShortcut;
        private readonly CheckBox _serviceRiskConsent;
        private readonly CheckBox _statisticsConsent;
        public bool InstallSucceeded { get; private set; }

        public SetupForm(SetupOptions options)
        {
            _options = options ?? new SetupOptions();
            _engine = new SetupEngine(_options, UpdateStatus);

            Text = "KINOJO Meter Setup " + SetupVersionInfo.Current;
            ClientSize = new Size(720, 785);
            MinimumSize = new Size(720, 815);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            var kicker = new Label { Text = "KINOJO METER", ForeColor = Color.FromArgb(79, 70, 229), Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Location = new Point(38, 27) };
            var title = new Label { Text = "KINOJO Meter " + SetupVersionInfo.Current, ForeColor = Color.FromArgb(23, 35, 58), Font = new Font("Segoe UI", 23F, FontStyle.Bold), AutoSize = true, Location = new Point(34, 49) };
            _mode = new Label { ForeColor = Color.FromArgb(37, 99, 235), Font = new Font("Segoe UI", 10F, FontStyle.Bold), AutoSize = true, Location = new Point(39, 94) };
            var body = new Label
            {
                Text = "하나의 설치기가 신규 설치, 업데이트, 복구 설치를 자동으로 판단합니다. 업데이트 중에는 기존 파일을 백업하고 새 버전 실행이 확인되지 않으면 자동 복원합니다.",
                ForeColor = Color.FromArgb(102, 112, 133),
                Location = new Point(38, 124),
                Size = new Size(644, 52)
            };

            var consentTitle = new Label { Text = "필수 이용 동의", ForeColor = Color.FromArgb(30, 41, 59), Font = new Font("Segoe UI", 10F, FontStyle.Bold), AutoSize = true, Location = new Point(38, 188) };
            var consentVersion = new Label { Text = MeterConsentContract.DocumentVersion, ForeColor = Color.FromArgb(79, 70, 229), Font = new Font("Segoe UI", 8F, FontStyle.Bold), AutoSize = true, Location = new Point(536, 191) };
            var consentDetails = new RichTextBox
            {
                Text = MeterConsentContract.DisplayText,
                ReadOnly = true,
                DetectUrls = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(38, 214),
                Size = new Size(644, 236),
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabStop = false
            };
            var aion2Policy = new LinkLabel { Text = "AION2 운영정책 확인", AutoSize = true, Location = new Point(38, 462), LinkColor = Color.FromArgb(37, 99, 235) };
            aion2Policy.LinkClicked += delegate { MeterConsentContract.OpenUrl(MeterConsentContract.Aion2PolicyUrl); };
            var privacy = new LinkLabel { Text = "KINOJO 개인정보처리방침", AutoSize = true, Location = new Point(180, 462), LinkColor = Color.FromArgb(37, 99, 235) };
            privacy.LinkClicked += delegate { MeterConsentContract.OpenUrl(MeterConsentContract.PrivacyUrl); };

            var preAccepted = MeterConsentContract.HasCurrentReceipt();
            _serviceRiskConsent = new CheckBox
            {
                Text = "[필수] 비공식 외부 프로그램 사용에 따른 위험과 서비스 변경·종료 가능성을 확인했습니다.",
                Checked = preAccepted,
                AutoSize = false,
                Location = new Point(38, 492),
                Size = new Size(644, 40),
                ForeColor = Color.FromArgb(51, 65, 85)
            };
            _statisticsConsent = new CheckBox
            {
                Text = "[필수] 전투 통계 수집 항목·목적·보유 기간과 철회 방법을 확인하고 동의합니다.",
                Checked = preAccepted,
                AutoSize = false,
                Location = new Point(38, 534),
                Size = new Size(644, 40),
                ForeColor = Color.FromArgb(51, 65, 85)
            };
            _serviceRiskConsent.CheckedChanged += delegate { RefreshConsentState(); };
            _statisticsConsent.CheckedChanged += delegate { RefreshConsentState(); };

            var pathTitle = new Label { Text = "설치 위치", ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Location = new Point(38, 591) };
            _path = new TextBox { Text = _engine.InstallPath, Location = new Point(38, 615), Size = new Size(538, 31), BorderStyle = BorderStyle.FixedSingle };
            _browse = new Button { Text = "변경", Location = new Point(586, 613), Size = new Size(96, 34), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, BackColor = Color.FromArgb(226, 232, 240), ForeColor = Color.FromArgb(30, 41, 59) };
            _browse.FlatAppearance.BorderColor = Color.FromArgb(148, 163, 184);
            _browse.Click += BrowseClicked;

            _desktopShortcut = new CheckBox
            {
                Text = "바탕화면 바로가기 만들기",
                Checked = _engine.Mode == SetupMode.NewInstall || _engine.ExistingDesktopShortcut,
                AutoSize = true,
                Location = new Point(38, 664),
                ForeColor = Color.FromArgb(71, 85, 105)
            };
            _status = new Label { ForeColor = Color.FromArgb(79, 70, 229), Location = new Point(38, 718), Size = new Size(420, 48) };
            _install = new Button
            {
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(482, 707),
                Size = new Size(200, 50),
                Cursor = Cursors.Hand
            };
            _install.FlatAppearance.BorderSize = 0;
            _install.Click += InstallClicked;

            Controls.Add(kicker);
            Controls.Add(title);
            Controls.Add(_mode);
            Controls.Add(body);
            Controls.Add(consentTitle);
            Controls.Add(consentVersion);
            Controls.Add(consentDetails);
            Controls.Add(aion2Policy);
            Controls.Add(privacy);
            Controls.Add(_serviceRiskConsent);
            Controls.Add(_statisticsConsent);
            Controls.Add(pathTitle);
            Controls.Add(_path);
            Controls.Add(_browse);
            Controls.Add(_desktopShortcut);
            Controls.Add(_status);
            Controls.Add(_install);

            RefreshModeUi();
            RefreshConsentState();
        }

        private void RefreshModeUi()
        {
            _mode.Text = _engine.ModeLabel + (_engine.Mode == SetupMode.NewInstall || String.IsNullOrWhiteSpace(_engine.ExistingVersion)
                ? ""
                : " · 설치된 버전 " + _engine.ExistingVersion);
            _path.Text = _engine.InstallPath;
            var existing = _engine.Mode != SetupMode.NewInstall;
            _path.ReadOnly = existing;
            _browse.Enabled = !existing;

            if (_engine.Mode == SetupMode.NewInstall)
            {
                _install.Text = "설치하고 실행";
                _status.Text = "신규 설치 준비 완료";
            }
            else if (_engine.Mode == SetupMode.Update)
            {
                _install.Text = "업데이트하고 실행";
                _status.Text = "기존 설정과 바로가기를 유지해 안전하게 업데이트합니다.";
            }
            else if (_engine.Mode == SetupMode.Repair)
            {
                _install.Text = "복구 설치하고 실행";
                _status.Text = "같은 버전의 누락되거나 손상된 프로그램 파일을 복구합니다.";
            }
            else
            {
                _install.Text = "설치할 수 없음";
                _install.Enabled = false;
                _status.Text = "현재 설치된 버전이 이 설치기보다 최신입니다.";
            }
            RefreshConsentState();
        }

        private void RefreshConsentState()
        {
            if (_install == null || _serviceRiskConsent == null || _statisticsConsent == null) return;
            var accepted = _serviceRiskConsent.Checked && _statisticsConsent.Checked;
            _install.Enabled = _engine.Mode != SetupMode.DowngradeBlocked && accepted;
            if (_engine.Mode != SetupMode.DowngradeBlocked && !accepted)
                _status.Text = "두 필수 항목을 모두 확인하고 동의해야 설치할 수 있습니다.";
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "KINOJO Meter 설치 폴더를 선택하세요.", SelectedPath = _path.Text, ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
            }
        }

        private void InstallClicked(object sender, EventArgs e)
        {
            if (!_serviceRiskConsent.Checked || !_statisticsConsent.Checked)
            {
                UpdateStatus("두 필수 항목을 모두 확인하고 동의해 주세요.");
                RefreshConsentState();
                return;
            }
            _install.Enabled = false;
            _path.Enabled = false;
            _browse.Enabled = false;
            try
            {
                var requestedPath = (_path.Text ?? "").Trim();
                if (String.IsNullOrWhiteSpace(requestedPath)) throw new InvalidOperationException("설치 위치를 선택해 주세요.");
                _options.InstallPath = Path.GetFullPath(requestedPath);
                _options.PathSpecified = true;
                _engine = new SetupEngine(_options, UpdateStatus);
                var launch = !_options.Silent || _options.Launch;
                _engine.Install(_desktopShortcut.Checked, launch);
                MeterConsentContract.WriteReceipt(SetupVersionInfo.Current);
                InstallSucceeded = true;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                InstallSucceeded = false;
                UpdateStatus("설치 실패: " + ex.Message);
                _install.Enabled = _engine.Mode != SetupMode.DowngradeBlocked;
                _path.Enabled = true;
                _browse.Enabled = _engine.Mode == SetupMode.NewInstall;
                RefreshConsentState();
            }
        }

        public void InstallSilent()
        {
            if (!_serviceRiskConsent.Checked || !_statisticsConsent.Checked)
            {
                InstallSucceeded = false;
                UpdateStatus("필수 동의 이력이 없어 자동 설치를 중단했습니다.");
                return;
            }
            _desktopShortcut.Checked = _engine.Mode == SetupMode.NewInstall || _engine.ExistingDesktopShortcut;
            InstallClicked(this, EventArgs.Empty);
        }

        private void UpdateStatus(string message)
        {
            _status.Text = message ?? "";
            _status.Refresh();
            Refresh();
            Application.DoEvents();
        }
    }

    internal static class SetupBootstrap
    {
        public static bool RelaunchOutsideInstallIfNeeded(SetupOptions options, string[] args)
        {
            if (options == null || options.Uninstall || options.Relocated) return false;
            var snapshot = InstallationSnapshot.Detect(options.PathSpecified ? options.InstallPath : null);
            if (!snapshot.Exists || String.IsNullOrWhiteSpace(snapshot.InstallPath)) return false;

            var current = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            var installRoot = Path.GetFullPath(snapshot.InstallPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!current.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase)) return false;

            var tempDirectory = Path.Combine(Path.GetTempPath(), "KINOJO-Meter-Setup", SetupVersionInfo.Current, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var relocated = Path.Combine(tempDirectory, "KINOJO_Meter_" + SetupVersionInfo.Current + "_Setup.exe");
            File.Copy(current, relocated, true);

            var forwarded = (args ?? new string[0]).ToList();
            forwarded.Add("/relocated");
            if (!options.PathSpecified)
            {
                forwarded.Add("/path");
                forwarded.Add(snapshot.InstallPath);
            }
            Process.Start(new ProcessStartInfo(relocated, String.Join(" ", forwarded.Select(QuoteArgument)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = tempDirectory
            });
            return true;
        }

        private static string QuoteArgument(string value)
        {
            value = value ?? "";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var options = SetupOptions.Parse(args);
            if (SetupBootstrap.RelaunchOutsideInstallIfNeeded(options, args)) return;

            if (options.Uninstall)
            {
                Uninstall(options);
                return;
            }
            if (options.Silent)
            {
                using (var form = new SetupForm(options))
                {
                    form.InstallSilent();
                    if (!form.InstallSucceeded) Environment.ExitCode = 1;
                }
                return;
            }
            Application.Run(new SetupForm(options));
        }

        private static void Uninstall(SetupOptions options)
        {
            var snapshot = InstallationSnapshot.Detect(options.PathSpecified ? options.InstallPath : null);
            var installPath = snapshot.InstallPath;
            if (!options.Silent)
            {
                var answer = MessageBox.Show(
                    "KINOJO Meter 프로그램 파일을 제거할까요?\n\n사용자 설정과 진단 로그는 다음 설치를 위해 유지됩니다.",
                    "KINOJO Meter 제거",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }

            try
            {
                SetupEngine.StopRunningMeter();
                InstallerRegistry.Remove();
                ShortcutManager.DeleteKnownShortcuts();
                ScheduleDirectoryRemoval(installPath);
                if (!options.Silent) MessageBox.Show("KINOJO Meter 제거를 완료했습니다. 사용자 설정은 유지됩니다.", "KINOJO Meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!options.Silent) MessageBox.Show("제거하지 못했습니다.\n\n" + ex.Message, "KINOJO Meter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Environment.ExitCode = 1;
            }
        }

        private static void ScheduleDirectoryRemoval(string installPath)
        {
            if (String.IsNullOrWhiteSpace(installPath)) return;
            SetupEngine.ValidateManagedDirectory(installPath);
            var escaped = installPath.Replace("\"", "\"\"");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + escaped + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
    }
}
