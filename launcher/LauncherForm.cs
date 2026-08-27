using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KinojoMeterLauncher
{
    internal sealed class LauncherForm : LauncherWindowForm
    {
        private const string KinojoInfoUrl = "https://kinojo.info/";
        private const string KinojoMeterWebUrl = "https://kinojo.info/meter/";
        private const string PrivacyUrl = "https://kinojo.info/pages/privacy.html";

        private readonly LauncherLoginResult _login;
        private readonly LauncherActionButton _start;
        private readonly LauncherConsentCheckBox _terms;
        private readonly Label _status;
        private readonly Label _statusTitle;
        private readonly Label _progressText;
        private readonly Label _version;
        private Label _sidebarCore;
        private Label _sidebarBrand;
        private Label _sidebarLauncherVersion;
        private readonly LauncherProgressBar _progress;
        private readonly LauncherCard _launchCard;
        private readonly LauncherCard _noticeCard;
        private readonly Label _heroTitle;
        private readonly Label _heroDescription;
        private readonly Panel _viewHost;
        private readonly LauncherBackdrop _mainView;
        private readonly LauncherBackdrop _feedView;
        private FlowLayoutPanel _feedList;
        private Label _feedTitle;
        private Label _feedStatus;
        private Label _newsState;
        private Label _latestUpdateTitle;
        private Label _latestUpdateSummary;
        private Label _latestNoticeTitle;
        private Label _latestNoticeSummary;
        private Panel _latestUpdateRow;
        private Panel _latestNoticeRow;
        private Button _mainTab;
        private Button _updateTab;
        private Button _noticeTab;
        private List<LauncherContentItem> _contentItems = new List<LauncherContentItem>();
        private HashSet<string> _seenContentIds = new HashSet<string>(StringComparer.Ordinal);
        private string _activeView = "main";
        private string _contentStatus = "공지를 불러오는 중";
        private readonly CancellationTokenSource _contentCancellation = new CancellationTokenSource();
        private CancellationTokenSource _cancellation;
        private CoreInstallResult _preparedCore;
        private string _installationId;
        private MeterLaunchOperation _launchOperation;
        private string _runtimeApiEndpoint;
        private bool _operationBusy;

        public LauncherForm(LauncherLoginResult login)
            : this(login, false)
        {
        }

        internal LauncherForm(LauncherLoginResult login, bool suppressStartup)
        {
            if (login == null || String.IsNullOrWhiteSpace(login.SessionToken))
                throw new ArgumentException("로그인 세션이 필요합니다.", "login");
            _login = login;

            SuspendLayout();
            Text = "KINOJO Meter Launcher" + LauncherBuildProfile.DisplaySuffix + " " + LauncherVersion.Current;
            ClientSize = new Size(1180, 720);
            MinimumSize = new Size(1120, 680);
            StartPosition = FormStartPosition.CenterScreen;
            ForeColor = LauncherPalette.Text;
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
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

            _viewHost = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                BackColor = LauncherPalette.Window
            };
            workspace.Controls.Add(_viewHost, 0, 1);

            _mainView = new LauncherBackdrop
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _viewHost.Controls.Add(_mainView);
            var content = _mainView;

            var eyebrowText = "KINOJO METER" + (LauncherVersion.IsStaging ? " · 테스트 버전" : "");
            content.Controls.Add(CreateLabel(
                eyebrowText,
                new Font("Segoe UI", 9F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(48, 43),
                new Size(380, 22)));

            _heroTitle = CreateLabel(
                "정확한 데미지 연산 엔진으로\r\n정확하게.",
                new Font("Segoe UI", 24F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(43, 75),
                new Size(445, 106));
            content.Controls.Add(_heroTitle);

            _heroDescription = CreateLabel(
                "레기온을 위한, 레기온만의 미터기.",
                new Font("Segoe UI", 11F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(48, 191),
                new Size(455, 40));
            content.Controls.Add(_heroDescription);

            _noticeCard = BuildNoticeCard();
            content.Controls.Add(_noticeCard);

            _launchCard = new LauncherCard
            {
                Size = new Size(430, 440),
                BackColor = Color.FromArgb(23, 28, 40),
                BorderColor = Color.FromArgb(62, 71, 92),
                CornerRadius = 18,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            content.Controls.Add(_launchCard);

            _launchCard.Controls.Add(CreateLabel(
                "런처 상태",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(27, 23),
                new Size(150, 20)));

            _statusTitle = CreateLabel(
                "실행 준비",
                new Font("Segoe UI", 17F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(24, 49),
                new Size(350, 34));
            _launchCard.Controls.Add(_statusTitle);

            _status = CreateLabel(
                "로그인되었습니다. 최신 Core를 자동으로 확인합니다.",
                new Font("Segoe UI", 9F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(27, 86),
                new Size(376, 44));
            _launchCard.Controls.Add(_status);

            _launchCard.Controls.Add(new Panel
            {
                BackColor = LauncherPalette.Border,
                Location = new Point(27, 137),
                Size = new Size(376, 1)
            });

            _launchCard.Controls.Add(CreateLabel(
                "로그인 계정",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(27, 154),
                new Size(210, 20)));

            var accountCard = new Panel
            {
                BackColor = Color.FromArgb(17, 21, 31),
                Location = new Point(27, 178),
                Size = new Size(376, 56)
            };
            accountCard.Controls.Add(CreateLabel(
                String.IsNullOrWhiteSpace(_login.DisplayName) ? "KINOJO 사용자" : _login.DisplayName,
                new Font("Segoe UI", 10F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(13, 8),
                new Size(250, 22)));
            accountCard.Controls.Add(CreateLabel(
                "●  PASS KEY 로그인 완료",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                LauncherPalette.Success,
                new Point(13, 31),
                new Size(220, 18)));
            _launchCard.Controls.Add(accountCard);

            _launchCard.Controls.Add(CreateLabel(
                "업데이트 진행률",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(27, 249),
                new Size(190, 20)));

            _progressText = CreateLabel(
                "대기",
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Muted,
                new Point(325, 249),
                new Size(78, 20));
            _progressText.TextAlign = ContentAlignment.TopRight;
            _launchCard.Controls.Add(_progressText);

            _progress = new LauncherProgressBar
            {
                Location = new Point(27, 274),
                Size = new Size(376, 8),
                Value = 0
            };
            _launchCard.Controls.Add(_progress);

            _version = CreateLabel(
                CurrentVersionText(),
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(27, 294),
                new Size(376, 19));
            _launchCard.Controls.Add(_version);

            _start = new LauncherActionButton
            {
                Text = "미터기 실행",
                Location = new Point(27, 326),
                Size = new Size(376, 52),
                Enabled = false
            };
            _start.Click += async delegate { await StartMeterAsync(); };
            _launchCard.Controls.Add(_start);

            _terms = new LauncherConsentCheckBox
            {
                Text = "모든 약관에 동의",
                Location = new Point(27, 387),
                Size = new Size(250, 24)
            };
            _terms.CheckedChanged += delegate
            {
                RefreshStartButton();
                if (_terms.Checked && _progress.Value == 0)
                    SetOperationState("실행 준비", "최신 Core 확인이 끝나면 미터기를 실행할 수 있습니다.", false, 0);
            };
            _launchCard.Controls.Add(_terms);

            var termsLink = new LinkLabel
            {
                Text = "약관 보기",
                Font = new Font("Segoe UI", 8F, FontStyle.Regular),
                LinkColor = LauncherPalette.AccentBright,
                ActiveLinkColor = Color.White,
                VisitedLinkColor = LauncherPalette.AccentBright,
                BackColor = Color.Transparent,
                Location = new Point(315, 390),
                Size = new Size(88, 20),
                TextAlign = ContentAlignment.TopRight,
                TabStop = true
            };
            termsLink.LinkClicked += delegate { OpenExternalLink(PrivacyUrl); };
            _launchCard.Controls.Add(termsLink);

            var securityHint = CreateLabel(
                "RSA 서명과 파일 무결성을 확인한 뒤 실행합니다.",
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                Color.FromArgb(126, 137, 156),
                new Point(27, 417),
                new Size(376, 18));
            securityHint.TextAlign = ContentAlignment.TopCenter;
            _launchCard.Controls.Add(securityHint);

            content.Resize += delegate { LayoutContent(content); };
            LayoutContent(content);

            _feedView = BuildFeedView();
            _feedView.Visible = false;
            _viewHost.Controls.Add(_feedView);
            _mainView.BringToFront();

            _mainTab.Click += delegate { ShowView("main"); };
            _updateTab.Click += delegate { ShowView("update"); };
            _noticeTab.Click += delegate { ShowView("notice"); };

            FormClosing += delegate
            {
                _contentCancellation.Cancel();
                if (_cancellation != null) _cancellation.Cancel();
            };
            if (!suppressStartup)
            {
                Shown += async delegate
                {
                    var contentTask = LoadContentAsync();
                    await PrepareCoreAsync();
                    await RefreshLaunchOperationAsync(true);
                    await contentTask;
                };
            }
            ResumeLayout(true);
        }

        public bool SessionHandedOff { get; private set; }
        public SplitRuntimeLaunchResult RuntimeLaunchResult { get; private set; }

        internal bool SidebarBrandContractForTesting
        {
            get
            {
                if (_sidebarBrand == null || _sidebarLauncherVersion == null) return false;
                var measured = TextRenderer.MeasureText(_sidebarBrand.Text, _sidebarBrand.Font,
                    new Size(Int32.MaxValue, _sidebarBrand.Height), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return String.Equals(_sidebarBrand.Text, "KINOJO LAUNCHER", StringComparison.Ordinal) &&
                    measured.Width <= _sidebarBrand.Width && _sidebarLauncherVersion.Top >= _sidebarBrand.Bottom;
            }
        }

        private Panel BuildSidebar()
        {
            var sidebar = new Panel
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = LauncherPalette.Sidebar
            };

            var brandBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                BackColor = LauncherPalette.Sidebar
            };
            AttachTitleBar(brandBar);
            brandBar.Controls.Add(CreateBrandIcon(new Point(20, 18), new Size(28, 28)));
            _sidebarBrand = CreateLabel(
                "KINOJO LAUNCHER",
                new Font("Segoe UI", 9F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(57, 10),
                new Size(150, 22));
            brandBar.Controls.Add(_sidebarBrand);
            _sidebarLauncherVersion = CreateLabel(
                "v" + LauncherVersion.Current,
                new Font("Segoe UI", 7F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(57, 35),
                new Size(145, 18));
            brandBar.Controls.Add(_sidebarLauncherVersion);
            sidebar.Controls.Add(brandBar);

            if (LauncherVersion.IsStaging)
            {
                sidebar.Controls.Add(CreateLabel(
                    "테스트 버전",
                    new Font("Segoe UI", 7F, FontStyle.Bold),
                    LauncherPalette.AccentBright,
                    new Point(20, 73),
                    new Size(174, 18)));
            }

            sidebar.Controls.Add(CreateSidebarButton("●   MAIN", 101, true));
            var infoHome = CreateSidebarButton("↗   KINOJO INFO 홈", 151, false);
            infoHome.Cursor = Cursors.Hand;
            infoHome.Click += delegate { OpenExternalLink(KinojoInfoUrl); };
            sidebar.Controls.Add(infoHome);
            var meterWeb = CreateSidebarButton("↗   KINOJO METER 웹", 201, false);
            meterWeb.Cursor = Cursors.Hand;
            meterWeb.Click += delegate { OpenExternalLink(KinojoMeterWebUrl); };
            sidebar.Controls.Add(meterWeb);

            var bottomStatus = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 136,
                BackColor = LauncherPalette.Sidebar
            };
            sidebar.Controls.Add(bottomStatus);

            var ready = CreateLabel(
                "●  런처 준비됨",
                new Font("Segoe UI", 8F, FontStyle.Bold),
                LauncherPalette.Success,
                new Point(20, 1),
                new Size(174, 22));
            bottomStatus.Controls.Add(ready);

            var coreCard = new LauncherCard
            {
                BackColor = Color.FromArgb(18, 23, 33),
                BorderColor = Color.FromArgb(40, 49, 65),
                CornerRadius = 12,
                Location = new Point(16, 32),
                Size = new Size(182, 92)
            };
            bottomStatus.Controls.Add(coreCard);
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
            AttachTitleBar(topbar);
            _mainTab = CreateTopTab("MAIN", 22, true);
            _updateTab = CreateTopTab("업데이트", 104, false);
            _noticeTab = CreateTopTab("공지", 202, false);
            topbar.Controls.Add(_mainTab);
            topbar.Controls.Add(_updateTab);
            topbar.Controls.Add(_noticeTab);

            var rightHost = new Panel
            {
                Dock = DockStyle.Right,
                Width = 138,
                BackColor = LauncherPalette.Topbar
            };
            topbar.Controls.Add(rightHost);
            var windowControls = CreateWindowControls(true);
            windowControls.Dock = DockStyle.Fill;
            rightHost.Controls.Add(windowControls);
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
                "최신 소식",
                new Font("Segoe UI", 12F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(20, 18),
                new Size(240, 28)));
            _newsState = CreateLabel(
                "연동 준비 중",
                new Font("Segoe UI", 8F, FontStyle.Bold),
                LauncherPalette.AccentBright,
                new Point(288, 21),
                new Size(100, 22));
            _newsState.TextAlign = ContentAlignment.TopRight;
            card.Controls.Add(_newsState);

            _latestUpdateRow = CreateNewsPlaceholder("업데이트 내역", "새 버전 소식이 이곳에 표시됩니다.", 58, out _latestUpdateTitle, out _latestUpdateSummary);
            BindNewsRow(_latestUpdateRow);
            card.Controls.Add(_latestUpdateRow);
            _latestNoticeRow = CreateNewsPlaceholder("KINOJO 공지", "중요 안내를 런처에서 바로 확인할 수 있습니다.", 116, out _latestNoticeTitle, out _latestNoticeSummary);
            BindNewsRow(_latestNoticeRow);
            card.Controls.Add(_latestNoticeRow);
            return card;
        }

        private static Panel CreateNewsPlaceholder(string title, string description, int top, out Label titleLabel, out Label summaryLabel)
        {
            var row = new Panel
            {
                BackColor = Color.FromArgb(25, 31, 44),
                Location = new Point(20, top),
                Size = new Size(370, 48)
            };
            titleLabel = CreateLabel(
                title,
                new Font("Segoe UI", 8.5F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(12, 7),
                new Size(330, 18));
            summaryLabel = CreateLabel(
                description,
                new Font("Segoe UI", 7.8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(12, 26),
                new Size(342, 16));
            row.Controls.Add(titleLabel);
            row.Controls.Add(summaryLabel);
            return row;
        }

        private void BindNewsRow(Panel row)
        {
            row.Cursor = Cursors.Hand;
            row.Click += delegate { OpenContentItem(row.Tag as LauncherContentItem); };
            foreach (Control child in row.Controls)
            {
                child.Cursor = Cursors.Hand;
                child.Click += delegate { OpenContentItem(row.Tag as LauncherContentItem); };
            }
        }

        private LauncherBackdrop BuildFeedView()
        {
            var view = new LauncherBackdrop
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _feedTitle = CreateLabel(
                "업데이트",
                new Font("Segoe UI", 23F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(43, 37),
                new Size(500, 46));
            view.Controls.Add(_feedTitle);
            _feedStatus = CreateLabel(
                "공지를 불러오는 중입니다.",
                new Font("Segoe UI", 9F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(48, 82),
                new Size(600, 24));
            view.Controls.Add(_feedStatus);

            _feedList = new FlowLayoutPanel
            {
                Location = new Point(48, 121),
                Size = new Size(820, 490),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Transparent,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Margin = Padding.Empty,
                Padding = new Padding(0, 0, 8, 0)
            };
            view.Controls.Add(_feedList);
            view.Resize += delegate
            {
                _feedList.Size = new Size(
                    Math.Max(500, view.ClientSize.Width - 96),
                    Math.Max(280, view.ClientSize.Height - 153));
                ResizeFeedRows();
            };
            return view;
        }

        private async Task LoadContentAsync()
        {
            _seenContentIds = LauncherContentClient.ReadSeenIds();
            using (var client = new LauncherContentClient())
            {
                var cached = client.LoadCached();
                if (cached != null) ApplyContentResult(cached);
                var result = await client.LoadAsync(_contentCancellation.Token);
                if (_contentCancellation.IsCancellationRequested || IsDisposed || Disposing) return;
                ApplyContentResult(result);
            }
        }

        private void ApplyContentResult(LauncherContentLoadResult result)
        {
            _contentItems = result == null || result.Items == null
                ? new List<LauncherContentItem>()
                : result.Items;
            _contentStatus = result == null ? "공지를 불러오지 못했습니다" : result.Status;
            UpdateNewsCard();
            UpdateUnreadTabs();
            if (_activeView != "main") RenderFeedItems(_activeView);
        }

        private void ShowView(string view)
        {
            _activeView = view == "update" || view == "notice" ? view : "main";
            var main = _activeView == "main";
            _mainView.Visible = main;
            _feedView.Visible = !main;
            if (main) _mainView.BringToFront();
            else
            {
                _feedView.BringToFront();
                RenderFeedItems(_activeView);
                MarkVisibleItemsSeen(_activeView);
            }
            SetTabState(_mainTab, main);
            SetTabState(_updateTab, _activeView == "update");
            SetTabState(_noticeTab, _activeView == "notice");
        }

        private void UpdateNewsCard()
        {
            _newsState.Text = _contentStatus;
            _newsState.ForeColor = _contentStatus == "최신 공지" ? LauncherPalette.Success : LauncherPalette.Muted;
            ApplyNewsRow(
                _latestUpdateRow,
                _latestUpdateTitle,
                _latestUpdateSummary,
                _contentItems.FirstOrDefault(item => item.Type == "update"),
                "업데이트 소식이 없습니다.");
            ApplyNewsRow(
                _latestNoticeRow,
                _latestNoticeTitle,
                _latestNoticeSummary,
                _contentItems.FirstOrDefault(item => item.Type == "notice"),
                "등록된 공지가 없습니다.");
        }

        private static void ApplyNewsRow(Panel row, Label title, Label summary, LauncherContentItem item, string empty)
        {
            row.Tag = item;
            row.Cursor = item == null ? Cursors.Default : Cursors.Hand;
            title.Cursor = row.Cursor;
            summary.Cursor = row.Cursor;
            title.Text = item == null ? empty : item.Title;
            summary.Text = item == null ? "" : item.Summary;
            title.ForeColor = item == null ? LauncherPalette.Muted : LauncherPalette.Text;
        }

        private void RenderFeedItems(string type)
        {
            _feedTitle.Text = type == "notice" ? "공지" : "업데이트";
            var items = _contentItems.Where(item => item.Type == type).ToList();
            _feedStatus.Text = items.Count == 0
                ? _contentStatus + " · 표시할 항목이 없습니다."
                : _contentStatus + " · " + items.Count + "개";
            _feedList.SuspendLayout();
            try
            {
                while (_feedList.Controls.Count > 0)
                {
                    var control = _feedList.Controls[0];
                    _feedList.Controls.RemoveAt(0);
                    control.Dispose();
                }
                if (items.Count == 0)
                {
                    _feedList.Controls.Add(CreateLabel(
                        "새로운 " + (type == "notice" ? "공지" : "업데이트") + "가 등록되면 이곳에 표시됩니다.",
                        new Font("Segoe UI", 10F, FontStyle.Regular),
                        LauncherPalette.Muted,
                        Point.Empty,
                        new Size(620, 40)));
                }
                else
                {
                    foreach (var item in items) _feedList.Controls.Add(CreateFeedRow(item));
                }
                ResizeFeedRows();
            }
            finally
            {
                _feedList.ResumeLayout(true);
            }
        }

        private LauncherCard CreateFeedRow(LauncherContentItem item)
        {
            var row = new LauncherCard
            {
                BackColor = Color.FromArgb(20, 25, 36),
                BorderColor = Color.FromArgb(48, 58, 76),
                CornerRadius = 14,
                Width = Math.Max(500, _feedList.ClientSize.Width - 28),
                Height = 118,
                Margin = new Padding(0, 0, 0, 12),
                Tag = item
            };
            row.Controls.Add(CreateLabel(
                item.Pinned ? "중요" : (item.Type == "notice" ? "공지" : "업데이트"),
                new Font("Segoe UI", 7.5F, FontStyle.Bold),
                item.Pinned ? LauncherPalette.AccentBright : LauncherPalette.Muted,
                new Point(18, 15),
                new Size(72, 18)));
            row.Controls.Add(CreateLabel(
                item.Title,
                new Font("Segoe UI", 11F, FontStyle.Bold),
                LauncherPalette.Text,
                new Point(18, 37),
                new Size(570, 26)));
            row.Controls.Add(CreateLabel(
                item.Summary,
                new Font("Segoe UI", 8.5F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(18, 68),
                new Size(620, 38)));
            var metadata = CreateLabel(
                item.PublishedAt.ToLocalTime().ToString("yyyy.MM.dd") + (String.IsNullOrWhiteSpace(item.Version) ? "" : "  ·  v" + item.Version),
                new Font("Segoe UI", 8F, FontStyle.Regular),
                LauncherPalette.Muted,
                new Point(605, 17),
                new Size(130, 20));
            metadata.TextAlign = ContentAlignment.TopRight;
            metadata.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(metadata);
            var open = new Button
            {
                Text = "자세히 보기  ↗",
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                ForeColor = LauncherPalette.AccentBright,
                BackColor = Color.FromArgb(28, 35, 49),
                FlatStyle = FlatStyle.Flat,
                Size = new Size(118, 32),
                Location = new Point(617, 61),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Cursor = Cursors.Hand,
                TabStop = false
            };
            open.FlatAppearance.BorderSize = 0;
            open.Click += delegate { OpenContentItem(item); };
            row.Controls.Add(open);
            return row;
        }

        private void ResizeFeedRows()
        {
            if (_feedList == null) return;
            var width = Math.Max(500, _feedList.ClientSize.Width - 28);
            foreach (Control control in _feedList.Controls)
            {
                var card = control as LauncherCard;
                if (card != null) card.Width = width;
            }
        }

        private void MarkVisibleItemsSeen(string type)
        {
            var changed = false;
            foreach (var item in _contentItems.Where(item => item.Type == type))
                changed |= _seenContentIds.Add(item.Id);
            if (changed) LauncherContentClient.SaveSeenIds(_seenContentIds);
            UpdateUnreadTabs();
        }

        private void OpenContentItem(LauncherContentItem item)
        {
            if (item == null) return;
            if (_seenContentIds.Add(item.Id)) LauncherContentClient.SaveSeenIds(_seenContentIds);
            UpdateUnreadTabs();
            OpenExternalLink(item.Url);
        }

        private void UpdateUnreadTabs()
        {
            var updates = _contentItems.Count(item => item.Type == "update" && !_seenContentIds.Contains(item.Id));
            var notices = _contentItems.Count(item => item.Type == "notice" && !_seenContentIds.Contains(item.Id));
            _updateTab.Text = updates > 0 ? "업데이트  •" : "업데이트";
            _noticeTab.Text = notices > 0 ? "공지  •" : "공지";
        }

        private static void SetTabState(Button button, bool active)
        {
            var style = active ? FontStyle.Bold : FontStyle.Regular;
            if (button.Font.Style != style)
            {
                var previous = button.Font;
                button.Font = new Font("Segoe UI", 9F, style);
                previous.Dispose();
            }
            button.ForeColor = active ? LauncherPalette.Text : LauncherPalette.Muted;
            button.BackColor = active ? Color.FromArgb(24, 30, 43) : LauncherPalette.Topbar;
        }

        private void LayoutContent(Control content)
        {
            const int margin = 32;
            _launchCard.Location = new Point(
                Math.Max(16, content.ClientSize.Width - _launchCard.Width - margin),
                Math.Max(20, content.ClientSize.Height - _launchCard.Height - margin));

            var noticeWidth = Math.Max(300, Math.Min(410, _launchCard.Left - 80));
            _noticeCard.Width = noticeWidth;
            _noticeCard.Location = new Point(48, Math.Max(300, content.ClientSize.Height - _noticeCard.Height - margin));
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

        private static Button CreateTopTab(string text, int left, bool active)
        {
            var button = new Button
            {
                Text = text,
                Font = new Font("Segoe UI", 9F, active ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = active ? LauncherPalette.Text : LauncherPalette.Muted,
                BackColor = active ? Color.FromArgb(24, 30, 43) : LauncherPalette.Topbar,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(left, 12),
                Size = new Size(text.Length * 18 + 34, 40),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(31, 38, 53);
            return button;
        }

        private string CurrentVersionText()
        {
            using (var installer = new CorePackageInstaller())
            {
                var active = installer.ReadActiveState();
                var channel = LauncherVersion.IsStaging ? "  ·  테스트 버전" : "";
                return "Core " + (active == null ? "설치 전" : active.CoreVersion) + channel;
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
            if (!_terms.Checked)
            {
                SetOperationState("약관 동의 필요", "모든 약관에 동의한 뒤 미터기를 실행해 주세요.", true, _progress.Value);
                return;
            }
            if (String.IsNullOrWhiteSpace(_login.SessionToken))
            {
                SetOperationState("로그인 만료", "런처를 다시 실행해 PASS KEY로 로그인해 주세요.", true, 0);
                return;
            }
            if (_operationBusy) return;

            if (_preparedCore == null && !await PrepareCoreAsync()) return;

            _operationBusy = true;
            _start.Enabled = false;
            _start.Text = "KINOJO Meter 실행 중...";
            _terms.Enabled = false;
            _progress.Error = false;
            try
            {
                if (!await RefreshLaunchOperationAsync(false)) return;
                var runtimeLaunch = await RuntimeLaunchCoordinator.TryLaunchAsync(
                    _login,
                    _installationId,
                    _runtimeApiEndpoint);
                if (runtimeLaunch != null)
                {
                    RuntimeLaunchResult = runtimeLaunch;
                    _version.Text = "Runtime " + runtimeLaunch.RuntimeBundleRevision;
                    _sidebarCore.Text = "Runtime " + runtimeLaunch.RuntimeBundleRevision;
                    SessionHandedOff = true;
                    _login.SessionToken = "";
                    SetOperationState(
                        "분리 Runtime 실행 완료",
                        "검증된 Bundle의 Shell과 EngineHost가 새 Launcher session으로 실행되었습니다.",
                        false,
                        100);
                    await Task.Delay(500);
                    Close();
                    return;
                }

                using (var installer = new CorePackageInstaller())
                {
                    SetOperationState("복구 Core 실행 확인 중", "활성 Bundle이 없어 검증된 0.2.x 복구 Core를 실행합니다.", false, 92);
                    await installer.LaunchAndVerifyAsync(_preparedCore, _login, _installationId);
                    _version.Text = "Core " + _preparedCore.Active.CoreVersion;
                    _sidebarCore.Text = "Core " + _preparedCore.Active.CoreVersion;
                    SessionHandedOff = true;
                    _login.SessionToken = "";
                    SetOperationState("실행 완료", "KINOJO Meter가 정상적으로 실행되었습니다.", false, 100);
                    await Task.Delay(500);
                    Close();
                }
            }
            catch (OperationCanceledException)
            {
                SetOperationState("작업 취소됨", "요청한 작업이 취소되었습니다.", true, _progress.Value);
            }
            catch (Exception error)
            {
                SetOperationState("확인이 필요합니다", error.Message, true, _progress.Value);
            }
            finally
            {
                _operationBusy = false;
                if (!IsDisposed)
                {
                    _start.Text = "미터기 실행";
                    _terms.Enabled = true;
                    RefreshStartButton();
                }
            }
        }

        private async Task<bool> PrepareCoreAsync()
        {
            if (_operationBusy) return _preparedCore != null;
            if (String.IsNullOrWhiteSpace(_login.SessionToken)) return false;

            _operationBusy = true;
            _preparedCore = null;
            RefreshStartButton();
            if (_cancellation == null || _cancellation.IsCancellationRequested)
            {
                if (_cancellation != null) _cancellation.Dispose();
                _cancellation = new CancellationTokenSource();
            }
            try
            {
                using (var api = new LauncherApiClient())
                using (var installer = new CorePackageInstaller())
                using (var catalogInstaller = new CatalogPackInstaller())
                using (var uiAssetInstaller = new UiAssetPackInstaller())
                using (var privateRuntimeUpdater = new PrivateRuntimePackageUpdater())
                using (var captureUpdater = new CaptureModuleUpdater())
                using (var protocolUpdater = new ProtocolModuleUpdater())
                using (var combatEncounterUpdater = new CombatEncounterCompatibilityGroupUpdater())
                using (var combatUpdater = new CombatEncounterIndividualModuleUpdater("combat"))
                using (var encounterUpdater = new CombatEncounterIndividualModuleUpdater("encounter"))
                using (var syncUpdater = new SyncModuleUpdater())
                using (var shellUpdater = new ShellModuleUpdater())
                {
                    _installationId = LauncherPaths.GetOrCreateInstallationId();
                    var current = installer.ReadActiveState();
                    SetOperationState("최신 Core 확인 중", "설치된 Core와 최신 배포 버전을 자동으로 비교하고 있습니다.", false, 12);
                    var authorization = await api.AuthorizeCoreUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        current == null ? "" : current.CoreVersion);
                    _runtimeApiEndpoint = api.ApiEndpoint;
                    if (!authorization.Authorized || authorization.Release == null)
                        throw new InvalidOperationException(String.IsNullOrWhiteSpace(authorization.Message)
                            ? "현재 Core 다운로드가 허용되지 않았습니다."
                            : authorization.Message);

                    var sameVersion = current != null && String.Equals(current.CoreVersion, authorization.Release.CoreVersion, StringComparison.Ordinal);
                    SetOperationState(
                        sameVersion ? "설치 상태 확인 중" : "Core 업데이트 중",
                        sameVersion ? "설치된 Core의 무결성을 확인하고 있습니다." : "최신 Core를 안전하게 내려받고 있습니다.",
                        false,
                        24);
                    var prepareProgressActive = true;
                    var progress = new Progress<int>(value =>
                    {
                        if (!prepareProgressActive || IsDisposed || Disposing) return;
                        var mapped = 24 + (int)Math.Round(Math.Max(0, Math.Min(100, value)) * 0.70D);
                        _progress.Value = Math.Max(0, Math.Min(94, mapped));
                        _progressText.Text = _progress.Value + "%";
                    });
                    try
                    {
                        _preparedCore = await installer.EnsureInstalledAsync(
                            authorization.Release,
                            api.ProjectHost,
                            progress,
                            _cancellation.Token);
                    }
                    finally
                    {
                        // Progress<T> posts callbacks asynchronously to the UI context. Once
                        // install/verification is complete, queued 94% callbacks must never
                        // overwrite the terminal 100% state below.
                        prepareProgressActive = false;
                    }

                    SetOperationState("Bundle 확인 중", "서명된 Server Bundle과 7개 모듈을 검증하고 원자적으로 활성화하고 있습니다.", false, 94);
                    var activeBundle = ModuleBundleActivator.ReadVerifiedActiveBundle();
                    var bundleAuthorization = await api.AuthorizeModuleBundleBootstrapAsync(
                        _login.SessionToken,
                        _installationId,
                        activeBundle);
                    var bundleResult = await ModuleBundleBootstrapCoordinator.ApplyAsync(
                        bundleAuthorization,
                        api.ProjectHost,
                        null,
                        _cancellation.Token);

                    SetOperationState("Catalog Pack 확인 중", "분리된 Catalog Pack의 승인 버전과 무결성을 확인하고 있습니다.", false, 95);
                    var catalogAuthorization = await api.AuthorizeCatalogPackUpdatesAsync(
                        _login.SessionToken,
                        _installationId,
                        CatalogPackUpdateCoordinator.CurrentStatePayload(catalogInstaller));
                    var catalogResults = await CatalogPackUpdateCoordinator.ApplyAsync(
                        catalogInstaller,
                        catalogAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var changedCatalogs = catalogResults.Count(value => value.Changed);
                    SetOperationState("UI Asset Pack 확인 중", "분리된 UI Asset Pack의 승인 버전과 무결성을 확인하고 있습니다.", false, 98);
                    var uiAssetAuthorization = await api.AuthorizeUiAssetPackUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        UiAssetPackUpdateCoordinator.CurrentStatePayload(uiAssetInstaller));
                    var uiAssetResult = await UiAssetPackUpdateCoordinator.ApplyAsync(
                        uiAssetInstaller,
                        uiAssetAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var uiAssetChanged = uiAssetResult != null && uiAssetResult.Changed;
                    SetOperationState("private runtime 확인 중", "Server가 승인한 전체 runtime 패키지와 활성 Bundle을 확인하고 있습니다.", false, 99);
                    var privateRuntimeAuthorization = await api.AuthorizePrivateRuntimeUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var privateRuntimeResult = await PrivateRuntimeUpdateCoordinator.ApplyAsync(
                        privateRuntimeUpdater,
                        privateRuntimeAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var privateRuntimeChanged = privateRuntimeResult != null && privateRuntimeResult.Changed;
                    SetOperationState("Capture Engine 확인 중", "활성 private runtime에 결합된 Server 승인 Capture 모듈을 확인하고 있습니다.", false, 99);
                    var captureAuthorization = await api.AuthorizeCaptureModuleUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var captureResult = await CaptureModuleUpdateCoordinator.ApplyAsync(
                        captureUpdater,
                        captureAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var captureChanged = captureResult != null && captureResult.Changed;
                    SetOperationState("Protocol Engine 확인 중", "활성 Capture와 private runtime에 결합된 Server 승인 Protocol 모듈을 확인하고 있습니다.", false, 99);
                    var protocolAuthorization = await api.AuthorizeProtocolModuleUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var protocolResult = await ProtocolModuleUpdateCoordinator.ApplyAsync(
                        protocolUpdater,
                        protocolAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var protocolChanged = protocolResult != null && protocolResult.Changed;
                    SetOperationState("Combat·Encounter 호환 그룹 확인 중", "두 엔진의 Server 승인 조합과 exact parent chain을 확인하고 있습니다.", false, 99);
                    var combatEncounterChanged = false;
                    if (CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater) == null)
                    {
                        var combatEncounterAuthorization = await api.AuthorizeCombatEncounterCompatibilityGroupUpdateAsync(
                            _login.SessionToken,
                            _installationId,
                            null,
                            ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                            CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                            PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                        var combatEncounterResult = await CombatEncounterCompatibilityGroupUpdateCoordinator.ApplyAsync(
                            combatEncounterUpdater,
                            combatEncounterAuthorization,
                            api.ProjectHost,
                            _cancellation.Token);
                        combatEncounterChanged = combatEncounterResult != null && combatEncounterResult.Changed;
                    }
                    SetOperationState("Combat Engine 개별 확인 중", "호환 그룹 안에서 Server 승인 Combat 패키지만 독립 확인하고 있습니다.", false, 99);
                    var combatAuthorization = await api.AuthorizeCombatEncounterIndividualModuleUpdateAsync(
                        "combat",
                        _login.SessionToken,
                        _installationId,
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(combatUpdater),
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(encounterUpdater),
                        CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var combatResult = await CombatEncounterIndividualModuleUpdateCoordinator.ApplyAsync(
                        combatUpdater, combatAuthorization, api.ProjectHost, _cancellation.Token);
                    var combatChanged = combatResult != null && combatResult.Changed;
                    SetOperationState("Encounter Engine 개별 확인 중", "갱신된 Combat 호환 상태에서 Server 승인 Encounter 패키지만 독립 확인하고 있습니다.", false, 99);
                    var encounterAuthorization = await api.AuthorizeCombatEncounterIndividualModuleUpdateAsync(
                        "encounter",
                        _login.SessionToken,
                        _installationId,
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(encounterUpdater),
                        CombatEncounterIndividualModuleUpdateCoordinator.CurrentStatePayload(combatUpdater),
                        CombatEncounterCompatibilityGroupUpdateCoordinator.CurrentStatePayload(combatEncounterUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var encounterResult = await CombatEncounterIndividualModuleUpdateCoordinator.ApplyAsync(
                        encounterUpdater, encounterAuthorization, api.ProjectHost, _cancellation.Token);
                    var encounterChanged = encounterResult != null && encounterResult.Changed;
                    SetOperationState("Sync Engine 확인 중", "활성 Protocol·Capture와 private runtime에 결합된 Server 승인 Sync 모듈을 확인하고 있습니다.", false, 99);
                    var syncAuthorization = await api.AuthorizeSyncModuleUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        SyncModuleUpdateCoordinator.CurrentStatePayload(syncUpdater),
                        ProtocolModuleUpdateCoordinator.CurrentStatePayload(protocolUpdater),
                        CaptureModuleUpdateCoordinator.CurrentStatePayload(captureUpdater),
                        PrivateRuntimeUpdateCoordinator.CurrentStatePayload(privateRuntimeUpdater));
                    var syncResult = await SyncModuleUpdateCoordinator.ApplyAsync(
                        syncUpdater,
                        syncAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var syncChanged = syncResult != null && syncResult.Changed;
                    SetOperationState("Meter Shell 확인 중", "Server가 승인한 Shell 모듈과 private runtime 호환성을 확인하고 있습니다.", false, 99);
                    var shellAuthorization = await api.AuthorizeShellModuleUpdateAsync(
                        _login.SessionToken,
                        _installationId,
                        ShellModuleUpdateCoordinator.CurrentStatePayload(shellUpdater));
                    var shellResult = await ShellModuleUpdateCoordinator.ApplyAsync(
                        shellUpdater,
                        shellAuthorization,
                        api.ProjectHost,
                        _cancellation.Token);
                    var shellChanged = shellResult != null && shellResult.Changed;
                    _version.Text = "Core " + _preparedCore.Active.CoreVersion;
                    _sidebarCore.Text = "Core " + _preparedCore.Active.CoreVersion;
                    SetOperationState(
                        _preparedCore.Changed || changedCatalogs > 0 || uiAssetChanged || privateRuntimeChanged || captureChanged || protocolChanged || combatEncounterChanged || combatChanged || encounterChanged || syncChanged || shellChanged ? "업데이트가 완료되었습니다" : "현재 최신 버전입니다",
                        shellChanged || privateRuntimeChanged || captureChanged || protocolChanged || combatEncounterChanged || combatChanged || encounterChanged || syncChanged
                            ? "최신 Core, Catalog Pack, UI Asset Pack, private runtime, Capture·Protocol·Sync Engine, Combat·Encounter 개별 모듈과 Meter Shell의 업데이트·무결성 검증을 완료했습니다."
                            : (uiAssetChanged
                            ? "최신 Core, Catalog Pack과 UI Asset Pack의 독립 업데이트·무결성 검증을 완료했습니다."
                            : (changedCatalogs > 0
                                ? "최신 Core와 Catalog Pack " + changedCatalogs + "개의 독립 업데이트·무결성 검증을 완료했습니다."
                                : (_preparedCore.Changed ? "최신 Core 업데이트와 파일 무결성 검증을 완료했습니다." : "최신 Core, Catalog Pack, UI Asset Pack과 Meter Shell 파일 무결성 검증까지 완료했습니다."))),
                        false,
                        100);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                SetOperationState("작업 취소됨", "Core 확인 작업이 취소되었습니다.", true, _progress.Value);
                return false;
            }
            catch (Exception error)
            {
                SetOperationState("확인이 필요합니다", error.Message, true, _progress.Value);
                return false;
            }
            finally
            {
                _operationBusy = false;
                if (!IsDisposed) RefreshStartButton();
            }
        }

        private async Task<bool> RefreshLaunchOperationAsync(bool showAllowedState)
        {
            try
            {
                using (var api = new LauncherApiClient())
                    _launchOperation = await api.GetLaunchOperationAsync();
                if (_launchOperation == null || !_launchOperation.Enabled)
                {
                    var message = _launchOperation == null ? "미터기 실행 운영 상태를 확인하지 못했습니다." : _launchOperation.Message;
                    SetOperationState("미터기 실행 중지", message, false, _progress.Value);
                    return false;
                }
                if (showAllowedState && _preparedCore != null)
                    SetOperationState("실행 준비 완료", "현재 미터기 실행이 허용되어 있습니다.", false, 100);
                return true;
            }
            catch (Exception error)
            {
                _launchOperation = new MeterLaunchOperation
                {
                    Channel = LauncherVersion.Channel,
                    Enabled = false,
                    Message = error.Message
                };
                SetOperationState("실행 상태 확인 필요", error.Message, true, _progress.Value);
                return false;
            }
            finally
            {
                if (!IsDisposed) RefreshStartButton();
            }
        }

        private void RefreshStartButton()
        {
            if (_start == null) return;
            _start.Enabled = _terms.Checked && !_operationBusy && !String.IsNullOrWhiteSpace(_login.SessionToken) &&
                _launchOperation != null && _launchOperation.Enabled;
        }

        private void SetOperationState(string title, string text, bool error, int progress)
        {
            _statusTitle.Text = String.IsNullOrWhiteSpace(title) ? "런처 상태" : title.Trim();
            _status.Text = String.IsNullOrWhiteSpace(text) ? "-" : text.Trim();
            _status.ForeColor = error ? LauncherPalette.Error : LauncherPalette.Muted;
            _statusTitle.ForeColor = error ? LauncherPalette.Error : LauncherPalette.Text;
            _progress.Error = error;
            _progress.Value = Math.Max(0, Math.Min(100, progress));
            _progressText.Text = error ? "오류" : (_progress.Value >= 100 ? "완료" : (_progress.Value == 0 ? "대기" : _progress.Value + "%"));
            _progressText.ForeColor = error ? LauncherPalette.Error : (_progress.Value >= 100 ? LauncherPalette.Success : LauncherPalette.Muted);
        }

        private static void OpenExternalLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("웹 브라우저를 열지 못했습니다.\r\n\r\n" + url, "KINOJO Meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
