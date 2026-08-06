using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherLoginForm : LauncherWindowForm
    {
        private readonly TextBox _passKey;
        private readonly LauncherActionButton _loginButton;
        private readonly Label _status;
        private readonly LauncherProgressBar _progress;
        private bool _busy;

        public LauncherLoginForm()
        {
            Text = "KINOJO PASS KEY 로그인" + LauncherBuildProfile.DisplaySuffix;
            ClientSize = new Size(520, 470);
            MinimumSize = new Size(520, 470);
            MaximumSize = new Size(520, 470);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherPalette.Window,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var topbar = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = LauncherPalette.Topbar
            };
            AttachTitleBar(topbar);
            topbar.Controls.Add(CreateWindowControls(false));
            topbar.Controls.Add(new Label
            {
                Text = "K",
                BackColor = LauncherPalette.Accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(18, 18),
                Size = new Size(28, 28)
            });
            topbar.Controls.Add(CreateLabel(
                "KINOJO LOGIN",
                new Font("Segoe UI", 10F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(56, 20),
                new Size(240, 26)));
            if (LauncherVersion.IsStaging)
            {
                topbar.Controls.Add(CreateLabel(
                    "테스트 버전",
                    new Font("Segoe UI", 7.5F, FontStyle.Bold),
                    LauncherPalette.AccentBright,
                    new Point(180, 22),
                    new Size(100, 22)));
            }
            root.Controls.Add(topbar, 0, 0);

            var content = new LauncherBackdrop
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            root.Controls.Add(content, 0, 1);

            content.Controls.Add(CreateLabel(
                "PASS KEY 로그인",
                new Font("Segoe UI", 22F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(42, 38),
                new Size(430, 46)));
            content.Controls.Add(CreateLabel(
                "승인된 PASS KEY로 로그인하면 KINOJO Meter MAIN으로 이동합니다.",
                new Font("Segoe UI", 9F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(44, 89),
                new Size(430, 42)));

            var keyLabel = CreateLabel(
                "6자리 PASS KEY",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(44, 143),
                new Size(220, 20));
            content.Controls.Add(keyLabel);

            var keyFrame = new Panel
            {
                BackColor = Color.FromArgb(48, 57, 76),
                Location = new Point(44, 169),
                Size = new Size(430, 54),
                Padding = new Padding(1)
            };
            content.Controls.Add(keyFrame);
            var keyInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(16, 20, 30),
                Padding = new Padding(16, 9, 16, 7)
            };
            keyFrame.Controls.Add(keyInner);
            _passKey = new TextBox
            {
                Dock = DockStyle.Fill,
                MaxLength = 6,
                CharacterCasing = CharacterCasing.Upper,
                UseSystemPasswordChar = true,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI", 16F, FontStyle.Bold),
                BorderStyle = BorderStyle.None,
                BackColor = keyInner.BackColor,
                ForeColor = LauncherPalette.Text
            };
            _passKey.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                await LoginAsync();
            };
            keyInner.Controls.Add(_passKey);

            _progress = new LauncherProgressBar
            {
                Location = new Point(44, 240),
                Size = new Size(430, 7),
                Value = 0
            };
            content.Controls.Add(_progress);

            _status = CreateLabel(
                "PASS KEY를 입력해 주세요.",
                new Font("Segoe UI", 8.5F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(44, 260),
                new Size(430, 38));
            content.Controls.Add(_status);

            _loginButton = new LauncherActionButton
            {
                Text = "로그인",
                Location = new Point(44, 306),
                Size = new Size(430, 52)
            };
            _loginButton.Click += async delegate { await LoginAsync(); };
            content.Controls.Add(_loginButton);

            var security = CreateLabel(
                "로그인 세션은 KINOJO Meter 실행에만 사용됩니다.",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                Color.FromArgb(126, 137, 156),
                new Point(44, 371),
                new Size(430, 18));
            security.TextAlign = ContentAlignment.TopCenter;
            content.Controls.Add(security);

            AcceptButton = _loginButton;
            Shown += delegate { _passKey.Focus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (_busy && DialogResult != DialogResult.OK) args.Cancel = true;
            };
        }

        public LauncherLoginResult LoginResult { get; private set; }

        private async Task LoginAsync()
        {
            if (_busy) return;
            var passKey = (_passKey.Text ?? "").Trim().ToUpperInvariant();
            if (passKey.Length != 6)
            {
                SetStatus("PASS KEY 6자리를 입력해 주세요.", true, 0);
                _passKey.Focus();
                return;
            }

            _busy = true;
            _loginButton.Enabled = false;
            _loginButton.Text = "로그인 확인 중...";
            _passKey.Enabled = false;
            SetStatus("KINOJO 서버에서 이용 권한을 확인하고 있습니다.", false, 28);
            try
            {
                using (var api = new LauncherApiClient())
                {
                    var login = await api.LoginAsync(passKey);
                    if (login == null || String.IsNullOrWhiteSpace(login.SessionToken))
                        throw new InvalidOperationException("서버 로그인 세션을 받지 못했습니다.");
                    LoginResult = login;
                }
                SetStatus("로그인되었습니다. MAIN 화면을 준비합니다.", false, 100);
                await Task.Delay(250);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error)
            {
                SetStatus(error.Message, true, 28);
            }
            finally
            {
                _busy = false;
                _passKey.Clear();
                if (!IsDisposed && DialogResult != DialogResult.OK)
                {
                    _loginButton.Enabled = true;
                    _loginButton.Text = "다시 로그인";
                    _passKey.Enabled = true;
                    _passKey.Focus();
                }
            }
        }

        private void SetStatus(string text, bool error, int progress)
        {
            _status.Text = String.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
            _status.ForeColor = error ? LauncherPalette.Error : LauncherPalette.Muted;
            _progress.Error = error;
            _progress.Value = progress;
        }

        private static Label CreateLabel(string text, Font font, Color color, Point location, Size size)
        {
            return new Label
            {
                Text = text,
                Font = font,
                ForeColor = color,
                BackColor = Color.Transparent,
                Location = location,
                Size = size,
                AutoEllipsis = true,
                UseCompatibleTextRendering = false
            };
        }
    }
}
