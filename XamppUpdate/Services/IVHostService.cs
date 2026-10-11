using System.Collections.Generic;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IVHostService
    {
        Task<VHostParseResult> ParseVHostFileAsync(string filePath);
        Task SaveVHostFileAsync(string filePath, List<VHostEntry> entries, List<string>? preambleLines, VHostGlobalSettings? globalSettings, List<string>? trailingLines = null);
        Task<string> BackupConfigFileAsync(string filePath);
        Task<ModMdStatus> CheckModMdStatusAsync(string? apachePath = null);
        Task<(bool Success, string Message)> EnableModMdInHttpdConfAsync(string? apachePath = null);
    }
}
