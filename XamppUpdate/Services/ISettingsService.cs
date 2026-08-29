using System;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface ISettingsService
    {
        AppSettings CurrentSettings { get; }
        AppSettings LoadSettings();
        Task SaveSettingsAsync(AppSettings settings);
        void ResetToDefaults();
        event EventHandler<AppSettings>? SettingsChanged;
        string ConfigFilePath { get; }
    }
}
