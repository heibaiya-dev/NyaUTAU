using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OpenUtau.App.Views {
    public partial class SmartSpeechDialog : Window {
        public bool Confirmed { get; private set; }
        public string InputText { get; private set; } = string.Empty;
        public int BaseTone { get; private set; } = 45;
        public double BeatsPerToken { get; private set; } = 0.3;
        public bool ReplaceSelection { get; private set; } = true;
        public bool FitSelection { get; private set; } = true;

        public SmartSpeechDialog() {
            InitializeComponent();
            InputBox.AttachedToVisualTree += (_, _) => {
                InputBox.Focus();
            };
        }

        void OnOkClicked(object? sender, RoutedEventArgs e) {
            var text = InputBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) {
                return;
            }
            InputText = text;
            BaseTone = (int)Math.Clamp(ToneBox.Value ?? 45, 24, 96);
            if (double.TryParse(DurationBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                    out var beats) || double.TryParse(DurationBox.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out beats)) {
                BeatsPerToken = Math.Clamp(beats, 0.125, 8);
            }
            ReplaceSelection = ReplaceSelectionBox.IsChecked == true;
            FitSelection = FitSelectionBox.IsChecked == true;
            Confirmed = true;
            Close();
        }

        void OnCancelClicked(object? sender, RoutedEventArgs e) {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                Close();
            } else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
                e.Handled = true;
                OnOkClicked(this, e);
            } else {
                base.OnKeyDown(e);
            }
        }
    }
}
