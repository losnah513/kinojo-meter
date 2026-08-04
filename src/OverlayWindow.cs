using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KinojoMeterPrototype
{
    internal sealed class OverlayWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x20;
        private const int WsExToolWindow = 0x80;
        private const int WsExNoActivate = 0x08000000;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private const int HotkeyId = 0x4B49;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint VkF8 = 0x77;
        private static readonly Color Accent = Color.FromRgb(56, 189, 248);

        private readonly MeterPreferences _preferences;
        private readonly CharacterProfile _selected;
        private readonly bool _isMeterAdmin;
        private readonly HashSet<string> _bossNames;
        private readonly List<CatalogDungeon> _catalogDungeons;
        private readonly List<CatalogDifficulty> _catalogDifficulties;
        private readonly List<CatalogBoss> _catalogBosses;
        private readonly Border _surface;
        private readonly Border _bossPanel;
        private readonly StackPanel _rows;
        private readonly TextBlock _bossName;
        private readonly TextBlock _encounterTime;
        private readonly TextBlock _stateLabel;
        private readonly KinojoSpinner _spinner;
        private readonly Border _bossHpHost;
        private readonly Border _bossHpFill;
        private readonly TextBlock _bossHpPercent;
        private readonly ScaleTransform _bossHpScale;
        private readonly Button _lockButton;
        private readonly DispatcherTimer _timer;
        private readonly CombatSessionEngine _engine;
        private CombatCaptureCoordinator _capture;
        private bool _running;
        private bool _partyObserved;
        private bool _dungeonEntered;
        private string _hudDungeonName = "";
        private string _hudDifficultyName = "";
        private string _hudDungeonKey = "";
        private readonly Dictionary<string, int> _bossOrderByTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CombatEvent>> _pendingDamageByTarget = new Dictionary<string, List<CombatEvent>>(StringComparer.OrdinalIgnoreCase);
        private string _currentBossTarget = "";
        private readonly Dictionary<string, int> _lastRowIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private List<string> _rankOrder = new List<string>();
        private DateTime _lastRankUpdateUtc = DateTime.MinValue;
        private readonly Dictionary<string, Color> _observedClassColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Color> _classRawColors = new Dictionary<int, Color>();
        private readonly Dictionary<int, string> _classRawKeys = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _classRawNames = new Dictionary<int, string>();
        private readonly Dictionary<string, double> _lastShareByRow = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private string _encounterProcessingState = "";
        private string _encounterProcessingText = "";
        private string _lastHudRosterSignature = "";
        private Button _diagnosticStartButton;
        private Button _diagnosticStopButton;
        private TextBlock _diagnosticState;

        public event EventHandler HideRequested;
        public event EventHandler ExitRequested;
        public event EventHandler<CombatSnapshot> EncounterCompleted;
        public event EventHandler<CombatRow> ParticipantDetected;
        public event EventHandler<PartyRosterDetectedEventArgs> PartyRosterObserved;
        public event EventHandler<CombatEvent> CharacterIdentityObserved;
        public event EventHandler<string> CaptureStatusChanged;
        public event EventHandler<string> CaptureDiagnosticChanged;
        public event EventHandler FixtureCaptureStateChanged;

        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public OverlayWindow(CharacterProfile selected, MeterPreferences preferences, MeterCatalog catalog, bool isMeterAdmin)
        {
            _selected = selected;
            _isMeterAdmin = isMeterAdmin;
            _catalogDungeons = (catalog == null ? new List<CatalogDungeon>() : catalog.Dungeons ?? new List<CatalogDungeon>()).ToList();
            _catalogDifficulties = (catalog == null ? new List<CatalogDifficulty>() : catalog.Difficulties ?? new List<CatalogDifficulty>()).ToList();
            _catalogBosses = (catalog == null ? new List<CatalogBoss>() : catalog.Bosses ?? new List<CatalogBoss>()).ToList();
            _bossNames = new HashSet<string>((catalog == null ? Enumerable.Empty<CatalogBoss>() : catalog.Bosses ?? new List<CatalogBoss>())
                .Select(value => (value.BossName ?? "").Trim()).Where(value => value.Length > 0), StringComparer.OrdinalIgnoreCase);
            _preferences = preferences ?? MeterPreferences.Default();
            _engine = new CombatSessionEngine(_selected, _preferences.GroupSize);
            _engine.EncounterCompleted += delegate
            {
                var completed = _engine.Snapshot();
                _encounterProcessingState = "FINALIZING";
                _encounterProcessingText = "보스 전투 종료 · 결과 고정 및 가상 처리 중";
                Render(completed);
                EncounterCompleted?.Invoke(this, completed);
            };
            _engine.ParticipantChanged += delegate(object sender, CombatRow row) { ParticipantDetected?.Invoke(this, row); };

            Title = "KINOJO Meter Overlay " + KinojoVersion.Current;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = false;
            ShowActivated = false;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 350;
            Width = Math.Max(350, Math.Min(500, _preferences.OverlayWidth));
            Height = 280;
            Left = _preferences.OverlayLeft;
            Top = _preferences.OverlayTop;

            _surface = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2)
            };
            Content = _surface;
            var root = new StackPanel();
            _surface.Child = root;

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var drag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(196, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, 71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = Cursors.SizeAll
            };
            var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            brand.Children.Add(new TextBlock { Text = "KINOJO-METER", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 9 });
            brand.Children.Add(new TextBlock { Text = "  v" + KinojoVersion.Current, Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 7, VerticalAlignment = VerticalAlignment.Center });
            drag.Child = brand;
            drag.MouseLeftButtonDown += delegate { if (!_preferences.Locked) DragMove(); };
            toolbar.Children.Add(drag);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            _lockButton = ToolButton("", "");
            UpdateLockButton();
            _lockButton.Click += delegate
            {
                _preferences.Locked = !_preferences.Locked;
                UpdateLockButton();
                PreferencesStore.Save(_preferences);
            };
            var hideButton = ToolButton("—", "트레이로 숨기기");
            hideButton.Margin = new Thickness(4, 0, 0, 0);
            hideButton.Click += delegate { HideRequested?.Invoke(this, EventArgs.Empty); };
            var closeButton = ToolButton("×", "프로그램 완전 종료");
            closeButton.Margin = new Thickness(4, 0, 0, 0);
            closeButton.Click += delegate
            {
                var answer = MessageBox.Show(this, "프로그램을 완전히 종료하시겠습니까?", "KINOJO Meter", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer == MessageBoxResult.Yes) ExitRequested?.Invoke(this, EventArgs.Empty);
            };
            tools.Children.Add(_lockButton);
            tools.Children.Add(hideButton);
            tools.Children.Add(closeButton);
            Grid.SetColumn(tools, 1);
            toolbar.Children.Add(tools);
            root.Children.Add(toolbar);

            _bossPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(178, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(125, 71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 0, 3)
            };
            var bossContent = new StackPanel();
            _bossPanel.Child = bossContent;
            var activity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            _spinner = new KinojoSpinner { Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            _stateLabel = new TextBlock
            {
                Text = "파티 구성원 체크 중",
                Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253)),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            activity.Children.Add(_spinner);
            activity.Children.Add(_stateLabel);
            bossContent.Children.Add(activity);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _bossName = new TextBlock { Text = "전투 대기", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold };
            _encounterTime = new TextBlock { Text = "00:00", Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)), FontSize = 11 };
            Grid.SetColumn(_encounterTime, 1);
            header.Children.Add(_bossName);
            header.Children.Add(_encounterTime);
            bossContent.Children.Add(header);

            _bossHpScale = new ScaleTransform(0, 1);
            _bossHpFill = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = CreateBossHpBrush(),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = _bossHpScale
            };
            _bossHpPercent = new TextBlock
            {
                Text = "0.0%",
                Foreground = Brushes.White,
                FontSize = 7,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var bossHpGrid = new Grid();
            bossHpGrid.Children.Add(_bossHpFill);
            bossHpGrid.Children.Add(_bossHpPercent);
            _bossHpHost = new Border
            {
                Height = 10,
                Margin = new Thickness(0, 4, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(185, 51, 65, 85)),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Child = bossHpGrid,
                Visibility = Visibility.Collapsed
            };
            bossContent.Children.Add(_bossHpHost);
            root.Children.Add(_bossPanel);

            _rows = new StackPanel();
            root.Children.Add(_rows);
            if (_isMeterAdmin)
            {
                var diagnostics = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                _diagnosticStartButton = ToolButton("패킷 진단 수집 시작", "관리자 패킷 픽스처 수집 시작");
                _diagnosticStopButton = ToolButton("수집 종료", "관리자 패킷 픽스처 수집 종료");
                _diagnosticStartButton.Width = 118;
                _diagnosticStartButton.FontSize = 8;
                _diagnosticStopButton.Width = 62;
                _diagnosticStopButton.FontSize = 8;
                _diagnosticStopButton.Margin = new Thickness(4, 0, 0, 0);
                _diagnosticStopButton.IsEnabled = false;
                _diagnosticStartButton.Click += delegate { StartFixtureCapture(); };
                _diagnosticStopButton.Click += delegate { StopFixtureCapture(); };
                _diagnosticState = new TextBlock
                {
                    Text = "관리자 진단 대기",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    FontSize = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(7, 0, 0, 0)
                };
                diagnostics.Children.Add(_diagnosticStartButton);
                diagnostics.Children.Add(_diagnosticStopButton);
                diagnostics.Children.Add(_diagnosticState);
                root.Children.Add(diagnostics);
            }
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Tick;
            LocationChanged += delegate { SaveGeometry(); };
            SizeChanged += delegate { SaveGeometry(); };
            SourceInitialized += delegate { InitializeWindowHooks(); };
            Loaded += delegate { StartCapture(); };
            Closed += delegate
            {
                _timer.Stop();
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
                SaveGeometry();
                if (_capture != null) _capture.Dispose();
            };
            ApplyPreferences();
            Render(_engine.Snapshot());
        }

        public CaptureRuntimeInfo RuntimeInfo
        {
            get { return _capture == null ? new CaptureRuntimeInfo() : _capture.RuntimeInfo; }
        }

        public void RestartCapture()
        {
            if (_capture == null) StartCapture();
            else _capture.Restart();
        }

        public bool IsFixtureCaptureActive
        {
            get { return _capture != null && _capture.IsFixtureCaptureActive; }
        }

        public string ToggleFixtureCapture()
        {
            if (!_isMeterAdmin) return "";
            if (_capture == null) StartCapture();
            var directory = _capture == null ? "" : _capture.ToggleFixtureCapture();
            UpdateFixtureCaptureControls();
            FixtureCaptureStateChanged?.Invoke(this, EventArgs.Empty);
            return directory;
        }

        private void StartFixtureCapture()
        {
            if (!IsFixtureCaptureActive) ToggleFixtureCapture();
        }

        private void StopFixtureCapture()
        {
            if (IsFixtureCaptureActive) ToggleFixtureCapture();
        }

        private void UpdateFixtureCaptureControls()
        {
            if (_diagnosticStartButton == null) return;
            var active = IsFixtureCaptureActive;
            _diagnosticStartButton.IsEnabled = !active;
            _diagnosticStopButton.IsEnabled = active;
            _diagnosticState.Text = active ? "수집 중 · 최대 20분" : "관리자 진단 대기";
            _diagnosticState.Foreground = new SolidColorBrush(active ? Color.FromRgb(56, 189, 248) : Color.FromRgb(148, 163, 184));
        }

        public bool AddFixtureMarker(string marker)
        {
            return _capture != null && _capture.AddFixtureMarker(marker);
        }

        public void ShowWithoutActivation()
        {
            if (!IsVisible) Show();
            EnsureTopmostWithoutActivation();
        }

        private void StartCapture()
        {
            if (_capture != null) return;
            _capture = new CombatCaptureCoordinator(new[] { _selected == null ? "" : _selected.CharacterName });
            _engine.SetRuntimeInfo(_capture.RuntimeInfo);
            _capture.CombatEventReceived += delegate(object sender, CombatEvent value)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ApplyCombatEvent(value); }));
            };
            _capture.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyObservedPartyRoster(value);
                }));
            };
            _capture.StatusChanged += delegate(object sender, string text)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _engine.SetRuntimeInfo(_capture.RuntimeInfo);
                    CaptureStatusChanged?.Invoke(this, text);
                    if (!_partyObserved) SetActivityStatus("파티 구성원 체크 중");
                }));
            };
            _capture.DiagnosticStatusChanged += delegate(object sender, string text)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    UpdateFixtureCaptureControls();
                    CaptureDiagnosticChanged?.Invoke(this, text);
                }));
            };
            _capture.Start();
        }

        public void ApplyProfile(PartyProfileResult profile)
        {
            var rows = _engine.Snapshot().Rows.Where(row => !row.IsEmpty).ToList();
            var before = profile == null ? null : rows.FirstOrDefault(row =>
                (!String.IsNullOrWhiteSpace(profile.ParticipantKey) && String.Equals(row.ParticipantKey, profile.ParticipantKey, StringComparison.OrdinalIgnoreCase)) ||
                (!String.IsNullOrWhiteSpace(profile.PlatformCharacterId) && String.Equals(row.PlatformCharacterId, profile.PlatformCharacterId, StringComparison.OrdinalIgnoreCase)));
            if (before == null && profile != null && !String.IsNullOrWhiteSpace(profile.CharacterName))
            {
                var nameMatches = rows.Where(row => String.Equals(row.Name, profile.CharacterName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!String.IsNullOrWhiteSpace(profile.ServerId))
                    before = nameMatches.FirstOrDefault(row => String.Equals(row.ServerId, profile.ServerId, StringComparison.OrdinalIgnoreCase));
                else if (!String.IsNullOrWhiteSpace(profile.ServerName))
                    before = nameMatches.FirstOrDefault(row => String.Equals(row.ServerName, profile.ServerName, StringComparison.OrdinalIgnoreCase));
                else if (nameMatches.Count == 1) before = nameMatches[0];
            }
            _engine.ApplyProfile(profile);
            if (before != null && before.ClassRaw > 0 && profile != null &&
                (!String.IsNullOrWhiteSpace(profile.ClassKey) || !String.IsNullOrWhiteSpace(profile.ClassName)))
            {
                if (!String.IsNullOrWhiteSpace(profile.ClassKey)) _classRawKeys[before.ClassRaw] = profile.ClassKey;
                if (!String.IsNullOrWhiteSpace(profile.ClassName)) _classRawNames[before.ClassRaw] = profile.ClassName;
                _engine.ApplyClassMapping(before.ClassRaw, profile.ClassKey, profile.ClassName);
            }
            Render(_engine.Snapshot());
        }

        public IList<CombatRow> GetParticipantSnapshot()
        {
            return _engine.Snapshot().Rows.Where(row => !row.IsEmpty).ToList();
        }

        public void SetEncounterProcessingState(string state, string text)
        {
            _encounterProcessingState = (state ?? "").Trim().ToUpperInvariant();
            _encounterProcessingText = (text ?? "").Trim();
            Render(_engine.Snapshot());
        }

        public void ApplyHudObservation(GameHudObservation observation)
        {
            if (observation == null) return;
            var hudMembers = observation.PartyMembers ?? new List<DetectedPartyMember>();
            if (hudMembers.Count >= 2)
            {
                var hudSignature = String.Join("|", hudMembers
                    .Select(member => (member.CharacterName ?? "").Trim() + "[" + (member.ServerName ?? "").Trim() + "]")
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                if (!String.Equals(_lastHudRosterSignature, hudSignature, StringComparison.OrdinalIgnoreCase))
                {
                    _lastHudRosterSignature = hudSignature;
                    ApplyObservedPartyRoster(new PartyRosterDetectedEventArgs
                    {
                        ConnectionKey = "HUD_OCR",
                        Direction = "SCREEN",
                        Evidence = "HUD_OCR_PARTY_ROSTER_CONFIRMED",
                        Members = hudMembers
                    });
                    CaptureDiagnosticChanged?.Invoke(this, "HUD party roster accepted after repeated observation. Members=" +
                        hudMembers.Count.ToString(CultureInfo.InvariantCulture) + ".");
                }
            }
            foreach (var pair in observation.PartyClassColors ??
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                Color color;
                if (!TryParseColor(pair.Value, out color)) continue;
                _observedClassColors[pair.Key ?? ""] = color;
            }

            foreach (var row in _engine.Snapshot().Rows.Where(value => !value.IsEmpty && value.ClassRaw > 0))
            {
                Color observed;
                if (_observedClassColors.TryGetValue(row.Name ?? "", out observed))
                    _classRawColors[row.ClassRaw] = observed;
            }

            if (!String.IsNullOrWhiteSpace(observation.DungeonName))
            {
                var observedDungeon = observation.DungeonName.Trim();
                if (!String.Equals(_hudDungeonName, observedDungeon, StringComparison.OrdinalIgnoreCase))
                {
                    _bossOrderByTarget.Clear();
                    _pendingDamageByTarget.Clear();
                    _currentBossTarget = "";
                }
                _hudDungeonName = observedDungeon;
                var dungeon = _catalogDungeons.FirstOrDefault(value => String.Equals((value.DungeonName ?? "").Trim(), observedDungeon, StringComparison.OrdinalIgnoreCase));
                _hudDungeonKey = dungeon == null ? "" : dungeon.DungeonKey ?? "";
            }

            foreach (var pair in observation.PartyServers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(pair.Key) || String.IsNullOrWhiteSpace(pair.Value)) continue;
                _engine.ApplyProfile(new PartyProfileResult
                {
                    Ok = true,
                    CharacterName = pair.Key.Trim(),
                    ServerName = pair.Value.Trim()
                });
            }
            if (!String.IsNullOrWhiteSpace(observation.DifficultyName))
                _hudDifficultyName = observation.DifficultyName.Trim();

            if (!String.IsNullOrWhiteSpace(_hudDungeonName))
            {
                var difficulty = _catalogDifficulties.FirstOrDefault(value =>
                    String.Equals((value.DisplayName ?? "").Trim(), _hudDifficultyName, StringComparison.OrdinalIgnoreCase));
                _engine.Apply(new CombatEvent
                {
                    Kind = CombatEventKind.DungeonDetected,
                    TimestampUtc = observation.ObservedAtUtc == DateTime.MinValue ? DateTime.UtcNow : observation.ObservedAtUtc,
                    DungeonKey = _hudDungeonKey,
                    DungeonName = _hudDungeonName,
                    DifficultyKey = difficulty == null ? "" : difficulty.DifficultyKey ?? "",
                    DifficultyName = _hudDifficultyName
                });
            }

            // 파티 구성 창의 콘텐츠명은 입장 전에도 보이므로 표시 후보로만 사용한다.
            // 실제 입장은 검증된 ZoneEntered/DungeonDetected 패킷 이벤트가 확인할 때만 전환한다.
            Render(_engine.Snapshot());
        }

        private void ApplyObservedPartyRoster(PartyRosterDetectedEventArgs value)
        {
            if (value == null) return;
            var evidence = (value.Evidence ?? "PACKET_ROSTER").Trim();
            var roster = (value.Members ?? new List<DetectedPartyMember>())
                .Where(member => member != null && !String.IsNullOrWhiteSpace(member.CharacterName))
                .Select(member => new CombatEvent
                {
                    Kind = CombatEventKind.PartyMember,
                    TimestampUtc = DateTime.UtcNow,
                    ActorId = (evidence.StartsWith("HUD_", StringComparison.OrdinalIgnoreCase) ? "hud-roster:" : "party-probe:") +
                        member.ServerRaw.ToString(CultureInfo.InvariantCulture) + ":" + member.CharacterName,
                    ActorName = member.CharacterName,
                    ActorServer = member.ServerName,
                    ActorServerRaw = member.ServerRaw,
                    ActorClassRaw = member.ClassRaw,
                    PartyNumber = 1,
                    PartySlot = member.Slot
                })
                .ToList();
            if (roster.Count == 0) return;
            _engine.ReplaceObservedParty(roster, evidence);
            foreach (var member in value.Members ?? new List<DetectedPartyMember>())
            {
                Color observed;
                if (member.ClassRaw > 0 && _observedClassColors.TryGetValue(member.CharacterName ?? "", out observed))
                    _classRawColors[member.ClassRaw] = observed;
                if (member.ClassRaw > 0 && _selected != null &&
                    String.Equals(member.CharacterName, _selected.CharacterName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!String.IsNullOrWhiteSpace(_selected.ClassKey)) _classRawKeys[member.ClassRaw] = _selected.ClassKey;
                    if (!String.IsNullOrWhiteSpace(_selected.ClassName)) _classRawNames[member.ClassRaw] = _selected.ClassName;
                    _engine.ApplyClassMapping(member.ClassRaw, _selected.ClassKey, _selected.ClassName);
                }
            }
            _partyObserved = true;
            var rosterSnapshot = _engine.Snapshot();
            var observedCount = rosterSnapshot.Rows.Count(row => !row.IsEmpty);
            SetActivityStatus((_dungeonEntered ? "던전 입장 확인 · " : "") + "파티 구성원 " + observedCount + "명 실시간 확인 중");
            Render(rosterSnapshot);
            PartyRosterObserved?.Invoke(this, value);
        }

        public void ApplyCombatEvent(CombatEvent value)
        {
            if (value != null && value.Kind == CombatEventKind.EntityIdentity && !String.IsNullOrWhiteSpace(value.ActorName))
                CharacterIdentityObserved?.Invoke(this, value);
            if (value != null && value.Kind == CombatEventKind.BossHp)
            {
                ApplyTestBossIdentity(value);
                if (!value.IsBoss) return;
                if (value.IsBoss)
                {
                    _dungeonEntered = true;
                    ReplayPendingDamage(value, value.BossOrder);
                }
            }
            if (value != null && value.Kind == CombatEventKind.Damage)
            {
                int bossOrder;
                if (!String.IsNullOrWhiteSpace(value.TargetId) && _bossOrderByTarget.TryGetValue(RuntimeTargetKey(value), out bossOrder))
                {
                    ApplyKnownBossIdentity(value, bossOrder);
                    _dungeonEntered = true;
                }
                else
                {
                    BufferPendingDamage(value);
                    if (!String.IsNullOrWhiteSpace(_hudDungeonKey))
                        SetActivityStatus("보스 전투 신호 확인 중 · 파티 구성원 실시간 확인 중");
                    return;
                }
                if (String.IsNullOrWhiteSpace(value.ActorName) && !_engine.Snapshot().Rows.Any(row => !row.IsEmpty && String.Equals(row.ParticipantKey, value.ActorId, StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            _engine.SetRuntimeInfo(RuntimeInfo);
            _engine.Apply(value);
            if (value != null && (value.Kind == CombatEventKind.ZoneEntered || value.Kind == CombatEventKind.DungeonDetected))
                _dungeonEntered = true;
            var snapshot = _engine.Snapshot();
            if (snapshot.IsRunning && !_running)
            {
                _encounterProcessingState = "";
                _encounterProcessingText = "";
                _running = true;
                _timer.Start();
            }
            if (snapshot.IsCleared)
            {
                _running = false;
                _timer.Stop();
            }
            Render(snapshot);
        }

        private void BufferPendingDamage(CombatEvent value)
        {
            if (value == null || String.IsNullOrWhiteSpace(value.TargetId)) return;
            var targetKey = RuntimeTargetKey(value);
            List<CombatEvent> buffered;
            if (!_pendingDamageByTarget.TryGetValue(targetKey, out buffered))
            {
                if (_pendingDamageByTarget.Count >= 8)
                {
                    var oldest = _pendingDamageByTarget.OrderBy(pair => pair.Value.Count == 0 ? DateTime.MinValue : pair.Value[0].TimestampUtc).First().Key;
                    _pendingDamageByTarget.Remove(oldest);
                }
                buffered = new List<CombatEvent>();
                _pendingDamageByTarget[targetKey] = buffered;
            }
            if (buffered.Count < 128) buffered.Add(value);
        }

        private void ReplayPendingDamage(CombatEvent bossEvent, int bossOrder)
        {
            if (bossEvent == null || String.IsNullOrWhiteSpace(bossEvent.TargetId)) return;
            var targetKey = RuntimeTargetKey(bossEvent);
            List<CombatEvent> buffered;
            if (!_pendingDamageByTarget.TryGetValue(targetKey, out buffered)) return;
            _pendingDamageByTarget.Remove(targetKey);
            _engine.SetRuntimeInfo(RuntimeInfo);
            foreach (var pending in buffered.OrderBy(value => value.TimestampUtc))
            {
                ApplyKnownBossIdentity(pending, bossOrder);
                if (String.IsNullOrWhiteSpace(pending.ActorName) && !_engine.Snapshot().Rows.Any(row => !row.IsEmpty && String.Equals(row.ParticipantKey, pending.ActorId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _engine.Apply(pending);
            }
        }

        private void ApplyTestBossIdentity(CombatEvent value)
        {
            if (value == null || String.IsNullOrWhiteSpace(value.TargetId) || String.IsNullOrWhiteSpace(_hudDungeonKey)) return;
            var targetKey = RuntimeTargetKey(value);
            int order;
            var newlyMapped = false;
            if (!_bossOrderByTarget.TryGetValue(targetKey, out order))
            {
                var snapshot = _engine.Snapshot();
                var available = Math.Max(1, _catalogBosses.Count(boss => String.Equals(boss.DungeonKey, _hudDungeonKey, StringComparison.OrdinalIgnoreCase)));
                if (!String.IsNullOrWhiteSpace(_currentBossTarget) &&
                    !String.Equals(_currentBossTarget, value.TargetId, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.IsRunning && !snapshot.IsCleared)
                    return;

                if (snapshot.IsCleared && snapshot.BossOrder >= available)
                {
                    _bossOrderByTarget.Clear();
                    _pendingDamageByTarget.Clear();
                    _currentBossTarget = "";
                    _engine.Reset();
                    _encounterProcessingState = "";
                    _encounterProcessingText = "";
                    order = 1;
                    DiagnosticLog.Info("BOSS_ID_TEST", _hudDungeonName + " · same-dungeon new run detected · runtimeTarget=" + value.TargetRuntimeId);
                }
                else if (String.Equals(snapshot.CompletionMode, "PHASE_IDLE_12S", StringComparison.OrdinalIgnoreCase) && snapshot.BossOrder >= available)
                {
                    order = snapshot.BossOrder;
                    DiagnosticLog.Info("BOSS_PHASE_TEST", _hudDungeonName + " · boss order=" + order + " phase target=" + value.TargetRuntimeId);
                }
                else
                {
                    if (String.Equals(snapshot.CompletionMode, "PHASE_IDLE_12S", StringComparison.OrdinalIgnoreCase) && snapshot.BossOrder > 0)
                        _engine.FinalizeCurrentEncounter("NEXT_BOSS_SIGNAL", value.TimestampUtc);
                    order = _bossOrderByTarget.Count == 0 ? 1 : _bossOrderByTarget.Values.Max() + 1;
                }
                if (order < 1 || order > available) return;
                _bossOrderByTarget[targetKey] = order;
                newlyMapped = true;
                DiagnosticLog.Info("BOSS_ID_TEST", _hudDungeonName + " · order=" + order + " · runtimeTarget=" + value.TargetRuntimeId + " · scopedTarget=" + value.TargetId + " · firstHp=" + value.CurrentHp);
            }
            _currentBossTarget = value.TargetId;
            ApplyKnownBossIdentity(value, order);
            if (newlyMapped && _capture != null)
                _capture.AddFixtureMarker("AUTO_BOSS_ORDER_" + order + "_TARGET_" + value.TargetId + "_HP_" + value.CurrentHp);
        }

        private static string RuntimeTargetKey(CombatEvent value)
        {
            return value != null && value.TargetRuntimeId > 0 ? "runtime:" + value.TargetRuntimeId : (value == null ? "" : value.TargetId ?? "");
        }

        private void ApplyKnownBossIdentity(CombatEvent value, int order)
        {
            var boss = _catalogBosses
                .Where(candidate => String.Equals(candidate.DungeonKey, _hudDungeonKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.BossOrder)
                .FirstOrDefault(candidate => candidate.BossOrder == order);
            if (boss == null)
                boss = _catalogBosses
                    .Where(candidate => String.Equals(candidate.DungeonKey, _hudDungeonKey, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(candidate => candidate.BossOrder)
                    .Skip(Math.Max(0, order - 1))
                    .FirstOrDefault();
            value.TargetName = boss == null || String.IsNullOrWhiteSpace(boss.BossName) ? order + "보스" : boss.BossName;
            value.BossOrder = order;
            value.BossIdentityMode = "TEST_ORDER_INFERRED";
            value.IsBoss = true;
        }

        private void Tick(object sender, EventArgs args)
        {
            _engine.Tick(DateTime.UtcNow);
            var snapshot = _engine.Snapshot();
            if (!snapshot.IsRunning)
            {
                _running = false;
                _timer.Stop();
            }
            Render(snapshot);
        }

        private void Render(CombatSnapshot snapshot)
        {
            _bossName.Text = snapshot.BossConfirmed
                ? (IsVerifiedBossName(snapshot) ? snapshot.BossName : "전투 대상")
                : "전투 대기";
            var trustedHp = snapshot.BossConfirmed && IsTrustedBossHp(snapshot);
            _bossHpHost.Visibility = trustedHp ? Visibility.Visible : Visibility.Collapsed;
            if (trustedHp)
            {
                var ratio = Math.Max(0.0, Math.Min(1.0, snapshot.BossCurrentHp / (double)snapshot.BossMaxHp));
                var hpAnimation = new DoubleAnimation
                {
                    To = ratio,
                    Duration = TimeSpan.FromMilliseconds(320),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                _bossHpScale.BeginAnimation(ScaleTransform.ScaleXProperty, hpAnimation, HandoffBehavior.SnapshotAndReplace);
                _bossHpPercent.Text = (ratio * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            }
            _encounterTime.Text = snapshot.StartedAtUtc == DateTime.MinValue ? "00:00" : (snapshot.LastEventUtc - snapshot.StartedAtUtc).ToString(@"mm\:ss");
            if (!String.IsNullOrWhiteSpace(_encounterProcessingState))
                SetActivityStatus(String.IsNullOrWhiteSpace(_encounterProcessingText) ? _encounterProcessingState : _encounterProcessingText);
            else if (snapshot.IsCleared) SetActivityStatus("보스 전투 종료 · 결과 정리 중");
            else if (snapshot.IsRunning) SetActivityStatus(snapshot.DecoderValidated
                ? "보스 전투 중 · DPS 판독 중"
                : "보스 전투 중 · 부분 피해/DPS 판독 중");
            else if (String.Equals(snapshot.CompletionMode, "PHASE_IDLE_12S", StringComparison.OrdinalIgnoreCase)) SetActivityStatus("보스 페이즈 전환 대기 · 전투 데이터 유지 중");
            else if (_dungeonEntered) SetActivityStatus("던전 입장 확인 · 파티 구성원 실시간 확인 중");
            else if (_partyObserved) SetActivityStatus("파티 구성원 확인 중");
            _spinner.Visibility = snapshot.IsRunning || snapshot.IsCleared ? Visibility.Collapsed : Visibility.Visible;
            RenderRows(snapshot.Rows, snapshot.BossConfirmed && (snapshot.IsRunning || snapshot.Rows.Any(row => !row.IsEmpty && row.TotalDamage > 0)));
        }

        private static bool IsVerifiedBossName(CombatSnapshot snapshot)
        {
            if (snapshot == null || String.IsNullOrWhiteSpace(snapshot.BossName)) return false;
            return !String.Equals(snapshot.BossIdentityMode, "TEST_ORDER_INFERRED", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrustedBossHp(CombatSnapshot snapshot)
        {
            if (snapshot == null || snapshot.BossMaxHp <= 0 || snapshot.BossCurrentHp < 0) return false;
            return String.Equals(snapshot.BossHpSource, "PACKET_CURRENT_AND_MAX", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(snapshot.BossHpSource, "PACKET_VERIFIED_MAX", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(snapshot.BossHpSource, "SERVER_CANONICAL_MAX", StringComparison.OrdinalIgnoreCase);
        }

        private void RenderRows(IEnumerable<CombatRow> source, bool damageRanking)
        {
            var incoming = (source ?? Enumerable.Empty<CombatRow>()).ToList();
            var previousOrder = _rankOrder.Select((key, index) => new { key, index })
                .ToDictionary(value => value.key, value => value.index, StringComparer.OrdinalIgnoreCase);
            var desired = damageRanking
                ? incoming.OrderBy(row => row.IsEmpty)
                    .ThenByDescending(row => row.TotalDamage)
                    .ThenBy(row => previousOrder.ContainsKey(RowKey(row)) ? previousOrder[RowKey(row)] : Int32.MaxValue)
                    .ThenBy(row => row.PartyNumber)
                    .ThenBy(row => row.PartySlot)
                    .ToList()
                : incoming.OrderBy(row => row.PartyNumber).ThenBy(row => row.PartySlot).ToList();
            var desiredKeys = desired.Select(RowKey).ToList();
            var membershipChanged = desiredKeys.Count != _rankOrder.Count || desiredKeys.Any(key => !_rankOrder.Contains(key, StringComparer.OrdinalIgnoreCase));
            if (!damageRanking || membershipChanged || _rankOrder.Count == 0 || DateTime.UtcNow - _lastRankUpdateUtc >= TimeSpan.FromMilliseconds(450))
            {
                _rankOrder = desiredKeys;
                _lastRankUpdateUtc = DateTime.UtcNow;
            }
            var orderIndex = _rankOrder.Select((key, index) => new { key, index })
                .ToDictionary(value => value.key, value => value.index, StringComparer.OrdinalIgnoreCase);
            var rows = incoming.OrderBy(row => orderIndex.ContainsKey(RowKey(row)) ? orderIndex[RowKey(row)] : Int32.MaxValue)
                .ThenBy(row => row.PartyNumber).ThenBy(row => row.PartySlot).ToList();

            _rows.Children.Clear();
            var newIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var key = RowKey(row);
                newIndexes[key] = index;
                var element = BuildRow(row, index + 1, damageRanking) as FrameworkElement;
                int oldIndex;
                if (element != null && damageRanking && _lastRowIndexes.TryGetValue(key, out oldIndex) && oldIndex != index)
                {
                    var translate = new TranslateTransform(0, (oldIndex - index) * 38);
                    element.RenderTransform = translate;
                    Panel.SetZIndex(element, 10);
                    var animation = new DoubleAnimation
                    {
                        From = translate.Y,
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(420),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    translate.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
                    element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.78, 1.0, TimeSpan.FromMilliseconds(300)), HandoffBehavior.SnapshotAndReplace);
                }
                _rows.Children.Add(element ?? BuildRow(row, index + 1, damageRanking));
            }
            _lastRowIndexes.Clear();
            foreach (var pair in newIndexes) _lastRowIndexes[pair.Key] = pair.Value;
            var adminHeight = _isMeterAdmin ? 34 : 0;
            Height = Math.Max(150 + adminHeight, Math.Min(700, 92 + adminHeight + rows.Count * 38));
        }

        private static string RowKey(CombatRow row)
        {
            if (row == null) return "row:null";
            if (!String.IsNullOrWhiteSpace(row.ParticipantKey)) return row.ParticipantKey;
            return (row.IsEmpty ? "empty:" : "name:") + row.PartyNumber + ":" + row.PartySlot + ":" + (row.Name ?? "");
        }

        private UIElement BuildRow(CombatRow row, int rank, bool damageRanking)
        {
            var classColor = ResolveClassColor(row);
            var card = new Border
            {
                Height = 36,
                Margin = new Thickness(0, 1, 0, 1),
                Background = new SolidColorBrush(row.IsSelf ? Color.FromArgb(170, 11, 54, 84) : Color.FromArgb(150, 15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(row.IsSelf ? (byte)205 : (byte)125, classColor.R, classColor.G, classColor.B)),
                BorderThickness = new Thickness(row.IsSelf ? 1.25 : 1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true
            };
            var grid = new Grid
            {
                Height = 34,
                Background = Brushes.Transparent
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var share = Math.Max(0.0, Math.Min(100.0, row.Share));
            var rowKey = RowKey(row);
            double previousShare;
            if (!_lastShareByRow.TryGetValue(rowKey, out previousShare)) previousShare = share;
            _lastShareByRow[rowKey] = share;
            var gaugeScale = new ScaleTransform(Math.Max(0.0, Math.Min(1.0, previousShare / 100.0)), 1.0);
            var gauge = new Border
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = CreateShareBrush(classColor),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = gaugeScale
            };
            gauge.Loaded += delegate
            {
                gaugeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation
                {
                    To = share / 100.0,
                    Duration = TimeSpan.FromMilliseconds(360),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                }, HandoffBehavior.SnapshotAndReplace);
            };
            Grid.SetColumnSpan(gauge, 3);
            grid.Children.Add(gauge);
            var classStripe = new Border
            {
                Width = row.IsSelf ? 4 : 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(classColor)
            };
            Grid.SetColumnSpan(classStripe, 3);
            grid.Children.Add(classStripe);

            var iconHost = new Grid();
            var classBadge = new Border
            {
                Width = 27,
                Height = 27,
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Color.FromArgb(105, classColor.R, classColor.G, classColor.B)),
                BorderBrush = new SolidColorBrush(classColor),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = String.IsNullOrWhiteSpace(row.ClassName) ? "클래스 확인 중" : row.ClassName
            };
            var classGlyph = new Grid();
            classGlyph.Children.Add(new TextBlock
            {
                Text = row.IsEmpty ? "·" : (!String.IsNullOrWhiteSpace(row.ClassName) ? row.ClassName.Substring(0, 1) : "◆"),
                Foreground = Brushes.White,
                FontSize = 8,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            var classIconUri = ResolveClassIconUri(row);
            if (!String.IsNullOrWhiteSpace(classIconUri))
            {
                try
                {
                    classGlyph.Children.Add(new Image
                    {
                        Source = new BitmapImage(new Uri(classIconUri, UriKind.Absolute)),
                        Width = 23,
                        Height = 23,
                        Stretch = Stretch.Uniform
                    });
                }
                catch { }
            }
            classBadge.Child = classGlyph;
            iconHost.Children.Add(classBadge);
            if (damageRanking && !row.IsEmpty)
            {
                iconHost.Children.Add(new TextBlock
                {
                    Text = rank.ToString(CultureInfo.InvariantCulture),
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(215, 15, 23, 42)),
                    FontSize = 7,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(2, 0, 2, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(1, 1, 0, 0)
                });
            }
            grid.Children.Add(iconHost);
            var identity = new TextBlock
            {
                Text = row.IsEmpty ? "빈 자리" :
                    row.Name + (String.IsNullOrWhiteSpace(row.ServerName) ? "" : "[" + row.ServerName + "]") + "\n" +
                    (String.IsNullOrWhiteSpace(row.ClassName) ? "클래스 확인 중" : row.ClassName) +
                    (row.CombatPower > 0 ? " · 전투력 " + FormatCombatPower(row.CombatPower) : " · 전투력 확인 중"),
                Foreground = row.IsEmpty ? new SolidColorBrush(Color.FromRgb(100, 116, 139)) : Brushes.White,
                FontWeight = row.IsSelf ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 8.5,
                LineHeight = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(identity, 1);
            grid.Children.Add(identity);
            if (!row.IsEmpty)
            {
                var metrics = new Grid { Margin = new Thickness(2, 1, 5, 1) };
                metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                metrics.ColumnDefinitions.Add(new ColumnDefinition());
                var dps = new TextBlock
                {
                    Text = "DPS\n" + FormatNumber(row.Dps),
                    Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 7.5,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    LineHeight = 10.5
                };
                metrics.Children.Add(dps);
                var total = new TextBlock
                {
                    Text = FormatNumber(row.TotalDamage) + "\n" + row.Share.ToString("0.0") + "%",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.ExtraBold,
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    LineHeight = 12
                };
                Grid.SetColumn(total, 1);
                metrics.Children.Add(total);
                Grid.SetColumn(metrics, 2);
                grid.Children.Add(metrics);
            }
            card.Child = grid;
            return card;
        }

        private Color ResolveClassColor(CombatRow row)
        {
            Color color;
            if (row != null && _observedClassColors.TryGetValue(row.Name ?? "", out color)) return color;
            if (row != null && row.ClassRaw > 0 && _classRawColors.TryGetValue(row.ClassRaw, out color)) return color;
            var key = NormalizeClassAssetKey(row);
            var classColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
            {
                { "gladiator", Color.FromRgb(194, 65, 58) }, { "templar", Color.FromRgb(78, 117, 184) },
                { "assassin", Color.FromRgb(147, 51, 234) }, { "ranger", Color.FromRgb(101, 163, 13) },
                { "sorcerer", Color.FromRgb(124, 58, 237) }, { "elementalist", Color.FromRgb(14, 165, 233) },
                { "cleric", Color.FromRgb(20, 184, 166) }, { "chanter", Color.FromRgb(212, 167, 44) },
                { "fighter", Color.FromRgb(226, 74, 59) }
            };
            if (classColors.TryGetValue(key, out color)) return color;
            return Color.FromRgb(71, 85, 105);
        }

        private static string ResolveClassIconUri(CombatRow row)
        {
            if (row == null || row.IsEmpty) return "";
            var key = NormalizeClassAssetKey(row);
            var supported = new HashSet<string>(new[] { "assassin", "chanter", "cleric", "elementalist", "fighter", "gladiator", "ranger", "sorcerer", "templar" }, StringComparer.OrdinalIgnoreCase);
            return supported.Contains(key) ? "https://kinojo.info/assets/images/classes/class_icon_" + key + ".png" : "";
        }

        private static string NormalizeClassAssetKey(CombatRow row)
        {
            if (row == null) return "";
            var key = (row.ClassKey ?? "").Trim().ToLowerInvariant();
            var name = (row.ClassName ?? "").Trim();
            if (key == "brawler") key = "fighter";
            if (String.IsNullOrWhiteSpace(key) || key == "unknown")
            {
                if (name.Contains("검성")) key = "gladiator";
                else if (name.Contains("수호")) key = "templar";
                else if (name.Contains("살성")) key = "assassin";
                else if (name.Contains("궁성")) key = "ranger";
                else if (name.Contains("마도")) key = "sorcerer";
                else if (name.Contains("정령")) key = "elementalist";
                else if (name.Contains("치유")) key = "cleric";
                else if (name.Contains("호법")) key = "chanter";
                else if (name.Contains("격수") || name.Contains("권성")) key = "fighter";
            }
            return key;
        }

        private static bool TryParseColor(string value, out Color color)
        {
            color = Color.FromRgb(71, 85, 105);
            var text = (value ?? "").Trim().TrimStart('#');
            if (text.Length != 6) return false;
            byte red, green, blue;
            if (!Byte.TryParse(text.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red) ||
                !Byte.TryParse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green) ||
                !Byte.TryParse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
                return false;
            color = Color.FromRgb(red, green, blue);
            return true;
        }

        public void SetStatus(string text)
        {
            var value = String.IsNullOrWhiteSpace(text) ? "" : text.Trim();
            CaptureStatusChanged?.Invoke(this, value);
            SetActivityStatus(value);
        }

        private void SetActivityStatus(string text)
        {
            var value = String.IsNullOrWhiteSpace(text) ? "" : text.Trim();
            if (!String.Equals(_stateLabel.Text, value, StringComparison.Ordinal)) _stateLabel.Text = value;
        }

        public void SetClickThrough(bool enabled)
        {
            _preferences.ClickThrough = enabled;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var style = GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate;
                if (enabled) style |= WsExTransparent;
                else style &= ~WsExTransparent;
                SetWindowLong(handle, GwlExStyle, style);
            }
            PreferencesStore.Save(_preferences);
        }

        private void ApplyPreferences()
        {
            // The overlay body intentionally stays transparent. Only the toolbar,
            // encounter panel and participant cards should cover the game view.
            _surface.Background = Brushes.Transparent;
            _surface.LayoutTransform = new ScaleTransform(Math.Max(0.8, Math.Min(1.25, _preferences.UiScale)), Math.Max(0.8, Math.Min(1.25, _preferences.UiScale)));
        }

        private void UpdateLockButton()
        {
            if (_lockButton == null) return;
            _lockButton.Content = _preferences.Locked ? "🔒" : "🔓";
            _lockButton.ToolTip = _preferences.Locked ? "위치 잠금 해제" : "위치 잠금";
            ToolTipService.SetInitialShowDelay(_lockButton, 0);
            ToolTipService.SetBetweenShowDelay(_lockButton, 0);
        }

        private static string FormatCombatPower(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000) return (value / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static Brush CreateShareBrush(Color color)
        {
            return new LinearGradientBrush(
                Color.FromArgb(118, color.R, color.G, color.B),
                Color.FromArgb(40, color.R, color.G, color.B),
                new Point(0, 0.5),
                new Point(1, 0.5));
        }

        private static Brush CreateBossHpBrush()
        {
            return new LinearGradientBrush(
                Color.FromRgb(239, 68, 68),
                Color.FromRgb(153, 27, 27),
                new Point(0, 0.5),
                new Point(1, 0.5));
        }

        private void InitializeWindowHooks()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var source = HwndSource.FromHwnd(handle);
            if (source != null) source.AddHook(WindowProc);
            RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkF8);
            SetClickThrough(_preferences.ClickThrough);
            EnsureTopmostWithoutActivation();
        }

        private void EnsureTopmostWithoutActivation()
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var style = GetWindowLong(handle, GwlExStyle) | WsExToolWindow | WsExNoActivate;
            SetWindowLong(handle, GwlExStyle, style);
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
            {
                SetClickThrough(false);
                _preferences.Locked = false;
                UpdateLockButton();
                SetStatus("오버레이 조작 가능");
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void SaveGeometry()
        {
            if (WindowState != WindowState.Normal) return;
            _preferences.OverlayLeft = Left;
            _preferences.OverlayTop = Top;
            _preferences.OverlayWidth = Width;
            _preferences.OverlayLayoutVersion = 3;
            PreferencesStore.Save(_preferences);
        }

        private static string FormatNumber(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "b";
            if (value >= 1000000) return (value / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture) + "m";
            if (value >= 1000) return (value / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static Button ToolButton(string text, string tooltip)
        {
            var button = new Button
            {
                Content = text,
                ToolTip = tooltip,
                Width = 28,
                Height = 26,
                Background = new SolidColorBrush(Color.FromArgb(100, 30, 41, 59)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 71, 85, 105)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                FontSize = 10
            };
            ToolTipService.SetInitialShowDelay(button, 0);
            ToolTipService.SetBetweenShowDelay(button, 0);
            return button;
        }
    }
}
