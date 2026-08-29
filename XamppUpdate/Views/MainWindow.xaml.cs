using System;
using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly Func<SettingsViewModel> _settingsVmFactory;
        private readonly Func<ApacheWizardViewModel> _apacheWizardVmFactory;

        public MainWindow(
            MainViewModel viewModel,
            Func<SettingsViewModel> settingsVmFactory,
            Func<ApacheWizardViewModel> apacheWizardVmFactory)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settingsVmFactory = settingsVmFactory;
            _apacheWizardVmFactory = apacheWizardVmFactory;

            DataContext = _viewModel;

            _viewModel.RequestOpenSettings += OnOpenSettings;
            _viewModel.RequestOpenApacheWizard += OnOpenApacheWizard;

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.D &&
                    (System.Windows.Input.Keyboard.Modifiers & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
                {
                    TriggerSampleDebugError();
                }
            };

            Loaded += async (_, _) => await _viewModel.RefreshStatusesAsync();
        }

        private void BtnTestErrorTrace_Click(object sender, RoutedEventArgs e)
        {
            TriggerSampleDebugError();
        }

        private void TriggerSampleDebugError()
        {
            try
            {
                // Generate a real sample nested exception with full stack trace for testing
                throw new InvalidOperationException(
                    "Simulated debug error: Component health check failed due to an unexpected null reference.",
                    new System.IO.FileNotFoundException("Simulated missing configuration file: httpd.conf", "httpd.conf"));
            }
            catch (Exception ex)
            {
                ErrorDialogWindow.Show(ex, "Debug Error Trace Test", this);
            }
        }

        private void OnOpenSettings()
        {
            var settingsVm = _settingsVmFactory();
            var settingsWindow = new SettingsWindow(settingsVm)
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private void OnOpenApacheWizard()
        {
            var wizardVm = _apacheWizardVmFactory();
            var wizardWindow = new ApacheUpdateWizardWindow(wizardVm)
            {
                Owner = this
            };
            wizardWindow.ShowDialog();
            _ = _viewModel.RefreshStatusesAsync();
        }
    }
}
