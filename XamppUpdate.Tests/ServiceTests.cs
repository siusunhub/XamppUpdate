using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XamppUpdate.Models;
using XamppUpdate.Services;
using XamppUpdate.ViewModels;
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
            Assert.NotEmpty(result.DiffRows);
            Assert.Equal(result.LocalLines.Count, result.IncomingLines.Count);
            Assert.Equal(result.LocalLines.Count, result.DiffRows.Count);
            Assert.Equal(result.DiffRows[0].Local.Text, result.LocalLines[0].Text);
            Assert.Equal(result.DiffRows[0].Incoming.Text, result.IncomingLines[0].Text);

            // Test RebuildDiff after user modifies content to match incoming
            diffService.RebuildDiff(result, "ServerRoot \"C:/Apache24\"\nListen 80\nServerName localhost:80\n# New Comment\n", "ServerRoot \"C:/Apache24\"\nListen 80\nServerName localhost:80\n# New Comment\n");
            Assert.False(result.HasDifferences);
            Assert.Equal(0, result.AdditionsCount);
            Assert.Equal(0, result.DeletionsCount);
            Assert.Equal(0, result.ModificationsCount);
        }

        [Fact]
        public async Task ConfigDiffService_CompareConfigFiles_IncludesRequiredAndDiscoveredDifferences()
        {
            string localRoot = Path.Combine(_testTempDir, "local_apache_conf_test");
            string incomingRoot = Path.Combine(_testTempDir, "incoming_apache_conf_test");

            Directory.CreateDirectory(Path.Combine(localRoot, "conf", "extra"));
            Directory.CreateDirectory(Path.Combine(incomingRoot, "conf", "extra"));

            // 1. Required files (httpd.conf identical, httpd-ssl.conf different, httpd-xampp.conf only local)
            await File.WriteAllTextAsync(Path.Combine(localRoot, "conf", "httpd.conf"), "ServerRoot \"C:/xampp/apache\"");
            await File.WriteAllTextAsync(Path.Combine(incomingRoot, "conf", "httpd.conf"), "ServerRoot \"C:/xampp/apache\"");

            await File.WriteAllTextAsync(Path.Combine(localRoot, "conf", "extra", "httpd-ssl.conf"), "Listen 443");
            await File.WriteAllTextAsync(Path.Combine(incomingRoot, "conf", "extra", "httpd-ssl.conf"), "Listen 8443");

            await File.WriteAllTextAsync(Path.Combine(localRoot, "conf", "extra", "httpd-xampp.conf"), "# XAMPP local only");

            // 2. Extra .conf files:
            // httpd-vhosts.conf is DIFFERENT -> should be discovered and added
            await File.WriteAllTextAsync(Path.Combine(localRoot, "conf", "extra", "httpd-vhosts.conf"), "<VirtualHost *:80>\nServerName myapp.local\n</VirtualHost>");
            await File.WriteAllTextAsync(Path.Combine(incomingRoot, "conf", "extra", "httpd-vhosts.conf"), "<VirtualHost *:80>\nServerName dummy-host.example.com\n</VirtualHost>");

            // httpd-default.conf is IDENTICAL -> should NOT be added
            await File.WriteAllTextAsync(Path.Combine(localRoot, "conf", "extra", "httpd-default.conf"), "Timeout 60");
            await File.WriteAllTextAsync(Path.Combine(incomingRoot, "conf", "extra", "httpd-default.conf"), "Timeout 60");

            var requiredList = new[]
            {
                "conf/httpd.conf",
                "conf/extra/httpd-ssl.conf",
                "conf/extra/httpd-xampp.conf"
            };

            var diffService = new ConfigDiffService();
            var results = await diffService.CompareConfigFilesAsync(localRoot, incomingRoot, requiredList);

            var resultPaths = results.Select(r => r.RelativeFilePath.Replace('\\', '/')).ToList();

            // The 3 required files MUST be present:
            Assert.Contains("conf/httpd.conf", resultPaths);
            Assert.Contains("conf/extra/httpd-ssl.conf", resultPaths);
            Assert.Contains("conf/extra/httpd-xampp.conf", resultPaths);

            // The different file httpd-vhosts.conf MUST be added:
            Assert.Contains("conf/extra/httpd-vhosts.conf", resultPaths);

            // The identical file httpd-default.conf MUST NOT be added:
            Assert.DoesNotContain("conf/extra/httpd-default.conf", resultPaths);

            Assert.Equal(4, results.Count);
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
        public async Task ArchiveService_CreateZipBackup_CreatesValidZipAndExcludesLogs()
        {
            string sourceFolder = Path.Combine(_testTempDir, "mock_apache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(sourceFolder, "bin"));
            Directory.CreateDirectory(Path.Combine(sourceFolder, "conf"));
            Directory.CreateDirectory(Path.Combine(sourceFolder, "logs"));

            File.WriteAllText(Path.Combine(sourceFolder, "bin", "httpd.exe"), "hello binary");
            File.WriteAllText(Path.Combine(sourceFolder, "conf", "httpd.conf"), "ServerRoot \"C:/xampp/apache\"");
            File.WriteAllText(Path.Combine(sourceFolder, "logs", "access.log"), "127.0.0.1 - - [29/Aug/2026] GET /");
            File.WriteAllText(Path.Combine(sourceFolder, "logs", "error.log"), "[crit] server error");
            File.WriteAllText(Path.Combine(sourceFolder, "access.log.2026-08-29"), "archived log");

            string backupZip = Path.Combine(_testTempDir, "backup_" + Guid.NewGuid().ToString("N") + ".zip");

            var archiveService = new ArchiveService();
            await archiveService.CreateZipBackupAsync(sourceFolder, backupZip);

            Assert.True(File.Exists(backupZip));
            Assert.True(new FileInfo(backupZip).Length > 0);

            using var zip = System.IO.Compression.ZipFile.OpenRead(backupZip);
            var entryKeys = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

            Assert.Contains(entryKeys, k => k.Contains("httpd.exe"));
            Assert.Contains(entryKeys, k => k.Contains("httpd.conf"));
            Assert.DoesNotContain(entryKeys, k => k.Contains("access.log"));
            Assert.DoesNotContain(entryKeys, k => k.Contains("error.log"));
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

        [Fact]
        public void AppInfo_Properties_ArePopulatedCorrectly()
        {
            Assert.Equal("0.1", AppInfo.Version);
            Assert.NotEmpty(AppInfo.BuildNumber);
            Assert.NotEmpty(AppInfo.BuildDate);
            Assert.Contains("0.1", AppInfo.WindowTitle);
            Assert.Contains("Build", AppInfo.WindowTitle);
        }

        [Fact]
        public async Task ApacheWizardViewModel_VersionTransitionDisplay_FormatsCorrectly()
        {
            var vm = new ApacheWizardViewModel(
                new ApacheUpdateService(
                    new SettingsService(Path.Combine(_testTempDir, "cfg.json")),
                    new WindowsServiceManager(),
                    new VersionDetectionService(),
                    new ArchiveService(),
                    new ConfigDiffService()),
                new SettingsService(Path.Combine(_testTempDir, "cfg2.json")),
                new VersionDetectionService());

            await Task.Delay(100);
            vm.CurrentInstalledVersion = "2.4.58";
            vm.IncomingDetectedVersion = "2.4.63";

            Assert.Equal("Update from 2.4.58 to 2.4.63", vm.VersionTransitionDisplay);
        }

        [Fact]
        public async Task ApacheUpdateService_TestMode_SimulatesAndHoldsWorkspace()
        {
            string localApache = Path.Combine(_testTempDir, "local_apache");
            Directory.CreateDirectory(Path.Combine(localApache, "bin"));
            Directory.CreateDirectory(Path.Combine(localApache, "conf"));
            await File.WriteAllTextAsync(Path.Combine(localApache, "bin", "httpd.exe"), "local httpd");
            await File.WriteAllTextAsync(Path.Combine(localApache, "conf", "httpd.conf"), "ServerRoot \"C:/xampp/apache\"\nListen 80\n");

            string incomingApache = Path.Combine(_testTempDir, "incoming_apache");
            Directory.CreateDirectory(Path.Combine(incomingApache, "bin"));
            Directory.CreateDirectory(Path.Combine(incomingApache, "conf"));
            Directory.CreateDirectory(Path.Combine(incomingApache, "modules"));
            await File.WriteAllTextAsync(Path.Combine(incomingApache, "bin", "httpd.exe"), "incoming new httpd");
            await File.WriteAllTextAsync(Path.Combine(incomingApache, "conf", "httpd.conf"), "ServerRoot \"C:/xampp/apache\"\nListen 8080\n");
            await File.WriteAllTextAsync(Path.Combine(incomingApache, "modules", "mod_test.so"), "new module binary");

            string configPath = Path.Combine(_testTempDir, "test_config.json");
            var settingsService = new SettingsService(configPath);
            var settings = settingsService.LoadSettings();
            settings.Apache.InstallationPath = localApache;
            settings.Apache.ServiceName = "TestServiceNonExistent_" + Guid.NewGuid().ToString("N");
            settings.General.BackupDirectory = Path.Combine(_testTempDir, "backups");
            await settingsService.SaveSettingsAsync(settings);

            var diffService = new ConfigDiffService();
            var diff = await diffService.CompareSingleFileAsync(
                Path.Combine(localApache, "conf", "httpd.conf"),
                Path.Combine(incomingApache, "conf", "httpd.conf"),
                "conf/httpd.conf");
            diff.Resolution = ConfigFileResolution.KeepCurrent;

            var updateService = new ApacheUpdateService(
                settingsService,
                new WindowsServiceManager(),
                new VersionDetectionService(),
                new ArchiveService(),
                diffService);

            var logs = new List<string>();
            var progress = new Progress<UpdateProgressReport>(r => logs.Add(r.Message));

            bool result = await updateService.ExecuteUpdatePipelineAsync(
                incomingApache,
                new List<ConfigDiffItem> { diff },
                isTestMode: true,
                progress: progress);

            Assert.True(result);

            // Verify live files were NOT overwritten (Test Mode protection)
            Assert.Equal("local httpd", await File.ReadAllTextAsync(Path.Combine(localApache, "bin", "httpd.exe")));
            Assert.Contains("Listen 80\n", await File.ReadAllTextAsync(Path.Combine(localApache, "conf", "httpd.conf")));
            Assert.False(File.Exists(Path.Combine(localApache, "modules", "mod_test.so")));

            // Verify workspace was HELD (not deleted)
            Assert.True(Directory.Exists(incomingApache));
            Assert.True(File.Exists(Path.Combine(incomingApache, "bin", "httpd.exe")));

            // Verify simulation logs were generated
            Assert.Contains(logs, m => m.Contains("[SIMULATE COPY]"));
            Assert.Contains(logs, m => m.Contains("[SIMULATE SKIP]"));
            Assert.Contains(logs, m => m.Contains("[TEST MODE]"));
        }

        [Fact]
        public async Task SslCertificateService_DetectsGlobalAndVirtualHostCertificates()
        {
            string apacheRoot = Path.Combine(_testTempDir, "mock_ssl_apache_" + Guid.NewGuid().ToString("N"));
            string confDir = Path.Combine(apacheRoot, "conf");
            string extraDir = Path.Combine(confDir, "extra");
            string sslDir = Path.Combine(confDir, "ssl");

            Directory.CreateDirectory(extraDir);
            Directory.CreateDirectory(sslDir);

            // Create dummy cert files
            string defaultCert = Path.Combine(sslDir, "server.crt");
            string defaultKey = Path.Combine(sslDir, "server.key");
            string vhostCert = Path.Combine(sslDir, "api.crt");
            string vhostKey = Path.Combine(sslDir, "api.key");
            string vhostChain = Path.Combine(sslDir, "api.chain.crt");

            await File.WriteAllTextAsync(defaultCert, "mock cert default");
            await File.WriteAllTextAsync(defaultKey, "mock key default");
            await File.WriteAllTextAsync(vhostCert, "mock cert api");
            await File.WriteAllTextAsync(vhostKey, "mock key api");
            await File.WriteAllTextAsync(vhostChain, "mock chain api");

            // Write httpd.conf with ServerRoot
            await File.WriteAllTextAsync(Path.Combine(confDir, "httpd.conf"), $"ServerRoot \"{apacheRoot.Replace('\\', '/')}\"\n");

            // Write httpd-ssl.conf with global cert
            await File.WriteAllTextAsync(Path.Combine(extraDir, "httpd-ssl.conf"),
                "Listen 443\n" +
                "SSLCertificateFile \"conf/ssl/server.crt\"\n" +
                "SSLCertificateKeyFile \"conf/ssl/server.key\"\n");

            // Write httpd-vhosts.conf with a VirtualHost *:443
            await File.WriteAllTextAsync(Path.Combine(extraDir, "httpd-vhosts.conf"),
                "<VirtualHost *:443>\n" +
                "    ServerName api.mysite.com\n" +
                "    SSLEngine on\n" +
                "    SSLCertificateFile \"conf/ssl/api.crt\"\n" +
                "    SSLCertificateKeyFile \"conf/ssl/api.key\"\n" +
                "    SSLCertificateChainFile \"conf/ssl/api.chain.crt\"\n" +
                "</VirtualHost>\n");

            var sslService = new SslCertificateService();
            var detected = await sslService.DetectCertificatesAsync(apacheRoot);

            Assert.NotNull(detected);
            Assert.Equal(2, detected.Count);

            var defaultEntry = detected.FirstOrDefault(c => c.HostName.Contains("Global") || c.HostName.Contains("Default"));
            Assert.NotNull(defaultEntry);
            Assert.Equal(defaultCert, defaultEntry.CertificateFilePath);
            Assert.Equal(defaultKey, defaultEntry.KeyFilePath);
            Assert.True(defaultEntry.CertificateFileExists);
            Assert.True(defaultEntry.KeyFileExists);

            var vhostEntry = detected.FirstOrDefault(c => c.HostName == "api.mysite.com");
            Assert.NotNull(vhostEntry);
            Assert.Equal(vhostCert, vhostEntry.CertificateFilePath);
            Assert.Equal(vhostKey, vhostEntry.KeyFilePath);
            Assert.Equal(vhostChain, vhostEntry.ChainFilePath);
            Assert.True(vhostEntry.CertificateFileExists);
            Assert.True(vhostEntry.KeyFileExists);
        }

        [Fact]
        public void SslCertificateService_InspectsRealCertificateMetadata()
        {
            string certFolder = Path.Combine(_testTempDir, "mock_cert_inspect_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(certFolder);

            string certPath = Path.Combine(certFolder, "test.crt");

            // Generate real self-signed certificate using System.Security.Cryptography
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=test.example.com",
                rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);

            using var realCert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(90));
            File.WriteAllBytes(certPath, realCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));

            var certInfo = new SslCertificateInfo
            {
                CertificateFilePath = certPath
            };

            var sslService = new SslCertificateService();
            sslService.InspectCertificateFile(certInfo);

            Assert.True(certInfo.CertificateFileExists);
            Assert.Equal("test.example.com", certInfo.CommonName);
            Assert.Equal(SslCertificateStatus.Valid, certInfo.Status);
            Assert.True(certInfo.DaysRemaining > 80 && certInfo.DaysRemaining <= 90);
            Assert.Contains("Valid", certInfo.StatusDisplayText);
        }

        [Fact]
        public void ArchiveService_FindPhpRoot_LocatesNestedPhpExe()
        {
            string baseFolder = Path.Combine(_testTempDir, "extract_php_test_" + Guid.NewGuid().ToString("N"));
            string nestedPhp = Path.Combine(baseFolder, "php-8.3.10", "ext");
            Directory.CreateDirectory(nestedPhp);
            File.WriteAllText(Path.Combine(baseFolder, "php-8.3.10", "php.exe"), "dummy php");

            var archiveService = new ArchiveService();
            string resolvedRoot = archiveService.FindPhpRoot(baseFolder);

            Assert.Equal(Path.Combine(baseFolder, "php-8.3.10"), resolvedRoot);
        }

        [Fact]
        public async Task ConfigDiffService_ComparePhpConfigFiles_DetectsPhpIniAndProduction()
        {
            string localPhp = Path.Combine(_testTempDir, "local_php_" + Guid.NewGuid().ToString("N"));
            string incomingPhp = Path.Combine(_testTempDir, "incoming_php_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(localPhp);
            Directory.CreateDirectory(incomingPhp);

            await File.WriteAllTextAsync(Path.Combine(localPhp, "php.ini"), "memory_limit = 256M\nupload_max_filesize = 50M\n");
            await File.WriteAllTextAsync(Path.Combine(incomingPhp, "php.ini-production"), "memory_limit = 128M\nupload_max_filesize = 2M\n");
            await File.WriteAllTextAsync(Path.Combine(localPhp, "custom.ini"), "xdebug.mode = debug\n");
            await File.WriteAllTextAsync(Path.Combine(incomingPhp, "custom.ini"), "xdebug.mode = develop\n");

            var diffService = new ConfigDiffService();
            var diffs = await diffService.ComparePhpConfigFilesAsync(localPhp, incomingPhp);

            Assert.NotNull(diffs);
            Assert.Equal(2, diffs.Count);

            var phpIniDiff = diffs.FirstOrDefault(d => d.RelativeFilePath == "php.ini");
            Assert.NotNull(phpIniDiff);
            Assert.True(phpIniDiff.HasDifferences);
            Assert.Equal(Path.Combine(incomingPhp, "php.ini-production"), phpIniDiff.IncomingFilePath);

            var customIniDiff = diffs.FirstOrDefault(d => d.RelativeFilePath == "custom.ini");
            Assert.NotNull(customIniDiff);
            Assert.True(customIniDiff.HasDifferences);
        }

        [Fact]
        public async Task PhpUpdateService_TestModeSimulation_ExecutesSafely()
        {
            string localPhp = Path.Combine(_testTempDir, "php_live_" + Guid.NewGuid().ToString("N"));
            string incomingPhp = Path.Combine(_testTempDir, "php_new_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(localPhp);
            Directory.CreateDirectory(incomingPhp);

            await File.WriteAllTextAsync(Path.Combine(localPhp, "php.exe"), "old php binary");
            await File.WriteAllTextAsync(Path.Combine(localPhp, "php.ini"), "memory_limit = 512M\n");
            await File.WriteAllTextAsync(Path.Combine(incomingPhp, "php.exe"), "new php binary");
            await File.WriteAllTextAsync(Path.Combine(incomingPhp, "php.ini"), "memory_limit = 128M\n");

            var diffService = new ConfigDiffService();
            var diffs = await diffService.ComparePhpConfigFilesAsync(localPhp, incomingPhp);
            var primaryDiff = diffs.First();
            primaryDiff.Resolution = ConfigFileResolution.KeepCurrent;

            var settingsService = new SettingsService();
            settingsService.CurrentSettings.Php.InstallationPath = localPhp;
            var serviceManager = new WindowsServiceManager();
            var versionService = new VersionDetectionService();
            var archiveService = new ArchiveService();

            var phpUpdateService = new PhpUpdateService(
                settingsService,
                serviceManager,
                versionService,
                archiveService,
                diffService);

            var logs = new List<string>();
            var progress = new Progress<UpdateProgressReport>(r =>
            {
                if (!string.IsNullOrEmpty(r.Message)) logs.Add(r.Message);
            });

            bool success = await phpUpdateService.ExecuteUpdatePipelineAsync(
                incomingPhp,
                diffs,
                isTestMode: true,
                progress: progress);

            Assert.True(success);
            Assert.Equal("old php binary", await File.ReadAllTextAsync(Path.Combine(localPhp, "php.exe")));
            Assert.Equal("memory_limit = 512M\n", await File.ReadAllTextAsync(Path.Combine(localPhp, "php.ini")));
            Assert.Contains(logs, l => l.Contains("[TEST MODE]"));
        }

        [Fact]
        public async Task VersionDetectionService_DetectPhpBuildInfo_DetectsThreadSafetyAndArchitecture()
        {
            string phpDir = Path.Combine(_testTempDir, "php_build_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(phpDir);

            // Create dummy php.exe with PE header machine = 0x8664 (x64)
            byte[] dummyPe = new byte[512];
            dummyPe[0] = (byte)'M';
            dummyPe[1] = (byte)'Z';
            dummyPe[0x3C] = 0x80; // PE offset at 0x80
            dummyPe[0x80] = (byte)'P';
            dummyPe[0x81] = (byte)'E';
            dummyPe[0x82] = 0;
            dummyPe[0x83] = 0;
            dummyPe[0x84] = 0x64; // 0x8664 (x64 machine type)
            dummyPe[0x85] = 0x86;
            await File.WriteAllBytesAsync(Path.Combine(phpDir, "php.exe"), dummyPe);

            // Add php8ts.dll
            await File.WriteAllTextAsync(Path.Combine(phpDir, "php8ts.dll"), "dummy dll");

            var versionService = new VersionDetectionService();
            var info = await versionService.DetectPhpBuildInfoAsync(phpDir);

            Assert.NotNull(info);
            Assert.Equal("x64", info.Architecture);
            Assert.True(info.IsThreadSafe);
            Assert.Contains("Thread Safe", info.ThreadSafetyDisplay);
            Assert.Contains("64-bit", info.ArchitectureDisplay);
            Assert.True(info.IsApacheCompatible);
        }

        [Fact]
        public void PhpWizardViewModel_VersionTransitionDisplay_PreservesBaselineVersion()
        {
            var vm = new PhpWizardViewModel(
                new PhpUpdateService(
                    new SettingsService(Path.Combine(_testTempDir, "cfg_php.json")),
                    new WindowsServiceManager(),
                    new VersionDetectionService(),
                    new ArchiveService(),
                    new ConfigDiffService()),
                new SettingsService(Path.Combine(_testTempDir, "cfg_php2.json")),
                new VersionDetectionService());

            vm.BaselineInstalledBuild = new PhpBuildInfo { Version = "8.3.30" };
            vm.IncomingPhpBuild = new PhpBuildInfo { Version = "8.3.33" };

            Assert.Equal("Update PHP from v8.3.30 → v8.3.33", vm.VersionTransitionDisplay);
        }
    }
}
