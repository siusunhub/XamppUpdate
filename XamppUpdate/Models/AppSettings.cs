using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace XamppUpdate.Models
{
    public class AppSettings
    {
        public GeneralSettings General { get; set; } = new();
        public ApacheSettings Apache { get; set; } = new();
        public PhpSettings Php { get; set; } = new();
        public MySqlSettings MySql { get; set; } = new();
        public ComposerSettings Composer { get; set; } = new();
        public SslSettings Ssl { get; set; } = new();
        public PhpMyAdminSettings PhpMyAdmin { get; set; } = new();
    }

    public class GeneralSettings
    {
        public string BackupDirectory { get; set; } = "updatebackup";
        public string TempDirectory { get; set; } = "updatetemp";
        public int MaxBackupRetentionCount { get; set; } = 10;
        public bool AutoCheckUpdatesOnStartup { get; set; } = false;

        [JsonIgnore]
        public string ResolvedBackupDirectory => Path.IsPathRooted(BackupDirectory)
            ? BackupDirectory
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BackupDirectory);

        [JsonIgnore]
        public string ResolvedTempDirectory => Path.IsPathRooted(TempDirectory)
            ? TempDirectory
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, TempDirectory);
    }

    public class ApacheSettings
    {
        public string InstallationPath { get; set; } = @"C:\xampp\apache";
        public string ServiceName { get; set; } = "apache2.4";
        public List<string> ConfigFiles { get; set; } = new()
        {
            "conf/httpd.conf",
            "conf/extra/httpd-ssl.conf",
            "conf/extra/httpd-xampp.conf"
        };

        [JsonIgnore]
        public string ExecutablePath => Path.Combine(InstallationPath, "bin", "httpd.exe");

        [JsonIgnore]
        public string ModulesPath => Path.Combine(InstallationPath, "modules");

        [JsonIgnore]
        public string ConfPath => Path.Combine(InstallationPath, "conf");
    }

    public class PhpSettings
    {
        public string InstallationPath { get; set; } = @"C:\xampp\php";
        public string IniPath { get; set; } = @"C:\xampp\php\php.ini";

        [JsonIgnore]
        public string ExecutablePath => Path.Combine(InstallationPath, "php.exe");
    }

    public class MySqlSettings
    {
        public string InstallationPath { get; set; } = @"C:\xampp\mysql";
        public string ServiceName { get; set; } = "mysql";
        public string IniPath { get; set; } = @"C:\xampp\mysql\bin\my.ini";

        [JsonIgnore]
        public string ExecutablePath => Path.Combine(InstallationPath, "bin", "mysqld.exe");
    }

    public class ComposerSettings
    {
        public string ExecutablePath { get; set; } = @"C:\ProgramData\ComposerSetup\bin\composer.bat";
        public string DownloadTargetPath { get; set; } = @"C:\xampp\composer_downloads";
        public string WwwTargetPath { get; set; } = @"C:\xampp\htdocs\composer";

        // Compatibility fallback for older configuration files
        [JsonPropertyName("WebTargetPath")]
        public string? LegacyWebTargetPath
        {
            get => null;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(WwwTargetPath))
                {
                    WwwTargetPath = value;
                }
            }
        }
    }

    public class SslSettings
    {
        public string CertificatesPath { get; set; } = @"C:\xampp\apache\conf\ssl.crt";
        public string KeysPath { get; set; } = @"C:\xampp\apache\conf\ssl.key";
    }

    public class PhpMyAdminSettings
    {
        public string InstallationPath { get; set; } = @"C:\xampp\phpMyAdmin";
        public string ConfigFile { get; set; } = "config.inc.php";
    }
}
