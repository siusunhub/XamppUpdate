using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class PhpUpdateWizardWindow : Window
    {
        public PhpUpdateWizardWindow(PhpWizardViewModel viewModel)
        {
            InitializeComponent();
            Title = $"PHP Update Wizard - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }
    }
}
