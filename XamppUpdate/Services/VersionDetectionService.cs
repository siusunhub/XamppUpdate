using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XamppUpdate.Models;

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
            var info = await DetectPhpBuildInfoAsync(installationPath);
            return info.Version;
        }

        public async Task<PhpBuildInfo> DetectPhpBuildInfoAsync(string installationPath)
        {
            return await Task.Run(() =>
            {
                var buildInfo = new PhpBuildInfo();

                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                {
                    buildInfo.Version = "Not Installed / Path Invalid";
                    return buildInfo;
                }

                string phpExe = Path.Combine(installationPath, "php.exe");
                if (!File.Exists(phpExe))
                {
                    buildInfo.Version = "Executable Not Found";
                    return buildInfo;
                }

                // 1. Static inspection from files and PE binary header
                string detectedArch = "x64";
                bool isThreadSafe = true;

                // Check DLLs in root directory
                try
                {
                    bool hasTsDll = Directory.EnumerateFiles(installationPath, "*ts.dll", SearchOption.TopDirectoryOnly).Any(f =>
                    {
                        string name = Path.GetFileName(f);
                        return name.StartsWith("php", StringComparison.OrdinalIgnoreCase) && name.EndsWith("ts.dll", StringComparison.OrdinalIgnoreCase);
                    });

                    bool hasNonTsDll = Directory.EnumerateFiles(installationPath, "php*.dll", SearchOption.TopDirectoryOnly).Any(f =>
                    {
                        string name = Path.GetFileName(f);
                        return !name.EndsWith("ts.dll", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^php\d*\.dll$", RegexOptions.IgnoreCase);
                    });

                    if (hasTsDll) isThreadSafe = true;
                    else if (hasNonTsDll) isThreadSafe = false;
                }
                catch { }

                // Read PE header of php.exe
                try
                {
                    using var fs = new FileStream(phpExe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new BinaryReader(fs);

                    if (fs.Length >= 64)
                    {
                        fs.Seek(0x3C, SeekOrigin.Begin);
                        int peOffset = reader.ReadInt32();

                        if (peOffset > 0 && fs.Length >= peOffset + 6)
                        {
                            fs.Seek(peOffset, SeekOrigin.Begin);
                            uint peSignature = reader.ReadUInt32();
                            if (peSignature == 0x00004550) // "PE\0\0"
                            {
                                ushort machine = reader.ReadUInt16();
                                detectedArch = machine switch
                                {
                                    0x8664 => "x64",
                                    0x014C => "x86",
                                    0xAA64 => "ARM64",
                                    _ => "x64"
                                };
                            }
                        }
                    }
                }
                catch { }

                // 2. Dynamic execution: php.exe -v
                try
                {
                    string output = ExecuteProcess(phpExe, "-v");
                    buildInfo.RawOutput = output;

                    var versionMatch = Regex.Match(output, @"PHP\s+(\d+\.\d+\.\d+)");
                    if (versionMatch.Success)
                    {
                        buildInfo.Version = versionMatch.Groups[1].Value;
                    }

                    if (output.Contains("ZTS", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("TS", StringComparison.OrdinalIgnoreCase) ||
                        output.Contains("Thread Safe", StringComparison.OrdinalIgnoreCase))
                    {
                        isThreadSafe = true;
                    }
                    else if (output.Contains("NTS", StringComparison.OrdinalIgnoreCase) ||
                             output.Contains("Non-Thread Safe", StringComparison.OrdinalIgnoreCase))
                    {
                        isThreadSafe = false;
                    }

                    if (output.Contains("x64", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedArch = "x64";
                    }
                    else if (output.Contains("x86", StringComparison.OrdinalIgnoreCase))
                    {
                        detectedArch = "x86";
                    }
                }
                catch { }

                // 3. Fallback to FileVersionInfo if version still not detected
                if (string.IsNullOrWhiteSpace(buildInfo.Version) || buildInfo.Version == "Unknown")
                {
                    try
                    {
                        var versionInfo = FileVersionInfo.GetVersionInfo(phpExe);
                        if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                        {
                            buildInfo.Version = versionInfo.ProductVersion.Trim();
                        }
                    }
                    catch { }
                }

                buildInfo.Architecture = detectedArch;
                buildInfo.IsThreadSafe = isThreadSafe;

                return buildInfo;
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
