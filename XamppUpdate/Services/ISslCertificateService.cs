using System.Collections.Generic;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface ISslCertificateService
    {
        Task<List<SslCertificateInfo>> DetectCertificatesAsync(string apacheInstallationPath);
        Task<bool> UpdateCertificateAsync(SslCertificateInfo targetCert, string newCertPath, string newKeyPath, string? newChainPath, bool backupOld = true);
        Task<SslCertificateInfo> ReadDefaultXamppCertificateAsync(string? apachePath = null);
        Task<GenerateDefaultSslCertResult> GenerateDefaultCertificateAsync(GenerateDefaultSslCertRequest request, IProgress<string>? progress = null);
        void OpenFileInExplorer(string filePath);
        void OpenCertificateInViewer(string certificateFilePath);
    }
}
