using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace XamppUpdate.Views
{
    public partial class ErrorDialogWindow : Window
    {
        private readonly DispatcherTimer _copyNoticeTimer;
        private string _fullDiagnosticText = string.Empty;

        public ErrorDialogWindow()
        {
            InitializeComponent();

            _copyNoticeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _copyNoticeTimer.Tick += (s, e) =>
            {
                _copyNoticeTimer.Stop();
                BorderCopiedNotice.Visibility = Visibility.Collapsed;
            };
        }

        public void SetException(Exception ex, string? customTitle = null)
        {
            ArgumentNullException.ThrowIfNull(ex);

            if (!string.IsNullOrWhiteSpace(customTitle))
            {
                Title = customTitle;
                TxtHeaderTitle.Text = customTitle;
            }

            TxtExceptionType.Text = ex.GetType().FullName ?? ex.GetType().Name;
            TxtTimestamp.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            TxtMessage.Text = ex.Message;

            var sb = new StringBuilder();
            sb.AppendLine("=== DIAGNOSTIC DETAILS ===");
            sb.AppendLine($"Timestamp    : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Application  : {Assembly.GetExecutingAssembly().GetName().Name} v{Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine($"OS           : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET Runtime : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine();
            sb.AppendLine("=== EXCEPTION DETAILS ===");
            sb.AppendLine($"Type    : {ex.GetType().FullName}");
            sb.AppendLine($"Message : {ex.Message}");
            sb.AppendLine($"Source  : {ex.Source}");
            if (ex.TargetSite != null)
            {
                sb.AppendLine($"TargetSite: {ex.TargetSite}");
            }
            sb.AppendLine();
            sb.AppendLine("=== STACK TRACE ===");
            sb.AppendLine(string.IsNullOrWhiteSpace(ex.StackTrace) ? "(No stack trace available)" : ex.StackTrace);

            var inner = ex.InnerException;
            int depth = 1;
            while (inner != null)
            {
                sb.AppendLine();
                sb.AppendLine($"=== INNER EXCEPTION #{depth} ===");
                sb.AppendLine($"Type    : {inner.GetType().FullName}");
                sb.AppendLine($"Message : {inner.Message}");
                sb.AppendLine($"Stack   :\n{inner.StackTrace}");
                inner = inner.InnerException;
                depth++;
            }

            _fullDiagnosticText = sb.ToString();
            TxtStackTrace.Text = _fullDiagnosticText;
        }

        public void SetManualError(string message, string? stackTrace = null, string? customTitle = null)
        {
            if (!string.IsNullOrWhiteSpace(customTitle))
            {
                Title = customTitle;
                TxtHeaderTitle.Text = customTitle;
            }

            TxtExceptionType.Text = "Custom Debug Trace";
            TxtTimestamp.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            TxtMessage.Text = message;

            var sb = new StringBuilder();
            sb.AppendLine("=== DIAGNOSTIC DETAILS ===");
            sb.AppendLine($"Timestamp    : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Application  : {Assembly.GetExecutingAssembly().GetName().Name} v{Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine($"OS           : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET Runtime : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine();
            sb.AppendLine("=== MESSAGE ===");
            sb.AppendLine(message);
            sb.AppendLine();
            sb.AppendLine("=== STACK TRACE ===");
            sb.AppendLine(string.IsNullOrWhiteSpace(stackTrace) ? Environment.StackTrace : stackTrace);

            _fullDiagnosticText = sb.ToString();
            TxtStackTrace.Text = _fullDiagnosticText;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetDataObject(_fullDiagnosticText, true);
                BorderCopiedNotice.Visibility = Visibility.Visible;
                _copyNoticeTimer.Stop();
                _copyNoticeTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to copy to clipboard: {ex.Message}", "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            TxtStackTrace.Focus();
            TxtStackTrace.SelectAll();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Displays an error popup with full C# stack trace and copy support.
        /// Thread-safe: can be called from any background or UI thread.
        /// </summary>
        public static void Show(Exception ex, string? title = null, Window? owner = null)
        {
            ExecuteOnUi(() =>
            {
                var dialog = new ErrorDialogWindow();
                dialog.SetException(ex, title);

                var targetOwner = owner ?? Application.Current?.MainWindow;
                if (targetOwner != null && targetOwner.IsVisible && targetOwner != dialog)
                {
                    dialog.Owner = targetOwner;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                dialog.ShowDialog();
            });
        }

        /// <summary>
        /// Displays a custom error trace popup.
        /// Thread-safe: can be called from any background or UI thread.
        /// </summary>
        public static void Show(string message, string? stackTrace = null, string? title = null, Window? owner = null)
        {
            ExecuteOnUi(() =>
            {
                var dialog = new ErrorDialogWindow();
                dialog.SetManualError(message, stackTrace, title);

                var targetOwner = owner ?? Application.Current?.MainWindow;
                if (targetOwner != null && targetOwner.IsVisible && targetOwner != dialog)
                {
                    dialog.Owner = targetOwner;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                dialog.ShowDialog();
            });
        }

        private static void ExecuteOnUi(Action action)
        {
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted)
            {
                if (app.Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    app.Dispatcher.Invoke(action);
                }
            }
            else
            {
                // Fallback for thread when application dispatcher is unavailable
                var thread = new Thread(() => action());
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            }
        }
    }
}
