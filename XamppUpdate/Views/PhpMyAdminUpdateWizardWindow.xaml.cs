using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class PhpMyAdminUpdateWizardWindow : Window
    {
        public PhpMyAdminUpdateWizardWindow(PhpMyAdminWizardViewModel viewModel)
        {
            InitializeComponent();
            Title = $"phpMyAdmin Update Wizard - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }
    }
}
