using System.Windows;
using System.Windows.Controls;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class MySqlUpdateWizardWindow : Window
    {
        public MySqlUpdateWizardWindow(MySqlWizardViewModel viewModel)
        {
            InitializeComponent();
            Title = $"MySQL / MariaDB Update Wizard - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }

        private void UpgradePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is MySqlWizardViewModel vm && sender is PasswordBox pb)
            {
                vm.MySqlUpgradePassword = pb.Password;
            }
        }
    }
}
