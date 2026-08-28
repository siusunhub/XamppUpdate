using System;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public interface IWindowsServiceManager
    {
        bool ServiceExists(string serviceName);
        Task<ServiceControllerStatus?> GetServiceStatusAsync(string serviceName);
        Task<bool> StopServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null);
        Task<bool> StartServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null);
        Task<bool> WaitForStatusAsync(string serviceName, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress = null);
    }
}
