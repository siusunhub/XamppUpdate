using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public class VersionDetectionService : IVersionDetectionService
    {
        public async Task<string> DetectApacheVersionAsync(string installationPath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                {
                    return "Not Installed / Path Invalid";
                }

                string httpdExe = Path.Combine(installationPath, "bin", "httpd.exe");
                if (!File.Exists(httpdExe))
                {
                    return "Executable Not Found";
                }

                // Try running httpd.exe -v
                try
                {
                    string output = ExecuteProcess(httpdExe, "-v");
                    var match = Regex.Match(output, @"Apache/(\d+\.\d+\.\d+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch
                {
                    // Fallback to FileVersionInfo
                }

                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(httpdExe);
                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                    {
                        return versionInfo.ProductVersion.Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion))
                    {
                        return versionInfo.FileVersion.Trim();
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        public async Task<string> DetectPhpVersionAsync(string installationPath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                {
                    return "Not Installed / Path Invalid";
                }

                string phpExe = Path.Combine(installationPath, "php.exe");
                if (!File.Exists(phpExe))
                {
                    return "Executable Not Found";
                }

                try
                {
                    string output = ExecuteProcess(phpExe, "-v");
                    var match = Regex.Match(output, @"PHP\s+(\d+\.\d+\.\d+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch { }

                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(phpExe);
                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                    {
                        return versionInfo.ProductVersion.Trim();
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        public async Task<string> DetectMySqlVersionAsync(string installationPath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                {
                    return "Not Installed / Path Invalid";
                }

                string mysqldExe = Path.Combine(installationPath, "bin", "mysqld.exe");
                if (!File.Exists(mysqldExe))
                {
                    return "Executable Not Found";
                }

                try
                {
                    string output = ExecuteProcess(mysqldExe, "--version");
                    var match = Regex.Match(output, @"Ver\s+([0-9\.\-A-Za-z]+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
                catch { }

                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(mysqldExe);
                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                    {
                        return versionInfo.ProductVersion.Trim();
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        public async Task<string> DetectComposerVersionAsync(string executablePath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return "Not Installed / Path Invalid";
                }

                try
                {
                    string output = ExecuteProcess(executablePath, "--version");
                    var match = Regex.Match(output, @"Composer\s+version\s+([0-9\.]+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        return output.Split('\n')[0].Trim();
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        public async Task<string> DetectSslStatusAsync(string certificatesPath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(certificatesPath) || !Directory.Exists(certificatesPath))
                {
                    return "Directory Not Found";
                }

                try
                {
                    var files = Directory.GetFiles(certificatesPath, "*.*");
                    return $"{files.Length} Certificate file(s) found";
                }
                catch
                {
                    return "Status Unknown";
                }
            });
        }

        private static string ExecuteProcess(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
    }
}
