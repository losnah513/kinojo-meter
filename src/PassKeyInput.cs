using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KinojoMeterPrototype
{
    internal sealed class PassKeyInput : Grid
    {
        private const int PreviewCellCount = 6;
        private const int MaxPassKeyCharacters = 12;

        private readonly TextBox _editor;
        private readonly TextBlock _placeholder;
        private readonly List<Border> _cells = new List<Border>();
        private readonly List<TextBlock> _cellTexts = new List<TextBlock>();
        private bool _internalChange;
        private bool _isComposing;
        private bool _isFocused;

        public event EventHandler EnterPressed;

        public PassKeyInput()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var editorHost = new Grid();
            _editor = new TextBox
            {
                Height = 44,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(12, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(12, 18, 29)),
                Foreground = Brushes.White,
                CaretBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            SpellCheck.SetIsEnabled(_editor, false);
            InputMethod.SetIsInputMethodEnabled(_editor, true);
            InputMethod.SetPreferredImeState(_editor, InputMethodState.On);

            _placeholder = new TextBlock
            {
                Text = "PASS KEY를 입력하세요",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 12,
                Margin = new Thickness(13, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            _editor.TextChanged += EditorTextChanged;
            _editor.PreviewKeyDown += EditorPreviewKeyDown;
            _editor.GotKeyboardFocus += delegate
            {
                _isFocused = true;
                UpdateVisualState();
            };
            _editor.LostKeyboardFocus += delegate
            {
                _isFocused = false;
                UpdateVisualState();
            };
            DataObject.AddPastingHandler(_editor, EditorPasting);
            TextCompositionManager.AddPreviewTextInputStartHandler(_editor, CompositionStarted);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(_editor, CompositionUpdated);
            TextCompositionManager.AddTextInputHandler(_editor, CompositionCommitted);

            editorHost.Children.Add(_editor);
            editorHost.Children.Add(_placeholder);
            Children.Add(editorHost);

            var preview = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center
            };
            preview.MouseLeftButtonDown += delegate
            {
                FocusFirst();
            };

            for (var index = 0; index < PreviewCellCount; index++)
            {
                preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                if (index < PreviewCellCount - 1)
                    preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });

                var value = new TextBlock
                {
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                var cell = new Border
                {
                    Width = 44,
                    Height = 48,
                    Background = new SolidColorBrush(Color.FromRgb(12, 18, 29)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Child = value,
                    IsHitTestVisible = false
                };
                Grid.SetColumn(cell, index * 2);
                preview.Children.Add(cell);
                _cells.Add(cell);
                _cellTexts.Add(value);
            }

            Grid.SetRow(preview, 2);
            Children.Add(preview);
            RenderPreview("");
        }

        public string Value
        {
            get { return Normalize(_editor.Text); }
        }

        public bool IsComplete
        {
            get { return GetTextElements(Value).Count >= PreviewCellCount; }
        }

        public void FocusFirst()
        {
            if (!_editor.IsEnabled) return;
            _editor.Focus();
            _editor.CaretIndex = _editor.Text.Length;
        }

        public void Clear()
        {
            Clear(true);
        }

        public void Clear(bool focusEditor)
        {
            SetEditorText("", 0);
            if (focusEditor) FocusFirst();
        }

        public void SetInputEnabled(bool enabled)
        {
            _editor.IsEnabled = enabled;
            Opacity = enabled ? 1.0 : 0.7;
            UpdateVisualState();
        }

        private void EditorTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_internalChange) return;

            if (_isComposing)
            {
                RenderPreview(Normalize(_editor.Text));
                return;
            }

            SynchronizeEditor();
        }

        private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var handler = EnterPressed;
            if (handler != null) handler(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void EditorPasting(object sender, DataObjectPastingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(SynchronizeEditor), DispatcherPriority.Background);
        }

        private void CompositionStarted(object sender, TextCompositionEventArgs e)
        {
            _isComposing = true;
            UpdateVisualState();
        }

        private void CompositionUpdated(object sender, TextCompositionEventArgs e)
        {
            _isComposing = true;
        }

        private void CompositionCommitted(object sender, TextCompositionEventArgs e)
        {
            _isComposing = false;
            Dispatcher.BeginInvoke(new Action(SynchronizeEditor), DispatcherPriority.Background);
        }

        private void SynchronizeEditor()
        {
            if (_internalChange || _isComposing) return;

            var normalized = Normalize(_editor.Text);
            if (!String.Equals(_editor.Text, normalized, StringComparison.Ordinal))
            {
                var caret = Math.Min(normalized.Length, _editor.CaretIndex);
                SetEditorText(normalized, caret);
                return;
            }

            RenderPreview(normalized);
        }

        private void SetEditorText(string value, int caretIndex)
        {
            _internalChange = true;
            try
            {
                _editor.Text = value ?? "";
                _editor.CaretIndex = Math.Max(0, Math.Min(_editor.Text.Length, caretIndex));
            }
            finally
            {
                _internalChange = false;
            }
            RenderPreview(_editor.Text);
        }

        private void RenderPreview(string value)
        {
            var characters = GetTextElements(Normalize(value));
            for (var index = 0; index < _cellTexts.Count; index++)
                _cellTexts[index].Text = index < characters.Count ? characters[index] : "";

            _placeholder.Visibility = String.IsNullOrEmpty(_editor.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateVisualState();
        }

        private void UpdateVisualState()
        {
            var accent = new SolidColorBrush(Color.FromRgb(56, 189, 248));
            var normal = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            var filled = Math.Min(PreviewCellCount, GetTextElements(Normalize(_editor.Text)).Count);
            var activeIndex = Math.Min(PreviewCellCount - 1, filled);

            _editor.BorderBrush = _isFocused ? accent : normal;
            _editor.BorderThickness = _isFocused ? new Thickness(2) : new Thickness(1);

            for (var index = 0; index < _cells.Count; index++)
            {
                var isActive = _isFocused && index == activeIndex;
                var hasValue = index < filled;
                _cells[index].BorderBrush = isActive || hasValue ? accent : normal;
                _cells[index].BorderThickness = isActive ? new Thickness(2) : new Thickness(1);
            }
        }

        private static string Normalize(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";

            var output = new List<string>();
            var elements = GetTextElements(value);
            foreach (var element in elements)
            {
                if (output.Count >= MaxPassKeyCharacters) break;
                if (String.IsNullOrEmpty(element)) continue;
                if (element.All(character => Char.IsWhiteSpace(character) || Char.IsControl(character))) continue;

                if (element.Length == 1 && element[0] >= 'a' && element[0] <= 'z')
                    output.Add(Char.ToUpperInvariant(element[0]).ToString());
                else
                    output.Add(element);
            }
            return String.Concat(output);
        }

        private static List<string> GetTextElements(string value)
        {
            var result = new List<string>();
            if (String.IsNullOrEmpty(value)) return result;

            var enumerator = StringInfo.GetTextElementEnumerator(value);
            while (enumerator.MoveNext())
                result.Add(enumerator.GetTextElement());
            return result;
        }
    }
}
