using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KinojoMeterPrototype
{
    internal sealed class MainWindow : Window
    {
        private static readonly Color Accent = Color.FromRgb(37, 99, 235);
        private static readonly Color AccentDeep = Color.FromRgb(79, 70, 229);
        private static readonly Color AccentViolet = Color.FromRgb(124, 58, 237);
        private static readonly Color Surface = Color.FromRgb(10, 15, 24);
        private static readonly Color Panel = Color.FromRgb(24, 31, 43);
        private static readonly Color PanelSoft = Color.FromRgb(30, 41, 59);
        private static readonly Color Line = Color.FromRgb(51, 65, 85);
        private static readonly Color Muted = Color.FromRgb(148, 163, 184);

        private readonly Grid _root = new Grid();
        private readonly Border _contentHost = new Border();
        private readonly KinojoApiClient _api = new KinojoApiClient();
        private readonly MeterPreferences _preferences = PreferencesStore.Load();
        private readonly Dictionary<string, DateTime> _profileRequestedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Button, CharacterProfile> _characterCards = new Dictionary<Button, CharacterProfile>();
        private readonly object _profileRequestGate = new object();
        private readonly Border _updateHost = new Border();

        private LoginResult _login;
        private MeterCatalog _catalog;
        private CharacterProfile _selected;
        private CharacterProfile _candidate;
        private OverlayWindow _overlay;
        private SystemTrayController _tray;
        private DispatcherTimer _foregroundTimer;
        private TextBlock _message;
        private PassKeyInput _passKeyInput;
        private Button _loginButton;
        private Button _meterStartButton;
        private Button _infoButton;
        private Border _selectedSummary;
        private TextBlock _selectedName;
        private TextBlock _selectedMeta;
        private TextBlock _selectedPower;
        private TextBlock _selectedFallback;
        private Image _selectedImage;
        private TextBlock _updateTitle;
        private TextBlock _updateDetail;
        private TextBlock _updateProgressText;
        private ProgressBar _updateProgress;
        private Button _updateActionButton;
        private MeterUpdateManifest _pendingUpdate;
        private MeterUpdateManifest _serverUpdateManifest;
        private bool _pendingUpdateMandatory;
        private bool _updateBusy;
        private bool _loginBusy;
        private bool _closing;
        private bool _manualOverlayVisible;
        private bool _manualOverlayHidden;
        private bool? _lastOverlayVisibilityDemand;
        private bool _selectionBusy;
        private bool _startupUpdateChecked;
        private CombatCaptureCoordinator _characterDetector;
        private bool _autoSelectionStarted;
        private GameHudProbe _hudProbe;
        private GameHudObservation _lastHudObservation;
        private CharacterDiscoveryWindow _discovery;
        private DispatcherTimer _characterDiscoveryTimeoutTimer;

        public MainWindow()
        {
            Title = "KINOJO Meter " + KinojoVersion.Current;
            Width = 520;
            Height = 430;
            MinWidth = 500;
            MinHeight = 410;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            Background = new SolidColorBrush(Surface);
            Foreground = Brushes.White;
            Content = _root;
            Closing += OnClosing;
            Loaded += async delegate { await CheckStartupUpdateAsync(); };
            InitializeChrome();
            ShowLogin();
        }

        private void InitializeChrome()
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            _root.RowDefinitions.Add(new RowDefinition());
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleBarBorder = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(10, 15, 24), Color.FromRgb(18, 24, 38), 0),
                BorderBrush = AccentBrush(),
                BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var titleBar = new Grid();
            titleBar.ColumnDefinitions.Add(new ColumnDefinition());
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleBarBorder.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
                try
                {
                    DragMove();
                    e.Handled = true;
                }
                catch (InvalidOperationException)
                {
                    // Ignore a released mouse button between the preview event and DragMove.
                }
            };

            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            brand.Children.Add(new TextBlock { Text = "KINOJO Meter", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.White });
            brand.Children.Add(new TextBlock { Text = "  " + KinojoVersion.Current, FontSize = 9, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Center });
            titleBar.Children.Add(brand);

            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            var minimize = ChromeButton("—");
            minimize.Click += delegate { WindowState = WindowState.Minimized; };
            var close = ChromeButton("×");
            close.Click += delegate { Close(); };
            controls.Children.Add(minimize);
            controls.Children.Add(close);
            Grid.SetColumn(controls, 1);
            titleBar.Children.Add(controls);
            titleBarBorder.Child = titleBar;
            _root.Children.Add(titleBarBorder);

            _contentHost.Background = new SolidColorBrush(Surface);
            Grid.SetRow(_contentHost, 1);
            _root.Children.Add(_contentHost);

            InitializeUpdatePanel();
            Grid.SetRow(_updateHost, 2);
            _root.Children.Add(_updateHost);
        }

        private void InitializeUpdatePanel()
        {
            _updateHost.Visibility = Visibility.Collapsed;
            _updateHost.Background = new LinearGradientBrush(Color.FromRgb(17, 24, 39), Color.FromRgb(28, 25, 56), 0);
            _updateHost.BorderBrush = AccentBrush();
            _updateHost.BorderThickness = new Thickness(0, 1, 0, 0);
            _updateHost.Padding = new Thickness(20, 10, 20, 10);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });

            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _updateTitle = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            _updateDetail = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(Muted), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            copy.Children.Add(_updateTitle);
            copy.Children.Add(_updateDetail);
            grid.Children.Add(copy);

            var progressHost = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            _updateProgress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0, Style = CreateProgressBarStyle() };
            _updateProgressText = new TextBlock { Text = "업데이트 준비", FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)), Margin = new Thickness(0, 5, 0, 0) };
            progressHost.Children.Add(_updateProgress);
            progressHost.Children.Add(_updateProgressText);
            Grid.SetColumn(progressHost, 1);
            grid.Children.Add(progressHost);

            _updateActionButton = PrimaryButton("업데이트 다운로드");
            _updateActionButton.Width = 140;
            _updateActionButton.Height = 38;
            _updateActionButton.Click += async delegate { await InstallPendingUpdateAsync(); };
            Grid.SetColumn(_updateActionButton, 2);
            grid.Children.Add(_updateActionButton);

            _updateHost.Child = grid;
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closing) return;
            if (_selected != null && _tray != null)
            {
                e.Cancel = true;
                Hide();
                _tray.SetStatus("백그라운드 실행 중");
                return;
            }
            _closing = true;
        }

        private void ShowLogin()
        {
            Width = 520;
            Height = 430;
            MinWidth = 520;
            MinHeight = 430;
            MaxWidth = 520;
            MaxHeight = 430;
            _selectionBusy = false;

            var page = new Grid { Margin = new Thickness(34, 22, 34, 28) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition());

            var heading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 18) };
            heading.Children.Add(new TextBlock { Text = "KINOJO Meter", FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            heading.Children.Add(new TextBlock { Text = "PASS KEY 인증 후 캐릭터를 선택하면 자동으로 시작됩니다.", Foreground = new SolidColorBrush(Muted), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
            page.Children.Add(heading);

            var card = new Border
            {
                Width = 410,
                Background = new SolidColorBrush(Panel),
                BorderBrush = new SolidColorBrush(Line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 20, 24, 22),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(card, 1);
            page.Children.Add(card);

            var stack = new StackPanel();
            card.Child = stack;
            stack.Children.Add(new TextBlock { Text = "PASS KEY", FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253)), FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });
            _passKeyInput = new PassKeyInput();
            _passKeyInput.EnterPressed += async delegate { await BeginLoginAsync(); };
            stack.Children.Add(_passKeyInput);

            _message = new TextBlock
            {
                Text = "한글·영문·숫자를 입력할 수 있으며 PASS KEY는 프로그램에 저장되지 않습니다.",
                Foreground = new SolidColorBrush(Muted),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 11, 0, 13)
            };
            stack.Children.Add(_message);

            _loginButton = PrimaryButton("인증 후 자동 연결");
            _loginButton.Click += async delegate { await BeginLoginAsync(); };
            stack.Children.Add(_loginButton);
            RefreshUpdateBlockedState();

            stack.Children.Add(new TextBlock
            {
                Text = "로그인 후 게임 화면과 패킷에서 현재 캐릭터를 자동 확인합니다. 확인할 수 없을 때만 직접 선택할 수 있습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 8,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(8, 12, 8, 0)
            });

            _contentHost.Child = page;
            QueueCenterWindow();
            Dispatcher.BeginInvoke(new Action(delegate { if (_passKeyInput != null) _passKeyInput.FocusFirst(); }), DispatcherPriority.Input);
        }

        private async Task BeginLoginAsync()
        {
            if (_loginButton == null || !_loginButton.IsEnabled) return;
            var passKey = _passKeyInput == null ? "" : _passKeyInput.Value;
            if (String.IsNullOrWhiteSpace(passKey))
            {
                SetMessage("PASS KEY를 입력해 주세요.", true);
                if (_passKeyInput != null) _passKeyInput.FocusFirst();
                return;
            }

            _loginBusy = true;
            RefreshUpdateBlockedState();
            try { await LoginAsync(passKey); }
            finally
            {
                _loginBusy = false;
                RefreshUpdateBlockedState();
            }
        }

        private async Task LoginAsync(string passKey)
        {
            try
            {
                SetMessage("서버에서 계정을 확인하고 있습니다...", false);
                _login = await _api.LoginAsync(passKey);
                if (_passKeyInput != null) _passKeyInput.Clear(false);
                DiagnosticLog.Info("AUTH", "Login succeeded for role " + (_login.RoleLabel ?? "Member"));
                ShowCharacterDiscovery();
                StartAutomaticCharacterDetection();
                StartGameHudProbe();
            }
            catch (MeterApiException ex)
            {
                DiagnosticLog.Error("AUTH", ex.Code, ex);
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("AUTH", "Unexpected login failure", ex);
                ShowError("서버 연결을 확인하지 못했습니다. 잠시 후 다시 시도해 주세요.");
            }
        }

        private void ShowCharacterDiscovery()
        {
            var characters = (_login == null ? null : _login.Characters) ?? new List<CharacterProfile>();
            if (characters.Count == 0) { ShowLogin(); return; }
            if (_discovery != null) _discovery.Close();
            _discovery = new CharacterDiscoveryWindow(characters, _preferences);
            _discovery.CharacterSelected += delegate(object sender, CharacterProfile profile)
            {
                Dispatcher.BeginInvoke(new Action(async delegate { await SelectDetectedCharacterAsync(profile, "직접 선택"); }));
            };
            _discovery.Show();
            Hide();
            StartCharacterDiscoveryTimeout();
        }

        private async Task SelectDetectedCharacterAsync(CharacterProfile profile, string evidence)
        {
            if (profile == null || _selectionBusy || _login == null) return;
            if (_selected != null &&
                ((!String.IsNullOrWhiteSpace(_selected.CharacterKey) &&
                  !String.IsNullOrWhiteSpace(profile.CharacterKey) &&
                  String.Equals(_selected.CharacterKey, profile.CharacterKey, StringComparison.OrdinalIgnoreCase)) ||
                 (String.Equals(_selected.CharacterName, profile.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                  String.Equals(_selected.ServerName, profile.ServerName, StringComparison.OrdinalIgnoreCase)))) return;
            _selectionBusy = true;
            if (_discovery != null) _discovery.MarkDetected(profile, evidence);
            try { await SelectCharacterAsync(profile); }
            finally { _selectionBusy = false; RefreshMeterStartState(); }
        }

        private void ShowCharacters()
        {
            var characters = (_login == null ? null : _login.Characters) ?? new List<CharacterProfile>();
            if (characters.Count == 0)
            {
                ShowLogin();
                return;
            }

            Width = 1180;
            Height = 700;
            MinWidth = 1180;
            MinHeight = 700;
            MaxWidth = 1180;
            MaxHeight = 700;
            _candidate = null;
            _characterCards.Clear();

            var page = new Grid();
            page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
            page.ColumnDefinitions.Add(new ColumnDefinition());

            var side = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(31, 41, 55)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(18, 24, 18, 18)
            };
            var sideGrid = new Grid();
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            sideGrid.RowDefinitions.Add(new RowDefinition());
            sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.Child = sideGrid;

            var sideHeader = new StackPanel();
            sideHeader.Children.Add(new TextBlock { Text = "내 캐릭터", FontSize = 18, FontWeight = FontWeights.Bold });
            sideHeader.Children.Add(new TextBlock { Text = "게임 화면에서 접속 캐릭터를 자동 확인합니다. 필요할 때만 직접 선택하세요.", Foreground = new SolidColorBrush(Muted), FontSize = 9, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18) });
            sideGrid.Children.Add(sideHeader);

            var selectedStack = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            selectedStack.Children.Add(new TextBlock { Text = "선택한 캐릭터", FontWeight = FontWeights.Bold, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(191, 219, 254)), Margin = new Thickness(0, 0, 0, 9) });
            _selectedSummary = BuildSelectedSummary();
            selectedStack.Children.Add(_selectedSummary);
            _infoButton = SecondaryAccentButton("KINOJO INFO 연결");
            _infoButton.IsEnabled = false;
            _infoButton.Margin = new Thickness(0, 10, 0, 0);
            _infoButton.Click += delegate { OpenSelectedCharacterInfo(); };
            selectedStack.Children.Add(_infoButton);
            Grid.SetRow(selectedStack, 1);
            sideGrid.Children.Add(selectedStack);

            var sideNote = new TextBlock
            {
                Text = "접속 캐릭터 자동 확인 중 · 자동 확인이 어려우면 아래 카드로 직접 선택할 수 있습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 8,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 14
            };
            Grid.SetRow(sideNote, 2);
            sideGrid.Children.Add(sideNote);
            page.Children.Add(side);

            var main = new Grid { Margin = new Thickness(26, 20, 24, 18) };
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition());
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(main, 1);
            page.Children.Add(main);

            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headerText = new StackPanel();
            headerText.Children.Add(new TextBlock { Text = "본인 캐릭터 선택", FontSize = 24, FontWeight = FontWeights.Bold });
            headerText.Children.Add(new TextBlock { Text = "캐릭터를 선택한 뒤 미터 실행을 눌러 측정을 시작합니다.", Foreground = new SolidColorBrush(Muted), FontSize = 10, Margin = new Thickness(0, 4, 0, 0) });
            header.Children.Add(headerText);
            _message = new TextBlock { Text = "", Foreground = new SolidColorBrush(Accent), FontSize = 9, VerticalAlignment = VerticalAlignment.Bottom, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(_message, 1);
            header.Children.Add(_message);
            main.Children.Add(header);

            var cardGrid = new UniformGrid { Columns = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var profile in characters.OrderByDescending(value => value.IsMain).ThenBy(value => value.CharacterName))
                cardGrid.Children.Add(BuildCharacterCard(profile));

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = cardGrid,
                Padding = new Thickness(0, 0, 6, 0)
            };
            scroll.Resources[typeof(ScrollBar)] = CreateVerticalScrollBarStyle();
            Grid.SetRow(scroll, 1);
            main.Children.Add(scroll);

            var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var helper = new TextBlock
            {
                Text = "선택한 캐릭터는 실행 전까지 서버에 확정되지 않습니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(helper);
            _meterStartButton = PrimaryButton("미터 실행");
            _meterStartButton.Width = 168;
            _meterStartButton.Height = 44;
            _meterStartButton.IsEnabled = false;
            _meterStartButton.Click += async delegate
            {
                if (_candidate == null || _selectionBusy) return;
                _selectionBusy = true;
                RefreshMeterStartState();
                _message.Text = _candidate.CharacterName + " 연결 중...";
                try { await SelectCharacterAsync(_candidate); }
                finally
                {
                    _selectionBusy = false;
                    RefreshMeterStartState();
                }
            };
            Grid.SetColumn(_meterStartButton, 1);
            footer.Children.Add(_meterStartButton);
            Grid.SetRow(footer, 2);
            main.Children.Add(footer);

            _contentHost.Child = page;
            RefreshMeterStartState();
            QueueCenterWindow();
        }

        private void QueueCenterWindow()
        {
            Dispatcher.BeginInvoke(new Action(CenterWindowOnWorkingArea), DispatcherPriority.Loaded);
        }

        private void CenterWindowOnWorkingArea()
        {
            var workArea = SystemParameters.WorkArea;
            var windowWidth = !Double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
            var windowHeight = !Double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
            if (Double.IsNaN(windowWidth) || windowWidth <= 0) windowWidth = ActualWidth;
            if (Double.IsNaN(windowHeight) || windowHeight <= 0) windowHeight = ActualHeight;

            Left = Math.Round(workArea.Left + Math.Max(0, (workArea.Width - windowWidth) / 2.0));
            Top = Math.Round(workArea.Top + Math.Max(0, (workArea.Height - windowHeight) / 2.0));
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is ButtonBase) return true;
                if (current is Visual)
                    current = VisualTreeHelper.GetParent(current);
                else
                    current = LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private Border BuildSelectedSummary()
        {
            var border = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(23, 37, 65), Color.FromRgb(36, 28, 68), 135),
                BorderBrush = AccentBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                MinHeight = 96
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var portrait = new Grid { Width = 58, Height = 58, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            _selectedFallback = new TextBlock
            {
                Text = "?",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            portrait.Children.Add(_selectedFallback);
            _selectedImage = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            portrait.Children.Add(_selectedImage);
            var portraitBorder = new Border
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 147, 197, 253)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                Child = portrait
            };
            grid.Children.Add(portraitBorder);

            var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            _selectedName = new TextBlock { Text = "캐릭터 미선택", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            _selectedMeta = new TextBlock { Text = "카드를 선택해 주세요.", FontSize = 8, Foreground = new SolidColorBrush(Muted), Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            _selectedPower = new TextBlock { Text = "", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(191, 219, 254)), Margin = new Thickness(0, 4, 0, 0) };
            copy.Children.Add(_selectedName);
            copy.Children.Add(_selectedMeta);
            copy.Children.Add(_selectedPower);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);
            border.Child = grid;
            return border;
        }

        private Button BuildCharacterCard(CharacterProfile profile)
        {
            var button = new Button
            {
                Height = 194,
                Margin = new Thickness(6),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(13, 19, 30)),
                BorderBrush = new SolidColorBrush(Line),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Template = CreateRoundedButtonTemplate(11)
            };

            var card = new Grid { ClipToBounds = true };
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(126) });
            card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });

            var visual = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(6, 10, 18)),
                ClipToBounds = true
            };
            var fallback = new TextBlock
            {
                Text = String.IsNullOrWhiteSpace(profile.ClassName) ? "K" : profile.ClassName.Substring(0, 1),
                FontSize = 36,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            visual.Children.Add(fallback);
            if (!String.IsNullOrWhiteSpace(profile.ProfileImageUrl))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(profile.ProfileImageUrl, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 280;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    var image = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        SnapsToDevicePixels = true
                    };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    visual.Children.Add(image);
                }
                catch { }
            }
            if (profile.IsMain)
            {
                var badge = new Border
                {
                    Background = AccentBrush(),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(7, 3, 7, 3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(7)
                };
                badge.Child = new TextBlock { Text = "본캐", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
                visual.Children.Add(badge);
            }
            card.Children.Add(visual);

            var infoPanel = new Border
            {
                Background = CardInfoBrush(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 165, 180, 252)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(11, 7, 11, 7)
            };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = profile.CharacterName, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = profile.ServerName + " · " + profile.ClassName, FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(224, 231, 255)), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = "PVE " + profile.PveCombatPower.ToString("N0"), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 3, 0, 0) });
            infoPanel.Child = info;
            Grid.SetRow(infoPanel, 1);
            card.Children.Add(infoPanel);
            button.Content = card;
            _characterCards[button] = profile;
            button.Click += delegate { ChooseCharacter(profile); };
            button.MouseEnter += delegate { ApplyCardVisual(button, profile, true); };
            button.MouseLeave += delegate { ApplyCardVisual(button, profile, false); };
            return button;
        }

        private void ChooseCharacter(CharacterProfile profile)
        {
            if (_selectionBusy || profile == null) return;
            _candidate = profile;
            foreach (var pair in _characterCards) ApplyCardVisual(pair.Key, pair.Value, false);
            UpdateSelectedSummary(profile);
            if (_message != null) _message.Text = profile.CharacterName + " 선택됨";
            RefreshMeterStartState();
        }

        private void ApplyCardVisual(Button button, CharacterProfile profile, bool hover)
        {
            var selected = _candidate != null && String.Equals(_candidate.CharacterKey, profile.CharacterKey, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                button.BorderBrush = AccentBrush();
                button.BorderThickness = new Thickness(2);
                button.Background = new LinearGradientBrush(Color.FromRgb(21, 45, 83), Color.FromRgb(45, 31, 78), 135);
            }
            else if (hover)
            {
                button.BorderBrush = new LinearGradientBrush(Color.FromRgb(96, 165, 250), Color.FromRgb(129, 140, 248), 135);
                button.BorderThickness = new Thickness(1);
                button.Background = new SolidColorBrush(Color.FromRgb(27, 36, 51));
            }
            else
            {
                button.BorderBrush = new SolidColorBrush(Line);
                button.BorderThickness = new Thickness(1);
                button.Background = new SolidColorBrush(Color.FromRgb(22, 29, 40));
            }
        }

        private void UpdateSelectedSummary(CharacterProfile profile)
        {
            if (profile == null) return;
            _selectedName.Text = profile.CharacterName + (profile.IsMain ? " · 본캐" : " · 부캐");
            _selectedMeta.Text = profile.ServerName + " · " + profile.ClassName;
            _selectedPower.Text = "PVE " + profile.PveCombatPower.ToString("N0");
            _selectedFallback.Text = String.IsNullOrWhiteSpace(profile.ClassName) ? "K" : profile.ClassName.Substring(0, 1);
            _selectedImage.Source = null;
            _selectedImage.Visibility = Visibility.Collapsed;
            if (!String.IsNullOrWhiteSpace(profile.ProfileImageUrl))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(profile.ProfileImageUrl, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 90;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    _selectedImage.Source = bitmap;
                    _selectedImage.Visibility = Visibility.Visible;
                }
                catch { }
            }
            if (_infoButton != null) _infoButton.IsEnabled = true;
        }

        private void OpenSelectedCharacterInfo()
        {
            if (_candidate == null) return;
            var url = String.IsNullOrWhiteSpace(_candidate.DetailUrl) ? "https://kinojo.info/" : _candidate.DetailUrl;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("INFO", "KINOJO INFO open failed", ex);
                MessageBox.Show(this, "KINOJO INFO를 열지 못했습니다.", "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RefreshMeterStartState()
        {
            if (_meterStartButton == null) return;
            _meterStartButton.IsEnabled = _candidate != null && !_selectionBusy && !_updateBusy && !_pendingUpdateMandatory;
            _meterStartButton.Opacity = _meterStartButton.IsEnabled ? 1.0 : 0.52;
        }

        private void RefreshUpdateBlockedState()
        {
            if (_loginButton != null)
            {
                _loginButton.IsEnabled = !_pendingUpdateMandatory && !_updateBusy && !_loginBusy;
                _loginButton.Opacity = _loginButton.IsEnabled ? 1.0 : 0.52;
            }
            if (_passKeyInput != null) _passKeyInput.SetInputEnabled(!_pendingUpdateMandatory && !_updateBusy && !_loginBusy);
            RefreshMeterStartState();
        }

        private async Task SelectCharacterAsync(CharacterProfile profile)
        {
            try
            {
                StopAutomaticCharacterDetection();
                await _api.SelectCharacterAsync(_login.SessionToken, profile);
                _catalog = await _api.DesktopBootstrapAsync(_catalog == null ? null : _catalog.CatalogVersion);
                if (_hudProbe != null) _hudProbe.UpdateDungeons(_catalog == null ? null : _catalog.Dungeons);
                if (_hudProbe != null) _hudProbe.UpdateDifficulties(_catalog == null ? null : _catalog.Difficulties);
                _serverUpdateManifest = _catalog.DesktopUpdate ?? _serverUpdateManifest;
                PresentUpdateIfAvailable(_serverUpdateManifest, false);
                if (_pendingUpdateMandatory)
                {
                    if (_message != null) _message.Text = "필수 업데이트 후 미터를 실행할 수 있습니다.";
                    return;
                }
                _selected = profile;
                DiagnosticLog.Info("CHARACTER", "Selected " + profile.CharacterName + " / " + profile.ServerName);
                var discovery = _discovery;
                _discovery = null;
                StopCharacterDiscoveryTimeout();
                if (discovery != null) discovery.Close();
                OpenBackgroundMeter();
            }
            catch (MeterApiException ex)
            {
                DiagnosticLog.Error("CHARACTER", ex.Code, ex);
                if (_discovery != null) _discovery.SetStatus("캐릭터 연결에 실패했습니다 · 자동 검색을 다시 시작합니다");
                if (_selected == null) StartAutomaticCharacterDetection();
                MessageBox.Show(this, ex.Message, "캐릭터 연결 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("CHARACTER", "Unexpected selection failure", ex);
                if (_discovery != null) _discovery.SetStatus("캐릭터 연결에 실패했습니다 · 자동 검색을 다시 시작합니다");
                if (_selected == null) StartAutomaticCharacterDetection();
                MessageBox.Show(this, "캐릭터를 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.", "캐릭터 연결 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void StartAutomaticCharacterDetection()
        {
            StopAutomaticCharacterDetection();
            if (_login == null || _login.Characters == null || _login.Characters.Count == 0 || _pendingUpdateMandatory) return;
            _autoSelectionStarted = true;
            _characterDetector = new CombatCaptureCoordinator();
            _characterDetector.StatusChanged += delegate(object sender, string status)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_message != null && _selected == null) _message.Text = "접속 캐릭터 자동 확인 중 · " + status;
                    if (_discovery != null) _discovery.SetStatus("캐릭터 자동 검색 중 · " + status);
                }));
            };
            _characterDetector.DiagnosticStatusChanged += delegate(object sender, string status)
            {
                DiagnosticLog.Info("AUTO_CHARACTER", status);
            };
            _characterDetector.CombatEventReceived += delegate(object sender, CombatEvent value)
            {
                if (value == null || value.Kind != CombatEventKind.EntityIdentity || String.IsNullOrWhiteSpace(value.ActorName)) return;
                Dispatcher.BeginInvoke(new Action(async delegate
                {
                    if (!_autoSelectionStarted || _selectionBusy || _selected != null || _login == null) return;
                    var matches = _login.Characters.Where(character => String.Equals((character.CharacterName ?? "").Trim(), value.ActorName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 1) await SelectDetectedCharacterAsync(matches[0], "게임 패킷");
                }));
            };
            _characterDetector.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                if (_hudProbe != null) _hudProbe.UpdatePartyMembers(value == null ? null : value.Members);
                Dispatcher.BeginInvoke(new Action(async delegate
                {
                    if (!_autoSelectionStarted || _selectionBusy || _selected != null || _login == null) return;
                    var names = new HashSet<string>(
                        (value.Members ?? new List<DetectedPartyMember>())
                            .Select(member => (member.CharacterName ?? "").Trim())
                            .Where(name => name.Length > 0),
                        StringComparer.OrdinalIgnoreCase);
                    var matches = _login.Characters
                        .Where(character => names.Contains((character.CharacterName ?? "").Trim()))
                        .ToList();
                    if (matches.Count != 1)
                    {
                        DiagnosticLog.Info("AUTO_CHARACTER", "Roster observed but owned-character match count was " + matches.Count + ".");
                        return;
                    }

                    var detected = matches[0];
                    DiagnosticLog.Info("AUTO_CHARACTER", "Detected " + detected.CharacterName + " from party roster" + (value.LateAttached ? " by late attach." : "."));
                    await SelectDetectedCharacterAsync(detected, "파티 패킷");
                }));
            };
            _characterDetector.Start();
            if (_message != null) _message.Text = "아이온2 접속 캐릭터 자동 확인 중";
            if (_discovery != null) _discovery.SetStatus("캐릭터 자동 검색 중 · 아이온2 연결을 확인하고 있습니다");
            DiagnosticLog.Info("AUTO_CHARACTER", "Detection capture started before character selection.");
        }

        private void StartCharacterDiscoveryTimeout()
        {
            StopCharacterDiscoveryTimeout();
            _characterDiscoveryTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _characterDiscoveryTimeoutTimer.Tick += delegate
            {
                StopCharacterDiscoveryTimeout();
                if (_selected != null || _discovery == null) return;
                _discovery.ShowManualCards("자동 검색이 지연되고 있습니다 · 검색은 계속되며 직접 선택도 가능합니다");
                DiagnosticLog.Info("AUTO_CHARACTER", "Automatic selection exceeded 5 seconds; manual character cards were expanded while detection continues.");
            };
            _characterDiscoveryTimeoutTimer.Start();
        }

        private void StopCharacterDiscoveryTimeout()
        {
            var timer = _characterDiscoveryTimeoutTimer;
            _characterDiscoveryTimeoutTimer = null;
            if (timer != null) timer.Stop();
        }

        private void StopAutomaticCharacterDetection()
        {
            _autoSelectionStarted = false;
            var detector = _characterDetector;
            _characterDetector = null;
            if (detector == null) return;
            try { detector.Stop(); }
            catch { }
            detector.Dispose();
        }

        private void StartGameHudProbe()
        {
            StopGameHudProbe();
            if (_login == null || _login.Characters == null || _login.Characters.Count == 0) return;
            _hudProbe = new GameHudProbe();
            _hudProbe.UpdateCharacters(_login.Characters);
            _hudProbe.UpdateDungeons(_catalog == null ? null : _catalog.Dungeons);
            _hudProbe.UpdateDifficulties(_catalog == null ? null : _catalog.Difficulties);
            _hudProbe.StatusChanged += delegate(object sender, string status)
            {
                DiagnosticLog.Info("HUD_PROBE", status);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_message != null && _selected == null) _message.Text = status;
                    if (_discovery != null && _selected == null) _discovery.SetStatus(status);
                }));
            };
            _hudProbe.ObservationReady += delegate(object sender, GameHudObservation observation)
            {
                Dispatcher.BeginInvoke(new Action(async delegate { await HandleHudObservationAsync(observation); }));
            };
            _hudProbe.Start();
            DiagnosticLog.Info("HUD_PROBE", "Fixed-region Windows OCR probe started.");
        }

        private async Task HandleHudObservationAsync(GameHudObservation observation)
        {
            if (observation == null) return;
            _lastHudObservation = observation;
            if (_overlay != null) _overlay.ApplyHudObservation(observation);
            if (String.IsNullOrWhiteSpace(observation.CharacterName) || _login == null || _selectionBusy) return;

            var matches = (_login.Characters ?? new List<CharacterProfile>())
                .Where(character => String.Equals(
                    (character.CharacterName ?? "").Trim(),
                    observation.CharacterName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                DiagnosticLog.Info("HUD_PROBE", "Owned-character OCR match count was " + matches.Count +
                    " for '" + observation.CharacterName + "'.");
                return;
            }

            var detected = matches[0];
            if (_selected != null &&
                ((!String.IsNullOrWhiteSpace(detected.CharacterKey) &&
                  String.Equals(detected.CharacterKey, _selected.CharacterKey, StringComparison.OrdinalIgnoreCase)) ||
                 (!String.IsNullOrWhiteSpace(detected.CharKey) &&
                  String.Equals(detected.CharKey, _selected.CharKey, StringComparison.OrdinalIgnoreCase)) ||
                 String.Equals(detected.CharacterName, _selected.CharacterName, StringComparison.OrdinalIgnoreCase)))
                return;

            _selectionBusy = true;
            RefreshMeterStartState();
            var fromWindowTitle = String.Equals(observation.Evidence, "AION2_WINDOW_TITLE", StringComparison.OrdinalIgnoreCase);
            var evidenceLabel = fromWindowTitle ? "게임 창 제목" : "화면 OCR";
            if (_message != null) _message.Text = detected.CharacterName + " " + evidenceLabel + " 확인 · 자동 연결 중";
            if (_discovery != null) _discovery.MarkDetected(detected, evidenceLabel);
            DiagnosticLog.Info("HUD_PROBE", (_selected == null ? "Character detected" : "Character change detected") +
                (fromWindowTitle ? " by AION2 window title: " : " by centered self-name OCR: ") + detected.CharacterName + ".");
            try { await SelectCharacterAsync(detected); }
            finally
            {
                _selectionBusy = false;
                RefreshMeterStartState();
            }
        }

        private void StopGameHudProbe()
        {
            var probe = _hudProbe;
            _hudProbe = null;
            _lastHudObservation = null;
            if (probe == null) return;
            try { probe.Stop(); }
            catch { }
            probe.Dispose();
        }

        private void OpenBackgroundMeter()
        {
            if (_overlay != null) _overlay.Close();
            _overlay = new OverlayWindow(_selected, _preferences, _catalog, _login != null && _login.IsMeterAdmin);
            if (_lastHudObservation != null) _overlay.ApplyHudObservation(_lastHudObservation);
            _overlay.HideRequested += delegate
            {
                _manualOverlayVisible = false;
                _manualOverlayHidden = true;
                _lastOverlayVisibilityDemand = false;
                HideOverlay();
            };
            _overlay.CaptureStatusChanged += delegate(object sender, string status)
            {
                if (_tray != null) _tray.SetStatus(status);
            };
            _overlay.CaptureDiagnosticChanged += delegate(object sender, string status)
            {
                DiagnosticLog.Info("CAPTURE", status);
                if (_tray != null) _tray.SetAdminCaptureStatus(status);
            };
            _overlay.FixtureCaptureStateChanged += delegate
            {
                if (_tray == null || _overlay == null) return;
                _tray.SetFixtureCaptureActive(_overlay.IsFixtureCaptureActive);
                _tray.SetStatus(_overlay.IsFixtureCaptureActive ? "관리자 패킷 진단 수집 중 · 최대 20분" : "패킷 진단 수집 완료");
            };
            _overlay.CharacterIdentityObserved += delegate(object sender, CombatEvent value)
            {
                Dispatcher.BeginInvoke(new Action(async delegate
                {
                    if (_selectionBusy || _login == null || _selected == null || value == null) return;
                    var matches = _login.Characters.Where(character => String.Equals((character.CharacterName ?? "").Trim(), (value.ActorName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1 || String.Equals(matches[0].CharacterName, _selected.CharacterName, StringComparison.OrdinalIgnoreCase)) return;
                    DiagnosticLog.Info("AUTO_CHARACTER", "Character change detected from entity identity: " + _selected.CharacterName + " -> " + matches[0].CharacterName + ".");
                    await SelectDetectedCharacterAsync(matches[0], "캐릭터 변경 패킷");
                }));
            };
            _overlay.ParticipantDetected += async delegate(object sender, CombatRow row) { await EnrichParticipantAsync(row); };
            _overlay.EncounterCompleted += async delegate(object sender, CombatSnapshot snapshot) { await UploadEncounterAsync(snapshot); };
            _overlay.PartyRosterObserved += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                if (_hudProbe != null) _hudProbe.UpdatePartyMembers(value == null ? null : value.Members);
                Dispatcher.BeginInvoke(new Action(async delegate
                {
                    if (_selectionBusy || _login == null || _selected == null) return;
                    var names = new HashSet<string>(
                        (value.Members ?? new List<DetectedPartyMember>())
                            .Select(member => (member.CharacterName ?? "").Trim())
                            .Where(name => name.Length > 0),
                        StringComparer.OrdinalIgnoreCase);
                    var matches = _login.Characters
                        .Where(character => names.Contains((character.CharacterName ?? "").Trim()))
                        .ToList();
                    if (matches.Count != 1) return;
                    var detected = matches[0];
                    if ((!String.IsNullOrWhiteSpace(detected.CharacterKey) &&
                         String.Equals(detected.CharacterKey, _selected.CharacterKey, StringComparison.OrdinalIgnoreCase)) ||
                        (!String.IsNullOrWhiteSpace(detected.CharKey) &&
                         String.Equals(detected.CharKey, _selected.CharKey, StringComparison.OrdinalIgnoreCase)) ||
                        String.Equals(detected.CharacterName, _selected.CharacterName, StringComparison.OrdinalIgnoreCase))
                        return;

                    _selectionBusy = true;
                    DiagnosticLog.Info("AUTO_CHARACTER", "Character change detected from live party roster: " +
                        _selected.CharacterName + " -> " + detected.CharacterName + ".");
                    try { await SelectCharacterAsync(detected); }
                    finally
                    {
                        _selectionBusy = false;
                        RefreshMeterStartState();
                    }
                }));
            };

            _tray?.Dispose();
            _tray = new SystemTrayController(_selected.CharacterName, _login != null && _login.IsMeterAdmin);
            _tray.ShowOverlayRequested += delegate
            {
                _manualOverlayHidden = false;
                _manualOverlayVisible = true;
                _lastOverlayVisibilityDemand = true;
                ShowOverlay();
            };
            _tray.HideOverlayRequested += delegate
            {
                _manualOverlayVisible = false;
                _manualOverlayHidden = true;
                _lastOverlayVisibilityDemand = false;
                HideOverlay();
            };
            _tray.RestartCaptureRequested += delegate { if (_overlay != null) _overlay.RestartCapture(); };
            _tray.ToggleFixtureCaptureRequested += delegate
            {
                if (_overlay == null) return;
                var directory = _overlay.ToggleFixtureCapture();
                var active = _overlay.IsFixtureCaptureActive;
                _tray.SetFixtureCaptureActive(active);
                _tray.SetStatus(active ? "관리자 패킷 진단 수집 중 · 최대 20분" : "패킷 진단 수집 완료");
                DiagnosticLog.Info("FIXTURE", (active ? "Started" : "Stopped") + " · " + directory);
            };
            _tray.DiagnosticMarkerRequested += delegate(string marker)
            {
                if (_overlay == null || !_overlay.IsFixtureCaptureActive)
                {
                    _tray.SetStatus("먼저 패킷 진단 수집을 시작하세요");
                    return;
                }
                var added = _overlay.AddFixtureMarker(marker);
                _tray.SetStatus(added ? "진단 마커 기록 · " + marker : "진단 마커 기록 실패");
            };
            _tray.OpenDiagnosticsRequested += delegate { DiagnosticLog.OpenFolder(); };
            _tray.CheckUpdateRequested += async delegate { await CheckForUpdatesFromTrayAsync(); };
            _tray.LogoutRequested += async delegate
            {
                await LogoutAsync(false);
                Show();
                Activate();
                ShowLogin();
            };
            _tray.ExitRequested += async delegate { await ExitAsync(); };

            _overlay.Show();
            _overlay.Hide();
            _lastOverlayVisibilityDemand = false;
            _tray.SetOverlayVisible(false);
            _tray.ShowReadyBalloon();
            _tray.SetStatus("게임 연결 준비 중");

            _foregroundTimer?.Stop();
            _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _foregroundTimer.Tick += delegate
            {
                if (_overlay == null || _tray == null || _tray.IsMenuOpen) return;
                UpdateOverlayVisibility();
            };
            _foregroundTimer.Start();
            UpdateOverlayVisibility();
            Hide();
        }

        private void UpdateOverlayVisibility()
        {
            var shouldShow = !_manualOverlayHidden && (_manualOverlayVisible || AionWindowMonitor.IsAion2Foreground());
            if (_lastOverlayVisibilityDemand.HasValue &&
                _lastOverlayVisibilityDemand.Value == shouldShow &&
                _overlay.IsVisible == shouldShow) return;

            _lastOverlayVisibilityDemand = shouldShow;
            if (shouldShow) ShowOverlay();
            else HideOverlay();
        }

        private void ShowOverlay()
        {
            if (_overlay == null) return;
            _overlay.ShowWithoutActivation();
            if (_tray != null) _tray.SetOverlayVisible(true);
        }

        private void HideOverlay()
        {
            if (_overlay != null && _overlay.IsVisible) _overlay.Hide();
            if (_tray != null) _tray.SetOverlayVisible(false);
        }

        private async Task CheckStartupUpdateAsync()
        {
            if (_startupUpdateChecked) return;
            _startupUpdateChecked = true;
            try
            {
                var check = await _api.GetDesktopUpdateAsync();
                if (!check.ClientVersionValid)
                {
                    DiagnosticLog.Info("UPDATE", "Server rejected the installed client version format: " + KinojoVersion.Current);
                    return;
                }
                _serverUpdateManifest = check.DesktopUpdate;
                PresentUpdateIfAvailable(_serverUpdateManifest, false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPDATE", "Startup update check skipped", ex);
            }
        }

        private bool PresentUpdateIfAvailable(MeterUpdateManifest manifest, bool notifyWhenCurrent)
        {
            var service = new DesktopUpdateService();
            string manifestError;
            if (manifest != null && !service.TryValidateManifest(manifest, out manifestError))
            {
                DiagnosticLog.Info("UPDATE", "Server update manifest rejected: " + manifestError);
                _pendingUpdate = null;
                _pendingUpdateMandatory = false;
                _updateHost.Visibility = Visibility.Collapsed;
                RefreshUpdateBlockedState();
                if (notifyWhenCurrent) MessageBox.Show(this, "업데이트 배포 정보를 검증하지 못했습니다.", "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            bool mandatory;
            if (!service.IsUpdateAvailable(manifest, out mandatory))
            {
                _pendingUpdate = null;
                _pendingUpdateMandatory = false;
                _updateHost.Visibility = Visibility.Collapsed;
                RefreshUpdateBlockedState();
                if (notifyWhenCurrent) MessageBox.Show(this, "현재 최신 버전을 사용하고 있습니다.", "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            _pendingUpdate = manifest;
            _pendingUpdateMandatory = mandatory;
            _updateTitle.Text = "KINOJO Meter " + manifest.Version + (mandatory ? " 필수 업데이트" : " 업데이트");
            _updateDetail.Text = String.IsNullOrWhiteSpace(manifest.ReleaseNote) ? "새 버전 다운로드와 설치를 준비합니다." : manifest.ReleaseNote;
            _updateProgress.Value = 0;
            _updateProgressText.Text = mandatory ? "업데이트 후 로그인과 미터 실행이 가능합니다." : "원할 때 다운로드하고 설치할 수 있습니다.";
            _updateActionButton.Content = "업데이트 다운로드";
            _updateActionButton.IsEnabled = true;
            _updateHost.Visibility = Visibility.Visible;
            RefreshUpdateBlockedState();
            return true;
        }

        private async Task InstallPendingUpdateAsync()
        {
            if (_pendingUpdate == null || _updateBusy) return;
            _updateBusy = true;
            _updateActionButton.IsEnabled = false;
            _updateActionButton.Content = "업데이트 준비 중";
            RefreshUpdateBlockedState();
            try
            {
                var progress = new Progress<UpdateProgressInfo>(delegate(UpdateProgressInfo value)
                {
                    _updateProgress.Value = Math.Max(0, Math.Min(100, value.Percentage));
                    _updateProgressText.Text = value.Stage + (String.IsNullOrWhiteSpace(value.Detail) ? "" : " · " + value.Detail);
                });
                var service = new DesktopUpdateService();
                var started = await service.DownloadAndLaunchAsync(_pendingUpdate, progress);
                if (started)
                {
                    _updateProgress.Value = 100;
                    _updateProgressText.Text = "설치 프로그램 실행 · KINOJO Meter를 다시 시작합니다.";
                    _closing = true;
                    Application.Current.Shutdown();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPDATE", "Update installation failed", ex);
                _updateProgressText.Text = "업데이트 실패 · 관리자 진단 로그를 확인해 주세요.";
                _updateActionButton.Content = "다시 다운로드";
                MessageBox.Show(this, "업데이트를 설치하지 못했습니다.\n\n" + ex.Message, "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _updateBusy = false;
                if (_updateActionButton != null) _updateActionButton.IsEnabled = true;
                RefreshUpdateBlockedState();
            }
        }

        private async Task CheckForUpdatesFromTrayAsync()
        {
            try
            {
                var check = await _api.GetDesktopUpdateAsync();
                _serverUpdateManifest = check.DesktopUpdate;
                if (PresentUpdateIfAvailable(_serverUpdateManifest, true))
                {
                    Show();
                    Activate();
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPDATE", "Manual update check failed", ex);
                MessageBox.Show("업데이트 정보를 확인하지 못했습니다.", "KINOJO Meter", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task EnrichParticipantAsync(CombatRow row)
        {
            if (_login == null || row == null || row.IsEmpty) return;
            var key = !String.IsNullOrWhiteSpace(row.PlatformCharacterId) ? row.PlatformCharacterId : (row.ServerId + ":" + row.Name);
            if (String.IsNullOrWhiteSpace(key)) return;
            lock (_profileRequestGate)
            {
                DateTime lastAttempt;
                if (_profileRequestedAt.TryGetValue(key, out lastAttempt) && DateTime.UtcNow - lastAttempt < TimeSpan.FromMinutes(2)) return;
                _profileRequestedAt[key] = DateTime.UtcNow;
            }
            try
            {
                var profiles = await _api.GetPartyProfilesAsync(_login.SessionToken, new[] { row });
                foreach (var profile in profiles)
                {
                    if (profile.Ok) _overlay?.ApplyProfile(profile);
                    else DiagnosticLog.Info("PROFILE", "Party profile unresolved · name=" + (row.Name ?? "") + " · reason=" + (profile.ReasonCode ?? "") + " · " + (profile.Message ?? ""));
                }
                if (profiles.Count == 0 || profiles.Any(profile => !profile.Ok || String.Equals(profile.ProfileRefreshStatus, "QUEUED", StringComparison.OrdinalIgnoreCase)))
                {
                    lock (_profileRequestGate) _profileRequestedAt[key] = DateTime.UtcNow - TimeSpan.FromMinutes(1);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("PROFILE", "Party profile enrichment failed", ex);
                lock (_profileRequestGate) _profileRequestedAt[key] = DateTime.UtcNow - TimeSpan.FromMinutes(1);
            }
        }

        private async Task UploadEncounterAsync(CombatSnapshot snapshot)
        {
            if (_login == null || _selected == null || snapshot == null || !snapshot.IsCleared) return;
            _overlay?.SetEncounterProcessingState("FINALIZING", "보스 전투 종료 · 결과 고정 및 가상 서버 처리 중");
            var localResultPath = DiagnosticLog.SaveEncounterSnapshot(snapshot);
            if (!String.IsNullOrWhiteSpace(localResultPath))
                DiagnosticLog.Info("LOCAL_RESULT", "Encounter snapshot saved · " + localResultPath);
            var outboxPath = DiagnosticLog.SaveEncounterOutbox(snapshot, _selected, snapshot.UploadEligible ? "SUBMISSION_READY" : "SIMULATED");
            if (!String.IsNullOrWhiteSpace(outboxPath))
                DiagnosticLog.Info("OUTBOX", "Encounter staged · " + outboxPath);
            if (!snapshot.UploadEligible)
            {
                _overlay?.SetEncounterProcessingState("WAITING_NEXT_BOSS", "가상 서버 처리 완료 · 다음 보스 전투 데이터 수집 대기");
                DiagnosticLog.Info("UPLOAD", "Upload blocked by decoder validation gate");
                return;
            }
            var durationMs = snapshot.StartedAtUtc == DateTime.MinValue || snapshot.LastEventUtc == DateTime.MinValue ? 0L : Math.Max(0L, (long)(snapshot.LastEventUtc - snapshot.StartedAtUtc).TotalMilliseconds);
            var self = snapshot.Rows.FirstOrDefault(row => row.IsSelf && !row.IsEmpty);
            if (durationMs < 5000 || self == null || self.TotalDamage <= 0 || String.IsNullOrWhiteSpace(snapshot.BossName)) return;

            try
            {
                _overlay?.SetEncounterProcessingState("UPLOADING", "보스 전투 종료 · 서버 결과 저장 중");
                var context = new EncounterCatalogContext
                {
                    CatalogVersion = _catalog == null ? "" : _catalog.CatalogVersion,
                    ContentKey = snapshot.ContentKey,
                    ContentName = snapshot.ContentName,
                    DungeonKey = snapshot.DungeonKey,
                    DungeonName = snapshot.DungeonName,
                    DifficultyKey = snapshot.DifficultyKey,
                    DifficultyName = snapshot.DifficultyName,
                    VariantKey = snapshot.VariantKey,
                    PartySize = snapshot.Rows.Count(row => !row.IsEmpty)
                };
                var canonical = await _api.ResolveEncounterCatalogAsync(context, _selected, snapshot.BossName);
                var participants = snapshot.Rows.Where(row => !row.IsEmpty).Select(row => (object)new Dictionary<string, object>
                {
                    { "participantKey", row.ParticipantKey ?? ((row.Name ?? "") + ":" + row.PartyNumber + ":" + row.PartySlot) },
                    { "platformCharacterId", row.PlatformCharacterId ?? "" },
                    { "serverId", row.ServerId ?? "" },
                    { "serverName", row.ServerName ?? "" },
                    { "characterName", row.Name ?? "" },
                    { "classKey", row.ClassKey ?? "" },
                    { "className", row.ClassName ?? "" },
                    { "profileImageUrl", row.ProfileImageUrl ?? "" },
                    { "pveCombatPower", row.CombatPower },
                    { "itemLevel", row.ItemLevel },
                    { "partyNumber", row.PartyNumber },
                    { "partySlot", row.PartySlot },
                    { "totalDamage", row.TotalDamage },
                    { "dps", row.Dps },
                    { "damageShare", row.Share },
                    { "isSelf", row.IsSelf }
                }).ToList();
                var payload = new Dictionary<string, object>
                {
                    { "sourceEventId", BuildSourceEventId(snapshot, _selected) },
                    { "catalogVersion", canonical.CatalogVersion },
                    { "classKey", canonical.ClassKey },
                    { "serverId", _selected.ServerId ?? "" },
                    { "pveCombatPower", _selected.PveCombatPower },
                    { "contentKey", canonical.ContentKey },
                    { "dungeonKey", canonical.DungeonKey },
                    { "difficultyKey", canonical.DifficultyKey },
                    { "variantKey", canonical.VariantKey },
                    { "bossKey", canonical.BossKey },
                    { "bossName", canonical.BossName },
                    { "dungeonName", canonical.DungeonName },
                    { "encounterStatus", "CLEARED" },
                    { "status", "CLEARED" },
                    { "totalDamage", self.TotalDamage },
                    { "dps", self.Dps },
                    { "durationMs", durationMs },
                    { "activeDurationMs", durationMs },
                    { "partySize", participants.Count },
                    { "schemaVersion", 3 },
                    { "clientVersion", KinojoApiClient.ClientVersion },
                    { "occurredAt", snapshot.LastEventUtc.ToUniversalTime().ToString("o") },
                    { "captureEngine", snapshot.CaptureEngine ?? "" },
                    { "captureMode", snapshot.CaptureMode ?? "" },
                    { "decoderType", snapshot.DecoderType ?? "" },
                    { "decoderVersion", snapshot.DecoderVersion ?? "" },
                    { "catalogResolutionMode", "SERVER_CANONICAL" },
                    { "participants", participants }
                };
                await _api.SubmitEncounterAsync(_login.SessionToken, payload);
                _overlay?.SetEncounterProcessingState("WAITING_NEXT_BOSS", "서버 저장 완료 · 다음 보스 전투 데이터 수집 대기");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Error("UPLOAD", "Encounter submission failed", ex);
                _overlay?.SetEncounterProcessingState("WAITING_NEXT_BOSS", "서버 저장 실패 · 로컬 보관 완료 · 다음 보스 대기");
            }
        }

        private static string BuildSourceEventId(CombatSnapshot snapshot, CharacterProfile character)
        {
            var raw = String.Join("|", new[] { character.CharacterKey ?? "", snapshot.BossId ?? "", snapshot.BossName ?? "", snapshot.StartedAtUtc.ToUniversalTime().Ticks.ToString(), snapshot.LastEventUtc.ToUniversalTime().Ticks.ToString() });
            using (var sha = SHA256.Create()) return "meter_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").ToLowerInvariant();
        }

        private async Task LogoutAsync(bool exit)
        {
            StopAutomaticCharacterDetection();
            StopCharacterDiscoveryTimeout();
            var discovery = _discovery;
            _discovery = null;
            if (discovery != null) discovery.Close();
            _foregroundTimer?.Stop();
            _foregroundTimer = null;
            var overlay = _overlay;
            _overlay = null;
            if (overlay != null) overlay.Close();
            _tray?.Dispose();
            _tray = null;
            if (_login != null)
            {
                try { await _api.LogoutAsync(_login.SessionToken); }
                catch (Exception ex) { DiagnosticLog.Error("AUTH", "Logout request failed", ex); }
            }
            StopGameHudProbe();
            _login = null;
            _catalog = null;
            _selected = null;
            lock (_profileRequestGate) _profileRequestedAt.Clear();
            _manualOverlayVisible = false;
            if (exit)
            {
                _closing = true;
                Application.Current.Shutdown();
            }
        }

        private async Task ExitAsync()
        {
            await LogoutAsync(true);
        }

        private void SetMessage(string text, bool error)
        {
            if (_message == null) return;
            _message.Text = text;
            _message.Foreground = new SolidColorBrush(error ? Color.FromRgb(248, 113, 113) : Accent);
        }

        private void ShowError(string message)
        {
            SetMessage(String.IsNullOrWhiteSpace(message) ? "인증 정보를 확인해 주세요." : message, true);
        }

        private static Button ChromeButton(string text)
        {
            return new Button
            {
                Content = text,
                Width = 42,
                Height = 38,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = Cursors.Hand
            };
        }

        private static Button PrimaryButton(string text)
        {
            var normal = AccentBrush();
            var hover = AccentHoverBrush();
            var button = new Button
            {
                Content = text,
                Height = 43,
                Background = normal,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(10)
            };
            AttachRolloverAnimation(button, normal, hover, 1.025);
            return button;
        }

        private static Button SecondaryAccentButton(string text)
        {
            var normal = SecondaryAccentBrush();
            var hover = SecondaryAccentHoverBrush();
            var button = new Button
            {
                Content = text,
                Height = 40,
                Background = normal,
                Foreground = new SolidColorBrush(Color.FromRgb(238, 242, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(135, 129, 140, 248)),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(10)
            };
            AttachRolloverAnimation(button, normal, hover, 1.018);
            return button;
        }

        private static Button NeutralButton(string text)
        {
            var normal = new SolidColorBrush(PanelSoft);
            var hover = new SolidColorBrush(Color.FromRgb(40, 52, 73));
            normal.Freeze();
            hover.Freeze();
            var button = new Button
            {
                Content = text,
                Height = 40,
                Background = normal,
                Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                BorderBrush = new SolidColorBrush(Line),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Template = CreateRoundedButtonTemplate(10)
            };
            AttachRolloverAnimation(button, normal, hover, 1.012);
            return button;
        }

        private static LinearGradientBrush AccentBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Accent, 0.0));
            brush.GradientStops.Add(new GradientStop(AccentDeep, 0.48));
            brush.GradientStops.Add(new GradientStop(AccentViolet, 1.0));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush AccentHoverBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(59, 130, 246), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(99, 102, 241), 0.48));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(139, 92, 246), 1.0));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush SecondaryAccentBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(30, 58, 138), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(49, 46, 129), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(76, 29, 149), 1.0));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush SecondaryAccentHoverBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(37, 99, 235), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(67, 56, 202), 0.52));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(109, 40, 217), 1.0));
            brush.Freeze();
            return brush;
        }

        private static LinearGradientBrush CardInfoBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(222, 37, 99, 235), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(214, 79, 70, 229), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(206, 124, 58, 237), 1.0));
            brush.Freeze();
            return brush;
        }

        private static void AttachRolloverAnimation(Button button, Brush normal, Brush hover, double hoverScale)
        {
            if (button == null) return;
            var scale = new ScaleTransform(1.0, 1.0);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = scale;

            button.MouseEnter += delegate
            {
                if (!button.IsEnabled) return;
                button.Background = hover;
                AnimateButtonScale(scale, hoverScale, 120);
                AnimateButtonOpacity(button, 1.0, 120);
            };
            button.MouseLeave += delegate
            {
                button.Background = normal;
                AnimateButtonScale(scale, 1.0, 140);
                AnimateButtonOpacity(button, button.IsEnabled ? 1.0 : 0.48, 140);
            };
            button.PreviewMouseLeftButtonDown += delegate
            {
                if (!button.IsEnabled) return;
                AnimateButtonScale(scale, 0.985, 70);
            };
            button.PreviewMouseLeftButtonUp += delegate
            {
                if (!button.IsEnabled) return;
                AnimateButtonScale(scale, button.IsMouseOver ? hoverScale : 1.0, 100);
            };
        }

        private static void AnimateButtonScale(ScaleTransform scale, double value, int milliseconds)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = easing };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static void AnimateButtonOpacity(UIElement element, double value, int milliseconds)
        {
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }

        private static ControlTemplate CreateRoundedButtonTemplate(double radius)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.AppendChild(presenter);
            template.VisualTree = border;
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.48));
            template.Triggers.Add(disabled);
            return template;
        }

        private static Style CreateVerticalScrollBarStyle()
        {
            const string xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>
<Setter Property='Width' Value='9'/>
<Setter Property='Margin' Value='5,0,0,0'/>
<Setter Property='Background' Value='#111827'/>
<Setter Property='Template'>
<Setter.Value>
<ControlTemplate TargetType='{x:Type ScrollBar}'>
<Border Background='{TemplateBinding Background}' CornerRadius='5'>
<Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='{TemplateBinding Orientation}'>
<Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>
<Track.Thumb>
<Thumb MinHeight='34'>
<Thumb.Template>
<ControlTemplate TargetType='{x:Type Thumb}'>
<Border x:Name='ThumbBody' Margin='1' Background='#4F46E5' CornerRadius='4'/>
<ControlTemplate.Triggers>
<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ThumbBody' Property='Background' Value='#7C3AED'/></Trigger>
<Trigger Property='IsDragging' Value='True'><Setter TargetName='ThumbBody' Property='Background' Value='#2563EB'/></Trigger>
</ControlTemplate.Triggers>
</ControlTemplate>
</Thumb.Template>
</Thumb>
</Track.Thumb>
<Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>
</Track>
</Border>
</ControlTemplate>
</Setter.Value>
</Setter>
</Style>";
            return (Style)XamlReader.Parse(xaml);
        }

        private static Style CreateProgressBarStyle()
        {
            const string xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ProgressBar}'>
<Setter Property='Background' Value='#253047'/>
<Setter Property='Foreground'>
<Setter.Value><LinearGradientBrush StartPoint='0,0.5' EndPoint='1,0.5'><GradientStop Color='#2563EB' Offset='0'/><GradientStop Color='#4F46E5' Offset='0.48'/><GradientStop Color='#7C3AED' Offset='1'/></LinearGradientBrush></Setter.Value>
</Setter>
<Setter Property='Template'>
<Setter.Value>
<ControlTemplate TargetType='{x:Type ProgressBar}'>
<Border x:Name='PART_Track' Background='{TemplateBinding Background}' CornerRadius='4' ClipToBounds='True'>
<Border x:Name='PART_Indicator' HorizontalAlignment='Left' Background='{TemplateBinding Foreground}' CornerRadius='4'/>
</Border>
</ControlTemplate>
</Setter.Value>
</Setter>
</Style>";
            return (Style)XamlReader.Parse(xaml);
        }
    }
}
