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
        private readonly Func<PhpWizardViewModel> _phpWizardVmFactory;
        private readonly Func<SslManagerViewModel> _sslManagerVmFactory;

        public MainWindow(
            MainViewModel viewModel,
            Func<SettingsViewModel> settingsVmFactory,
            Func<ApacheWizardViewModel> apacheWizardVmFactory,
            Func<PhpWizardViewModel> phpWizardVmFactory,
            Func<SslManagerViewModel> sslManagerVmFactory)
        {
            InitializeComponent();
            Title = AppInfo.WindowTitle;
            _viewModel = viewModel;
            _settingsVmFactory = settingsVmFactory;
            _apacheWizardVmFactory = apacheWizardVmFactory;
            _phpWizardVmFactory = phpWizardVmFactory;
            _sslManagerVmFactory = sslManagerVmFactory;

            DataContext = _viewModel;

            _viewModel.RequestOpenSettings += OnOpenSettings;
            _viewModel.RequestOpenApacheWizard += OnOpenApacheWizard;
            _viewModel.RequestOpenPhpWizard += OnOpenPhpWizard;
            _viewModel.RequestOpenSslManager += OnOpenSslManager;

            Loaded += async (_, _) => await _viewModel.RefreshStatusesAsync();
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

        private void OnOpenPhpWizard()
        {
            var wizardVm = _phpWizardVmFactory();
            var wizardWindow = new PhpUpdateWizardWindow(wizardVm)
            {
                Owner = this
            };
            wizardWindow.ShowDialog();
            _ = _viewModel.RefreshStatusesAsync();
        }

        private void OnOpenSslManager()
        {
            var sslVm = _sslManagerVmFactory();
            var sslWindow = new SslManagerWindow(sslVm)
            {
                Owner = this
            };
            sslWindow.ShowDialog();
        }
    }
}
