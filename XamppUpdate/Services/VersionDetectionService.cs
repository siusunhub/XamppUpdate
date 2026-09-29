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
                    mysqldExe = Path.Combine(installationPath, "bin", "mariadbd.exe");
                }
                if (!File.Exists(mysqldExe))
                {
                    mysqldExe = Path.Combine(installationPath, "mysqld.exe");
                }
                if (!File.Exists(mysqldExe))
                {
                    mysqldExe = Path.Combine(installationPath, "mariadbd.exe");
                }
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
                        string ver = match.Groups[1].Value;
                        if (output.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) &&
                            !ver.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
                        {
                            ver += "-MariaDB";
                        }
                        return ver;
                    }
                }
                catch { }

                try
                {
                    var versionInfo = FileVersionInfo.GetVersionInfo(mysqldExe);
                    if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                    {
                        string pVer = versionInfo.ProductVersion.Trim();
                        if ((versionInfo.FileDescription?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true ||
                             versionInfo.ProductName?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true) &&
                            !pVer.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
                        {
                            pVer += "-MariaDB";
                        }
                        return pVer;
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

        public async Task<string> DetectPhpMyAdminVersionAsync(string installationPath)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(installationPath) || !Directory.Exists(installationPath))
                {
                    return "Not Installed / Path Invalid";
                }

                try
                {
                    // 1. Check RELEASE-DATE-* file in root directory (standard in official PMA packages)
                    var releaseFiles = Directory.GetFiles(installationPath, "RELEASE-DATE-*", SearchOption.TopDirectoryOnly);
                    if (releaseFiles.Length > 0)
                    {
                        string fileName = Path.GetFileName(releaseFiles[0]);
                        string ver = fileName.Substring("RELEASE-DATE-".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(ver))
                        {
                            return ver;
                        }
                    }

                    // 2. Check package.json / composer.json
                    string packageJson = Path.Combine(installationPath, "package.json");
                    if (File.Exists(packageJson))
                    {
                        string content = File.ReadAllText(packageJson);
                        var match = Regex.Match(content, @"""version""\s*:\s*""([^""]+)""");
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }

                    string composerJson = Path.Combine(installationPath, "composer.json");
                    if (File.Exists(composerJson))
                    {
                        string content = File.ReadAllText(composerJson);
                        var match = Regex.Match(content, @"""version""\s*:\s*""([^""]+)""");
                        if (match.Success) return match.Groups[1].Value.Trim();
                    }

                    // 3. Check libraries/classes/Version.php or libraries/vendor_config.php
                    var candidatePhpFiles = new[]
                    {
                        Path.Combine(installationPath, "libraries", "classes", "Version.php"),
                        Path.Combine(installationPath, "libraries", "Version.php"),
                        Path.Combine(installationPath, "libraries", "vendor_config.php")
                    };

                    foreach (var phpFile in candidatePhpFiles)
                    {
                        if (File.Exists(phpFile))
                        {
                            string content = File.ReadAllText(phpFile);
                            var match = Regex.Match(content, @"(?:const\s+VERSION|define\('PMA_VERSION',\s*|\$VERSION\s*=\s*)['""]([^'""]+)['""]");
                            if (match.Success) return match.Groups[1].Value.Trim();
                        }
                    }

                    // 4. Check ChangeLog / README
                    string changeLog = Path.Combine(installationPath, "ChangeLog");
                    if (File.Exists(changeLog))
                    {
                        using var reader = new StreamReader(changeLog);
                        for (int i = 0; i < 30; i++)
                        {
                            string? line = reader.ReadLine();
                            if (line == null) break;
                            var match = Regex.Match(line, @"(?:phpMyAdmin\s+Version|Version|Release)\s+([0-9\.]+(?:-[a-zA-Z0-9\.]+)?)", RegexOptions.IgnoreCase);
                            if (match.Success) return match.Groups[1].Value.Trim();
                        }
                    }

                    // If folder has index.php and config.inc.php
                    if (File.Exists(Path.Combine(installationPath, "index.php")))
                    {
                        return "Detected (Version Unknown)";
                    }
                }
                catch { }

                return "Unknown Version";
            });
        }

        private static string ExecuteProcess(string fileName, string arguments)
        {
            try
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

                using var process = new Process { StartInfo = psi };
                var sb = new System.Text.StringBuilder();
                object lockObj = new object();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (lockObj)
                        {
                            sb.AppendLine(e.Data);
                        }
                    }
                };

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (lockObj)
                        {
                            sb.AppendLine(e.Data);
                        }
                    }
                };

                if (!process.Start()) return string.Empty;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(2000))
                {
                    try { process.Kill(); } catch { }
                }

                lock (lockObj)
                {
                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
