using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherPassKeyInput : UserControl
    {
        private readonly TextBox _input;

        public LauncherPassKeyInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor |
                     ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.IBeam;
            TabStop = true;
            AccessibleName = "키노조 웹 PASS KEY 입력";
            Font = new Font("Malgun Gothic", 15.5F, FontStyle.Bold, GraphicsUnit.Point);

            _input = new TextBox
            {
                BorderStyle = BorderStyle.None,
                CharacterCasing = CharacterCasing.Normal,
                Font = new Font("Malgun Gothic", 9F, FontStyle.Regular, GraphicsUnit.Point),
                ForeColor = Color.FromArgb(16, 20, 30),
                BackColor = Color.FromArgb(16, 20, 30),
                AutoSize = false,
                ImeMode = ImeMode.Off,
                MaxLength = LauncherPassKeyContract.RequiredTextElements,
                Multiline = false,
                ShortcutsEnabled = true,
                TabStop = true,
                UseSystemPasswordChar = false,
                PasswordChar = '\0'
            };
            _input.TextChanged += delegate
            {
                UppercaseAsciiInput();
                Invalidate();
            };
            _input.KeyPress += delegate(object sender, KeyPressEventArgs args)
            {
                if (args.KeyChar >= 'a' && args.KeyChar <= 'z')
                    args.KeyChar = Char.ToUpperInvariant(args.KeyChar);
            };
            _input.Enter += delegate { Invalidate(); };
            _input.Leave += delegate { Invalidate(); };
            _input.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                SubmitRequested?.Invoke(this, EventArgs.Empty);
            };
            Controls.Add(_input);
            PositionInputHost();
        }

        public event EventHandler SubmitRequested;

        public string PassKey
        {
            get { return _input.Text ?? ""; }
        }

        public void ClearPassKey()
        {
            _input.Clear();
        }

        public void FocusInput()
        {
            if (_input.CanFocus) _input.Focus();
        }

        protected override void OnMouseDown(MouseEventArgs args)
        {
            base.OnMouseDown(args);
            FocusInput();
        }

        protected override void OnResize(EventArgs args)
        {
            base.OnResize(args);
            PositionInputHost();
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var values = LauncherPassKeyContract.TextElements(_input.Text);
            const int cellCount = LauncherPassKeyContract.RequiredTextElements;
            var activeIndex = values.Length >= cellCount ? cellCount - 1 : values.Length;
            var outer = new Rectangle(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1));

            using (var background = new SolidBrush(Color.FromArgb(16, 20, 30)))
            using (var border = new Pen(Color.FromArgb(48, 57, 76), 1F))
            {
                args.Graphics.FillRectangle(background, outer);
                args.Graphics.DrawRectangle(border, outer);
                for (var divider = 1; divider < cellCount; divider++)
                {
                    var x = ClientSize.Width * divider / cellCount;
                    args.Graphics.DrawLine(border, x, 8, x, Math.Max(8, ClientSize.Height - 9));
                }
            }

            for (var index = 0; index < cellCount; index++)
            {
                var left = ClientSize.Width * index / cellCount;
                var right = ClientSize.Width * (index + 1) / cellCount;
                var rectangle = new Rectangle(left, 0, Math.Max(1, right - left), Math.Max(1, ClientSize.Height));
                var active = _input.Focused && index == activeIndex;
                if (active)
                {
                    using (var activeBorder = new Pen(LauncherPalette.AccentBright, 2F))
                    {
                        var activeRectangle = new Rectangle(rectangle.X + 1, 1, Math.Max(0, rectangle.Width - 2), Math.Max(0, rectangle.Height - 2));
                        args.Graphics.DrawRectangle(activeBorder, activeRectangle);
                    }
                }

                if (index < values.Length)
                {
                    TextRenderer.DrawText(
                        args.Graphics,
                        values[index],
                        Font,
                        rectangle,
                        LauncherPalette.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }
        }

        private void PositionInputHost()
        {
            if (_input == null) return;
            _input.Location = new Point(-4, -4);
            _input.Size = new Size(1, 1);
        }

        private void UppercaseAsciiInput()
        {
            var current = _input.Text ?? "";
            var characters = current.ToCharArray();
            var changed = false;
            for (var index = 0; index < characters.Length; index++)
            {
                if (characters[index] < 'a' || characters[index] > 'z') continue;
                characters[index] = Char.ToUpperInvariant(characters[index]);
                changed = true;
            }
            if (!changed) return;

            var selectionStart = _input.SelectionStart;
            _input.Text = new string(characters);
            _input.SelectionStart = Math.Min(selectionStart, _input.TextLength);
        }
    }

    internal sealed class LauncherLoginForm : LauncherWindowForm
    {
        private readonly LauncherPassKeyInput _passKey;
        private readonly LauncherActionButton _loginButton;
        private readonly Label _status;
        private readonly LauncherProgressBar _progress;
        private bool _busy;
        private bool _launcherUpdateRequired;

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
            topbar.Controls.Add(CreateBrandIcon(new Point(18, 18), new Size(28, 28)));
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
                "키노조 웹 PASS KEY로 로그인하면 KINOJO Meter MAIN으로 이동합니다.",
                new Font("Segoe UI", 9F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(44, 89),
                new Size(430, 42)));

            var keyLabel = CreateLabel(
                "키노조 웹 PASS KEY 입력",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(44, 143),
                new Size(220, 20));
            content.Controls.Add(keyLabel);

            _passKey = new LauncherPassKeyInput
            {
                Location = new Point(44, 169),
                Size = new Size(430, 54),
                Enabled = false
            };
            _passKey.SubmitRequested += async delegate { await HandlePrimaryActionAsync(); };
            content.Controls.Add(_passKey);

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
                Size = new Size(430, 52),
                Enabled = false
            };
            _loginButton.Click += async delegate { await HandlePrimaryActionAsync(); };
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
            Shown += async delegate { await CheckLauncherUpdateOnStartupAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (_busy && DialogResult != DialogResult.OK) args.Cancel = true;
            };
        }

        public LauncherLoginResult LoginResult { get; private set; }

        private async Task HandlePrimaryActionAsync()
        {
            if (_launcherUpdateRequired) await CheckLauncherUpdateOnStartupAsync();
            else await LoginAsync();
        }

        private async Task CheckLauncherUpdateOnStartupAsync()
        {
            if (_busy) return;
            _busy = true;
            _launcherUpdateRequired = false;
            _loginButton.Enabled = false;
            _loginButton.Text = "Launcher 확인 중...";
            _passKey.Enabled = false;
            SetStatus("최신 Launcher 버전을 확인하고 있습니다.", false, 8);
            var updateDetected = false;
            var installerStarted = false;
            try
            {
                LauncherUpdateCheckResult check;
                using (var api = new LauncherApiClient())
                    check = await api.CheckLauncherUpdateAsync();

                if (check != null && check.ReleaseAvailable && check.Release != null)
                {
                    using (var updater = new LauncherUpdateService())
                    {
                        var calculated = updater.IsUpdateAvailable(check.Release);
                        if (calculated != check.UpdateAvailable)
                            throw new InvalidOperationException("Launcher 업데이트 버전 판정이 Server와 일치하지 않습니다.");
                        if (calculated)
                        {
                            updateDetected = true;
                            _launcherUpdateRequired = true;
                            var progress = new Progress<LauncherUpdateProgress>(value =>
                            {
                                var percentage = value == null ? 0 : Math.Max(0, Math.Min(100, value.Percentage));
                                SetStatus(value == null ? "Launcher 업데이트 중" : value.Stage, false, percentage);
                            });
                            installerStarted = await updater.DownloadAndLaunchAsync(check.Release, progress, CancellationToken.None);
                            if (!installerStarted) throw new InvalidOperationException("Launcher 업데이트 설치기를 시작하지 못했습니다.");
                        }
                    }
                }

                if (installerStarted)
                {
                    SetStatus("Launcher를 업데이트한 뒤 자동으로 다시 실행합니다.", false, 100);
                    _busy = false;
                    Close();
                    return;
                }

                SetStatus("Launcher 최신 버전입니다. PASS KEY를 입력해 주세요.", false, 0);
            }
            catch (Exception error)
            {
                if (updateDetected)
                {
                    _launcherUpdateRequired = true;
                    SetStatus(error.Message, true, _progress.Value);
                }
                else
                {
                    SetStatus("Launcher 업데이트 확인을 건너뛰었습니다. PASS KEY 로그인을 계속할 수 있습니다.", false, 0);
                }
            }
            finally
            {
                _busy = false;
                if (!IsDisposed && !installerStarted)
                {
                    _loginButton.Enabled = true;
                    _loginButton.Text = _launcherUpdateRequired ? "업데이트 다시 시도" : "로그인";
                    _passKey.Enabled = !_launcherUpdateRequired;
                    if (!_launcherUpdateRequired) _passKey.FocusInput();
                }
            }
        }

        private async Task LoginAsync()
        {
            if (_busy) return;
            var passKey = LauncherPassKeyContract.Normalize(_passKey.PassKey);
            if (!LauncherPassKeyContract.IsValid(passKey))
            {
                SetStatus("키노조 웹 PASS KEY 6자리를 입력해 주세요.", true, 0);
                _passKey.FocusInput();
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
                _passKey.ClearPassKey();
                if (!IsDisposed && DialogResult != DialogResult.OK)
                {
                    _loginButton.Enabled = true;
                    _loginButton.Text = _launcherUpdateRequired ? "업데이트 다시 시도" : "다시 로그인";
                    _passKey.Enabled = !_launcherUpdateRequired;
                    if (!_launcherUpdateRequired) _passKey.FocusInput();
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
