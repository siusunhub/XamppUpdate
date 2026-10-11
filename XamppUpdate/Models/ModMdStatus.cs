namespace XamppUpdate.Models
{
    public class ModMdStatus
    {
        public bool HttpdConfExists { get; set; }
        public string HttpdConfPath { get; set; } = string.Empty;
        public bool IsMdModuleLoaded { get; set; }
        public bool IsWatchdogModuleLoaded { get; set; }
        public bool IsFullyConfigured => IsMdModuleLoaded;
        public string StatusMessage { get; set; } = string.Empty;
    }
}
