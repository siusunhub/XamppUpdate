using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class VHostService : IVHostService
    {
        private readonly ISettingsService? _settingsService;

        public VHostService(ISettingsService? settingsService = null)
        {
            _settingsService = settingsService;
        }

        private string ResolveApachePath(string? explicitPath = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath) && Directory.Exists(explicitPath))
                return explicitPath;

            string fromSettings = _settingsService?.CurrentSettings?.Apache?.InstallationPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(fromSettings) && Directory.Exists(fromSettings))
                return fromSettings;

            if (Directory.Exists(@"C:\xampp\apache"))
                return @"C:\xampp\apache";

            return fromSettings;
        }

        private string ResolveHttpdConfPath(string? apachePath = null)
        {
            if (!string.IsNullOrWhiteSpace(apachePath))
            {
                if (File.Exists(apachePath) && Path.GetFileName(apachePath).Equals("httpd.conf", StringComparison.OrdinalIgnoreCase))
                    return apachePath;

                string p1 = Path.Combine(apachePath, "conf", "httpd.conf");
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(apachePath, "httpd.conf");
                if (File.Exists(p2)) return p2;
            }

            string root = ResolveApachePath(apachePath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                string p = Path.Combine(root, "conf", "httpd.conf");
                if (File.Exists(p)) return p;
            }
            if (File.Exists(@"C:\xampp\apache\conf\httpd.conf"))
                return @"C:\xampp\apache\conf\httpd.conf";

            return Path.Combine(string.IsNullOrWhiteSpace(root) ? @"C:\xampp\apache" : root, "conf", "httpd.conf");
        }

        private static readonly Regex VHostStartRegex = new(
            @"^(?<hash>#*)\s*<VirtualHost\s+(?<target>[^>]+)>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VHostEndRegex = new(
            @"^(?<hash>#*)\s*</VirtualHost>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ServerNameRegex = new(
            @"^\s*ServerName\s+([^\s#]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ServerAliasRegex = new(
            @"^\s*ServerAlias\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DocumentRootRegex = new(
            @"^\s*DocumentRoot\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SslEngineRegex = new(
            @"^\s*SSLEngine\s+(on|off)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SslCertFileRegex = new(
            @"^\s*SSLCertificateFile\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SslKeyFileRegex = new(
            @"^\s*SSLCertificateKeyFile\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SslChainFileRegex = new(
            @"^\s*SSLCertificateChainFile\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ErrorLogRegex = new(
            @"^\s*ErrorLog\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CustomLogRegex = new(
            @"^\s*CustomLog\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DirectoryIndexRegex = new(
            @"^\s*DirectoryIndex\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MDomainRegex = new(
            @"^\s*(?:MDomain|MDMember)\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Global directives
        private static readonly Regex MdAgreementRegex = new(
            @"^\s*MDCertificateAgreement\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MdContactEmailRegex = new(
            @"^\s*MDContactEmail\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MdBaseServerRegex = new(
            @"^\s*MDBaseServer\s+(on|off)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MdCaRegex = new(
            @"^\s*MDCertificateAuthority\s+""?([^""#\r\n]+)""?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<VHostParseResult> ParseVHostFileAsync(string filePath)
        {
            var result = new VHostParseResult();

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                result.Success = true;
                result.PreambleLines = GetDefaultPreamble();
                return result;
            }

            try
            {
                var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
                var entries = new List<VHostEntry>();
                var preamble = new List<string>();
                var pendingLeadingLines = new List<string>();
                var globalSettings = new VHostGlobalSettings();

                bool foundFirstVHost = false;
                bool inBlock = false;
                VHostEntry? current = null;
                string blockCommentHash = string.Empty;

                foreach (var line in lines)
                {
                    var startMatch = VHostStartRegex.Match(line);
                    if (startMatch.Success && !inBlock)
                    {
                        foundFirstVHost = true;
                        inBlock = true;

                        blockCommentHash = startMatch.Groups["hash"].Value;
                        bool isCommented = blockCommentHash.Length > 0;
                        string target = startMatch.Groups["target"].Value.Trim();

                        string address = "*";
                        int port = 80;

                        int colonIdx = target.LastIndexOf(':');
                        if (colonIdx >= 0)
                        {
                            address = target.Substring(0, colonIdx).Trim();
                            if (int.TryParse(target.Substring(colonIdx + 1).Trim(), out int p))
                            {
                                port = p;
                            }
                        }
                        else
                        {
                            address = target;
                        }

                        current = new VHostEntry
                        {
                            IsEnabled = !isCommented,
                            ListenAddress = string.IsNullOrEmpty(address) ? "*" : address,
                            Port = port,
                            IsSsl = (port == 443),
                            LeadingLines = new List<string>(pendingLeadingLines)
                        };
                        pendingLeadingLines.Clear();
                        current.RawLines.Add(line);
                        continue;
                    }

                    if (inBlock && current != null)
                    {
                        current.RawLines.Add(line);

                        var endMatch = VHostEndRegex.Match(line);
                        if (endMatch.Success)
                        {
                            inBlock = false;
                            entries.Add(current);
                            current = null;
                            blockCommentHash = string.Empty;
                            continue;
                        }

                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed))
                        {
                            continue;
                        }

                        // Parse inside block directives.
                        // CRITICAL: Comment lines starting with '#' must NEVER be parsed as active directives!
                        string contentLine;
                        if (current.IsEnabled)
                        {
                            // In an active VirtualHost, any line starting with '#' is a comment.
                            if (trimmed.StartsWith('#'))
                            {
                                current.AdditionalDirectives.Add(line);
                                continue;
                            }
                            contentLine = trimmed;
                        }
                        else
                        {
                            // In a disabled VirtualHost block (where the whole block was commented out):
                            // Strip outer block comment hash once.
                            string stripped = trimmed;
                            if (!string.IsNullOrEmpty(blockCommentHash) && stripped.StartsWith(blockCommentHash))
                            {
                                stripped = stripped.Substring(blockCommentHash.Length).TrimStart();
                            }
                            else if (stripped.StartsWith('#'))
                            {
                                stripped = stripped.TrimStart('#').TrimStart();
                            }

                            // If the line still starts with '#', it's an inner comment inside the disabled block!
                            if (stripped.StartsWith('#'))
                            {
                                current.AdditionalDirectives.Add(line);
                                continue;
                            }
                            contentLine = stripped;
                        }

                        var mServerName = ServerNameRegex.Match(contentLine);
                        if (mServerName.Success && string.IsNullOrWhiteSpace(current.ServerName))
                        {
                            current.ServerName = mServerName.Groups[1].Value.Trim();
                            continue;
                        }

                        var mServerAlias = ServerAliasRegex.Match(contentLine);
                        if (mServerAlias.Success && string.IsNullOrWhiteSpace(current.ServerAlias))
                        {
                            current.ServerAlias = mServerAlias.Groups[1].Value.Trim();
                            continue;
                        }

                        var mDocRoot = DocumentRootRegex.Match(contentLine);
                        if (mDocRoot.Success && string.IsNullOrWhiteSpace(current.DocumentRoot))
                        {
                            current.DocumentRoot = mDocRoot.Groups[1].Value.Trim();
                            continue;
                        }

                        var mSslEngine = SslEngineRegex.Match(contentLine);
                        if (mSslEngine.Success)
                        {
                            if (string.Equals(mSslEngine.Groups[1].Value, "on", StringComparison.OrdinalIgnoreCase))
                            {
                                current.IsSsl = true;
                            }
                            else if (string.Equals(mSslEngine.Groups[1].Value, "off", StringComparison.OrdinalIgnoreCase))
                            {
                                current.IsSsl = false;
                            }
                            continue;
                        }

                        var mSslCert = SslCertFileRegex.Match(contentLine);
                        if (mSslCert.Success)
                        {
                            current.SslCertificateFile = mSslCert.Groups[1].Value.Trim();
                            current.IsSsl = true;
                            continue;
                        }

                        var mSslKey = SslKeyFileRegex.Match(contentLine);
                        if (mSslKey.Success)
                        {
                            current.SslCertificateKeyFile = mSslKey.Groups[1].Value.Trim();
                            current.IsSsl = true;
                            continue;
                        }

                        var mSslChain = SslChainFileRegex.Match(contentLine);
                        if (mSslChain.Success)
                        {
                            current.SslCertificateChainFile = mSslChain.Groups[1].Value.Trim();
                            continue;
                        }

                        var mErrorLog = ErrorLogRegex.Match(contentLine);
                        if (mErrorLog.Success && string.IsNullOrWhiteSpace(current.ErrorLog))
                        {
                            current.ErrorLog = mErrorLog.Groups[1].Value.Trim();
                            continue;
                        }

                        var mCustomLog = CustomLogRegex.Match(contentLine);
                        if (mCustomLog.Success && string.IsNullOrWhiteSpace(current.CustomLog))
                        {
                            current.CustomLog = mCustomLog.Groups[1].Value.Trim();
                            continue;
                        }

                        var mDirIndex = DirectoryIndexRegex.Match(contentLine);
                        if (mDirIndex.Success && string.IsNullOrWhiteSpace(current.DirectoryIndex))
                        {
                            current.DirectoryIndex = mDirIndex.Groups[1].Value.Trim();
                            continue;
                        }

                        var mMdomain = MDomainRegex.Match(contentLine);
                        if (mMdomain.Success)
                        {
                            current.UseLetsEncrypt = true;
                            current.IsSsl = true;
                            continue;
                        }

                        // Also ignore <IfModule md_module> and </IfModule> tags inside vhost block
                        if (Regex.IsMatch(contentLine, @"^</?IfModule(?:\s+md_module)?>", RegexOptions.IgnoreCase))
                        {
                            continue;
                        }

                        // Preserve unmanaged configuration lines (e.g., from httpd-ssl.conf or custom directives)
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            current.AdditionalDirectives.Add(line);
                        }
                    }
                    else
                    {
                        // Lines outside of VirtualHost blocks
                        if (!foundFirstVHost)
                        {
                            preamble.Add(line);

                            // Detect global MD / Let's Encrypt settings only on UNCOMMENTED lines
                            string trimmedPreamble = line.Trim();
                            if (!trimmedPreamble.StartsWith('#'))
                            {
                                var mAgreement = MdAgreementRegex.Match(trimmedPreamble);
                                if (mAgreement.Success)
                                {
                                    globalSettings.EnableLetsEncrypt = true;
                                    globalSettings.CertificateAgreement = mAgreement.Groups[1].Value.Trim();
                                }

                                var mContact = MdContactEmailRegex.Match(trimmedPreamble);
                                if (mContact.Success)
                                {
                                    globalSettings.EnableLetsEncrypt = true;
                                    globalSettings.ContactEmail = mContact.Groups[1].Value.Trim();
                                }

                                var mBase = MdBaseServerRegex.Match(trimmedPreamble);
                                if (mBase.Success)
                                {
                                    globalSettings.MdBaseServer = string.Equals(mBase.Groups[1].Value, "on", StringComparison.OrdinalIgnoreCase);
                                }

                                var mCa = MdCaRegex.Match(trimmedPreamble);
                                if (mCa.Success)
                                {
                                    globalSettings.MdCertificateAuthority = mCa.Groups[1].Value.Trim();
                                }
                            }
                        }
                        else
                        {
                            // Inter-block comments, blank lines, or trailing lines after blocks
                            pendingLeadingLines.Add(line);
                        }
                    }
                }

                // If file ended before closing </VirtualHost>
                if (inBlock && current != null)
                {
                    current.IsParseError = true;
                    current.ParseErrorMessage = "Unclosed <VirtualHost> block at end of file.";
                    entries.Add(current);
                }

                result.Entries = entries;
                result.PreambleLines = preamble.Count > 0 ? preamble : GetDefaultPreamble();
                result.TrailingLines = new List<string>(pendingLeadingLines);
                result.GlobalSettings = globalSettings;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public async Task SaveVHostFileAsync(
            string filePath,
            List<VHostEntry> entries,
            List<string>? preambleLines,
            VHostGlobalSettings? globalSettings,
            List<string>? trailingLines = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be empty", nameof(filePath));

            var sb = new StringBuilder();

            // 1. Process Preamble & Global Settings
            var preamble = preambleLines != null && preambleLines.Count > 0
                ? new List<string>(preambleLines)
                : GetDefaultPreamble();

            // Append updated Global MD settings if explicitly enabled OR if any active VirtualHost uses Let's Encrypt!
            bool hasLetsEncryptEntries = entries.Any(e => e.UseLetsEncrypt && e.IsEnabled);
            if ((globalSettings != null && globalSettings.EnableLetsEncrypt) || hasLetsEncryptEntries)
            {
                preamble = CleanPreambleMdDirectives(preamble);

                foreach (var line in preamble)
                {
                    sb.AppendLine(line);
                }

                sb.AppendLine();
                sb.AppendLine("# ===========================================================================");
                sb.AppendLine("# Let's Encrypt / mod_md Global Configuration");
                sb.AppendLine("# ===========================================================================");
                sb.AppendLine("<IfModule md_module>");
                string agreement = (globalSettings != null && !string.IsNullOrWhiteSpace(globalSettings.CertificateAgreement))
                    ? globalSettings.CertificateAgreement
                    : "accepted";
                sb.AppendLine($"    MDCertificateAgreement {agreement}");
                if (globalSettings != null && !string.IsNullOrWhiteSpace(globalSettings.ContactEmail))
                {
                    sb.AppendLine($"    MDContactEmail {globalSettings.ContactEmail}");
                }
                bool baseServer = globalSettings?.MdBaseServer ?? false;
                sb.AppendLine($"    MDBaseServer {(baseServer ? "on" : "off")}");
                if (globalSettings != null && !string.IsNullOrWhiteSpace(globalSettings.MdCertificateAuthority))
                {
                    sb.AppendLine($"    MDCertificateAuthority {globalSettings.MdCertificateAuthority}");
                }
                sb.AppendLine("</IfModule>");
            }
            else
            {
                // Write preamble lines exactly as original!
                foreach (var line in preamble)
                {
                    sb.AppendLine(line);
                }
            }

            // Static certs in Let's Encrypt vhosts are only needed when nothing else can satisfy mod_ssl at startup.
            // If mod_md is loaded AND another SSL host already carries a certificate (e.g. the _default_:443 host),
            // omit them so mod_md's issued certificate is used instead of the original one.
            bool omitFallbackCert = false;
            if (hasLetsEncryptEntries)
            {
                bool otherHostHasCert = entries.Any(e => e.IsEnabled && e.IsSsl && !e.UseLetsEncrypt &&
                                                         !string.IsNullOrWhiteSpace(e.SslCertificateFile));
                if (!otherHostHasCert &&
                    !string.Equals(Path.GetFileName(filePath), "httpd-ssl.conf", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string sslConf = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, "httpd-ssl.conf");
                        if (File.Exists(sslConf))
                        {
                            otherHostHasCert = (await File.ReadAllLinesAsync(sslConf, Encoding.UTF8))
                                .Any(l => SslCertFileRegex.IsMatch(l.Trim()) && !l.TrimStart().StartsWith('#'));
                        }
                    }
                    catch { /* unreadable: keep the fallback to be safe */ }
                }

                if (otherHostHasCert)
                {
                    string? apacheRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(filePath)));
                    var mdStatus = await CheckModMdStatusAsync(apacheRoot);
                    omitFallbackCert = mdStatus.HttpdConfExists && mdStatus.IsMdModuleLoaded;
                }
            }

            // 2. Write VirtualHost Blocks
            foreach (var entry in entries)
            {
                // Write leading inter-block lines (exact original comments, empty lines)
                if (entry.LeadingLines != null && entry.LeadingLines.Count > 0)
                {
                    foreach (var leadLine in entry.LeadingLines)
                    {
                        sb.AppendLine(leadLine);
                    }
                }
                else
                {
                    sb.AppendLine();
                }

                if (entry.IsParseError && entry.RawLines != null && entry.RawLines.Count > 0)
                {
                    // If block had parse errors, write back raw lines to avoid corrupting user's content
                    foreach (var rawLine in entry.RawLines)
                    {
                        sb.AppendLine(rawLine);
                    }
                    continue;
                }

                if (entry.RawLines != null && entry.RawLines.Count > 0)
                {
                    // EXISTING BLOCK: Update in-place to preserve ALL original format, tags, comments, and multi-line directives!
                    var updatedBlockLines = UpdateBlockInPlace(entry, omitFallbackCert && entry.UseLetsEncrypt);
                    foreach (var blockLine in updatedBlockLines)
                    {
                        sb.AppendLine(blockLine);
                    }
                }
                else
                {
                    // BRAND NEW BLOCK (created by user): Generate standard new block
                    var newBlockLines = GenerateNewVHostBlock(entry, omitFallbackCert && entry.UseLetsEncrypt);
                    foreach (var blockLine in newBlockLines)
                    {
                        sb.AppendLine(blockLine);
                    }
                }
            }

            // 3. Write trailing lines (lines after last block) exactly as original
            if (trailingLines != null && trailingLines.Count > 0)
            {
                foreach (var trailLine in trailingLines)
                {
                    sb.AppendLine(trailLine);
                }
            }

            // Write atomically via temporary file
            string dir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = Path.Combine(dir, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            if (File.Exists(filePath))
            {
                File.Move(tempFile, filePath, overwrite: true);
            }
            else
            {
                File.Move(tempFile, filePath);
            }
        }

        private static List<string> UpdateBlockInPlace(VHostEntry entry, bool omitFallbackCert = false)
        {
            var lines = new List<string>(entry.RawLines);
            if (lines.Count == 0) return lines;

            // Check original commented/disabled state
            var mStart = VHostStartRegex.Match(lines[0]);
            string origHash = mStart.Success ? mStart.Groups["hash"].Value : string.Empty;
            bool originallyDisabled = origHash.Length > 0;
            bool currentlyDisabled = !entry.IsEnabled;

            // 1. If enable/disable state changed, toggle comment prefix on lines
            if (originallyDisabled != currentlyDisabled)
            {
                if (currentlyDisabled)
                {
                    // Now disabled: prefix all non-empty lines with "## "
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                        {
                            lines[i] = "## " + lines[i];
                        }
                    }
                }
                else
                {
                    // Now enabled: strip outer comment prefix
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string trimmed = lines[i].TrimStart();
                        if (!string.IsNullOrEmpty(origHash) && trimmed.StartsWith(origHash))
                        {
                            lines[i] = trimmed.Substring(origHash.Length).TrimStart();
                        }
                        else if (trimmed.StartsWith("##"))
                        {
                            lines[i] = trimmed.Substring(2).TrimStart();
                        }
                        else if (trimmed.StartsWith("#"))
                        {
                            lines[i] = trimmed.Substring(1).TrimStart();
                        }
                    }
                }
            }

            string pfx = entry.IsEnabled ? "" : "## ";

            // 2. Update opening <VirtualHost ...> tag if address or port changed
            lines[0] = Regex.Replace(lines[0], @"<VirtualHost\s+[^>]+>", $"<VirtualHost {entry.ListenAddress}:{entry.Port}>", RegexOptions.IgnoreCase);

            // Helper to find index of an ACTIVE directive (never a comment!)
            int FindDirectiveIndex(Regex regex)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i].Trim();
                    if (!entry.IsEnabled && l.StartsWith("##"))
                    {
                        l = l.Substring(2).Trim();
                    }
                    if (l.StartsWith('#')) continue; // inner comment: skip!
                    if (regex.IsMatch(l)) return i;
                }
                return -1;
            }

            // 3. Update ServerName
            int snIdx = FindDirectiveIndex(ServerNameRegex);
            if (!string.IsNullOrWhiteSpace(entry.ServerName))
            {
                string newSnLine = $"{pfx}    ServerName {entry.ServerName}";
                if (snIdx >= 0) lines[snIdx] = newSnLine;
                else lines.Insert(1, newSnLine);
            }
            else if (snIdx >= 0)
            {
                lines.RemoveAt(snIdx);
            }

            // 4. Update ServerAlias
            int saIdx = FindDirectiveIndex(ServerAliasRegex);
            if (!string.IsNullOrWhiteSpace(entry.ServerAlias))
            {
                string newSaLine = $"{pfx}    ServerAlias {entry.ServerAlias}";
                if (saIdx >= 0) lines[saIdx] = newSaLine;
                else
                {
                    int insertPos = FindDirectiveIndex(ServerNameRegex);
                    lines.Insert(insertPos >= 0 ? insertPos + 1 : 1, newSaLine);
                }
            }
            else if (saIdx >= 0)
            {
                lines.RemoveAt(saIdx);
            }

            // 5. Update DocumentRoot
            int drIdx = FindDirectiveIndex(DocumentRootRegex);
            if (!string.IsNullOrWhiteSpace(entry.DocumentRoot))
            {
                string docRoot = entry.DocumentRoot.Replace('\\', '/');
                string newDrLine = $"{pfx}    DocumentRoot \"{docRoot}\"";
                if (drIdx >= 0) lines[drIdx] = newDrLine;
                else
                {
                    int insertPos = Math.Min(lines.Count - 1, 2);
                    lines.Insert(insertPos, newDrLine);
                }
            }
            else if (drIdx >= 0)
            {
                lines.RemoveAt(drIdx);
            }

            // 6. Update DirectoryIndex
            int diIdx = FindDirectiveIndex(DirectoryIndexRegex);
            if (!string.IsNullOrWhiteSpace(entry.DirectoryIndex))
            {
                string newDiLine = $"{pfx}    DirectoryIndex {entry.DirectoryIndex}";
                if (diIdx >= 0) lines[diIdx] = newDiLine;
                else
                {
                    int insertPos = Math.Min(lines.Count - 1, 3);
                    lines.Insert(insertPos, newDiLine);
                }
            }
            else if (diIdx >= 0)
            {
                lines.RemoveAt(diIdx);
            }

            // 7. Update SSLEngine
            int seIdx = FindDirectiveIndex(SslEngineRegex);
            if (entry.IsSsl)
            {
                string newSeLine = $"{pfx}    SSLEngine on";
                if (seIdx >= 0) lines[seIdx] = newSeLine;
                else
                {
                    int insertPos = Math.Max(1, lines.Count - 1);
                    lines.Insert(insertPos, newSeLine);
                }
            }
            else if (seIdx >= 0)
            {
                lines[seIdx] = $"{pfx}    SSLEngine off";
            }

            // 8. Update Let's Encrypt / mod_md OR SSLCertificateFile directives
            if (entry.IsSsl && entry.UseLetsEncrypt && !string.IsNullOrWhiteSpace(entry.ServerName))
            {
                int mdIdx = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    string l = lines[i].Trim();
                    if (!entry.IsEnabled && l.StartsWith("##")) l = l.Substring(2).Trim();
                    if (l.StartsWith('#')) continue;
                    if (MDomainRegex.IsMatch(l)) { mdIdx = i; break; }
                }

                // CRITICAL FOR WINDOWS / APACHE:
                // MDomain takes DNS domain names ONLY, NEVER port numbers!
                // Colons (':') in domain names cause Windows OS error 123 (invalid directory syntax) when mod_md stages certificates!
                string cleanDomain = CleanDomainName(entry.ServerName);
                string cleanAliases = CleanDomainNames(entry.ServerAlias);
                string aliasPart = string.IsNullOrWhiteSpace(cleanAliases) ? "" : $" {cleanAliases}";
                string mdomainLine = $"{pfx}        MDomain {cleanDomain}{aliasPart}";
                string agreeLine = $"{pfx}        MDCertificateAgreement accepted";

                if (mdIdx >= 0)
                {
                    lines[mdIdx] = mdomainLine;

                    // Ensure MDCertificateAgreement is also present inside this block
                    int agreeIdx = -1;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string l = lines[i].Trim();
                        if (!entry.IsEnabled && l.StartsWith("##")) l = l.Substring(2).Trim();
                        if (l.StartsWith('#')) continue;
                        if (MdAgreementRegex.IsMatch(l)) { agreeIdx = i; break; }
                    }

                    if (agreeIdx >= 0)
                    {
                        lines[agreeIdx] = agreeLine;
                    }
                    else
                    {
                        lines.Insert(mdIdx + 1, agreeLine);
                    }
                }
                else
                {
                    int insertPos = Math.Max(1, lines.Count - 1);
                    lines.Insert(insertPos, $"{pfx}    <IfModule md_module>");
                    lines.Insert(insertPos + 1, mdomainLine);
                    lines.Insert(insertPos + 2, agreeLine);
                    lines.Insert(insertPos + 3, $"{pfx}    </IfModule>");
                }

                // CRITICAL FOR APACHE STARTUP:
                // When mod_md is newly configured, no Let's Encrypt certificate has been downloaded yet!
                // Apache mod_ssl requires at least one certificate & key on startup, otherwise mod_ssl aborts with:
                // AH02572: "Failed to configure at least one certificate and key ... error:0A0000B1:SSL routines::no certificate assigned".
                // We ensure fallback SSLCertificateFile & SSLCertificateKeyFile are present!
                int cfIdx = FindDirectiveIndex(SslCertFileRegex);
                if (omitFallbackCert)
                {
                    // Another SSL host (e.g. _default_:443) already provides a startup certificate and mod_md is
                    // loaded, so a static cert here would keep mod_ssl serving the ORIGINAL cert instead of the
                    // Let's Encrypt one. Remove it and let mod_md supply the certificate for this vhost.
                    foreach (var regex in new[] { SslCertFileRegex, SslKeyFileRegex, SslChainFileRegex })
                    {
                        int idx;
                        while ((idx = FindDirectiveIndex(regex)) >= 0) lines.RemoveAt(idx);
                    }
                    lines.RemoveAll(l => l.Contains("# Fallback certificate while mod_md obtains"));
                }
                else if (cfIdx < 0)
                {
                    string fallbackCert = !string.IsNullOrWhiteSpace(entry.SslCertificateFile)
                        ? entry.SslCertificateFile.Replace('\\', '/')
                        : "conf/ssl.crt/server.crt";
                    string fallbackKey = !string.IsNullOrWhiteSpace(entry.SslCertificateKeyFile)
                        ? entry.SslCertificateKeyFile.Replace('\\', '/')
                        : "conf/ssl.key/server.key";

                    int insertPos = Math.Max(1, lines.Count - 1);
                    lines.Insert(insertPos, $"{pfx}    # Fallback certificate while mod_md obtains Let's Encrypt certificate:");
                    lines.Insert(insertPos + 1, $"{pfx}    SSLCertificateFile \"{fallbackCert}\"");
                    lines.Insert(insertPos + 2, $"{pfx}    SSLCertificateKeyFile \"{fallbackKey}\"");
                }
                else
                {
                    // Fallback already present: apply edits made in the editor
                    if (!string.IsNullOrWhiteSpace(entry.SslCertificateFile))
                        lines[cfIdx] = $"{pfx}    SSLCertificateFile \"{entry.SslCertificateFile.Replace('\\', '/')}\"";

                    int fbKeyIdx = FindDirectiveIndex(SslKeyFileRegex);
                    if (fbKeyIdx >= 0 && !string.IsNullOrWhiteSpace(entry.SslCertificateKeyFile))
                        lines[fbKeyIdx] = $"{pfx}    SSLCertificateKeyFile \"{entry.SslCertificateKeyFile.Replace('\\', '/')}\"";
                }
            }
            else
            {
                // If not using Let's Encrypt, update SSLCertificateFile if configured
                int cfIdx = FindDirectiveIndex(SslCertFileRegex);
                if (!string.IsNullOrWhiteSpace(entry.SslCertificateFile))
                {
                    string cert = entry.SslCertificateFile.Replace('\\', '/');
                    string newCfLine = $"{pfx}    SSLCertificateFile \"{cert}\"";
                    if (cfIdx >= 0) lines[cfIdx] = newCfLine;
                    else
                    {
                        int insertPos = Math.Max(1, lines.Count - 1);
                        lines.Insert(insertPos, newCfLine);
                    }
                }

                int kfIdx = FindDirectiveIndex(SslKeyFileRegex);
                if (!string.IsNullOrWhiteSpace(entry.SslCertificateKeyFile))
                {
                    string key = entry.SslCertificateKeyFile.Replace('\\', '/');
                    string newKfLine = $"{pfx}    SSLCertificateKeyFile \"{key}\"";
                    if (kfIdx >= 0) lines[kfIdx] = newKfLine;
                    else
                    {
                        int certPos = FindDirectiveIndex(SslCertFileRegex);
                        int insertPos = certPos >= 0 ? certPos + 1 : Math.Max(1, lines.Count - 1);
                        lines.Insert(insertPos, newKfLine);
                    }
                }

                int chIdx = FindDirectiveIndex(SslChainFileRegex);
                if (!string.IsNullOrWhiteSpace(entry.SslCertificateChainFile))
                {
                    string chain = entry.SslCertificateChainFile.Replace('\\', '/');
                    string newChLine = $"{pfx}    SSLCertificateChainFile \"{chain}\"";
                    if (chIdx >= 0) lines[chIdx] = newChLine;
                    else
                    {
                        int keyPos = FindDirectiveIndex(SslKeyFileRegex);
                        int insertPos = keyPos >= 0 ? keyPos + 1 : Math.Max(1, lines.Count - 1);
                        lines.Insert(insertPos, newChLine);
                    }
                }
            }

            return lines;
        }

        public static string CleanDomainName(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
            string domain = rawName.Trim();
            if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring(7);
            else if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                domain = domain.Substring(8);

            int colonIdx = domain.IndexOf(':');
            if (colonIdx >= 0)
            {
                domain = domain.Substring(0, colonIdx);
            }
            return domain.TrimEnd('/').Trim();
        }

        public static string CleanDomainNames(string? rawNames)
        {
            if (string.IsNullOrWhiteSpace(rawNames)) return string.Empty;
            var parts = rawNames.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanedParts = parts.Select(CleanDomainName).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct();
            return string.Join(" ", cleanedParts);
        }

        private static List<string> GenerateNewVHostBlock(VHostEntry entry, bool omitFallbackCert = false)
        {
            var lines = new List<string>();
            string pfx = entry.IsEnabled ? "" : "## ";

            lines.Add($"{pfx}<VirtualHost {entry.ListenAddress}:{entry.Port}>");
            if (!string.IsNullOrWhiteSpace(entry.ServerName))
                lines.Add($"{pfx}    ServerName {entry.ServerName}");
            if (!string.IsNullOrWhiteSpace(entry.ServerAlias))
                lines.Add($"{pfx}    ServerAlias {entry.ServerAlias}");
            if (!string.IsNullOrWhiteSpace(entry.DocumentRoot))
                lines.Add($"{pfx}    DocumentRoot \"{entry.DocumentRoot.Replace('\\', '/')}\"");
            if (!string.IsNullOrWhiteSpace(entry.DirectoryIndex))
                lines.Add($"{pfx}    DirectoryIndex {entry.DirectoryIndex}");

            if (entry.IsSsl)
            {
                lines.Add($"{pfx}    SSLEngine on");
                if (entry.UseLetsEncrypt && !string.IsNullOrWhiteSpace(entry.ServerName))
                {
                    string cleanDomain = CleanDomainName(entry.ServerName);
                    string cleanAliases = CleanDomainNames(entry.ServerAlias);
                    string aliasPart = string.IsNullOrWhiteSpace(cleanAliases) ? "" : $" {cleanAliases}";
                    lines.Add($"{pfx}    <IfModule md_module>");
                    lines.Add($"{pfx}        MDomain {cleanDomain}{aliasPart}");
                    lines.Add($"{pfx}        MDCertificateAgreement accepted");
                    lines.Add($"{pfx}    </IfModule>");

                    if (!omitFallbackCert)
                    {
                        // Fallback certificate so mod_ssl does not crash on startup
                        string cert = !string.IsNullOrWhiteSpace(entry.SslCertificateFile) ? entry.SslCertificateFile.Replace('\\', '/') : "conf/ssl.crt/server.crt";
                        string key = !string.IsNullOrWhiteSpace(entry.SslCertificateKeyFile) ? entry.SslCertificateKeyFile.Replace('\\', '/') : "conf/ssl.key/server.key";
                        lines.Add($"{pfx}    # Fallback certificate while mod_md obtains Let's Encrypt certificate:");
                        lines.Add($"{pfx}    SSLCertificateFile \"{cert}\"");
                        lines.Add($"{pfx}    SSLCertificateKeyFile \"{key}\"");
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(entry.SslCertificateFile))
                        lines.Add($"{pfx}    SSLCertificateFile \"{entry.SslCertificateFile.Replace('\\', '/')}\"");
                    if (!string.IsNullOrWhiteSpace(entry.SslCertificateKeyFile))
                        lines.Add($"{pfx}    SSLCertificateKeyFile \"{entry.SslCertificateKeyFile.Replace('\\', '/')}\"");
                    if (!string.IsNullOrWhiteSpace(entry.SslCertificateChainFile))
                        lines.Add($"{pfx}    SSLCertificateChainFile \"{entry.SslCertificateChainFile.Replace('\\', '/')}\"");
                }
            }

            if (!string.IsNullOrWhiteSpace(entry.ErrorLog))
                lines.Add($"{pfx}    ErrorLog \"{entry.ErrorLog.Replace('\\', '/')}\"");
            if (!string.IsNullOrWhiteSpace(entry.CustomLog))
                lines.Add($"{pfx}    CustomLog \"{entry.CustomLog.Replace('\\', '/')}\" combined");

            lines.Add($"{pfx}</VirtualHost>");
            return lines;
        }

        public async Task<string> BackupConfigFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return string.Empty;

            string backupPath = $"{filePath}.bak.{DateTime.Now:yyyyMMdd_HHmmss}";
            await Task.Run(() => File.Copy(filePath, backupPath, overwrite: true));
            return backupPath;
        }

        private static List<string> CleanPreambleMdDirectives(List<string> preamble)
        {
            var cleaned = new List<string>();
            bool skippingMdBlock = false;

            foreach (var line in preamble)
            {
                string trimmed = line.Trim();

                if (Regex.IsMatch(trimmed, @"^<IfModule\s+md_module>", RegexOptions.IgnoreCase))
                {
                    skippingMdBlock = true;
                    continue;
                }

                if (skippingMdBlock)
                {
                    if (Regex.IsMatch(trimmed, @"^</IfModule>", RegexOptions.IgnoreCase))
                    {
                        skippingMdBlock = false;
                    }
                    continue;
                }

                if (MdAgreementRegex.IsMatch(trimmed) ||
                    MdContactEmailRegex.IsMatch(trimmed) ||
                    MdBaseServerRegex.IsMatch(trimmed) ||
                    MdCaRegex.IsMatch(trimmed) ||
                    trimmed == "# Let's Encrypt / mod_md Global Configuration" ||
                    trimmed.StartsWith("# ==="))
                {
                    continue;
                }
                cleaned.Add(line);
            }
            return cleaned;
        }

        public async Task<ModMdStatus> CheckModMdStatusAsync(string? apachePath = null)
        {
            string confPath = ResolveHttpdConfPath(apachePath);
            var status = new ModMdStatus
            {
                HttpdConfPath = confPath,
                HttpdConfExists = File.Exists(confPath)
            };

            if (!status.HttpdConfExists)
            {
                status.StatusMessage = $"httpd.conf not found at: {confPath}";
                return status;
            }

            try
            {
                var lines = await File.ReadAllLinesAsync(confPath, Encoding.UTF8);
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (Regex.IsMatch(trimmed, @"^LoadModule\s+md_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        status.IsMdModuleLoaded = true;
                    }
                    if (Regex.IsMatch(trimmed, @"^LoadModule\s+watchdog_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        status.IsWatchdogModuleLoaded = true;
                    }
                }

                if (status.IsMdModuleLoaded && status.IsWatchdogModuleLoaded)
                {
                    status.StatusMessage = $"mod_md and mod_watchdog are enabled in httpd.conf ({confPath}).";
                }
                else if (status.IsMdModuleLoaded)
                {
                    status.StatusMessage = $"mod_md is enabled in httpd.conf ({confPath}).";
                }
                else
                {
                    status.StatusMessage = $"mod_md is disabled or not included in httpd.conf ({confPath}).";
                }
            }
            catch (Exception ex)
            {
                status.StatusMessage = $"Error reading httpd.conf ({confPath}): {ex.Message}";
            }

            return status;
        }

        public async Task<(bool Success, string Message)> EnableModMdInHttpdConfAsync(string? apachePath = null)
        {
            string confPath = ResolveHttpdConfPath(apachePath);
            if (!File.Exists(confPath))
            {
                return (false, $"httpd.conf was not found at '{confPath}'. Please check your Apache path settings.");
            }

            try
            {
                await BackupConfigFileAsync(confPath);

                var lines = (await File.ReadAllLinesAsync(confPath, Encoding.UTF8)).ToList();
                bool mdEnabled = false;
                bool watchdogEnabled = false;
                int lastLoadModuleIndex = -1;

                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    string trimmed = line.Trim();

                    if (Regex.IsMatch(trimmed, @"^#?\s*LoadModule\s+", RegexOptions.IgnoreCase))
                    {
                        lastLoadModuleIndex = i;
                    }

                    if (Regex.IsMatch(trimmed, @"^#+\s*LoadModule\s+md_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        lines[i] = Regex.Replace(line, @"^\s*#+\s*", "");
                        mdEnabled = true;
                    }
                    else if (Regex.IsMatch(trimmed, @"^LoadModule\s+md_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        mdEnabled = true;
                    }

                    if (Regex.IsMatch(trimmed, @"^#+\s*LoadModule\s+watchdog_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        lines[i] = Regex.Replace(line, @"^\s*#+\s*", "");
                        watchdogEnabled = true;
                    }
                    else if (Regex.IsMatch(trimmed, @"^LoadModule\s+watchdog_module(\s+.*)?$", RegexOptions.IgnoreCase))
                    {
                        watchdogEnabled = true;
                    }
                }

                int insertPos = lastLoadModuleIndex >= 0 ? lastLoadModuleIndex + 1 : lines.Count;

                if (!watchdogEnabled)
                {
                    lines.Insert(insertPos, "LoadModule watchdog_module modules/mod_watchdog.so");
                    insertPos++;
                    watchdogEnabled = true;
                }

                if (!mdEnabled)
                {
                    lines.Insert(insertPos, "LoadModule md_module modules/mod_md.so");
                    insertPos++;
                    mdEnabled = true;
                }

                string tempFile = $"{confPath}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllLinesAsync(tempFile, lines, Encoding.UTF8);
                File.Move(tempFile, confPath, overwrite: true);

                return (true, $"Successfully enabled 'mod_md' and 'mod_watchdog' in '{confPath}'.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to update httpd.conf: {ex.Message}");
            }
        }

        private static List<string> GetDefaultPreamble()
        {
            return new List<string>
            {
                "# Virtual Hosts",
                "#",
                "# Required modules: mod_log_config",
                "#",
                "# If you want to maintain multiple domains/hostnames on your",
                "# machine you can setup VirtualHost containers for them.",
                "#"
            };
        }
    }
}
