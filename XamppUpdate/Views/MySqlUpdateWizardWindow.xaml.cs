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
            Closed += (_, _) => viewModel.StopStatusMonitor();
        }

        private bool _syncingPassword;

        // Shared by the step-3 and step-4 password boxes; keeps both showing the same value.
        private void UpgradePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword) return;

            if (DataContext is MySqlWizardViewModel vm && sender is PasswordBox pb)
            {
                vm.MySqlUpgradePassword = pb.Password;

                _syncingPassword = true;
                try
                {
                    foreach (var other in new[] { UpgradePasswordBox, Step3PasswordBox })
                    {
                        if (!ReferenceEquals(other, pb) && other.Password != pb.Password)
                        {
                            other.Password = pb.Password;
                        }
                    }
                }
                finally
                {
                    _syncingPassword = false;
                }
            }
        }
    }
}
