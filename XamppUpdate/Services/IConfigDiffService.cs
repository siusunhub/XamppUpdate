using System.Collections.Generic;
using System.Threading.Tasks;
using XamppUpdate.Models;

namespace XamppUpdate.Services
{
    public interface IConfigDiffService
    {
        Task<List<ConfigDiffItem>> CompareConfigFilesAsync(string localApacheRoot, string incomingApacheRoot, IEnumerable<string> relativeConfigPaths);
        Task<ConfigDiffItem> CompareSingleFileAsync(string localFilePath, string incomingFilePath, string relativeFilePath);
    }
}
