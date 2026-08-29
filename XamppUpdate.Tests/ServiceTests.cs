using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XamppUpdate.Models;
using XamppUpdate.Services;
using Xunit;

namespace XamppUpdate.Tests
{
    public class ServiceTests : IDisposable
    {
        private readonly string _testTempDir;

        public ServiceTests()
        {
            _testTempDir = Path.Combine(Path.GetTempPath(), "xampp_update_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testTempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testTempDir))
                {
                    Directory.Delete(_testTempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public async Task SettingsService_LoadAndSave_WorksCorrectly()
        {
            string configPath = Path.Combine(_testTempDir, "XamppUpdate.config.json");
            var service = new SettingsService(configPath);

            var loaded = service.LoadSettings();
            Assert.NotNull(loaded);
            Assert.Equal(@"C:\xampp\apache", loaded.Apache.InstallationPath);
            Assert.Equal("apache2.4", loaded.Apache.ServiceName);

            // Modify and save
            loaded.Apache.InstallationPath = @"D:\my_xampp\apache";
            loaded.General.MaxBackupRetentionCount = 5;
            loaded.Composer.ExecutablePath = @"D:\tools\composer.bat";
            loaded.Composer.DownloadTargetPath = @"D:\xampp\composer_downloads";
            loaded.Composer.WwwTargetPath = @"D:\xampp\htdocs\composer";
            await service.SaveSettingsAsync(loaded);

            var service2 = new SettingsService(configPath);
            var reloaded = service2.LoadSettings();
            Assert.Equal(@"D:\my_xampp\apache", reloaded.Apache.InstallationPath);
            Assert.Equal(5, reloaded.General.MaxBackupRetentionCount);
            Assert.Equal(@"D:\tools\composer.bat", reloaded.Composer.ExecutablePath);
            Assert.Equal(@"D:\xampp\composer_downloads", reloaded.Composer.DownloadTargetPath);
            Assert.Equal(@"D:\xampp\htdocs\composer", reloaded.Composer.WwwTargetPath);
        }

        [Fact]
        public async Task ConfigDiffService_DetectsDifferencesAccurately()
        {
            string localFile = Path.Combine(_testTempDir, "local_httpd.conf");
            string incomingFile = Path.Combine(_testTempDir, "incoming_httpd.conf");

            await File.WriteAllTextAsync(localFile, "ServerRoot \"C:/xampp/apache\"\nListen 80\nServerName localhost:80\n");
            await File.WriteAllTextAsync(incomingFile, "ServerRoot \"C:/Apache24\"\nListen 80\nServerName localhost:80\n# New Comment\n");

            var diffService = new ConfigDiffService();
            var result = await diffService.CompareSingleFileAsync(localFile, incomingFile, "conf/httpd.conf");

            Assert.NotNull(result);
            Assert.True(result.HasDifferences);
            Assert.True(result.AdditionsCount > 0 || result.ModificationsCount > 0 || result.DeletionsCount > 0);
            Assert.NotEmpty(result.LocalLines);
            Assert.NotEmpty(result.IncomingLines);
            Assert.Equal(result.LocalLines.Count, result.IncomingLines.Count);

            // Test RebuildDiff after user modifies content to match incoming
            diffService.RebuildDiff(result, "ServerRoot \"C:/Apache24\"\nListen 80\nServerName localhost:80\n# New Comment\n", "ServerRoot \"C:/Apache24\"\nListen 80\nServerName localhost:80\n# New Comment\n");
            Assert.False(result.HasDifferences);
            Assert.Equal(0, result.AdditionsCount);
            Assert.Equal(0, result.DeletionsCount);
            Assert.Equal(0, result.ModificationsCount);
        }

        [Fact]
        public void ArchiveService_FindApacheRoot_DetectsNestedFolder()
        {
            string baseFolder = Path.Combine(_testTempDir, "extract_test");
            string nestedApache = Path.Combine(baseFolder, "Apache24", "bin");
            Directory.CreateDirectory(nestedApache);
            File.WriteAllText(Path.Combine(nestedApache, "httpd.exe"), "dummy exe");

            var archiveService = new ArchiveService();
            string resolvedRoot = archiveService.FindApacheRoot(baseFolder);

            Assert.Equal(Path.Combine(baseFolder, "Apache24"), resolvedRoot);
        }

        [Fact]
        public async Task ArchiveService_CreateZipBackup_CreatesValidZip()
        {
            string sourceFolder = Path.Combine(_testTempDir, "mock_apache");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(Path.Combine(sourceFolder, "test.txt"), "hello backup");

            string backupZip = Path.Combine(_testTempDir, "backup.zip");

            var archiveService = new ArchiveService();
            await archiveService.CreateZipBackupAsync(sourceFolder, backupZip);

            Assert.True(File.Exists(backupZip));
            Assert.True(new FileInfo(backupZip).Length > 0);
        }

        [Fact]
        public async Task WindowsServiceManager_NonExistentService_ReturnsSafeDefaults()
        {
            var manager = new WindowsServiceManager();
            string nonExistentName = "ServiceNonExistent_" + Guid.NewGuid().ToString("N");

            bool exists = manager.ServiceExists(nonExistentName);
            Assert.False(exists);

            var status = await manager.GetServiceStatusAsync(nonExistentName);
            Assert.Null(status);

            bool stopped = await manager.StopServiceAsync(nonExistentName, TimeSpan.FromSeconds(1));
            Assert.True(stopped); // Nonexistent returns true as already stopped
        }
    }
}
