using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
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
        private static readonly Color AccentDeep = Color.FromRgb(37, 99, 235);

        private readonly MeterPreferences _preferences;
        private readonly CharacterProfile _selected;
        private readonly bool _isMeterAdmin;
        private readonly HashSet<string> _bossNames;
        private readonly Border _surface;
        private readonly StackPanel _rows;
        private readonly TextBlock _bossName;
        private readonly TextBlock _encounterTime;
        private readonly TextBlock _stateLabel;
        private readonly KinojoSpinner _spinner;
        private readonly TextBlock _footerVersion;
        private readonly ProgressBar _bossHp;
        private readonly DispatcherTimer _timer;
        private readonly CombatSessionEngine _engine;
        private CombatCaptureCoordinator _capture;
        private bool _running;
        private bool _partyObserved;
        private bool _dungeonEntered;
        private string _hudDungeonName = "";
        private string _hudDifficultyName = "";
        private readonly Dictionary<string, Color> _observedClassColors =
            new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, Color> _classRawColors = new Dictionary<int, Color>();
        private Button _diagnosticStartButton;
        private Button _diagnosticStopButton;
        private TextBlock _diagnosticState;

        public event EventHandler HideRequested;
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
            _bossNames = new HashSet<string>((catalog == null ? Enumerable.Empty<CatalogBoss>() : catalog.Bosses ?? new List<CatalogBoss>())
                .Select(value => (value.BossName ?? "").Trim()).Where(value => value.Length > 0), StringComparer.OrdinalIgnoreCase);
            _preferences = preferences ?? MeterPreferences.Default();
            _engine = new CombatSessionEngine(_selected, _preferences.GroupSize);
            _engine.EncounterCompleted += delegate { EncounterCompleted?.Invoke(this, _engine.Snapshot()); };
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
            Height = 235;
            Left = _preferences.OverlayLeft;
            Top = _preferences.OverlayTop;

            _surface = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(185, 71, 85, 105)),
                Padding = new Thickness(8)
            };
            Content = _surface;
            var root = new StackPanel();
            _surface.Child = root;

            var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition());
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var drag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(72, 30, 41, 59)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(110, 71, 85, 105)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(8, 5, 8, 5),
                Cursor = Cursors.SizeAll
            };
            drag.Child = new TextBlock { Text = "KINOJO  ·  " + _selected.CharacterName, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 9 };
            drag.MouseLeftButtonDown += delegate { if (!_preferences.Locked) DragMove(); };
            toolbar.Children.Add(drag);

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
            var lockButton = ToolButton(_preferences.Locked ? "L" : "U", "위치 잠금");
            lockButton.Click += delegate
            {
                _preferences.Locked = !_preferences.Locked;
                lockButton.Content = _preferences.Locked ? "L" : "U";
                PreferencesStore.Save(_preferences);
            };
            var hideButton = ToolButton("—", "트레이로 숨기기");
            hideButton.Margin = new Thickness(4, 0, 0, 0);
            hideButton.Click += delegate { HideRequested?.Invoke(this, EventArgs.Empty); };
            tools.Children.Add(lockButton);
            tools.Children.Add(hideButton);
            Grid.SetColumn(tools, 1);
            toolbar.Children.Add(tools);
            root.Children.Add(toolbar);

            var activity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 0, 6) };
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
            root.Children.Add(activity);

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _bossName = new TextBlock { Text = "전투 대기", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold };
            _encounterTime = new TextBlock { Text = "00:00", Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)), FontSize = 11 };
            Grid.SetColumn(_encounterTime, 1);
            header.Children.Add(_bossName);
            header.Children.Add(_encounterTime);
            root.Children.Add(header);

            _bossHp = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 4,
                Margin = new Thickness(0, 5, 0, 4),
                Foreground = new SolidColorBrush(AccentDeep),
                Background = new SolidColorBrush(Color.FromRgb(51, 65, 85))
            };
            root.Children.Add(_bossHp);

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
            _footerVersion = new TextBlock
            {
                Text = "KINOJO Meter v" + KinojoVersion.Current,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 5, 2, 0)
            };
            root.Children.Add(_footerVersion);

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
            _capture = new CombatCaptureCoordinator();
            _engine.SetRuntimeInfo(_capture.RuntimeInfo);
            _capture.CombatEventReceived += delegate(object sender, CombatEvent value)
            {
                Dispatcher.BeginInvoke(new Action(delegate { ApplyCombatEvent(value); }));
            };
            _capture.PartyRosterDetected += delegate(object sender, PartyRosterDetectedEventArgs value)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    var roster = (value.Members ?? new List<DetectedPartyMember>())
                        .Select(member => new CombatEvent
                        {
                            Kind = CombatEventKind.PartyMember,
                            TimestampUtc = DateTime.UtcNow,
                            ActorId = "party-probe:" + member.ServerRaw.ToString(CultureInfo.InvariantCulture) + ":" + member.CharacterName,
                            ActorName = member.CharacterName,
                            ActorServerId = member.ServerRaw.ToString(CultureInfo.InvariantCulture),
                            ActorClassRaw = member.ClassRaw,
                            PartyNumber = 1,
                            PartySlot = member.Slot
                        })
                        .ToList();
                    _engine.ReplaceObservedParty(roster);
                    foreach (var member in value.Members ?? new List<DetectedPartyMember>())
                    {
                        Color observed;
                        if (member.ClassRaw > 0 && _observedClassColors.TryGetValue(member.CharacterName ?? "", out observed))
                            _classRawColors[member.ClassRaw] = observed;
                    }
                    _partyObserved = roster.Count > 0;
                    SetActivityStatus((_dungeonEntered ? "던전 입장 확인 · " : "") + "파티 구성원 " + roster.Count + "명 확인 중");
                    Render(_engine.Snapshot());
                    PartyRosterObserved?.Invoke(this, value);
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
            _engine.ApplyProfile(profile);
            Render(_engine.Snapshot());
        }

        public void ApplyHudObservation(GameHudObservation observation)
        {
            if (observation == null) return;
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
                _hudDungeonName = observation.DungeonName.Trim();
            if (!String.IsNullOrWhiteSpace(observation.DifficultyName))
                _hudDifficultyName = observation.DifficultyName.Trim();

            // 파티 구성 창의 콘텐츠명은 입장 전에도 보이므로 표시 후보로만 사용한다.
            // 실제 입장은 검증된 ZoneEntered/DungeonDetected 패킷 이벤트가 확인할 때만 전환한다.
            Render(_engine.Snapshot());
        }

        public void ApplyCombatEvent(CombatEvent value)
        {
            if (value != null && value.Kind == CombatEventKind.EntityIdentity && !String.IsNullOrWhiteSpace(value.ActorName))
                CharacterIdentityObserved?.Invoke(this, value);
            if (value != null && value.Kind == CombatEventKind.Damage)
            {
                var catalogBoss = !String.IsNullOrWhiteSpace(value.TargetName) && _bossNames.Contains(value.TargetName.Trim());
                if (!String.IsNullOrWhiteSpace(_hudDungeonName) && _bossNames.Count > 0 && !catalogBoss)
                {
                    SetActivityStatus("던전 상태 확인 · 파티 구성원 실시간 확인 중");
                    return;
                }
                value.IsBoss = value.IsBoss || catalogBoss;
                if (!String.IsNullOrWhiteSpace(_hudDungeonName)) _dungeonEntered = true;
            }
            _engine.SetRuntimeInfo(RuntimeInfo);
            _engine.Apply(value);
            if (value != null && (value.Kind == CombatEventKind.ZoneEntered || value.Kind == CombatEventKind.DungeonDetected))
                _dungeonEntered = true;
            var snapshot = _engine.Snapshot();
            if (snapshot.IsRunning && !_running)
            {
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
            _bossName.Text = snapshot.IsRunning && !String.IsNullOrWhiteSpace(snapshot.BossName)
                ? snapshot.BossName
                : snapshot.BossConfirmed
                ? snapshot.BossName
                : (!String.IsNullOrWhiteSpace(snapshot.DungeonName)
                    ? snapshot.DungeonName + " · 전투 대기"
                    : (!String.IsNullOrWhiteSpace(_hudDungeonName)
                        ? _hudDungeonName + (String.IsNullOrWhiteSpace(_hudDifficultyName) ? "" : " [" + _hudDifficultyName + "]") + " · 던전 상태 확인 중"
                        : "전투 대기"));
            _bossHp.Value = snapshot.BossMaxHp > 0 ? Math.Max(0, Math.Min(100, snapshot.BossCurrentHp * 100.0 / snapshot.BossMaxHp)) : 0;
            _encounterTime.Text = snapshot.StartedAtUtc == DateTime.MinValue ? "00:00" : (snapshot.LastEventUtc - snapshot.StartedAtUtc).ToString(@"mm\:ss");
            if (snapshot.IsCleared) SetActivityStatus("보스 전투 종료 · 결과 정리 중");
            else if (snapshot.IsRunning) SetActivityStatus("보스 전투 중 · DPS 판독 중");
            else if (_dungeonEntered) SetActivityStatus("던전 입장 확인 · 파티 구성원 실시간 확인 중");
            else if (_partyObserved) SetActivityStatus("파티 구성원 확인 중");
            _spinner.Visibility = snapshot.IsRunning || snapshot.IsCleared ? Visibility.Collapsed : Visibility.Visible;
            RenderRows(snapshot.Rows);
        }

        private void RenderRows(IEnumerable<CombatRow> source)
        {
            _rows.Children.Clear();
            var rows = (source ?? Enumerable.Empty<CombatRow>()).OrderBy(row => row.PartyNumber).ThenBy(row => row.PartySlot).ToList();
            foreach (var row in rows) _rows.Children.Add(BuildRow(row));
            var adminHeight = _isMeterAdmin ? 34 : 0;
            Height = Math.Max(235 + adminHeight, Math.Min(655, 180 + adminHeight + rows.Count * 34));
        }

        private UIElement BuildRow(CombatRow row)
        {
            var grid = new Grid
            {
                Height = 32,
                Margin = new Thickness(0, 1, 0, 1),
                Background = new SolidColorBrush(row.IsSelf ? Color.FromArgb(92, 14, 116, 144) : Color.FromArgb(78, 30, 41, 59))
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var classColor = ResolveClassColor(row);
            var share = Math.Max(0.0, Math.Min(100.0, row.Share));
            var gauge = new Grid { IsHitTestVisible = false };
            gauge.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, share), GridUnitType.Star) });
            gauge.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 100.0 - share), GridUnitType.Star) });
            gauge.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(92, classColor.R, classColor.G, classColor.B)),
                CornerRadius = new CornerRadius(3)
            });
            Grid.SetColumnSpan(gauge, 3);
            grid.Children.Add(gauge);
            var classStripe = new Border
            {
                Width = 3,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(classColor)
            };
            Grid.SetColumnSpan(classStripe, 3);
            grid.Children.Add(classStripe);

            var classBadge = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
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
                        Width = 18,
                        Height = 18,
                        Stretch = Stretch.Uniform
                    });
                }
                catch { }
            }
            classBadge.Child = classGlyph;
            grid.Children.Add(classBadge);
            var identity = new TextBlock
            {
                Text = row.IsEmpty ? "빈 자리" :
                    row.Name + (String.IsNullOrWhiteSpace(row.ServerName) ? "" : "[" + row.ServerName + "]") +
                    (String.IsNullOrWhiteSpace(row.ClassName) ? "" : " · " + row.ClassName) +
                    (row.CombatPower > 0 ? " · 전투력 " + FormatNumber(row.CombatPower) : ""),
                Foreground = row.IsEmpty ? new SolidColorBrush(Color.FromRgb(100, 116, 139)) : Brushes.White,
                FontWeight = row.IsSelf ? FontWeights.Bold : FontWeights.SemiBold,
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(identity, 1);
            grid.Children.Add(identity);
            if (!row.IsEmpty)
            {
                var number = new TextBlock
                {
                    Text = FormatNumber(row.Dps) + " DPS · " + FormatNumber(row.TotalDamage) + "\n" + row.Share.ToString("0.0") + "%",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                Grid.SetColumn(number, 2);
                grid.Children.Add(number);
            }
            return grid;
        }

        private Color ResolveClassColor(CombatRow row)
        {
            Color color;
            if (row != null && _observedClassColors.TryGetValue(row.Name ?? "", out color)) return color;
            if (row != null && row.ClassRaw > 0 && _classRawColors.TryGetValue(row.ClassRaw, out color)) return color;
            return Color.FromRgb(71, 85, 105);
        }

        private static string ResolveClassIconUri(CombatRow row)
        {
            if (row == null || row.IsEmpty) return "";
            var key = (row.ClassKey ?? "").Trim().ToLowerInvariant();
            var name = (row.ClassName ?? "").Trim();
            if (String.IsNullOrWhiteSpace(key))
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
            var supported = new HashSet<string>(new[] { "assassin", "chanter", "cleric", "elementalist", "fighter", "gladiator", "ranger", "sorcerer", "templar" }, StringComparer.OrdinalIgnoreCase);
            return supported.Contains(key) ? "https://kinojo.info/assets/images/classes/class_icon_" + key + ".png" : "";
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
            _surface.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Max(0.35, Math.Min(0.98, _preferences.BackgroundOpacity))), 15, 23, 42));
            _surface.LayoutTransform = new ScaleTransform(Math.Max(0.8, Math.Min(1.25, _preferences.UiScale)), Math.Max(0.8, Math.Min(1.25, _preferences.UiScale)));
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
            return new Button
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
        }
    }
}
