using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace XamppUpdate
{
    public static class AppInfo
    {
        public const string AppName = "XAMPP Component Updater";

        public static string Version { get; } = ResolveMetadata("BuildVersion", "0.2");
        public static string BuildNumber { get; } = ResolveMetadata("BuildNumber", "0.2");
        public static string BuildDate { get; } = ResolveBuildDate();

        public static string WindowTitle => $"{AppName} v{Version} (Build {BuildNumber} · {BuildDate})";

        private static string ResolveMetadata(string key, string defaultValue)
        {
            try
            {
                var attr = typeof(AppInfo).Assembly
                    .GetCustomAttributes<AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(attr?.Value))
                {
                    return attr.Value;
                }
            }
            catch { }

            return defaultValue;
        }

        private static string ResolveBuildDate()
        {
            string fromMetadata = ResolveMetadata("BuildDate", string.Empty);
            if (!string.IsNullOrEmpty(fromMetadata))
            {
                return fromMetadata;
            }

            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
                {
                    return File.GetLastWriteTime(Environment.ProcessPath).ToString("yyyy-MM-dd");
                }
            }
            catch { }

            return "2026-09-01";
        }
    }
}
