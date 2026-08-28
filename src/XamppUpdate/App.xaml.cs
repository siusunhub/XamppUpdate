using System;
using System.IO;
using System.Windows;
using XamppUpdate.Services;
using XamppUpdate.ViewModels;
using XamppUpdate.Views;

namespace XamppUpdate
{
    public partial class App : Application
    {
        private ISettingsService? _settingsService;
        private IWindowsServiceManager? _serviceManager;
        private IVersionDetectionService? _versionDetectionService;
        private IArchiveService? _archiveService;
        private IConfigDiffService? _configDiffService;
        private IApacheUpdateService? _apacheUpdateService;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Register global exception handlers
            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show(
                    $"An unhandled application error occurred:\n\n{args.Exception.Message}\n\nStack trace:\n{args.Exception.StackTrace}",
                    "Unexpected Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    MessageBox.Show(
                        $"Fatal Domain Error:\n\n{ex.Message}",
                        "Fatal Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };

            // Initialize Services
            _settingsService = new SettingsService();
            _serviceManager = new WindowsServiceManager();
            _versionDetectionService = new VersionDetectionService();
            _archiveService = new ArchiveService();
            _configDiffService = new ConfigDiffService();
            _apacheUpdateService = new ApacheUpdateService(
                _settingsService,
                _serviceManager,
                _versionDetectionService,
                _archiveService,
                _configDiffService);

            // Ensure folders
            EnsureAppDirectories();

            // Create ViewModels & Window
            var mainViewModel = new MainViewModel(_settingsService, _serviceManager, _versionDetectionService);

            var mainWindow = new MainWindow(
                mainViewModel,
                () => new SettingsViewModel(_settingsService),
                () => new ApacheWizardViewModel(_apacheUpdateService, _settingsService, _versionDetectionService));

            MainWindow = mainWindow;
            mainWindow.Show();
        }

        private void EnsureAppDirectories()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string configDir = Path.Combine(baseDir, "config");
                string tempDir = Path.Combine(baseDir, "temp");
                string backupDir = Path.Combine(baseDir, "backup");

                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to create app directories: {ex.Message}");
            }
        }
    }
}
