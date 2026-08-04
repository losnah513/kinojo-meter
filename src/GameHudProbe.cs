using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace KinojoMeterPrototype
{
    internal sealed class GameHudProbe : IDisposable
    {
        private sealed class OcrCapture
        {
            public string Text = "";
            public OcrResult Result;
            public Bitmap Bitmap;
        }

        private readonly object _gate = new object();
        private readonly Timer _timer;
        private List<string> _characterNames = new List<string>();
        private List<string> _dungeonNames = new List<string>();
        private List<string> _difficultyNames = new List<string>();
        private List<string> _partyNames = new List<string>();
        private OcrEngine _ocr;
        private int _probing;
        private bool _running;
        private bool _disposed;
        private string _pendingCharacter = "";
        private int _pendingCharacterCount;
        private string _pendingTitleCharacter = "";
        private int _pendingTitleCharacterCount;
        private string _lastEmittedTitleCharacter = "";
        private DateTime _lastTitleEmissionUtc = DateTime.MinValue;
        private DateTime _lastOcrProbeUtc = DateTime.MinValue;
        private DateTime _startedAtUtc = DateTime.MinValue;
        private string _lastStatusKey = "";
        private bool _ocrUnavailableReported;

        public event EventHandler<GameHudObservation> ObservationReady;
        public event EventHandler<string> StatusChanged;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        public GameHudProbe()
        {
            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                _ocr = OcrEngine.TryCreateFromLanguage(new Language("ko-KR")) ??
                       OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch
            {
                _ocr = null;
            }
        }

        public void UpdateCharacters(IEnumerable<CharacterProfile> characters)
        {
            lock (_gate)
                _characterNames = (characters ?? Enumerable.Empty<CharacterProfile>())
                    .Select(value => (value.CharacterName ?? "").Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        public void UpdateDungeons(IEnumerable<CatalogDungeon> dungeons)
        {
            lock (_gate)
                _dungeonNames = (dungeons ?? Enumerable.Empty<CatalogDungeon>())
                    .Select(value => (value.DungeonName ?? "").Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        public void UpdateDifficulties(IEnumerable<CatalogDifficulty> difficulties)
        {
            lock (_gate)
                _difficultyNames = (difficulties ?? Enumerable.Empty<CatalogDifficulty>())
                    .Select(value => (value.DisplayName ?? "").Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        public void UpdatePartyMembers(IEnumerable<DetectedPartyMember> members)
        {
            lock (_gate)
                _partyNames = (members ?? Enumerable.Empty<DetectedPartyMember>())
                    .Select(value => (value.CharacterName ?? "").Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        public void Start()
        {
            if (_disposed) return;
            _running = true;
            _startedAtUtc = DateTime.UtcNow;
            _lastOcrProbeUtc = DateTime.MinValue;
            _timer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
            RaiseStatusOnce("START", "게임 창 제목에서 접속 캐릭터 확인 중");
        }

        public void Stop()
        {
            _running = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void OnTimer(object state)
        {
            if (!_running || _disposed || Interlocked.Exchange(ref _probing, 1) != 0) return;
            ProbeAsync().ContinueWith(delegate(Task task)
            {
                if (task.Exception != null) RaiseStatusOnce("PROBE_RETRY", "게임 HUD 판독 재시도 중");
                Interlocked.Exchange(ref _probing, 0);
            }, TaskScheduler.Default);
        }

        private async Task ProbeAsync()
        {
            var observedAtUtc = DateTime.UtcNow;
            var gameWindow = FindAionWindow();
            if (gameWindow == IntPtr.Zero)
            {
                ResetPendingTitleCharacter();
                RaiseStatusOnce("WINDOW_WAIT", "AION2 실행 창을 찾는 중 · 자동 검색 계속");
                return;
            }

            var windowTitle = ReadWindowTitle(gameWindow);
            var titleCharacter = MatchWindowTitleCharacter(windowTitle);
            if (!String.IsNullOrWhiteSpace(titleCharacter))
            {
                if (String.Equals(_pendingTitleCharacter, titleCharacter, StringComparison.OrdinalIgnoreCase))
                    _pendingTitleCharacterCount++;
                else
                {
                    _pendingTitleCharacter = titleCharacter;
                    _pendingTitleCharacterCount = 1;
                }

                if (_pendingTitleCharacterCount >= 2)
                {
                    RaiseStatusOnce("TITLE_MATCH:" + titleCharacter, titleCharacter + " 확인 · 자동 연결 중");
                    if (!String.Equals(_lastEmittedTitleCharacter, titleCharacter, StringComparison.OrdinalIgnoreCase) ||
                        observedAtUtc - _lastTitleEmissionUtc >= TimeSpan.FromSeconds(5))
                    {
                        _lastEmittedTitleCharacter = titleCharacter;
                        _lastTitleEmissionUtc = observedAtUtc;
                        ObservationReady?.Invoke(this, new GameHudObservation
                        {
                            ObservedAtUtc = observedAtUtc,
                            CharacterName = titleCharacter,
                            Evidence = "AION2_WINDOW_TITLE"
                        });
                    }
                }
                else
                {
                    RaiseStatusOnce("TITLE_CONFIRM:" + titleCharacter, "게임 창에서 " + titleCharacter + " 재확인 중");
                }
            }
            else
            {
                ResetPendingTitleCharacter();
                if (observedAtUtc - _startedAtUtc >= TimeSpan.FromSeconds(5))
                    RaiseStatusOnce("TITLE_NO_MATCH", "AION2는 확인됨 · 계정 캐릭터명 일치 대기 중");
                else
                    RaiseStatusOnce("TITLE_SCAN", "AION2 게임 창 확인 · 접속 캐릭터명 읽는 중");
            }

            if (_ocr == null)
            {
                if (!_ocrUnavailableReported)
                {
                    _ocrUnavailableReported = true;
                    RaiseStatusOnce("OCR_UNAVAILABLE", "창 제목 판독 유지 · Windows OCR은 사용할 수 없음");
                }
                return;
            }

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || !IsAionWindow(foreground) || IsIconic(foreground)) return;
            if (_lastOcrProbeUtc != DateTime.MinValue && observedAtUtc - _lastOcrProbeUtc < TimeSpan.FromMilliseconds(1500)) return;
            _lastOcrProbeUtc = observedAtUtc;
            gameWindow = foreground;

            NativeRect rect;
            if (!GetClientRect(gameWindow, out rect)) return;
            var origin = new NativePoint();
            if (!ClientToScreen(gameWindow, ref origin)) return;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 800 || height < 500) return;

            using (var full = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(full))
                    graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

                // The controlled character may stand below the geometric center and its
                // nameplate is small at ultrawide resolutions. Read both a tight ROI that
                // preserves glyph size and a wider/lower ROI that tolerates camera poses.
                var centerRect = RelativeRect(width, height, 0.25, 0.20, 0.75, 0.80);
                var centerTightRect = RelativeRect(width, height, 0.37, 0.30, 0.63, 0.68);
                var panelRect = RelativeRect(width, height, 0.68, 0.03, 0.94, 0.50);
                using (var center = full.Clone(centerRect, PixelFormat.Format32bppArgb))
                using (var centerTight = full.Clone(centerTightRect, PixelFormat.Format32bppArgb))
                using (var panel = full.Clone(panelRect, PixelFormat.Format32bppArgb))
                {
                    var centerOcr = await RecognizeAsync(center);
                    var centerTightOcr = await RecognizeAsync(centerTight);
                    var panelOcr = await RecognizeAsync(panel);
                    var combinedCenter = new OcrCapture { Text = (centerTightOcr.Text ?? "") + Environment.NewLine + (centerOcr.Text ?? "") };
                    var observation = BuildObservation(combinedCenter, panelOcr);
                    centerOcr.Bitmap.Dispose();
                    centerTightOcr.Bitmap.Dispose();
                    panelOcr.Bitmap.Dispose();
                    if (observation != null) ObservationReady?.Invoke(this, observation);
                }
            }
        }

        private GameHudObservation BuildObservation(OcrCapture center, OcrCapture panel)
        {
            List<string> characters;
            List<string> dungeons;
            List<string> difficulties;
            List<string> party;
            lock (_gate)
            {
                characters = _characterNames.ToList();
                dungeons = _dungeonNames.ToList();
                difficulties = _difficultyNames.ToList();
                party = _partyNames.ToList();
            }

            var observedCharacter = BestKnownMatch(center.Text, characters);
            if (String.IsNullOrWhiteSpace(observedCharacter))
            {
                // The party panel has no reliable "self" crown: that icon means party
                // leader. It is still useful when exactly one owned character name is
                // visible anywhere in the panel, so use uniqueness rather than position.
                observedCharacter = UniqueKnownMatch(panel.Text, characters);
            }
            if (!String.IsNullOrWhiteSpace(observedCharacter))
            {
                if (String.Equals(_pendingCharacter, observedCharacter, StringComparison.OrdinalIgnoreCase))
                    _pendingCharacterCount++;
                else
                {
                    _pendingCharacter = observedCharacter;
                    _pendingCharacterCount = 1;
                }
            }
            else
            {
                _pendingCharacter = "";
                _pendingCharacterCount = 0;
            }

            var confirmedCharacter = _pendingCharacterCount >= 2 ? _pendingCharacter : "";
            var dungeon = BestKnownMatch(panel.Text, dungeons);
            var difficulty = BestKnownMatch(panel.Text, difficulties);
            var colors = ReadPartyIconColors(panel, party);
            var servers = ReadPartyServers(panel.Text, party);
            if (String.IsNullOrWhiteSpace(confirmedCharacter) && String.IsNullOrWhiteSpace(dungeon) && String.IsNullOrWhiteSpace(difficulty) && colors.Count == 0 && servers.Count == 0)
                return null;

            return new GameHudObservation
            {
                ObservedAtUtc = DateTime.UtcNow,
                CharacterName = confirmedCharacter,
                DungeonName = dungeon,
                DifficultyName = difficulty,
                PartyClassColors = colors,
                PartyServers = servers,
                Evidence = "WINDOWS_OCR_FIXED_ROI"
            };
        }

        private static Dictionary<string, string> ReadPartyServers(string panelText, IEnumerable<string> knownNames)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var source = panelText ?? "";
            if (source.Length == 0) return result;
            foreach (var name in knownNames ?? Enumerable.Empty<string>())
            {
                if (String.IsNullOrWhiteSpace(name)) continue;
                var match = Regex.Match(source, Regex.Escape(name.Trim()) + @"\s*[\[\(]\s*([가-힣A-Za-z0-9]{1,12})\s*[\]\)]", RegexOptions.IgnoreCase);
                if (match.Success) result[name.Trim()] = match.Groups[1].Value.Trim();
            }
            return result;
        }

        private Dictionary<string, string> ReadPartyIconColors(OcrCapture panel, IEnumerable<string> knownNames)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (panel == null || panel.Result == null || panel.Bitmap == null) return result;
            foreach (var line in panel.Result.Lines)
            {
                foreach (var word in line.Words)
                {
                    var name = knownNames.FirstOrDefault(value =>
                        String.Equals(Normalize(value), Normalize(word.Text), StringComparison.OrdinalIgnoreCase));
                    if (String.IsNullOrWhiteSpace(name)) continue;
                    var box = word.BoundingRect;
                    var sample = new Rectangle(
                        Math.Max(0, (int)Math.Floor(box.X) - 52),
                        Math.Max(0, (int)Math.Floor(box.Y) - 8),
                        44,
                        Math.Min(panel.Bitmap.Height - Math.Max(0, (int)Math.Floor(box.Y) - 8), Math.Max(24, (int)Math.Ceiling(box.Height) + 16)));
                    var color = SampleIconColor(panel.Bitmap, sample);
                    if (color.HasValue)
                        result[name] = "#" + color.Value.R.ToString("X2") + color.Value.G.ToString("X2") + color.Value.B.ToString("X2");
                }
            }
            return result;
        }

        private static Color? SampleIconColor(Bitmap bitmap, Rectangle area)
        {
            area.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            if (area.Width < 4 || area.Height < 4) return null;
            long red = 0, green = 0, blue = 0, weight = 0;
            for (var y = area.Top; y < area.Bottom; y += 2)
            {
                for (var x = area.Left; x < area.Right; x += 2)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    var max = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                    var min = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                    var saturation = max - min;
                    if (max < 80 || saturation < 28) continue;
                    var w = 1 + saturation;
                    red += pixel.R * w;
                    green += pixel.G * w;
                    blue += pixel.B * w;
                    weight += w;
                }
            }
            if (weight == 0) return null;
            return Color.FromArgb((int)(red / weight), (int)(green / weight), (int)(blue / weight));
        }

        private async Task<OcrCapture> RecognizeAsync(Bitmap bitmap)
        {
            var working = ResizeForOcr(bitmap);
            using (var memory = new MemoryStream())
            {
                working.Save(memory, ImageFormat.Png);
                using (var random = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(random))
                    {
                        writer.WriteBytes(memory.ToArray());
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }
                    random.Seek(0);
                    var decoder = await BitmapDecoder.CreateAsync(random);
                    using (var software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
                    {
                        var result = await _ocr.RecognizeAsync(software);
                        return new OcrCapture { Text = result == null ? "" : result.Text ?? "", Result = result, Bitmap = working };
                    }
                }
            }
        }

        private static Bitmap ResizeForOcr(Bitmap source)
        {
            const int maximum = 2200;
            if (source.Width <= maximum && source.Height <= maximum)
                return new Bitmap(source);
            var scale = Math.Min(maximum / (double)source.Width, maximum / (double)source.Height);
            var resized = new Bitmap(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)), PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, resized.Width, resized.Height);
            }
            return resized;
        }

        private static Rectangle RelativeRect(int width, int height, double left, double top, double right, double bottom)
        {
            var x = Math.Max(0, (int)Math.Round(width * left));
            var y = Math.Max(0, (int)Math.Round(height * top));
            var w = Math.Max(1, Math.Min(width - x, (int)Math.Round(width * (right - left))));
            var h = Math.Max(1, Math.Min(height - y, (int)Math.Round(height * (bottom - top))));
            return new Rectangle(x, y, w, h);
        }

        private static string BestKnownMatch(string source, IEnumerable<string> candidates)
        {
            var normalizedSource = Normalize(source);
            if (normalizedSource.Length == 0) return "";
            foreach (var candidate in candidates.OrderByDescending(value => Normalize(value).Length))
            {
                var normalized = Normalize(candidate);
                if (normalized.Length >= 2 && normalizedSource.IndexOf(normalized, StringComparison.OrdinalIgnoreCase) >= 0)
                    return candidate;
            }
            return "";
        }

        private static string UniqueKnownMatch(string source, IEnumerable<string> candidates)
        {
            var normalizedSource = Normalize(source);
            if (normalizedSource.Length == 0) return "";
            var matches = (candidates ?? Enumerable.Empty<string>())
                .Where(candidate => Normalize(candidate).Length >= 2 &&
                    normalizedSource.IndexOf(Normalize(candidate), StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return matches.Count == 1 ? matches[0] : "";
        }

        private static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            return new string(value.Trim().Where(Char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        private static IntPtr FindForegroundAionWindow()
        {
            var foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && IsAionWindow(foreground) && !IsIconic(foreground)) return foreground;
            return IntPtr.Zero;
        }

        private static IntPtr FindAionWindow()
        {
            var foreground = FindForegroundAionWindow();
            if (foreground != IntPtr.Zero) return foreground;

            var found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr handle, IntPtr state)
            {
                if (!IsWindowVisible(handle) || !IsAionWindow(handle)) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private string MatchWindowTitleCharacter(string windowTitle)
        {
            List<string> characters;
            lock (_gate) characters = _characterNames.ToList();
            return AionWindowCharacterDetector.MatchOwnedCharacter(windowTitle, characters);
        }

        private static string ReadWindowTitle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return "";
            var title = new System.Text.StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            return title.ToString().Trim();
        }

        private void ResetPendingTitleCharacter()
        {
            _pendingTitleCharacter = "";
            _pendingTitleCharacterCount = 0;
        }

        private static bool IsAionWindow(IntPtr handle)
        {
            try
            {
                uint processId;
                GetWindowThreadProcessId(handle, out processId);
                if (processId > 0)
                {
                    using (var process = Process.GetProcessById((int)processId))
                        if ((process.ProcessName ?? "").IndexOf("aion2", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                var title = new System.Text.StringBuilder(512);
                GetWindowText(handle, title, title.Capacity);
                return title.ToString().IndexOf("aion2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       title.ToString().IndexOf("아이온2", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private void RaiseStatusOnce(string key, string text)
        {
            if (String.Equals(_lastStatusKey, key ?? "", StringComparison.Ordinal)) return;
            _lastStatusKey = key ?? "";
            StatusChanged?.Invoke(this, text);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _timer.Dispose();
        }
    }
}
