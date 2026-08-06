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
        private readonly LauncherActionButton _start;
        private readonly Label _status;
        private readonly Label _statusTitle;
        private readonly Label _progressText;
        private readonly Label _version;
        private Label _sidebarCore;
        private readonly LauncherProgressBar _progress;
        private readonly LauncherCard _launchCard;
        private readonly LauncherCard _noticeCard;
        private readonly Label _heroTitle;
        private readonly Label _heroDescription;
        private CancellationTokenSource _cancellation;

        public LauncherForm()
        {
            SuspendLayout();
            Text = "KINOJO Meter Launcher" + LauncherBuildProfile.DisplaySuffix + " " + LauncherVersion.Current;
            ClientSize = new Size(1180, 720);
            MinimumSize = new Size(1120, 680);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = LauncherPalette.Window;
            ForeColor = LauncherPalette.Text;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            KeyPreview = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherPalette.Window,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 214F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            Controls.Add(root);

            var sidebar = BuildSidebar();
            root.Controls.Add(sidebar, 0, 0);

            var workspace = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = LauncherPalette.Window,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
            workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(workspace, 1, 0);
            workspace.Controls.Add(BuildTopbar(), 0, 0);

            var content = new LauncherBackdrop
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            workspace.Controls.Add(content, 0, 1);

            var eyebrow = CreateLabel(
                "KINOJO METER" + LauncherBuildProfile.DisplaySuffix,
                new Font("Segoe UI", 9F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(48, 43),
                new Size(380, 22));
            content.Controls.Add(eyebrow);

            _heroTitle = CreateLabel(
                "전투의 흐름을\r\n더 선명하게.",
                new Font("Segoe UI", 30F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(43, 72),
                new Size(430, 108));
            content.Controls.Add(_heroTitle);

            _heroDescription = CreateLabel(
                "PASS KEY 하나로 최신 KINOJO Meter를 안전하게 준비하고\r\n검증이 끝난 버전만 바로 실행합니다.",
                new Font("Segoe UI", 10F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(48, 188),
                new Size(455, 54));
            content.Controls.Add(_heroDescription);

            content.Controls.Add(CreateFeaturePill("✓  안전한 업데이트", new Point(48, 252), 145));
            content.Controls.Add(CreateFeaturePill("✓  무결성 검증", new Point(203, 252), 126));
            content.Controls.Add(CreateFeaturePill("✓  자동 복구", new Point(339, 252), 108));

            _noticeCard = BuildNoticeCard();
            content.Controls.Add(_noticeCard);

            _launchCard = new LauncherCard
            {
                Size = new Size(430, 410),
                BackColor = Color.FromArgb(23, 28, 40),
                BorderColor = Color.FromArgb(62, 71, 92),
                CornerRadius = 18,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            content.Controls.Add(_launchCard);

            var stateCaption = CreateLabel(
                "런처 상태",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(27, 23),
                new Size(150, 20));
            _launchCard.Controls.Add(stateCaption);

            _statusTitle = CreateLabel(
                "실행 준비됨",
                new Font("Segoe UI", 17F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(24, 49),
                new Size(350, 34));
            _launchCard.Controls.Add(_statusTitle);

            _status = CreateLabel(
                "PASS KEY를 입력하면 최신 버전을 확인합니다.",
                new Font("Segoe UI", 9F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(27, 86),
                new Size(376, 44));
            _launchCard.Controls.Add(_status);

            var divider = new Panel
            {
                BackColor = LauncherPalette.Border,
                Location = new Point(27, 137),
                Size = new Size(376, 1)
            };
            _launchCard.Controls.Add(divider);

            var passKeyLabel = CreateLabel(
                "6자리 PASS KEY",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(27, 154),
                new Size(210, 20));
            _launchCard.Controls.Add(passKeyLabel);

            var passKeyFrame = new Panel
            {
                BackColor = Color.FromArgb(43, 51, 68),
                Location = new Point(27, 178),
                Size = new Size(376, 50),
                Padding = new Padding(1)
            };
            _launchCard.Controls.Add(passKeyFrame);

            var passKeyInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(17, 21, 31),
                Padding = new Padding(14, 8, 14, 7)
            };
            passKeyFrame.Controls.Add(passKeyInner);

            _passKey = new TextBox
            {
                Dock = DockStyle.Fill,
                MaxLength = 6,
                CharacterCasing = CharacterCasing.Upper,
                UseSystemPasswordChar = true,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI", 15F, FontStyle.Bold),
                BorderStyle = BorderStyle.None,
                BackColor = passKeyInner.BackColor,
                ForeColor = LauncherPalette.Text
            };
            _passKey.KeyDown += async delegate(object sender, KeyEventArgs args)
            {
                if (args.KeyCode != Keys.Enter) return;
                args.SuppressKeyPress = true;
                await StartMeterAsync();
            };
            passKeyInner.Controls.Add(_passKey);

            var progressCaption = CreateLabel(
                "업데이트 진행률",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(27, 246),
                new Size(190, 20));
            _launchCard.Controls.Add(progressCaption);

            _progressText = CreateLabel(
                "대기",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(325, 246),
                new Size(78, 20));
            _progressText.TextAlign = ContentAlignment.TopRight;
            _launchCard.Controls.Add(_progressText);

            _progress = new LauncherProgressBar
            {
                Location = new Point(27, 271),
                Size = new Size(376, 8),
                Value = 0
            };
            _launchCard.Controls.Add(_progress);

            _version = CreateLabel(
                CurrentVersionText(),
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(27, 291),
                new Size(376, 19));
            _launchCard.Controls.Add(_version);

            _start = new LauncherActionButton
            {
                Text = "인증 후 미터기 실행",
                Location = new Point(27, 326),
                Size = new Size(376, 52)
            };
            _start.Click += async delegate { await StartMeterAsync(); };
            _launchCard.Controls.Add(_start);

            var securityHint = CreateLabel(
                "RSA 서명과 파일 무결성을 확인한 뒤 실행합니다.",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                Color.FromArgb(126, 137, 156),
                new Point(27, 387),
                new Size(376, 18));
            securityHint.TextAlign = ContentAlignment.TopCenter;
            _launchCard.Controls.Add(securityHint);

            content.Resize += delegate { LayoutContent(content); };
            LayoutContent(content);

            FormClosing += delegate { if (_cancellation != null) _cancellation.Cancel(); };
            Shown += delegate { _passKey.Focus(); };
            ResumeLayout(true);
        }

        private Panel BuildSidebar()
        {
            var sidebar = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = LauncherPalette.Sidebar
            };

            var logo = new Label
            {
                Text = "K",
                BackColor = LauncherPalette.Accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(20, 21),
                Size = new Size(28, 28)
            };
            sidebar.Controls.Add(logo);
            sidebar.Controls.Add(CreateLabel(
                "KINOJO",
                new Font("Segoe UI", 12F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(57, 22),
                new Size(104, 28)));

            var channel = CreateLabel(
                LauncherVersion.Channel.ToUpperInvariant(),
                new Font("Segoe UI", 7F, FontStyle.Bold),
                LauncherVersion.Channel == "staging" ? LauncherPalette.AccentBright : LauncherPalette.Success,
                new Point(20, 71),
                new Size(174, 18));
            sidebar.Controls.Add(channel);

            sidebar.Controls.Add(CreateSidebarButton("●   홈", 101, true));
            sidebar.Controls.Add(CreateSidebarButton("↻   업데이트", 151, false));
            sidebar.Controls.Add(CreateSidebarButton("▤   공지", 201, false));

            sidebar.Controls.Add(CreateLabel(
                "KINOJO METER",
                new Font("Segoe UI", 7.5F, FontStyle.Bold),
                Color.FromArgb(104, 115, 133),
                new Point(20, 278),
                new Size(174, 18)));
            sidebar.Controls.Add(CreateSidebarButton("◇   실행 상태", 306, false));

            var coreCard = new LauncherCard
            {
                BackColor = Color.FromArgb(18, 23, 33),
                BorderColor = Color.FromArgb(40, 49, 65),
                CornerRadius = 12,
                Location = new Point(16, 603),
                Size = new Size(182, 92),
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            sidebar.Controls.Add(coreCard);
            coreCard.Controls.Add(CreateLabel(
                "설치된 버전",
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(14, 14),
                new Size(150, 18)));
            _sidebarCore = CreateLabel(
                InstalledCoreText(),
                new Font("Segoe UI", 10F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(14, 35),
                new Size(150, 22));
            coreCard.Controls.Add(_sidebarCore);
            coreCard.Controls.Add(CreateLabel(
                "●  업데이트 확인 준비됨",
                new Font("Segoe UI", 7.5F, FontStyle.Regular),
                LauncherPalette.Success,
                new Point(14, 62),
                new Size(154, 18)));
            return sidebar;
        }

        private Panel BuildTopbar()
        {
            var topbar = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = LauncherPalette.Topbar
            };
            topbar.Controls.Add(CreateTopTab("홈", 26, true));
            topbar.Controls.Add(CreateTopTab("업데이트", 94, false));
            topbar.Controls.Add(CreateTopTab("공지", 192, false));

            var rightStatus = new Panel
            {
                Dock = DockStyle.Right,
                Width = 264,
                BackColor = LauncherPalette.Topbar
            };
            topbar.Controls.Add(rightStatus);

            var ready = CreateLabel(
                "●  런처 준비됨",
                new Font("Segoe UI", 8F, FontStyle.Bold),
                LauncherPalette.Success,
                new Point(0, 21),
                new Size(142, 22));
            rightStatus.Controls.Add(ready);

            var launcherVersion = CreateLabel(
                "Launcher " + LauncherVersion.Current,
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(145, 21),
                new Size(108, 22));
            launcherVersion.TextAlign = ContentAlignment.TopRight;
            rightStatus.Controls.Add(launcherVersion);
            return topbar;
        }

        private LauncherCard BuildNoticeCard()
        {
            var card = new LauncherCard
            {
                Size = new Size(410, 190),
                BackColor = Color.FromArgb(18, 23, 34),
                BorderColor = Color.FromArgb(48, 58, 76),
                CornerRadius = 16,
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };
            card.Controls.Add(CreateLabel(
                "공지 및 업데이트",
                new Font("Segoe UI", 12F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(20, 18),
                new Size(240, 28)));
            var preparing = CreateLabel(
                "연동 준비 중",
                new Font("Segoe UI", 8F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(288, 21),
                new Size(100, 22));
            preparing.TextAlign = ContentAlignment.TopRight;
            card.Controls.Add(preparing);

            var first = new Panel
            {
                BackColor = Color.FromArgb(25, 31, 44),
                Location = new Point(20, 58),
                Size = new Size(370, 48)
            };
            first.Controls.Add(CreateLabel(
                "업데이트 내역",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(12, 7),
                new Size(160, 18)));
            first.Controls.Add(CreateLabel(
                "새 버전 소식이 이곳에 표시됩니다.",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(12, 26),
                new Size(330, 16)));
            card.Controls.Add(first);

            var second = new Panel
            {
                BackColor = Color.FromArgb(25, 31, 44),
                Location = new Point(20, 116),
                Size = new Size(370, 48)
            };
            second.Controls.Add(CreateLabel(
                "KINOJO 공지",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(12, 7),
                new Size(160, 18)));
            second.Controls.Add(CreateLabel(
                "중요 안내를 런처에서 바로 확인할 수 있습니다.",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(12, 26),
                new Size(342, 16)));
            card.Controls.Add(second);
            return card;
        }

        private void LayoutContent(Control content)
        {
            const int margin = 32;
            _launchCard.Location = new Point(
                Math.Max(16, content.ClientSize.Width - _launchCard.Width - margin),
                Math.Max(20, content.ClientSize.Height - _launchCard.Height - margin));

            var noticeWidth = Math.Max(300, Math.Min(410, _launchCard.Left - 80));
            _noticeCard.Width = noticeWidth;
            _noticeCard.Location = new Point(48, Math.Max(315, content.ClientSize.Height - _noticeCard.Height - margin));
            foreach (Control child in _noticeCard.Controls)
            {
                if (child is Panel) child.Width = Math.Max(240, noticeWidth - 40);
            }

            var heroWidth = Math.Max(390, _launchCard.Left - 68);
            _heroTitle.Width = heroWidth;
            _heroDescription.Width = heroWidth;
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

        private static Label CreateFeaturePill(string text, Point location, int width)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = Color.FromArgb(198, 207, 222),
                BackColor = Color.FromArgb(39, 47, 64),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = location,
                Size = new Size(width, 32),
                UseCompatibleTextRendering = false
            };
        }

        private static Button CreateSidebarButton(string text, int top, bool active)
        {
            var button = new Button
            {
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(14, 0, 0, 0),
                Font = new Font("Segoe UI", 9F, active ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = active ? LauncherPalette.Text : LauncherPalette.Muted,
                BackColor = active ? Color.FromArgb(37, 44, 61) : LauncherPalette.Sidebar,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(12, top),
                Size = new Size(190, 42),
                Cursor = Cursors.Default,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = active ? Color.FromArgb(42, 50, 69) : Color.FromArgb(23, 29, 41);
            return button;
        }

        private static Label CreateTopTab(string text, int left, bool active)
        {
            var label = CreateLabel(
                text,
                new Font("Segoe UI", 9F, active ? FontStyle.Bold : FontStyle.Regular),
                active ? LauncherPalette.Text : LauncherPalette.Muted,
                new Point(left, 20),
                new Size(text.Length * 18 + 30, 28));
            label.TextAlign = ContentAlignment.TopCenter;
            if (active) label.BackColor = Color.FromArgb(24, 30, 43);
            return label;
        }

        private string CurrentVersionText()
        {
            using (var installer = new CorePackageInstaller())
            {
                var active = installer.ReadActiveState();
                return "Launcher " + LauncherVersion.Current + "  ·  " + LauncherVersion.Channel.ToUpperInvariant() + "  ·  Core " + (active == null ? "설치 전" : active.CoreVersion);
            }
        }

        private string InstalledCoreText()
        {
            using (var installer = new CorePackageInstaller())
            {
                var active = installer.ReadActiveState();
                return active == null ? "Core 설치 전" : "Core " + active.CoreVersion;
            }
        }

        private async Task StartMeterAsync()
        {
            if (!_start.Enabled) return;
            var passKey = (_passKey.Text ?? "").Trim().ToUpperInvariant();
            if (passKey.Length != 6)
            {
                SetOperationState("입력 확인", "PASS KEY 6자리를 입력해 주세요.", true, _progress.Value);
                _passKey.Focus();
                return;
            }

            _start.Enabled = false;
            _start.Text = "KINOJO Meter 준비 중...";
            _passKey.Enabled = false;
            _progress.Error = false;
            _progress.Value = 0;
            _cancellation = new CancellationTokenSource();
            var sessionToken = "";
            try
            {
                SetOperationState("이용 권한 확인 중", "KINOJO Server에서 PASS KEY를 확인하고 있습니다.", false, 8);
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
                        SetOperationState("최신 버전 확인 중", "설치된 Core와 최신 배포 버전을 비교하고 있습니다.", false, 20);
                        var authorization = await api.AuthorizeCoreUpdateAsync(
                            sessionToken,
                            installationId,
                            current == null ? "" : current.CoreVersion);
                        if (!authorization.Authorized || authorization.Release == null)
                            throw new InvalidOperationException(String.IsNullOrWhiteSpace(authorization.Message)
                                ? "현재 Core 다운로드가 허용되지 않았습니다."
                                : authorization.Message);

                        var sameVersion = current != null && String.Equals(current.CoreVersion, authorization.Release.CoreVersion, StringComparison.Ordinal);
                        SetOperationState(
                            sameVersion ? "설치 상태 확인 중" : "업데이트 다운로드 중",
                            sameVersion ? "설치된 Core의 무결성을 확인하고 있습니다." : "최신 Core를 안전하게 내려받고 있습니다.",
                            false,
                            28);
                        var progress = new Progress<int>(value =>
                        {
                            var mapped = 28 + (int)Math.Round(Math.Max(0, Math.Min(100, value)) * 0.57D);
                            _progress.Value = Math.Max(0, Math.Min(85, mapped));
                            _progressText.Text = _progress.Value + "%";
                        });
                        var install = await installer.EnsureInstalledAsync(
                            authorization.Release,
                            api.ProjectHost,
                            progress,
                            _cancellation.Token);

                        SetOperationState("미터기 실행 확인 중", "Core 실행과 준비 신호를 확인하고 있습니다.", false, 92);
                        await installer.LaunchAndVerifyAsync(install, login, installationId);
                        _version.Text = "Launcher " + LauncherVersion.Current + "  ·  " + LauncherVersion.Channel.ToUpperInvariant() + "  ·  Core " + install.Active.CoreVersion;
                        _sidebarCore.Text = "Core " + install.Active.CoreVersion;
                        sessionToken = "";
                        SetOperationState("실행 완료", "KINOJO Meter가 정상적으로 실행되었습니다.", false, 100);
                        await Task.Delay(500);
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
                SetOperationState("작업 취소됨", "요청한 작업이 취소되었습니다.", true, _progress.Value);
            }
            catch (Exception ex)
            {
                SetOperationState("확인이 필요합니다", ex.Message, true, _progress.Value);
            }
            finally
            {
                sessionToken = "";
                _passKey.Clear();
                if (!IsDisposed)
                {
                    _start.Enabled = true;
                    _start.Text = "다시 시도";
                    _passKey.Enabled = true;
                    _passKey.Focus();
                }
            }
        }

        private void SetOperationState(string title, string text, bool error, int progress)
        {
            _statusTitle.Text = String.IsNullOrWhiteSpace(title) ? "런처 상태" : title.Trim();
            _status.Text = String.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
            _status.ForeColor = error ? LauncherPalette.Error : LauncherPalette.Muted;
            _statusTitle.ForeColor = error ? LauncherPalette.Error : LauncherPalette.Text;
            _progress.Error = error;
            _progress.Value = Math.Max(0, Math.Min(100, progress));
            _progressText.Text = error ? "오류" : (_progress.Value >= 100 ? "완료" : _progress.Value + "%");
            _progressText.ForeColor = error ? LauncherPalette.Error : (_progress.Value >= 100 ? LauncherPalette.Success : LauncherPalette.Muted);
        }
    }
}
