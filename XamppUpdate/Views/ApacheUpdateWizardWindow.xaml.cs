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
        }
    }
}
