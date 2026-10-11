using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class ApacheUpdateWizardWindow : Window
    {
        public ApacheUpdateWizardWindow(ApacheWizardViewModel viewModel)
        {
            InitializeComponent();
            Title = $"Apache Update Wizard - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
            Closed += (_, _) => viewModel.StopStatusMonitor();
            viewModel.RequestOpenGenerateDefaultCert += () =>
            {
                var certVm = new GenerateDefaultSslCertViewModel(viewModel.SslCertificateService, viewModel.SettingsService);
                var certWindow = new GenerateDefaultSslCertWindow(certVm)
                {
                    Owner = this
                };
                certWindow.ShowDialog();
            };
            viewModel.RequestOpenVHostManager += () =>
            {
                var vhostVm = new VHostManagerViewModel(viewModel.VHostService, viewModel.SettingsService, viewModel.SslCertificateService);
                var vhostWindow = new VHostManagerWindow(vhostVm)
                {
                    Owner = this
                };
                vhostWindow.ShowDialog();
            };
        }
    }
}
