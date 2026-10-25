using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    /// <summary>
    /// Helper to detect and preserve exact file encodings (UTF-8 without BOM, UTF-8 with BOM, ANSI, UTF-16, etc.)
    /// and newline conventions (CRLF vs LF) when editing configuration files (ini, conf, etc.).
    /// </summary>
    public static class FileEncodingHelper
    {
        public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        public static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        public static Encoding DefaultEncoding => Utf8NoBom;

        static FileEncodingHelper()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch { }
        }

        /// <summary>
        /// Detects the text encoding of a file based on BOM or byte analysis.
        /// </summary>
        public static Encoding DetectEncoding(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return Utf8NoBom;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                return DetectEncoding(bytes);
            }
            catch
            {
                return Utf8NoBom;
            }
        }

        /// <summary>
        /// Detects encoding from a byte span (checks BOM first, then UTF-8 validity, then falls back to ANSI).
        /// </summary>
        public static Encoding DetectEncoding(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                return Utf8NoBom;
            }

            // 1. UTF-32 BOM
            if (bytes.Length >= 4)
            {
                if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                {
                    return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
                }
                if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                {
                    return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
                }
            }

            // 2. UTF-8 BOM (0xEF, 0xBB, 0xBF)
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Utf8WithBom;
            }

            // 3. UTF-16 BOM
            if (bytes.Length >= 2)
            {
                if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode;
                }
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return Encoding.Unicode;
                }
            }

            // 4. No BOM: Test if valid UTF-8
            if (IsValidUtf8(bytes))
            {
                return Utf8NoBom;
            }

            // 5. Fallback to Windows ANSI (e.g. Windows-1252 or system ANSI code page)
            return GetAnsiEncoding();
        }

        /// <summary>
        /// Checks whether the byte sequence conforms to valid UTF-8.
        /// </summary>
        public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
        {
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b <= 0x7F)
                {
                    // 1-byte ASCII (0x00 - 0x7F)
                    i++;
                }
                else if ((b & 0xE0) == 0xC0)
                {
                    // 2-byte sequence (110xxxxx 10xxxxxx)
                    if (b < 0xC2) return false; // Overlong encoding
                    if (i + 1 >= bytes.Length) return false;
                    if ((bytes[i + 1] & 0xC0) != 0x80) return false;
                    i += 2;
                }
                else if ((b & 0xF0) == 0xE0)
                {
                    // 3-byte sequence (1110xxxx 10xxxxxx 10xxxxxx)
                    if (i + 2 >= bytes.Length) return false;
                    byte c1 = bytes[i + 1];
                    byte c2 = bytes[i + 2];
                    if ((c1 & 0xC0) != 0x80 || (c2 & 0xC0) != 0x80) return false;
                    if (b == 0xE0 && c1 < 0xA0) return false; // Overlong
                    if (b == 0xED && c1 >= 0xA0) return false; // Surrogate
                    i += 3;
                }
                else if ((b & 0xF8) == 0xF0)
                {
                    // 4-byte sequence (11110xxx 10xxxxxx 10xxxxxx 10xxxxxx)
                    if (i + 3 >= bytes.Length) return false;
                    byte c1 = bytes[i + 1];
                    byte c2 = bytes[i + 2];
                    byte c3 = bytes[i + 3];
                    if ((c1 & 0xC0) != 0x80 || (c2 & 0xC0) != 0x80 || (c3 & 0xC0) != 0x80) return false;
                    if (b == 0xF0 && c1 < 0x90) return false; // Overlong
                    if (b == 0xF4 && c1 >= 0x90) return false; // Outside Unicode range
                    i += 4;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        private static Encoding GetAnsiEncoding()
        {
            try
            {
                int ansiCodePage = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (ansiCodePage > 0)
                {
                    return Encoding.GetEncoding(ansiCodePage);
                }
            }
            catch { }

            try
            {
                return Encoding.GetEncoding(1252);
            }
            catch
            {
                return Encoding.Latin1;
            }
        }

        /// <summary>
        /// Detects the predominant newline convention (\r\n vs \n).
        /// </summary>
        public static string DetectNewLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Environment.NewLine;
            }

            int crlf = 0;
            int lf = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        crlf++;
                        i++;
                    }
                }
                else if (text[i] == '\n')
                {
                    lf++;
                }
            }

            if (crlf > 0 && crlf >= lf)
            {
                return "\r\n";
            }
            if (lf > 0 && lf > crlf)
            {
                return "\n";
            }

            return Environment.NewLine;
        }

        /// <summary>
        /// Normalizes any newline style (\r\n, \r, or \n) to the target newline style.
        /// </summary>
        public static string NormalizeNewLines(string text, string targetNewLine)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\r\n|\r|\n", targetNewLine);
        }

        /// <summary>
        /// <summary>
        /// Checks whether a file path points to a server configuration file (Apache, PHP, MySQL, phpMyAdmin, Composer).
        /// </summary>
        public static bool IsConfigFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string name = Path.GetFileName(filePath).ToLowerInvariant();
            return ext is ".conf" or ".ini" or ".cnf" or ".php" or ".inc" or ".json"
                || name is ".htaccess" or "httpd.conf" or "my.ini" or "php.ini" or "config.inc.php" or "composer.json";
        }

        /// <summary>
        /// Ensures the encoding NEVER emits a Byte Order Mark (BOM).
        /// Apache (httpd.conf), PHP (php.ini, .php), and MySQL (my.ini) configuration parsers fail or corrupt
        /// if a UTF-8 BOM is present.
        /// </summary>
        public static Encoding EnsureNoBom(Encoding? encoding)
        {
            if (encoding == null || encoding is UTF8Encoding || encoding.CodePage == 65001)
            {
                return Utf8NoBom;
            }

            // If any encoding emits a preamble (such as UTF-16/32 with BOM), force UTF-8 without BOM
            // as server config files cannot parse multi-byte BOMs.
            if (encoding.GetPreamble().Length > 0)
            {
                return Utf8NoBom;
            }

            return encoding;
        }

        /// <summary>
        /// Strips any leading Unicode Byte Order Mark ('\uFEFF') from a text string.
        /// </summary>
        public static string StripLeadingBom(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            return content.TrimStart('\uFEFF');
        }

        /// <summary>
        /// Reads text from a byte span, explicitly skipping UTF-8 BOM bytes (0xEF, 0xBB, 0xBF)
        /// and stripping any leading '\uFEFF' character to guarantee clean configuration content.
        /// </summary>
        public static string ReadTextWithoutBom(ReadOnlySpan<byte> bytes, Encoding encoding)
        {
            if (bytes.Length == 0) return string.Empty;

            // If bytes start with UTF-8 BOM (0xEF, 0xBB, 0xBF), skip the 3 bytes
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes.Slice(3);
                encoding = Utf8NoBom;
            }

            string text = encoding.GetString(bytes);
            return StripLeadingBom(text);
        }

        /// <summary>
        /// Checks a physical file on disk and removes any UTF-8 BOM (0xEF, 0xBB, 0xBF) if present.
        /// </summary>
        public static void StripBomFromFile(string filePath)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    byte[] stripped = new byte[bytes.Length - 3];
                    Buffer.BlockCopy(bytes, 3, stripped, 0, stripped.Length);
                    File.WriteAllBytes(filePath, stripped);
                }
            }
            catch { }
        }

        /// <summary>
        /// Asynchronously checks a physical file on disk and removes any UTF-8 BOM (0xEF, 0xBB, 0xBF) if present.
        /// </summary>
        public static async Task StripBomFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    byte[] stripped = new byte[bytes.Length - 3];
                    Buffer.BlockCopy(bytes, 3, stripped, 0, stripped.Length);
                    await File.WriteAllBytesAsync(filePath, stripped, cancellationToken);
                }
            }
            catch { }
        }

        /// <summary>
        /// Detects both encoding and newline style of an existing file.
        /// Always ensures the returned encoding does NOT emit a BOM.
        /// </summary>
        public static (Encoding Encoding, string NewLine) DetectEncodingAndNewLine(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return (Utf8NoBom, Environment.NewLine);
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                var rawEncoding = DetectEncoding(bytes);
                var encoding = EnsureNoBom(rawEncoding);
                string text = ReadTextWithoutBom(bytes, rawEncoding);
                string newLine = DetectNewLine(text);
                return (encoding, newLine);
            }
            catch
            {
                return (Utf8NoBom, Environment.NewLine);
            }
        }

        /// <summary>
        /// Reads all text from a file, detecting its encoding automatically and stripping any BOM.
        /// </summary>
        public static async Task<string> ReadAllTextAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath)) return string.Empty;
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var encoding = DetectEncoding(bytes);
            return ReadTextWithoutBom(bytes, encoding);
        }

        /// <summary>
        /// Reads all text from a file synchronously, detecting its encoding automatically and stripping any BOM.
        /// </summary>
        public static string ReadAllText(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;
            byte[] bytes = File.ReadAllBytes(filePath);
            var encoding = DetectEncoding(bytes);
            return ReadTextWithoutBom(bytes, encoding);
        }

        /// <summary>
        /// Reads all lines from a file, detecting its encoding automatically and stripping any BOM.
        /// </summary>
        public static async Task<string[]> ReadAllLinesAsync(string filePath, Encoding? encoding = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath)) return Array.Empty<string>();
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            encoding ??= DetectEncoding(bytes);
            string text = ReadTextWithoutBom(bytes, encoding);
            var list = new List<string>();
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                list.Add(line);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Writes all text to a file, preserving the original file's newline style and non-BOM encoding.
        /// If the file is UTF-8 (with or without BOM) or new, it will ALWAYS be written in UTF-8 WITHOUT BOM
        /// to ensure Apache, PHP, and MySQL configuration parsers never encounter corrupting BOM bytes.
        /// Performs atomic replacement via a temporary file with post-write BOM verification.
        /// </summary>
        public static async Task WriteAllTextPreservingEncodingAsync(string filePath, string content, CancellationToken cancellationToken = default)
        {
            var (encoding, newLine) = DetectEncodingAndNewLine(filePath);
            encoding = EnsureNoBom(encoding);
            content = StripLeadingBom(content);
            content = NormalizeNewLines(content, newLine);

            string dir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = Path.Combine(dir, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, content, encoding, cancellationToken);

            // Double check: ensure no BOM byte sequence (0xEF, 0xBB, 0xBF) exists
            await StripBomFromFileAsync(tempFile, cancellationToken);

            File.Move(tempFile, filePath, overwrite: true);
        }

        /// <summary>
        /// Synchronously writes text preserving existing newline style and non-BOM encoding.
        /// Guarantees that UTF-8 BOM is never written.
        /// </summary>
        public static void WriteAllTextPreservingEncoding(string filePath, string content)
        {
            var (encoding, newLine) = DetectEncodingAndNewLine(filePath);
            encoding = EnsureNoBom(encoding);
            content = StripLeadingBom(content);
            content = NormalizeNewLines(content, newLine);

            string dir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = Path.Combine(dir, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempFile, content, encoding);

            // Double check: ensure no BOM byte sequence (0xEF, 0xBB, 0xBF) exists
            StripBomFromFile(tempFile);

            File.Move(tempFile, filePath, overwrite: true);
        }

        /// <summary>
        /// Writes lines to a file with specified encoding and newline atomically.
        /// Guarantees that UTF-8 BOM is never written.
        /// </summary>
        public static async Task WriteAllLinesAsync(string filePath, IEnumerable<string> lines, Encoding? encoding = null, string? newLine = null, CancellationToken cancellationToken = default)
        {
            encoding = EnsureNoBom(encoding ?? Utf8NoBom);
            newLine ??= Environment.NewLine;

            var lineList = lines.ToList();
            if (lineList.Count > 0 && !string.IsNullOrEmpty(lineList[0]))
            {
                lineList[0] = StripLeadingBom(lineList[0]);
            }

            string content = string.Join(newLine, lineList);
            if (!string.IsNullOrEmpty(content))
            {
                content += newLine;
            }

            string dir = Path.GetDirectoryName(filePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempFile = Path.Combine(dir, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, content, encoding, cancellationToken);

            // Double check: ensure no BOM byte sequence (0xEF, 0xBB, 0xBF) exists
            await StripBomFromFileAsync(tempFile, cancellationToken);

            File.Move(tempFile, filePath, overwrite: true);
        }
    }
}
