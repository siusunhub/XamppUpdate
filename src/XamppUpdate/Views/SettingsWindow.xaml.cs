using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }
    }
}
