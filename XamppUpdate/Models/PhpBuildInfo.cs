using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class PhpBuildInfo : ObservableObject
    {
        [ObservableProperty]
        private string _version = "Unknown";

        [ObservableProperty]
        private string _architecture = "x64";

        [ObservableProperty]
        private bool _isThreadSafe = true;

        [ObservableProperty]
        private string _rawOutput = string.Empty;

        public string ThreadSafetyDisplay => IsThreadSafe ? "Thread Safe (TS)" : "Non-Thread Safe (NTS)";
        public string ArchitectureDisplay => Architecture == "x64" ? "64-bit (x64)" : (Architecture == "x86" ? "32-bit (x86)" : Architecture);
        public string FullDisplayString => $"{Version} · {ArchitectureDisplay} · {ThreadSafetyDisplay}";
        public bool IsApacheCompatible => IsThreadSafe;
    }
}
