using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace XamppUpdate.Views
{
    public partial class ClearTempProgressWindow : Window, INotifyPropertyChanged
    {
        private readonly string _targetDirectory;
        private CancellationTokenSource? _cts;

        private string _statusText = "Initializing cleanup...";
        private string _currentItemName = string.Empty;
        private string _progressPercentText = string.Empty;
        private string _progressCountText = string.Empty;
        private string _detailedResultText = string.Empty;
        private int _currentItem;
        private int _totalItems = 1;
        private bool _isIndeterminate = true;
        private bool _canCancel = true;
        private bool _isFinished = false;

        public int DeletedFilesCount { get; private set; }
        public int DeletedDirsCount { get; private set; }
        public int FailedCount { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(nameof(StatusText)); }
        }

        public string CurrentItemName
        {
            get => _currentItemName;
            set { _currentItemName = value; OnPropertyChanged(nameof(CurrentItemName)); }
        }

        public string ProgressPercentText
        {
            get => _progressPercentText;
            set { _progressPercentText = value; OnPropertyChanged(nameof(ProgressPercentText)); }
        }

        public string ProgressCountText
        {
            get => _progressCountText;
            set { _progressCountText = value; OnPropertyChanged(nameof(ProgressCountText)); }
        }

        public string DetailedResultText
        {
            get => _detailedResultText;
            set { _detailedResultText = value; OnPropertyChanged(nameof(DetailedResultText)); }
        }

        public int CurrentItem
        {
            get => _currentItem;
            set { _currentItem = value; OnPropertyChanged(nameof(CurrentItem)); }
        }

        public int TotalItems
        {
            get => _totalItems;
            set { _totalItems = value; OnPropertyChanged(nameof(TotalItems)); }
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set { _isIndeterminate = value; OnPropertyChanged(nameof(IsIndeterminate)); }
        }

        public bool CanCancel
        {
            get => _canCancel;
            set { _canCancel = value; OnPropertyChanged(nameof(CanCancel)); }
        }

        public bool IsFinished
        {
            get => _isFinished;
            set { _isFinished = value; OnPropertyChanged(nameof(IsFinished)); }
        }

        public ClearTempProgressWindow(string targetDirectory)
        {
            InitializeComponent();
            DataContext = this;
            _targetDirectory = targetDirectory;
            Loaded += ClearTempProgressWindow_Loaded;
            Closing += ClearTempProgressWindow_Closing;
        }

        private void ClearTempProgressWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (!_isFinished)
            {
                _cts?.Cancel();
            }
        }

        private async void ClearTempProgressWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            await StartClearingAsync(_cts.Token);
        }

        private async Task StartClearingAsync(CancellationToken token)
        {
            try
            {
                StatusText = "Scanning temporary directory...";
                IsIndeterminate = true;

                string[] files = Array.Empty<string>();
                string[] subdirs = Array.Empty<string>();

                // Scan files in background
                await Task.Run(() =>
                {
                    if (Directory.Exists(_targetDirectory))
                    {
                        try
                        {
                            files = Directory.GetFiles(_targetDirectory, "*", SearchOption.AllDirectories);
                        }
                        catch { }

                        try
                        {
                            subdirs = Directory.GetDirectories(_targetDirectory, "*", SearchOption.AllDirectories);
                        }
                        catch { }
                    }
                }, token);

                int totalCount = files.Length + subdirs.Length;
                if (totalCount == 0)
                {
                    IsIndeterminate = false;
                    TotalItems = 1;
                    CurrentItem = 1;
                    StatusText = "Temporary directory is already clean.";
                    ProgressPercentText = "100%";
                    DetailedResultText = "No files or folders found.";
                    CanCancel = false;
                    IsFinished = true;
                    return;
                }

                TotalItems = totalCount;
                CurrentItem = 0;
                IsIndeterminate = false;
                StatusText = $"Deleting {files.Length} file(s) and {subdirs.Length} folder(s)...";

                int processed = 0;
                int filesDeleted = 0;
                int dirsDeleted = 0;
                int failed = 0;

                DateTime lastUiUpdate = DateTime.MinValue;

                // Delete items on background thread with throttled UI dispatch
                await Task.Run(() =>
                {
                    foreach (var file in files)
                    {
                        if (token.IsCancellationRequested) break;

                        string fileName = Path.GetFileName(file);
                        processed++;

                        bool isLast = (processed == totalCount);
                        if (isLast || (DateTime.UtcNow - lastUiUpdate).TotalMilliseconds > 30)
                        {
                            lastUiUpdate = DateTime.UtcNow;
                            int currentProcessed = processed;
                            Dispatcher.InvokeAsync(() =>
                            {
                                CurrentItemName = fileName;
                                CurrentItem = currentProcessed;
                                int pct = (int)((double)currentProcessed / totalCount * 100);
                                ProgressPercentText = $"{pct}%";
                                ProgressCountText = $"{currentProcessed} / {totalCount}";
                            });
                        }

                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            filesDeleted++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            System.Diagnostics.Debug.WriteLine($"Failed to delete temp file '{file}': {ex.Message}");
                        }
                    }

                    // Sort subdirectories descending so inner folders are deleted before parent folders
                    var sortedDirs = subdirs.OrderByDescending(d => d.Length).ToArray();

                    foreach (var dir in sortedDirs)
                    {
                        if (token.IsCancellationRequested) break;

                        string dirName = Path.GetFileName(dir);
                        processed++;

                        bool isLast = (processed == totalCount);
                        if (isLast || (DateTime.UtcNow - lastUiUpdate).TotalMilliseconds > 30)
                        {
                            lastUiUpdate = DateTime.UtcNow;
                            int currentProcessed = processed;
                            Dispatcher.InvokeAsync(() =>
                            {
                                CurrentItemName = dirName;
                                CurrentItem = currentProcessed;
                                int pct = (int)((double)currentProcessed / totalCount * 100);
                                ProgressPercentText = $"{pct}%";
                                ProgressCountText = $"{currentProcessed} / {totalCount}";
                            });
                        }

                        try
                        {
                            Directory.Delete(dir, true);
                            dirsDeleted++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            System.Diagnostics.Debug.WriteLine($"Failed to delete temp dir '{dir}': {ex.Message}");
                        }
                    }
                }, token);

                DeletedFilesCount = filesDeleted;
                DeletedDirsCount = dirsDeleted;
                FailedCount = failed;

                CurrentItem = totalCount;
                ProgressPercentText = "100%";
                ProgressCountText = $"{totalCount} / {totalCount}";
                CurrentItemName = string.Empty;

                if (token.IsCancellationRequested)
                {
                    StatusText = "Cleanup stopped by user.";
                    DetailedResultText = $"Removed {filesDeleted} file(s), {dirsDeleted} folder(s).";
                }
                else
                {
                    StatusText = "Temporary files cleanup completed!";
                    DetailedResultText = failed > 0
                        ? $"Removed {filesDeleted} file(s), {dirsDeleted} folder(s) ({failed} skipped/in-use)."
                        : $"Successfully removed {filesDeleted} file(s) and {dirsDeleted} folder(s).";
                }
            }
            catch (Exception ex)
            {
                StatusText = "Error during cleanup.";
                DetailedResultText = ex.Message;
            }
            finally
            {
                CanCancel = false;
                IsFinished = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            StatusText = "Cancelling cleanup...";
            CanCancel = false;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
