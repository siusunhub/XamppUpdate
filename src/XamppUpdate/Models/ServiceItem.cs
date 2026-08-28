using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class ServiceItem : ObservableObject
    {
        [ObservableProperty]
        private ServiceType _type;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _displayName = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private string _version = "Checking...";

        [ObservableProperty]
        private ServiceStatus _status = ServiceStatus.Unknown;

        [ObservableProperty]
        private string _statusText = "Checking...";

        [ObservableProperty]
        private bool _canUpdate;

        [ObservableProperty]
        private bool _isUnderConstruction;

        [ObservableProperty]
        private string _iconGlyph = string.Empty;

        [ObservableProperty]
        private string _installPath = string.Empty;

        [ObservableProperty]
        private string _serviceName = string.Empty;

        [ObservableProperty]
        private bool _isBusy;
    }
}
