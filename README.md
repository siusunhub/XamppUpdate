# XAMPP Component Updater (XamppUpdate)

[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)](https://microsoft.com/windows)
[![Target Framework](https://img.shields.io/badge/.NET-10.0%20WPF-purple.svg)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-0.5.0-green.svg)](https://github.com/)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

**XAMPP Component Updater** is a modern, reliable Windows desktop utility designed for developers and system administrators to independently upgrade, manage, and maintain individual core components of an existing [XAMPP](https://www.apachefriends.org/) installation.

Instead of performing disruptive full-suite reinstalls and risking database or vhost corruption, **XamppUpdate** allows you to update **Apache**, **PHP**, **MySQL / MariaDB**, and **phpMyAdmin** in-place—automatically preserving your configuration files, backing up existing installations, offering side-by-side config diffs, and managing Windows services gracefully.

---

## 🚀 Key Features

- **🌐 Apache HTTP Server Update Wizard**
  - Extract and apply new Apache builds (e.g. from [Apache Lounge](https://www.apachelounge.com/download/)).
  - Automatically identifies and preserves user configuration files (`httpd.conf`, `httpd-ssl.conf`, `httpd-xampp.conf`, virtual hosts, etc.).
  - Built-in visual configuration diff viewer (powered by DiffPlex) to review and merge config changes before applying.
  - Controls the Apache Windows service (`apache2.4`) automatically with safe start/stop verification.

- **🔀 Apache Virtual Host Manager (VHost Manager)**
  - Comprehensive GUI to inspect, add, edit, and toggle virtual hosts in `conf/extra/httpd-vhosts.conf` or included configs.
  - Configure `ServerName`, `ServerAlias`, `DocumentRoot`, custom port/IP bindings (`:80`, `:443`, custom ports), and directory access directives.
  - SSL/TLS configuration management (`SSLEngine`, `SSLCertificateFile`, `SSLCertificateKeyFile`, `SSLCACertificateFile`).
  - Automated Let's Encrypt / ACME support via Apache `mod_md` (contact email, Terms of Service agreement, ACME directory configuration).
  - Quick config presets (`vhosts`, `ssl`, `httpd.conf`), external Notepad editor launch, and Windows `hosts` file helper.

- **🐘 PHP Runtime Engine Update Wizard**
  - Upgrade PHP to any target version (Thread Safe x64 builds from [windows.php.net](https://windows.php.net/download/)).
  - Preserves your existing `php.ini` directives and custom extension modules (`ext/`).
  - Automatically updates Apache's `httpd-xampp.conf` to bind the new PHP module (`php8_module` / `LoadFile` directives) without manual editing.
  - Side-by-side visual diff comparison for `php.ini`.

- **🐬 MySQL / MariaDB Database Server Update Wizard**
  - In-place binary engine updates while strictly safeguarding the `data/` directory and `my.ini`.
  - Service management with graceful stop/start and health validation.
  - Safe folder rotation to timestamped backups (`mysql_old_{timestamp}`) retained on disk with exponential backoff retry handling.
  - Detects and triggers post-upgrade maintenance scripts (`mysql_upgrade` / MariaDB upgrade utilities).
  - Visual side-by-side configuration diff viewer for `my.ini`.

- **🧪 MySQL "Real Test Upgrade" (UAT Rehearsal Copy)**
  - Rehearse upgrades on a dedicated staging copy running in parallel on a dedicated port (default `13306`), completely isolated from production data.
  - **8-Stage Verification Pipeline**:
    1. *Preflight*: Validates target paths, port availability, disk space, and admin privileges without touching production.
    2. *Snapshot Copy*: Verified safe cold copy (brief production stop) or experimental live copy.
    3. *Config Rewriting*: Confines all data and log paths to the copy, binds to `127.0.0.1`, and disables replication and scheduled events.
    4. *Old Binaries Verification*: Runs the copy with existing binaries to confirm clean flush and shutdown.
    5. *Binary Deployment*: Deploys target binaries and migrates copied data into the staging target.
    6. *New Daemon Verification*: Launches the new version standalone or via a temporary `mysql_uat` Windows service.
    7. *Full Upgrade Execution*: Executes `mariadb-upgrade` / upgrade tools and verifies system tables and InnoDB health.
    8. *Cleanup & Reporting*: Stops the test daemon, removes temporary services, restores production, and writes an auditable execution report.
  - Error log evidence scanner checking for corrupt InnoDB tables, unrecognized directives, or fatal errors with sensitive credentials automatically redacted.

- **📑 phpMyAdmin Web Interface Wizard**
  - Seamlessly updates phpMyAdmin from official distribution archives.
  - Automatically preserves `config.inc.php` authentication keys, saved server profiles, and custom settings.
  - Visual side-by-side diff comparison for configuration changes.

- **🔒 SSL / TLS Certificate Manager**
  - Inspect installed certificates (`ssl.crt` and `ssl.key`) with expiration alerts and Subject Alternative Name (SAN) inspection.
  - 1-click generation of self-signed SSL/TLS certificates (RSA 2048/4096-bit) for `localhost` and local domains with proper SAN extensions.
  - Safe certificate import and replacement with automated backups.

- **📦 Composer Dependency Manager**
  - Real-time Composer executable version detection and health status.
  - Run `composer self-update` directly from the dashboard.
  - Inspect outdated packages (`composer outdated --all`) with intelligent log scanning and real-time **"Update Available"** status badge.
  - Trigger package updates (`composer update` / `--with-all-dependencies`).
  - Automated timestamped `vendor/` folder backups (`vendor_old_{timestamp}`) permanently preserved before deployment.
  - Deploy and sync packages to web targets (`htdocs`) with **Test Mode (Dry Run)** safety validation.

- **🔍 Visual Side-by-Side Configuration Diff Viewer**
  - Powered by DiffPlex, integrated into all component wizards.
  - Dual-pane side-by-side and unified diff views with synchronized scrolling, line numbers, change markers (added, deleted, modified), and direct merge editing before applying.

- **🔤 File Encoding & Character Set Safety**
  - Automatic encoding detection (`FileEncodingHelper`) supporting UTF-8, UTF-8 with BOM, ANSI / Windows-1252, and UTF-16 LE/BE.
  - Guarantees that configuration files retain their original encoding and line endings across edits, diff merges, and automated migrations without introducing character corruption or stripping byte order marks.

- **🛡️ Enterprise-Grade Safety & Reliability**
  - **Automated Backups**: Backs up target directories to timestamped archives before touching files.
  - **Retention Control**: Configurable maximum backup count to prevent disk bloat.
  - **Automatic Rollback**: Restores original state if extraction or service verification fails.
  - **Test Mode**: Simulate update operations and file transfers without modifying production files.

---

## 📊 Component Matrix

| Component | In-Place Upgrade | Config Retention | Service Management | Diff Inspection |
| :--- | :---: | :---: | :---: | :---: |
| **Apache HTTP Server** | ✅ | ✅ (`httpd*.conf`) | ✅ (`apache2.4`) | ✅ Side-by-side |
| **Apache Virtual Hosts** | ✅ (GUI Manager) | ✅ (`httpd-vhosts.conf`) | ✅ Reload Apache | ✅ Live Editor / Diff |
| **PHP Engine** | ✅ | ✅ (`php.ini`, `ext/`) | ✅ Apache restart | ✅ Side-by-side |
| **MySQL / MariaDB** | ✅ | ✅ (`my.ini`, `data/`) | ✅ (`mysql`) | ✅ Side-by-side |
| **MySQL UAT Rehearsal** | ✅ (Parallel Staging) | ✅ Isolated copy | ✅ (`mysql_uat`) | ✅ Report & Diffs |
| **phpMyAdmin** | ✅ | ✅ (`config.inc.php`)| N/A (Web app) | ✅ Side-by-side |
| **SSL Certificates** | ✅ (Generate/Import)| ✅ (Auto backup) | ✅ Reload Apache | ❌ (Cert Inspector) |
| **Composer** | ✅ (Self-Update) | ✅ (`vendor/` backup)| N/A (CLI tool) | ❌ (CLI Log) |

---

## 📋 System Requirements

- **Operating System**: Windows 10, Windows 11, or Windows Server 2016+ (64-bit).
- **Runtime**: [.NET 10.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) *(not required if using a self-contained release)*.
- **XAMPP**: Existing XAMPP installation (typically at `C:\xampp`).
- **Permissions**: Administrator privileges (required to control Windows Services and write to `C:\xampp`).

---

## ⚙️ Configuration (`XamppUpdate.config.json`)

The application loads its paths and service definitions from `XamppUpdate.config.json` located alongside the executable. Default configuration:

```json
{
  "General": {
    "BackupDirectory": "updatebackup",
    "TempDirectory": "updatetemp",
    "MaxBackupRetentionCount": 10,
    "AutoCheckUpdatesOnStartup": false
  },
  "Apache": {
    "InstallationPath": "C:\\xampp\\apache",
    "ServiceName": "apache2.4",
    "ConfigFiles": [
      "conf/httpd.conf",
      "conf/extra/httpd-ssl.conf",
      "conf/extra/httpd-xampp.conf"
    ]
  },
  "Php": {
    "InstallationPath": "C:\\xampp\\php",
    "IniPath": "C:\\xampp\\php\\php.ini"
  },
  "MySql": {
    "InstallationPath": "C:\\xampp\\mysql",
    "ServiceName": "mysql",
    "IniPath": "C:\\xampp\\mysql\\bin\\my.ini"
  },
  "Composer": {
    "ExecutablePath": "C:\\ProgramData\\ComposerSetup\\bin\\composer.bat",
    "DownloadTargetPath": "C:\\xampp\\composer_downloads",
    "WwwTargetPath": "C:\\xampp\\htdocs\\composer"
  },
  "Ssl": {
    "CertificatesPath": "C:\\xampp\\apache\\conf\\ssl.crt",
    "KeysPath": "C:\\xampp\\apache\\conf\\ssl.key"
  },
  "PhpMyAdmin": {
    "InstallationPath": "C:\\xampp\\phpMyAdmin",
    "ConfigFile": "config.inc.php"
  }
}
```

You can customize these paths either directly in `XamppUpdate.config.json` or via the **Settings** dialog in the application.

---

## 📖 How to Use

### 1. Launch the Application
Run `XamppUpdate.exe` as **Administrator**. The main dashboard immediately polls and displays the status and detected version of Apache, PHP, MySQL, phpMyAdmin, SSL certificates, and Composer.

### 2. Upgrading PHP
1. Download the latest **Thread Safe (TS) x64 ZIP** package from [windows.php.net](https://windows.php.net/download/).
2. On the XamppUpdate dashboard, click **Update** on the **PHP** card.
3. Select your downloaded `.zip` file or provide a direct download URL.
4. Review the detected PHP version and compare your current `php.ini` against the new release in the **Config Diff** tab using the side-by-side diff viewer.
5. Click **Apply Update**. The wizard will:
   - Stop Apache.
   - Create a timestamped backup of your current PHP directory.
   - Extract the new PHP engine.
   - Migrate custom extensions and your `php.ini`.
   - Update `LoadFile` and `LoadModule` in `httpd-xampp.conf` to reference the new PHP module.
   - Restart Apache and verify health.

### 3. Upgrading Apache
1. Download a compatible Apache x64 release (e.g. from [Apache Lounge](https://www.apachelounge.com/download/)).
2. Click **Update** on the **Apache** card.
3. Review config diffs for `httpd.conf` and `httpd-ssl.conf` in the side-by-side diff viewer.
4. Run in **Test Mode** first (optional) or click **Execute Update**.

### 4. Managing Apache Virtual Hosts (VHost Manager)
1. Within the Apache wizard or via configuration tools, launch the **Virtual Host Manager**.
2. View all defined virtual hosts, their status (enabled/disabled), port bindings, and SSL flags.
3. Add or edit virtual hosts: specify `ServerName`, `ServerAlias`, `DocumentRoot`, directory permissions, and custom directives.
4. Configure SSL/TLS bindings or configure **Let's Encrypt / ACME (`mod_md`)** global and host-level settings.
5. Save changes directly to `httpd-vhosts.conf` with automatic file backup and encoding preservation.

### 5. Upgrading MySQL / MariaDB (Production In-Place)
1. Download the new MySQL or MariaDB archive.
2. Select the archive in the **MySQL Update Wizard**.
3. Verify that the wizard has identified the existing `data/` directory.
4. Review changes to `my.ini` in the side-by-side diff viewer.
5. Click **Proceed**. The wizard stops the database service, creates a full backup, updates the server binaries, re-attaches the existing `data/` folder, starts the service, and provides an option to run `mysql_upgrade`.

### 6. Testing MySQL Upgrades in UAT Staging ("Real Test Upgrade")
1. On step 1 of the MySQL Wizard, click **Real Test Upgrade**.
2. Configure UAT options: staging destination path, isolated TCP port (default `13306`), temporary service option (`mysql_uat`), and database admin credentials.
3. Click **Run**. The automated 8-stage pipeline creates a snapshot of production, rewrites configuration for strict path/port isolation, tests old binary startup and clean flush, deploys target binaries, starts the test daemon, runs `mariadb-upgrade`, and verifies InnoDB table integrity.
4. Review the comprehensive execution log and sanitized summary report before touching production.

### 7. Managing SSL Certificates
1. Click **Manage SSL** on the **SSL / TLS Certificates** card.
2. View existing certificates, expiration status, and SAN domains.
3. Click **Generate Default Certificate** to instantly create a new 2048-bit or 4096-bit self-signed certificate with Subject Alternative Names for `localhost`, `127.0.0.1`, and custom hostnames.
4. Or click **Import Certificate** to install existing `.crt` / `.key` pairs into Apache.

### 8. Managing Composer
1. Click **Manage Composer** on the dashboard.
2. Check the current Composer version, executable path, download directory, and target web directory.
3. Click **Self Update** to run `composer self-update`.
4. Click **Check Outdated** to scan for newer package dependencies.
5. Use **Update Packages** and **Deploy to WWW** to synchronize dependencies into `htdocs` with automated `vendor` backups.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10/11 x64
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 / Rider / VS Code with C# Dev Kit

### Build Commands

Clone the repository:
```bash
git clone https://github.com/your-username/XamppUpdate.git
cd XamppUpdate
```

Build the solution in **Release** mode:
```powershell
dotnet build -c Release
```

Run tests:
```powershell
dotnet test -c Release
```

Publish single-file executable to the `public/` directory:
```powershell
dotnet publish XamppUpdate\XamppUpdate.csproj -c Release
```
The compiled executable will be placed in `public/XamppUpdate.exe`.

---

## 📁 Project Architecture

```
XamppUpdate/
├── XamppUpdate/
│   ├── App.xaml / App.xaml.cs       # Application entry point & DI setup
│   ├── AppInfo.cs                   # Metadata & build versioning
│   ├── XamppUpdate.config.json      # Component path & service definitions
│   ├── Converters/                  # WPF XAML value converters
│   ├── Models/                      # Data structures & update state models
│   ├── Services/                    # Core business logic:
│   │   ├── ApacheUpdateService.cs   # Apache backup, extraction, config migration
│   │   ├── VHostService.cs          # Virtual host parsing, editing, Let's Encrypt mod_md
│   │   ├── PhpUpdateService.cs      # PHP update & Apache integration
│   │   ├── MySqlUpdateService.cs    # MySQL/MariaDB update & data isolation
│   │   ├── Uat/                     # MySQL UAT copy rehearsal pipeline:
│   │   │   ├── UatUpgradeService.cs # 8-stage upgrade rehearsal engine
│   │   │   ├── UatPreflight.cs      # Disk, path, port & privilege validation
│   │   │   ├── UatIniRewriter.cs    # Configuration rewriting & path isolation
│   │   │   ├── MySqlToolRunner.cs   # Standalone daemon & mariadb-upgrade execution
│   │   │   └── ErrorLogEvidence.cs  # Log scanner & evidence extraction
│   │   ├── PhpMyAdminUpdateService.cs # phpMyAdmin update pipeline
│   │   ├── SslCertificateService.cs # SSL inspection & X509 generator
│   │   ├── ComposerService.cs       # Composer CLI execution & backup
│   │   ├── ConfigDiffService.cs     # DiffPlex text diff comparison
│   │   ├── FileEncodingHelper.cs    # UTF-8/BOM/ANSI encoding detection & safety
│   │   ├── ArchiveService.cs        # SharpCompress archive extraction
│   │   └── WindowsServiceManager.cs # ServiceController start/stop/status
│   ├── Styles/                      # Theme brushes, controls & typography
│   ├── ViewModels/                  # CommunityToolkit.Mvvm ViewModels:
│   │   ├── MainViewModel.cs         # Dashboard state & component monitoring
│   │   ├── ApacheWizardViewModel.cs # Apache update flow
│   │   ├── VHostManagerViewModel.cs # Virtual host & mod_md management
│   │   ├── PhpWizardViewModel.cs    # PHP runtime update
│   │   ├── MySqlWizardViewModel.cs  # MySQL/MariaDB in-place update
│   │   ├── UatUpgradeViewModel.cs   # UAT staging upgrade execution
│   │   ├── PhpMyAdminWizardViewModel.cs # phpMyAdmin update
│   │   ├── SslManagerViewModel.cs   # SSL certificate viewer & generator
│   │   ├── ComposerViewModel.cs     # Composer package & self-update
│   │   └── SettingsViewModel.cs     # Configuration editor
│   └── Views/                       # WPF XAML windows & custom controls:
│       ├── MainWindow.xaml          # Main dashboard
│       ├── ApacheUpdateWizardWindow.xaml
│       ├── VHostManagerWindow.xaml  # Virtual Host management interface
│       ├── PhpUpdateWizardWindow.xaml
│       ├── MySqlUpdateWizardWindow.xaml
│       ├── UatUpgradeWindow.xaml    # Real Test Upgrade execution & log viewer
│       ├── PhpMyAdminUpdateWizardWindow.xaml
│       ├── SslManagerWindow.xaml
│       ├── ComposerWindow.xaml
│       ├── SettingsWindow.xaml
│       └── Controls/
│           ├── SideBySideDiffViewer.xaml # Dual-pane DiffPlex diff viewer
│           └── StatusBadge.xaml     # Service health indicator
├── docs/                            # Design specifications and implementation plans
└── public/                          # Published binaries & default distribution files
```

---

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!
1. Fork the project.
2. Create your feature branch (`git checkout -b feature/AmazingFeature`).
3. Commit your changes (`git commit -m "feat: add AmazingFeature"`).
4. Push to the branch (`git push origin feature/AmazingFeature`).
5. Open a Pull Request.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
