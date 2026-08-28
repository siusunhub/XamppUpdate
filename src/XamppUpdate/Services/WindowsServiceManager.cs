using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;

namespace XamppUpdate.Services
{
    public class WindowsServiceManager : IWindowsServiceManager
    {
        public bool ServiceExists(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return false;

            try
            {
                return ServiceController.GetServices()
                    .Any(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private ServiceController? FindService(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName)) return null;

            try
            {
                var services = ServiceController.GetServices();
                return services.FirstOrDefault(s => string.Equals(s.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        public async Task<ServiceControllerStatus?> GetServiceStatusAsync(string serviceName)
        {
            return await Task.Run<ServiceControllerStatus?>(() =>
            {
                using var sc = FindService(serviceName);
                if (sc == null) return null;

                try
                {
                    sc.Refresh();
                    return sc.Status;
                }
                catch
                {
                    return null;
                }
            });
        }

        public async Task<bool> StopServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null)
                {
                    progress?.Report($"Service '{serviceName}' not found. Treating as stopped.");
                    return true;
                }

                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    progress?.Report($"Service '{serviceName}' is already stopped.");
                    return true;
                }

                try
                {
                    progress?.Report($"Sending stop request to service '{sc.ServiceName}'...");
                    if (sc.CanStop)
                    {
                        sc.Stop();
                    }
                    else
                    {
                        progress?.Report($"Warning: Service '{sc.ServiceName}' reports it cannot be stopped directly.");
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to request stop for '{serviceName}': {ex.Message}");
                }

                return await WaitForStatusInternalAsync(sc, ServiceControllerStatus.Stopped, timeout, progress);
            });
        }

        public async Task<bool> StartServiceAsync(string serviceName, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null)
                {
                    progress?.Report($"Service '{serviceName}' not found. Cannot start.");
                    return false;
                }

                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    progress?.Report($"Service '{serviceName}' is already running.");
                    return true;
                }

                try
                {
                    progress?.Report($"Sending start request to service '{sc.ServiceName}'...");
                    sc.Start();
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed to start service '{serviceName}': {ex.Message}");
                    return false;
                }

                return await WaitForStatusInternalAsync(sc, ServiceControllerStatus.Running, timeout, progress);
            });
        }

        public async Task<bool> WaitForStatusAsync(string serviceName, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress = null)
        {
            return await Task.Run(async () =>
            {
                using var sc = FindService(serviceName);
                if (sc == null) return false;
                return await WaitForStatusInternalAsync(sc, desiredStatus, timeout, progress);
            });
        }

        private async Task<bool> WaitForStatusInternalAsync(ServiceController sc, ServiceControllerStatus desiredStatus, TimeSpan timeout, IProgress<string>? progress)
        {
            var startTime = DateTime.UtcNow;
            var delayMs = 500;

            while (DateTime.UtcNow - startTime < timeout)
            {
                try
                {
                    sc.Refresh();
                    if (sc.Status == desiredStatus)
                    {
                        progress?.Report($"Service '{sc.ServiceName}' reached status: {desiredStatus}.");
                        return true;
                    }
                    progress?.Report($"Waiting for '{sc.ServiceName}' (Current: {sc.Status}, Target: {desiredStatus})...");
                }
                catch (Exception ex)
                {
                    progress?.Report($"Error checking service status: {ex.Message}");
                }

                await Task.Delay(delayMs);
            }

            progress?.Report($"Timed out waiting for service '{sc.ServiceName}' to reach status {desiredStatus}.");
            return false;
        }
    }
}
