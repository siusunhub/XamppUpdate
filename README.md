# XAMPP Component Updater (XamppUpdate)

[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)](https://microsoft.com/windows)
[![Target Framework](https://img.shields.io/badge/.NET-10.0%20WPF-purple.svg)](https://dotnet.microsoft.com/)
[![Version](https://img.shields.io/badge/Version-0.2.0-green.svg)](https://github.com/)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

**XAMPP Component Updater** is a modern, reliable Windows desktop utility designed for developers and system administrators to independently upgrade, manage, and maintain individual core components of an existing [XAMPP](https://www.apachefriends.org/) installation.

Instead of performing disruptive full-suite reinstalls and risking database or vhost corruption, **XamppUpdate** allows you to update **Apache**, **PHP**, **MySQL / MariaDB**, and **phpMyAdmin** in-place—automatically preserving your configuration files, backing up existing installations, offering side-by-side config diffs, and managing Windows services gracefully.

---

## 🚀 Key Features

- **🌐 Apache HTTP Server Update Wizard**
  - Extract and apply new Apache builds (e.g. from [Apache Lounge](https://www.apachelounge.com/download/)).
  - Automatically identifies and preserves user configuration files (`httpd.conf`, `httpd-ssl.conf`, `httpd-xampp.conf`, virtual hosts, etc.).
  - Built-in visual configuration diff viewer (powered by DiffPlex) to review and merge config changes before applying.
  - Controls the Apache Windows service (`apache2.4`) automatically.

- **🐘 PHP Runtime Engine Update Wizard**
  - Upgrade PHP to any target version (Thread Safe x64 builds from [windows.php.net](https://windows.php.net/download/)).
  - Preserves your existing `php.ini` directives and custom extension modules (`ext/`).
  - Automatically updates Apache's `httpd-xampp.conf` to bind the new PHP module (`php8_module` / `LoadFile` directives) without manual editing.

- **🐬 MySQL / MariaDB Database Server Update Wizard**
  - In-place binary engine updates while strictly safeguarding the `data/` directory and `my.ini`.
  - Service management with graceful stop/start and health validation.
  - Detects and triggers post-upgrade maintenance scripts (`mysql_upgrade` / MariaDB upgrade utilities).

- **📑 phpMyAdmin Web Interface Wizard**
  - Seamlessly updates phpMyAdmin from official distribution archives.
  - Automatically preserves `config.inc.php` authentication keys, saved server profiles, and custom settings.

- **🔒 SSL / TLS Certificate Manager**
  - Inspect installed certificates (`ssl.crt` and `ssl.key`) with expiration alerts and Subject Alternative Name (SAN) inspection.
  - 1-click generation of self-signed SSL/TLS certificates for `localhost` and local domains with proper SAN extensions.
  - Safe certificate import and replacement with automated backups.

- **📦 Composer Dependency Manager**
  - Real-time Composer executable version detection and health status.
  - Run `composer self-update` directly from the dashboard.
  - Inspect outdated packages (`composer outdated`) in configured projects.
  - Trigger package updates (`composer update` / `--with-all-dependencies`).
  - Automated timestamped `vendor/` folder backups before destructive operations.
  - Deploy and sync packages to web targets (`htdocs`) with **Test Mode (Dry Run)** safety validation.

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
| **PHP Engine** | ✅ | ✅ (`php.ini`, `ext/`) | ✅ Apache restart | ✅ Side-by-side |
| **MySQL / MariaDB** | ✅ | ✅ (`my.ini`, `data/`) | ✅ (`mysql`) | ✅ Side-by-side |
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
4. Review the detected PHP version and compare your current `php.ini` against the new release in the **Config Diff** tab.
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
3. Review config diffs for `httpd.conf` and `httpd-ssl.conf`.
4. Run in **Test Mode** first (optional) or click **Execute Update**.

### 4. Upgrading MySQL / MariaDB
1. Download the new MySQL or MariaDB archive.
2. Select the archive in the **MySQL Update Wizard**.
3. Verify that the wizard has identified the existing `data/` directory.
4. Click **Proceed**. The wizard stops the database service, creates a full backup, updates the server binaries, re-attaches the existing `data/` folder, starts the service, and provides an option to run `mysql_upgrade`.

### 5. Managing SSL Certificates
1. Click **Manage SSL** on the **SSL / TLS Certificates** card.
2. View existing certificates, expiration status, and SAN domains.
3. Click **Generate Default Certificate** to instantly create a new 2048-bit or 4096-bit self-signed certificate with Subject Alternative Names for `localhost`, `127.0.0.1`, and custom hostnames.
4. Or click **Import Certificate** to install existing `.crt` / `.key` pairs into Apache.

### 6. Managing Composer
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
│   ├── Models/                      # Data structures & update state models
│   ├── Services/                    # Core business logic:
│   │   ├── ApacheUpdateService.cs   # Apache backup, extraction, config migration
│   │   ├── PhpUpdateService.cs      # PHP update & Apache integration
│   │   ├── MySqlUpdateService.cs    # MySQL/MariaDB update & data isolation
│   │   ├── PhpMyAdminUpdateService.cs # phpMyAdmin update pipeline
│   │   ├── SslCertificateService.cs # SSL inspection & X509 generator
│   │   ├── ComposerService.cs       # Composer CLI execution & backup
│   │   ├── ConfigDiffService.cs     # DiffPlex text diff comparison
│   │   ├── ArchiveService.cs        # SharpCompress archive extraction
│   │   └── WindowsServiceManager.cs # ServiceController start/stop/status
│   ├── ViewModels/                  # CommunityToolkit.Mvvm ViewModels
│   └── Views/                       # WPF XAML windows, wizards & diff viewers
├── XamppUpdate.Tests/               # Comprehensive unit and integration test suite
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
