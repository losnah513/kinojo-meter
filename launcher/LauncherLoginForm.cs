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
            AccessibleName = "PASS KEY 입력";
            Font = new Font("Malgun Gothic", 12F, FontStyle.Bold, GraphicsUnit.Point);

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
                    var inset = Math.Max(4, ClientSize.Height / 6);
                    args.Graphics.DrawLine(border, x, inset, x, Math.Max(inset, ClientSize.Height - inset - 1));
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
        private readonly Label _loginTitle;
        private readonly Label _loginBrand;
        private bool _busy;
        private bool _launcherUpdateRequired;

        public LauncherLoginForm()
            : this(false)
        {
        }

        internal LauncherLoginForm(bool suppressStartup)
        {
            Text = "KINOJO PASS KEY 로그인" + LauncherBuildProfile.DisplaySuffix;
            ClientSize = new Size(360, 320);
            MinimumSize = new Size(360, 320);
            MaximumSize = new Size(360, 320);
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
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var topbar = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = LauncherPalette.Topbar
            };
            AttachTitleBar(topbar);
            topbar.Controls.Add(CreateWindowControls(false, 48, 34));
            topbar.Controls.Add(CreateBrandIcon(new Point(14, 14), new Size(20, 20)));
            _loginBrand = CreateLabel(
                "KINOJO LAUNCHER LOGIN" + (LauncherVersion.IsStaging ? " · TEST" : ""),
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(48, 9),
                new Size(244, 30));
            _loginBrand.TextAlign = ContentAlignment.MiddleCenter;
            topbar.Controls.Add(_loginBrand);
            root.Controls.Add(topbar, 0, 0);

            var content = new LauncherBackdrop
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            root.Controls.Add(content, 0, 1);

            _loginTitle = CreateLabel(
                "PASS KEY 로그인",
                new Font("Segoe UI", 17F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(24, 18),
                new Size(312, 38));
            _loginTitle.TextAlign = ContentAlignment.MiddleCenter;
            content.Controls.Add(_loginTitle);

            _passKey = new LauncherPassKeyInput
            {
                // Six 45x30 cells: each input rectangle is exactly 3:2.
                Location = new Point(45, 66),
                Size = new Size(270, 30),
                Enabled = false
            };
            _passKey.SubmitRequested += async delegate { await HandlePrimaryActionAsync(); };
            content.Controls.Add(_passKey);

            _progress = new LauncherProgressBar
            {
                Location = new Point(45, 139),
                Size = new Size(270, 5),
                Value = 0
            };
            content.Controls.Add(_progress);

            _status = CreateLabel(
                "PASS KEY를 입력해 주세요.",
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(30, 101),
                new Size(300, 32));
            _status.TextAlign = ContentAlignment.MiddleCenter;
            content.Controls.Add(_status);

            _loginButton = new LauncherActionButton
            {
                Text = "로그인",
                Location = new Point(45, 158),
                Size = new Size(270, 40),
                Enabled = false
            };
            _loginButton.Click += async delegate { await HandlePrimaryActionAsync(); };
            content.Controls.Add(_loginButton);

            var security = CreateLabel(
                "로그인 세션은 KINOJO Meter 실행에만 사용됩니다.",
                new Font("Segoe UI", 7.2F, FontStyle.Regular),
                Color.FromArgb(126, 137, 156),
                new Point(30, 217),
                new Size(300, 18));
            security.TextAlign = ContentAlignment.MiddleCenter;
            content.Controls.Add(security);

            AcceptButton = _loginButton;
            if (!suppressStartup) Shown += async delegate { await CheckLauncherUpdateOnStartupAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args)
            {
                if (_busy && DialogResult != DialogResult.OK) args.Cancel = true;
            };
        }

        public LauncherLoginResult LoginResult { get; private set; }

        internal bool VisualContractForTesting
        {
            get
            {
                var cellWidth = _passKey.Width / (double)LauncherPassKeyContract.RequiredTextElements;
                return ClientSize == new Size(360, 320) &&
                    _loginBrand.Text.StartsWith("KINOJO LAUNCHER LOGIN", StringComparison.Ordinal) &&
                    _loginBrand.TextAlign == ContentAlignment.MiddleCenter &&
                    _loginTitle.TextAlign == ContentAlignment.MiddleCenter &&
                    Math.Abs(cellWidth / _passKey.Height - 1.5) < 0.001 &&
                    _status.Top >= _passKey.Bottom && _status.TextAlign == ContentAlignment.MiddleCenter &&
                    !ContainsVisibleText(this, "키노조 웹 PASS KEY로 로그인하면") &&
                    !ContainsVisibleText(this, "키노조 웹 PASS KEY 입력");
            }
        }

        private static bool ContainsVisibleText(Control root, string value)
        {
            if (root == null) return false;
            foreach (Control child in root.Controls)
            {
                if (child.Visible && String.Equals(child.Text, value, StringComparison.Ordinal)) return true;
                if (ContainsVisibleText(child, value)) return true;
            }
            return false;
        }

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

                SetStatus("런처 최신 버전입니다.", false, 0);
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
                SetStatus("PASS KEY 6자리를 입력해 주세요.", true, 0);
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
