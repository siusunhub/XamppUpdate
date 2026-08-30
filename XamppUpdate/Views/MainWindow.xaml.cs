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
            Title = AppInfo.WindowTitle;
            _viewModel = viewModel;
            _settingsVmFactory = settingsVmFactory;
            _apacheWizardVmFactory = apacheWizardVmFactory;

            DataContext = _viewModel;

            _viewModel.RequestOpenSettings += OnOpenSettings;
            _viewModel.RequestOpenApacheWizard += OnOpenApacheWizard;

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
    }
}
