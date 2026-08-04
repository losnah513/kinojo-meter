using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KinojoMeterPrototype
{
    internal sealed class CharacterDiscoveryWindow : Window
    {
        private readonly Dictionary<string, Border> _cards = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly TextBlock _status;
        private readonly UniformGrid _cardGrid;
        private readonly int _cardRows;

        public event EventHandler<CharacterProfile> CharacterSelected;

        public CharacterDiscoveryWindow(IEnumerable<CharacterProfile> profiles, MeterPreferences preferences)
        {
            var items = (profiles ?? Enumerable.Empty<CharacterProfile>()).ToList();
            _cardRows = (int)Math.Ceiling(Math.Max(1, items.Count) / 3.0);
            Title = "KINOJO Meter · 캐릭터 자동 검색";
            Width = 390;
            Height = 94;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var working = SystemParameters.WorkArea;
            var requestedLeft = preferences == null ? working.Left + 24 : preferences.OverlayLeft;
            var requestedTop = preferences == null ? working.Top + 80 : preferences.OverlayTop;
            Left = Math.Max(working.Left + 8, Math.Min(working.Right - Width - 8, requestedLeft));
            Top = Math.Max(working.Top + 8, Math.Min(working.Bottom - Height - 8, requestedTop));

            var surface = new Border { CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(244, 10, 15, 24)), BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)), BorderThickness = new Thickness(1), Padding = new Thickness(18) };
            Content = surface;
            var root = new StackPanel();
            surface.Child = root;
            root.Children.Add(new TextBlock { Text = "KINOJO · 접속 캐릭터 자동 확인", Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.Bold });

            _cardGrid = new UniformGrid { Columns = 3, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var profile in items.OrderByDescending(value => value.IsMain).ThenBy(value => value.CharacterName))
            {
                var card = BuildCard(profile);
                _cardGrid.Children.Add(card);
                _cards[(profile.CharacterName ?? "").Trim()] = card;
            }

            var activity = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            activity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            activity.ColumnDefinitions.Add(new ColumnDefinition());
            activity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            activity.Children.Add(new KinojoSpinner { Margin = new Thickness(0, 0, 9, 0) });
            _status = new TextBlock { Text = "캐릭터 자동 검색 중 · 게임 패킷을 확인하고 있습니다", Foreground = new SolidColorBrush(Color.FromRgb(186, 230, 253)), FontSize = 8, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_status, 1);
            activity.Children.Add(_status);
            var manual = new Button { Content = "직접 선택", FontSize = 8, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
            manual.Click += delegate
            {
                var show = _cardGrid.Visibility != Visibility.Visible;
                _cardGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Width = show ? 520 : 390;
                Height = show ? Math.Min(520, 118 + _cardRows * 82) : 94;
                Left = Math.Max(working.Left + 8, Math.Min(working.Right - Width - 8, Left));
                Top = Math.Max(working.Top + 8, Math.Min(working.Bottom - Height - 8, Top));
            };
            Grid.SetColumn(manual, 2);
            activity.Children.Add(manual);
            root.Children.Add(activity);

            root.Children.Add(_cardGrid);
        }

        public void SetStatus(string value) { if (!String.IsNullOrWhiteSpace(value)) _status.Text = value; }

        public void MarkDetected(CharacterProfile profile, string evidence)
        {
            if (profile == null) return;
            Border card;
            if (_cards.TryGetValue((profile.CharacterName ?? "").Trim(), out card))
            {
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(34, 211, 238));
                card.BorderThickness = new Thickness(2);
                card.Background = new SolidColorBrush(Color.FromArgb(235, 14, 65, 83));
            }
            _status.Text = profile.CharacterName + " 확인 · " + evidence + " · 미터기로 전환 중";
        }

        private Border BuildCard(CharacterProfile profile)
        {
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(24, 31, 43)), BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Margin = new Thickness(3), Cursor = Cursors.Hand };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = profile.CharacterName + (profile.IsMain ? " · 본캐" : " · 부캐"), Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock { Text = (profile.ServerName ?? "서버 확인 중") + " · " + (profile.ClassName ?? "클래스 확인 중"), Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 8, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            card.Child = stack;
            card.MouseLeftButtonUp += delegate { CharacterSelected?.Invoke(this, profile); };
            return card;
        }
    }
}
