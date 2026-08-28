using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IVersionDetectionService
    {
        Task<string> DetectApacheVersionAsync(string installationPath);
        Task<string> DetectPhpVersionAsync(string installationPath);
        Task<string> DetectMySqlVersionAsync(string installationPath);
        Task<string> DetectComposerVersionAsync(string executablePath);
        Task<string> DetectSslStatusAsync(string certificatesPath);
    }
}
