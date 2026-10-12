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

            viewModel.RequestOpenUatUpgrade += info =>
            {
                var settings = viewModel.SettingsServiceForUat.CurrentSettings;
                string prodRoot = settings.MySql.InstallationPath;
                string iniPath = settings.MySql.IniPath;
                string? parent = System.IO.Path.GetDirectoryName(prodRoot.TrimEnd('\\', '/'));
                int prodPort = 3306;
                try
                {
                    if (System.IO.File.Exists(iniPath))
                    {
                        string? p = XamppUpdate.Services.Uat.UatIniRewriter.GetOption(
                            System.IO.File.ReadAllText(iniPath), "mysqld", "port");
                        if (int.TryParse(p, out int parsed) && parsed > 0) prodPort = parsed;
                    }
                }
                catch { /* keep default */ }

                var defaults = new XamppUpdate.Services.Uat.UatOptions
                {
                    ProductionRoot = prodRoot,
                    ProductionIniPath = iniPath,
                    ProductionServiceName = settings.MySql.ServiceName,
                    ProductionPort = prodPort,
                    IncomingRoot = info.IncomingRoot,
                    IncomingVersion = info.IncomingVersion,
                    TargetRoot = System.IO.Path.Combine(parent ?? prodRoot, "mysql_copy_upgrade"),
                    AdminUser = viewModel.MySqlUpgradeUser,
                    AdminPassword = viewModel.MySqlUpgradePassword
                };

                var service = new XamppUpdate.Services.Uat.UatUpgradeService(
                    viewModel.ServiceManagerForUat,
                    new XamppUpdate.Services.Uat.MySqlToolRunner(),
                    new XamppUpdate.Services.Uat.UatFileSystem());

                var uatWindow = new UatUpgradeWindow(new UatUpgradeViewModel(service, defaults)) { Owner = this };
                uatWindow.ShowDialog();
            };

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
