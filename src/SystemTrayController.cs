using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace KinojoMeterPrototype
{
    internal sealed class SystemTrayController : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _status;
        private readonly ToolStripMenuItem _showOverlay;
        private readonly ToolStripMenuItem _hideOverlay;
        private readonly ToolStripMenuItem _consent;
        private readonly ToolStripMenuItem _adminCaptureStatus;
        private readonly ToolStripMenuItem _adminFixtureCapture;

        public event EventHandler ShowOverlayRequested;
        public event EventHandler HideOverlayRequested;
        public event EventHandler RestartCaptureRequested;
        public event EventHandler OpenDiagnosticsRequested;
        public event EventHandler ToggleFixtureCaptureRequested;
        public event Action<string> DiagnosticMarkerRequested;
        public event EventHandler CheckUpdateRequested;
        public event EventHandler OpenConsentRequested;
        public event EventHandler LogoutRequested;
        public event EventHandler ExitRequested;

        public bool IsMenuOpen { get; private set; }

        public SystemTrayController(string characterName, bool administrator)
        {
            _status = new ToolStripMenuItem("선택 캐릭터 · " + (characterName ?? "")) { Enabled = false };
            _menu = new ContextMenuStrip();
            _menu.Opening += delegate { IsMenuOpen = true; };
            _menu.Closed += delegate { IsMenuOpen = false; };
            _menu.Items.Add(_status);
            _menu.Items.Add(new ToolStripSeparator());

            _showOverlay = new ToolStripMenuItem("오버레이 표시", null, delegate { ShowOverlayRequested?.Invoke(this, EventArgs.Empty); });
            _hideOverlay = new ToolStripMenuItem("오버레이 숨기기", null, delegate { HideOverlayRequested?.Invoke(this, EventArgs.Empty); });
            _menu.Items.Add(_showOverlay);
            _menu.Items.Add(_hideOverlay);
            _consent = new ToolStripMenuItem("웹 미터기 · 전투 기록 보기", null, delegate { OpenConsentRequested?.Invoke(this, EventArgs.Empty); });
            _menu.Items.Add(_consent);

            if (administrator)
            {
                var admin = new ToolStripMenuItem("관리자 도구");
                _adminCaptureStatus = new ToolStripMenuItem("캡처 상태 · 확인 중") { Enabled = false };
                admin.DropDownItems.Add(_adminCaptureStatus);
                admin.DropDownItems.Add(new ToolStripSeparator());
                admin.DropDownItems.Add("캡처 엔진 재시작", null, delegate { RestartCaptureRequested?.Invoke(this, EventArgs.Empty); });
                _adminFixtureCapture = new ToolStripMenuItem("패킷 진단 수집 시작", null, delegate { ToggleFixtureCaptureRequested?.Invoke(this, EventArgs.Empty); });
                admin.DropDownItems.Add(_adminFixtureCapture);
                var markers = new ToolStripMenuItem("진단 마커 기록");
                AddMarker(markers, "파티 구성 완료", "PARTY_READY");
                AddMarker(markers, "던전 입장", "DUNGEON_ENTER");
                AddMarker(markers, "난이도 확인", "DIFFICULTY_CONFIRMED");
                AddMarker(markers, "보스 전투 시작", "BOSS_START");
                AddMarker(markers, "보스 처치", "BOSS_DEFEATED");
                AddMarker(markers, "전멸·리셋", "WIPE_OR_RESET");
                AddMarker(markers, "던전 종료", "DUNGEON_END");
                admin.DropDownItems.Add(markers);
                admin.DropDownItems.Add("진단 로그 열기", null, delegate { OpenDiagnosticsRequested?.Invoke(this, EventArgs.Empty); });
                admin.DropDownItems.Add("업데이트 확인", null, delegate { CheckUpdateRequested?.Invoke(this, EventArgs.Empty); });
                _menu.Items.Add(admin);
            }

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("로그아웃", null, delegate { LogoutRequested?.Invoke(this, EventArgs.Empty); });
            _menu.Items.Add("프로그램 종료", null, delegate { ExitRequested?.Invoke(this, EventArgs.Empty); });

            _icon = new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                Text = "KINOJO Meter " + KinojoVersion.Current,
                ContextMenuStrip = _menu,
                Visible = true
            };
            _icon.MouseClick += delegate(object sender, MouseEventArgs args)
            {
                if (args.Button != MouseButtons.Left || _menu.Visible) return;
                _menu.Show(Cursor.Position);
            };
            SetOverlayVisible(false);
        }

        public void SetConsentRequired(bool required)
        {
            _consent.Text = required ? "웹 미터기 · 필수 동의 필요" : "웹 미터기 · 전투 기록 보기";
            _consent.ForeColor = required ? Color.Firebrick : SystemColors.ControlText;
        }

        public void SetStatus(string text)
        {
            var value = String.IsNullOrWhiteSpace(text) ? "백그라운드 실행 중" : text.Trim();
            _status.Text = value.Length > 70 ? value.Substring(0, 70) : value;
            var tooltip = "KINOJO Meter · " + value;
            _icon.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
        }

        public void SetAdminCaptureStatus(string text)
        {
            if (_adminCaptureStatus == null) return;
            var value = String.IsNullOrWhiteSpace(text) ? "확인 중" : text.Trim();
            _adminCaptureStatus.Text = "캡처 상태 · " + (value.Length > 74 ? value.Substring(0, 74) : value);
        }

        public void SetFixtureCaptureActive(bool active)
        {
            if (_adminFixtureCapture == null) return;
            _adminFixtureCapture.Text = active ? "패킷 진단 수집 중지" : "패킷 진단 수집 시작";
            _adminFixtureCapture.Checked = active;
        }

        private void AddMarker(ToolStripMenuItem parent, string label, string marker)
        {
            parent.DropDownItems.Add(label, null, delegate { DiagnosticMarkerRequested?.Invoke(marker); });
        }

        public void SetOverlayVisible(bool visible)
        {
            _showOverlay.Visible = !visible;
            _hideOverlay.Visible = visible;
        }

        public void ShowReadyBalloon()
        {
            _icon.BalloonTipTitle = "KINOJO Meter";
            _icon.BalloonTipText = "캐릭터 연결 완료 · 이후 과정은 자동으로 처리됩니다.";
            _icon.ShowBalloonTip(2500);
        }

        public void Dispose()
        {
            IsMenuOpen = false;
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }

    internal static class AionWindowMonitor
    {
        private static IntPtr _gameWindow;
        private static int _gameProcessId;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        public static bool IsAion2Foreground()
        {
            try
            {
                var foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero) return false;

                uint foregroundProcessId;
                GetWindowThreadProcessId(foreground, out foregroundProcessId);
                if (IsAion2Window(foreground, (int)foregroundProcessId))
                {
                    _gameWindow = foreground;
                    _gameProcessId = (int)foregroundProcessId;
                    return !IsIconic(foreground);
                }

                if (_gameWindow == IntPtr.Zero || !IsWindow(_gameWindow) || IsIconic(_gameWindow))
                {
                    ResetTrackedWindow();
                    return false;
                }

                return _gameProcessId > 0 && foregroundProcessId == (uint)_gameProcessId;
            }
            catch
            {
                ResetTrackedWindow();
                return false;
            }
        }

        private static bool IsAion2Window(IntPtr handle, int processId)
        {
            if (processId <= 0) return false;
            using (var process = Process.GetProcessById(processId))
            {
                var name = process.ProcessName ?? "";
                if (name.IndexOf("aion2", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            var titleBuilder = new System.Text.StringBuilder(512);
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            var title = titleBuilder.ToString();
            return title.IndexOf("aion2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("아이온2", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ResetTrackedWindow()
        {
            _gameWindow = IntPtr.Zero;
            _gameProcessId = 0;
        }
    }
}
