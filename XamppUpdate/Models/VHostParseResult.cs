using System.Collections.Generic;

namespace XamppUpdate.Models
{
    public class VHostParseResult
    {
        public List<VHostEntry> Entries { get; set; } = new();
        public List<string> PreambleLines { get; set; } = new();
        public List<string> TrailingLines { get; set; } = new();
        public VHostGlobalSettings GlobalSettings { get; set; } = new();
        public bool Success { get; set; } = true;
        public string? ErrorMessage { get; set; }
    }
}
