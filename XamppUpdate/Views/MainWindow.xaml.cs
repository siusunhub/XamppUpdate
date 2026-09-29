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
        private readonly Func<MySqlWizardViewModel> _mySqlWizardVmFactory;
        private readonly Func<PhpMyAdminWizardViewModel> _phpMyAdminWizardVmFactory;
        private readonly Func<SslManagerViewModel> _sslManagerVmFactory;
        private readonly Func<ComposerViewModel> _composerVmFactory;

        public MainWindow(
            MainViewModel viewModel,
            Func<SettingsViewModel> settingsVmFactory,
            Func<ApacheWizardViewModel> apacheWizardVmFactory,
            Func<PhpWizardViewModel> phpWizardVmFactory,
            Func<MySqlWizardViewModel> mySqlWizardVmFactory,
            Func<PhpMyAdminWizardViewModel> phpMyAdminWizardVmFactory,
            Func<SslManagerViewModel> sslManagerVmFactory,
            Func<ComposerViewModel> composerVmFactory)
        {
            InitializeComponent();
            Title = AppInfo.WindowTitle;
            _viewModel = viewModel;
            _settingsVmFactory = settingsVmFactory;
            _apacheWizardVmFactory = apacheWizardVmFactory;
            _phpWizardVmFactory = phpWizardVmFactory;
            _mySqlWizardVmFactory = mySqlWizardVmFactory;
            _phpMyAdminWizardVmFactory = phpMyAdminWizardVmFactory;
            _sslManagerVmFactory = sslManagerVmFactory;
            _composerVmFactory = composerVmFactory;

            DataContext = _viewModel;

            _viewModel.RequestOpenSettings += OnOpenSettings;
            _viewModel.RequestOpenApacheWizard += OnOpenApacheWizard;
            _viewModel.RequestOpenPhpWizard += OnOpenPhpWizard;
            _viewModel.RequestOpenMySqlWizard += OnOpenMySqlWizard;
            _viewModel.RequestOpenPhpMyAdminWizard += OnOpenPhpMyAdminWizard;
            _viewModel.RequestOpenSslManager += OnOpenSslManager;
            _viewModel.RequestOpenComposer += OnOpenComposer;

            Loaded += async (_, _) => await _viewModel.RefreshStatusesAsync();
            Closed += (_, _) => Application.Current.Shutdown();
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

        private void OnOpenMySqlWizard()
        {
            var wizardVm = _mySqlWizardVmFactory();
            var wizardWindow = new MySqlUpdateWizardWindow(wizardVm)
            {
                Owner = this
            };
            wizardWindow.ShowDialog();
            _ = _viewModel.RefreshStatusesAsync();
        }

        private void OnOpenPhpMyAdminWizard()
        {
            var wizardVm = _phpMyAdminWizardVmFactory();
            var wizardWindow = new PhpMyAdminUpdateWizardWindow(wizardVm)
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
            _ = _viewModel.RefreshStatusesAsync();
        }

        private void OnOpenComposer()
        {
            var composerVm = _composerVmFactory();
            var composerWindow = new ComposerWindow(composerVm)
            {
                Owner = this
            };
            composerWindow.ShowDialog();
            _ = _viewModel.RefreshStatusesAsync();
        }
    }
}
