using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class SslCertificateService : ISslCertificateService
    {
        private static readonly Regex VirtualHostStartRegex = new(@"<VirtualHost\s+([^>]+)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VirtualHostEndRegex = new(@"</VirtualHost>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ServerNameRegex = new(@"^\s*ServerName\s+([^\s#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SslCertFileRegex = new(@"^\s*SSLCertificateFile\s+""?([^""#\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SslKeyFileRegex = new(@"^\s*SSLCertificateKeyFile\s+""?([^""#\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SslChainFileRegex = new(@"^\s*SSLCertificateChainFile\s+""?([^""#\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ServerRootRegex = new(@"^\s*ServerRoot\s+""?([^""#\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<List<SslCertificateInfo>> DetectCertificatesAsync(string apacheInstallationPath)
        {
            return await Task.Run(async () =>
            {
                var certificates = new List<SslCertificateInfo>();
                if (string.IsNullOrWhiteSpace(apacheInstallationPath) || !Directory.Exists(apacheInstallationPath))
                {
                    return certificates;
                }

                string serverRoot = apacheInstallationPath;
                string confDir = Path.Combine(apacheInstallationPath, "conf");
                if (!Directory.Exists(confDir))
                {
                    return certificates;
                }

                // Check httpd.conf for ServerRoot
                string httpdConfPath = Path.Combine(confDir, "httpd.conf");
                if (File.Exists(httpdConfPath))
                {
                    string? detectedRoot = await DetectServerRootAsync(httpdConfPath);
                    if (!string.IsNullOrWhiteSpace(detectedRoot) && Directory.Exists(detectedRoot))
                    {
                        serverRoot = detectedRoot;
                    }
                }

                // Collect all candidate .conf files in conf/ and conf/extra/
                var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void AddIfExists(string relativePath)
                {
                    string full = Path.Combine(apacheInstallationPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) candidateFiles.Add(full);
                }

                AddIfExists("conf/httpd.conf");
                AddIfExists("conf/extra/httpd-ssl.conf");
                AddIfExists("conf/extra/httpd-vhosts.conf");

                // Also scan conf directory for any .conf files
                try
                {
                    foreach (var file in Directory.EnumerateFiles(confDir, "*.conf", SearchOption.AllDirectories))
                    {
                        candidateFiles.Add(file);
                    }
                }
                catch { }

                // Parse each config file for SSL directives
                foreach (var confFile in candidateFiles)
                {
                    var fileCerts = await ParseConfigFileForSslAsync(confFile, serverRoot, apacheInstallationPath);
                    certificates.AddRange(fileCerts);
                }

                // De-duplicate certificates by normalized CertificateFilePath and HostName
                var uniqueCerts = certificates
                    .GroupBy(c => (c.CertificateFilePath.ToLowerInvariant(), c.HostName.ToLowerInvariant()))
                    .Select(g => g.First())
                    .ToList();

                // Inspect and load certificate metadata from disk
                foreach (var cert in uniqueCerts)
                {
                    InspectCertificateFile(cert);
                }

                return uniqueCerts;
            });
        }

        public async Task<string?> DetectServerRootAsync(string httpdConfPath)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(httpdConfPath);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('#')) continue;

                    var match = ServerRootRegex.Match(trimmed);
                    if (match.Success)
                    {
                        string path = match.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar);
                        return path;
                    }
                }
            }
            catch { }

            return null;
        }

        public async Task<List<SslCertificateInfo>> ParseConfigFileForSslAsync(string confFilePath, string serverRoot, string apacheRoot)
        {
            var results = new List<SslCertificateInfo>();
            if (!File.Exists(confFilePath)) return results;

            string relativeConfPath = Path.GetRelativePath(apacheRoot, confFilePath).Replace('\\', '/');

            try
            {
                var lines = await File.ReadAllLinesAsync(confFilePath);

                bool insideVirtualHost = false;
                string vhostSpec = string.Empty;
                string? vhostServerName = null;
                string? currentCertFile = null;
                string? currentKeyFile = null;
                string? currentChainFile = null;

                void CommitCurrentVirtualHost()
                {
                    if (!string.IsNullOrEmpty(currentCertFile))
                    {
                        string resolvedCert = ResolvePath(currentCertFile, serverRoot);
                        string resolvedKey = !string.IsNullOrEmpty(currentKeyFile) ? ResolvePath(currentKeyFile, serverRoot) : string.Empty;
                        string resolvedChain = !string.IsNullOrEmpty(currentChainFile) ? ResolvePath(currentChainFile, serverRoot) : string.Empty;

                        string host = !string.IsNullOrWhiteSpace(vhostServerName)
                            ? vhostServerName
                            : (insideVirtualHost ? $"VirtualHost ({vhostSpec})" : "Global Server / Default");

                        string port = vhostSpec.Contains(':') ? vhostSpec.Split(':').Last().TrimEnd('>') : "443";

                        results.Add(new SslCertificateInfo
                        {
                            HostName = host,
                            Port = port,
                            CertificateFilePath = resolvedCert,
                            KeyFilePath = resolvedKey,
                            ChainFilePath = resolvedChain,
                            ConfigSourceFile = relativeConfPath
                        });
                    }

                    currentCertFile = null;
                    currentKeyFile = null;
                    currentChainFile = null;
                    vhostServerName = null;
                }

                foreach (var rawLine in lines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;

                    var vhostStartMatch = VirtualHostStartRegex.Match(line);
                    if (vhostStartMatch.Success)
                    {
                        // Commit any pending global cert before entering vhost
                        if (!insideVirtualHost && !string.IsNullOrEmpty(currentCertFile))
                        {
                            CommitCurrentVirtualHost();
                        }

                        insideVirtualHost = true;
                        vhostSpec = vhostStartMatch.Groups[1].Value.Trim();
                        vhostServerName = null;
                        currentCertFile = null;
                        currentKeyFile = null;
                        currentChainFile = null;
                        continue;
                    }

                    if (VirtualHostEndRegex.IsMatch(line))
                    {
                        CommitCurrentVirtualHost();
                        insideVirtualHost = false;
                        vhostSpec = string.Empty;
                        continue;
                    }

                    if (insideVirtualHost)
                    {
                        var serverNameMatch = ServerNameRegex.Match(line);
                        if (serverNameMatch.Success)
                        {
                            vhostServerName = serverNameMatch.Groups[1].Value.Trim();
                        }
                    }

                    var certMatch = SslCertFileRegex.Match(line);
                    if (certMatch.Success)
                    {
                        currentCertFile = certMatch.Groups[1].Value.Trim();
                        continue;
                    }

                    var keyMatch = SslKeyFileRegex.Match(line);
                    if (keyMatch.Success)
                    {
                        currentKeyFile = keyMatch.Groups[1].Value.Trim();
                        continue;
                    }

                    var chainMatch = SslChainFileRegex.Match(line);
                    if (chainMatch.Success)
                    {
                        currentChainFile = chainMatch.Groups[1].Value.Trim();
                        continue;
                    }
                }

                // Final commit if cert was found globally outside of any VirtualHost
                if (!string.IsNullOrEmpty(currentCertFile))
                {
                    CommitCurrentVirtualHost();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to parse {confFilePath} for SSL: {ex.Message}");
            }

            return results;
        }

        private static string ResolvePath(string path, string serverRoot)
        {
            path = path.Trim().Trim('"', '\'');
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            string combined = Path.Combine(serverRoot, path.Replace('/', Path.DirectorySeparatorChar));
            return Path.GetFullPath(combined);
        }

        public void InspectCertificateFile(SslCertificateInfo certInfo)
        {
            certInfo.CertificateFileExists = File.Exists(certInfo.CertificateFilePath);
            certInfo.KeyFileExists = !string.IsNullOrEmpty(certInfo.KeyFilePath) && File.Exists(certInfo.KeyFilePath);
            certInfo.ChainFileExists = !string.IsNullOrEmpty(certInfo.ChainFilePath) && File.Exists(certInfo.ChainFilePath);

            if (!certInfo.CertificateFileExists)
            {
                certInfo.Status = SslCertificateStatus.FileNotFound;
                certInfo.ErrorMessage = "Certificate file does not exist at specified path.";
                return;
            }

            try
            {
                // Load certificate using modern X509Certificate2
                using var cert = LoadCertificate(certInfo.CertificateFilePath);
                if (cert == null)
                {
                    certInfo.Status = SslCertificateStatus.ParseError;
                    certInfo.ErrorMessage = "Failed to decode certificate format (supported: PEM, CRT, CER, DER).";
                    return;
                }

                certInfo.Subject = cert.Subject;
                certInfo.CommonName = ExtractCommonName(cert.Subject);
                certInfo.Issuer = ExtractCommonName(cert.Issuer);
                certInfo.ValidFrom = cert.NotBefore;
                certInfo.ValidTo = cert.NotAfter;

                int days = (int)Math.Floor((cert.NotAfter - DateTime.Now).TotalDays);
                certInfo.DaysRemaining = days;

                if (days < 0)
                {
                    certInfo.Status = SslCertificateStatus.Expired;
                }
                else if (days <= 30)
                {
                    certInfo.Status = SslCertificateStatus.ExpiringSoon;
                }
                else
                {
                    certInfo.Status = SslCertificateStatus.Valid;
                }
            }
            catch (Exception ex)
            {
                certInfo.Status = SslCertificateStatus.ParseError;
                certInfo.ErrorMessage = ex.Message;
            }
        }

        private static X509Certificate2? LoadCertificate(string certPath)
        {
            try
            {
                return X509CertificateLoader.LoadCertificateFromFile(certPath);
            }
            catch
            {
                // Fallback for PEM or legacy formats
                try
                {
                    string text = File.ReadAllText(certPath);
                    return X509Certificate2.CreateFromPem(text);
                }
                catch
                {
#pragma warning disable SYSLIB0057
                    return new X509Certificate2(certPath);
#pragma warning restore SYSLIB0057
                }
            }
        }

        private static string ExtractCommonName(string distinguishedName)
        {
            if (string.IsNullOrWhiteSpace(distinguishedName)) return string.Empty;

            var match = Regex.Match(distinguishedName, @"CN=([^,]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }

            return distinguishedName;
        }

        public void OpenFileInExplorer(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{dir}\"",
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open file in explorer: {ex.Message}");
            }
        }

        public void OpenCertificateInViewer(string certificateFilePath)
        {
            try
            {
                if (File.Exists(certificateFilePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "rundll32.exe",
                        Arguments = $"cryptext.dll,CryptExtOpenCER \"{certificateFilePath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open certificate in viewer: {ex.Message}");
            }
        }

        public async Task<bool> UpdateCertificateAsync(
            SslCertificateInfo targetCert,
            string newCertPath,
            string newKeyPath,
            string? newChainPath,
            bool backupOld = true)
        {
            return await Task.Run(() =>
            {
                if (!File.Exists(newCertPath))
                {
                    throw new FileNotFoundException($"New certificate file not found: {newCertPath}");
                }

                if (!File.Exists(newKeyPath))
                {
                    throw new FileNotFoundException($"New private key file not found: {newKeyPath}");
                }

                // Verify that new certificate can be decoded
                using var testCert = LoadCertificate(newCertPath);
                if (testCert == null)
                {
                    throw new InvalidOperationException("The provided new certificate file could not be parsed as a valid X.509 certificate.");
                }

                // Backup existing files if requested
                if (backupOld)
                {
                    string targetDir = Path.GetDirectoryName(targetCert.CertificateFilePath) ?? string.Empty;
                    string backupDir = Path.Combine(targetDir, "backup_ssl_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(backupDir);

                    if (File.Exists(targetCert.CertificateFilePath))
                    {
                        File.Copy(targetCert.CertificateFilePath, Path.Combine(backupDir, Path.GetFileName(targetCert.CertificateFilePath)), true);
                    }
                    if (File.Exists(targetCert.KeyFilePath))
                    {
                        File.Copy(targetCert.KeyFilePath, Path.Combine(backupDir, Path.GetFileName(targetCert.KeyFilePath)), true);
                    }
                    if (!string.IsNullOrEmpty(targetCert.ChainFilePath) && File.Exists(targetCert.ChainFilePath))
                    {
                        File.Copy(targetCert.ChainFilePath, Path.Combine(backupDir, Path.GetFileName(targetCert.ChainFilePath)), true);
                    }
                }

                // Replace files
                string? certDestDir = Path.GetDirectoryName(targetCert.CertificateFilePath);
                if (!string.IsNullOrEmpty(certDestDir) && !Directory.Exists(certDestDir))
                {
                    Directory.CreateDirectory(certDestDir);
                }
                File.Copy(newCertPath, targetCert.CertificateFilePath, true);

                string? keyDestDir = Path.GetDirectoryName(targetCert.KeyFilePath);
                if (!string.IsNullOrEmpty(keyDestDir) && !Directory.Exists(keyDestDir))
                {
                    Directory.CreateDirectory(keyDestDir);
                }
                File.Copy(newKeyPath, targetCert.KeyFilePath, true);

                if (!string.IsNullOrWhiteSpace(newChainPath) && File.Exists(newChainPath) && !string.IsNullOrWhiteSpace(targetCert.ChainFilePath))
                {
                    string? chainDestDir = Path.GetDirectoryName(targetCert.ChainFilePath);
                    if (!string.IsNullOrEmpty(chainDestDir) && !Directory.Exists(chainDestDir))
                    {
                        Directory.CreateDirectory(chainDestDir);
                    }
                    File.Copy(newChainPath, targetCert.ChainFilePath, true);
                }

                // Re-inspect the certificate file to update status and UI
                InspectCertificateFile(targetCert);
                return true;
            });
        }
    }
}
