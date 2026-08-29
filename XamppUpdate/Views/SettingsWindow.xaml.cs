using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            Title = $"Settings - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }
    }
}
