using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class VHostGlobalSettings : ObservableObject
    {
        [ObservableProperty]
        private bool _enableLetsEncrypt;

        partial void OnEnableLetsEncryptChanged(bool value)
        {
            if (value)
            {
                CertificateAgreement = "accepted";
            }
        }

        [ObservableProperty]
        private string _certificateAgreement = "accepted";

        [ObservableProperty]
        private string _contactEmail = string.Empty;

        [ObservableProperty]
        private bool _mdBaseServer;

        [ObservableProperty]
        private string _mdCertificateAuthority = string.Empty; // e.g. Let's Encrypt Staging / Production
    }
}
