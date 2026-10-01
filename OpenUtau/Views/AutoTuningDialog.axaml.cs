using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OpenUtau.App.ViewModels;

namespace OpenUtau.App.Views {
    public partial class AutoTuningDialog : Window {
        public bool Confirmed { get; private set; }

        public AutoTuningDialog() {
            InitializeComponent();
        }

        void OnApplyClicked(object? sender, RoutedEventArgs e) {
            if (DataContext is AutoTuningViewModel { CanApply: true }) {
                Confirmed = true;
                Close();
            }
        }

        void OnCancelClicked(object? sender, RoutedEventArgs e) {
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e) {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                Close();
            } else {
                base.OnKeyDown(e);
            }
        }
    }
}
