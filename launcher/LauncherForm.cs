using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherForm : Form
    {
        private readonly TextBox _passKey;
        private readonly Button _start;
        private readonly Label _status;
        private readonly Label _version;
        private readonly ProgressBar _progress;
        private CancellationTokenSource _cancellation;

        public LauncherForm()
        {
            Text = "KINOJO Meter Launcher " + LauncherVersion.Current;
            ClientSize = new Size(520, 350);
            MinimumSize = new Size(520, 390);
            MaximumSize = new Size(520, 390);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            Controls.Add(new Label
            {
                Text = "KINOJO METER",
                ForeColor = Color.FromArgb(37, 99, 235),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(36, 29)
            });
            Controls.Add(new Label
            {
                Text = "런처에서 최신 미터기를 준비합니다.",
                ForeColor = Color.FromArgb(15, 23, 42),
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(32, 53)
            });
            Controls.Add(new Label
            {
                Text = "PASS KEY 인증 후 비공개 Core를 검증·업데이트하고 실행합니다.\r\n업데이트 작업은 미터기 실행 전에 끝나므로 실시간 DPS 연산에는 참여하지 않습니다.",
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(36, 99),
                Size = new Size(448, 52)
            });
            Controls.Add(new Label
            {
                Text = "6자리 PASS KEY",
                ForeColor = Color.FromArgb(71, 85, 105),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(36, 164)
            });

            _passKey = new TextBox
            {
                Location = new Point(36, 188),
                Size = new Size(448, 38),
                MaxLength = 6,
                CharacterCasing = CharacterCasing.Upper,
                UseSystemPasswordChar = true,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                BorderStyle = BorderStyle.FixedSingle
            };
            _passKey.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                await StartMeterAsync();
            };
            Controls.Add(_passKey);

            _progress = new ProgressBar
            {
                Location = new Point(36, 241),
                Size = new Size(448, 8),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            Controls.Add(_progress);

            _status = new Label
            {
                Text = "PASS KEY를 입력해 주세요.",
                ForeColor = Color.FromArgb(71, 85, 105),
                Location = new Point(36, 260),
                Size = new Size(320, 44)
            };
            Controls.Add(_status);

            _start = new Button
            {
                Text = "미터기 실행",
                Location = new Point(354, 263),
                Size = new Size(130, 42),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            _start.FlatAppearance.BorderSize = 0;
            _start.Click += async delegate { await StartMeterAsync(); };
            Controls.Add(_start);

            _version = new Label
            {
                Text = CurrentVersionText(),
                ForeColor = Color.FromArgb(100, 116, 139),
                Font = new Font("Segoe UI", 8F),
                AutoSize = true,
                Location = new Point(36, 321)
            };
            Controls.Add(_version);
            FormClosing += delegate { _cancellation?.Cancel(); };
            Shown += delegate { _passKey.Focus(); };
        }

        private string CurrentVersionText()
        {
            using (var installer = new CorePackageInstaller())
            {
                var active = installer.ReadActiveState();
                return "Launcher " + LauncherVersion.Current + " · Core " + (active == null ? "설치 전" : active.CoreVersion);
            }
        }

        private async Task StartMeterAsync()
        {
            if (!_start.Enabled) return;
            var passKey = (_passKey.Text ?? "").Trim().ToUpperInvariant();
            if (passKey.Length != 6)
            {
                SetStatus("PASS KEY 6자리를 입력해 주세요.", true);
                _passKey.Focus();
                return;
            }

            _start.Enabled = false;
            _passKey.Enabled = false;
            _progress.Value = 0;
            _cancellation = new CancellationTokenSource();
            var sessionToken = "";
            try
            {
                SetStatus("KINOJO Server에서 이용 권한을 확인하는 중입니다.", false);
                using (var api = new LauncherApiClient())
                using (var installer = new CorePackageInstaller())
                {
                    try
                    {
                        var login = await api.LoginAsync(passKey);
                        sessionToken = login.SessionToken;
                        if (String.IsNullOrWhiteSpace(sessionToken)) throw new InvalidOperationException("Server 세션을 받지 못했습니다.");
                        _passKey.Clear();

                        var installationId = LauncherPaths.GetOrCreateInstallationId();
                        var current = installer.ReadActiveState();
                        SetStatus("최신 Core 버전과 운영 상태를 확인하는 중입니다.", false);
                        var authorization = await api.AuthorizeCoreUpdateAsync(
                            sessionToken,
                            installationId,
                            current == null ? "" : current.CoreVersion);
                        if (!authorization.Authorized || authorization.Release == null)
                            throw new InvalidOperationException(String.IsNullOrWhiteSpace(authorization.Message)
                                ? "현재 Core 다운로드가 허용되지 않았습니다."
                                : authorization.Message);

                        SetStatus(current != null && String.Equals(current.CoreVersion, authorization.Release.CoreVersion, StringComparison.Ordinal)
                            ? "설치된 Core 무결성을 확인하는 중입니다."
                            : "최신 Core를 안전하게 내려받는 중입니다.", false);
                        var progress = new Progress<int>(value =>
                        {
                            _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, value));
                        });
                        var install = await installer.EnsureInstalledAsync(
                            authorization.Release,
                            api.ProjectHost,
                            progress,
                            _cancellation.Token);

                        SetStatus("Core 실행을 확인하는 중입니다.", false);
                        await installer.LaunchAndVerifyAsync(install, login, installationId);
                        _version.Text = "Launcher " + LauncherVersion.Current + " · Core " + install.Active.CoreVersion;
                        sessionToken = ""; // Core가 stdin으로 세션을 인계받았으므로 Launcher는 폐기한다.
                        SetStatus("KINOJO Meter가 실행되었습니다.", false);
                        _progress.Value = 100;
                        await Task.Delay(350);
                        Close();
                    }
                    catch
                    {
                        await api.LogoutAsync(sessionToken);
                        sessionToken = "";
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("작업이 취소되었습니다.", true);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message, true);
            }
            finally
            {
                sessionToken = "";
                _passKey.Clear();
                if (!IsDisposed)
                {
                    _start.Enabled = true;
                    _passKey.Enabled = true;
                    _passKey.Focus();
                }
            }
        }

        private void SetStatus(string text, bool error)
        {
            _status.Text = String.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
            _status.ForeColor = error ? Color.FromArgb(185, 28, 28) : Color.FromArgb(37, 99, 235);
        }
    }
}
