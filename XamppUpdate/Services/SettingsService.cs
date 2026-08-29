using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public class SettingsService : ISettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly SemaphoreSlim _fileLock = new(1, 1);
        private readonly string _configFilePath;
        private AppSettings _currentSettings;

        public event EventHandler<AppSettings>? SettingsChanged;

        public string ConfigFilePath => _configFilePath;
        public AppSettings CurrentSettings => _currentSettings;

        public const string DefaultConfigFileName = "XamppUpdate.config.json";

        public SettingsService(string? customConfigPath = null)
        {
            _configFilePath = customConfigPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultConfigFileName);
            _currentSettings = LoadSettings();
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath, Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        _currentSettings = settings;
                        return _currentSettings;
                    }
                }
                else
                {
                    // Fallback migration: check if legacy config/settings.json exists
                    string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "settings.json");
                    if (File.Exists(legacyPath))
                    {
                        string json = File.ReadAllText(legacyPath, Encoding.UTF8);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                        if (settings != null)
                        {
                            _currentSettings = settings;
                            SaveSettingsSync(_currentSettings);
                            return _currentSettings;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }

            _currentSettings = new AppSettings();
            SaveSettingsSync(_currentSettings);
            return _currentSettings;
        }

        private void SaveSettingsSync(AppSettings settings)
        {
            try
            {
                _fileLock.Wait();
                try
                {
                    string? directory = Path.GetDirectoryName(_configFilePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(_configFilePath, json, Encoding.UTF8);
                }
                finally
                {
                    _fileLock.Release();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save initial settings: {ex.Message}");
            }
        }

        public async Task SaveSettingsAsync(AppSettings settings)
        {
            await _fileLock.WaitAsync();
            try
            {
                string? directory = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonSerializer.Serialize(settings, JsonOptions);
                await File.WriteAllTextAsync(_configFilePath, json, Encoding.UTF8);
                _currentSettings = settings;
                SettingsChanged?.Invoke(this, _currentSettings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not save configuration to '{_configFilePath}': {ex.Message}", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        public void ResetToDefaults()
        {
            _currentSettings = new AppSettings();
            _ = SaveSettingsAsync(_currentSettings);
        }
    }
}
