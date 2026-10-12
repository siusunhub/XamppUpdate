using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class UatUpgradeWindow : Window
    {
        private readonly UatUpgradeViewModel _vm;

        public UatUpgradeWindow(UatUpgradeViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            if (!string.IsNullOrEmpty(vm.AdminPassword))
            {
                AdminPasswordBox.Password = vm.AdminPassword;
            }
        }

        private void AdminPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox pb) _vm.AdminPassword = pb.Password;
        }

        // The confirmation must come BEFORE the delete, so the handler (not a Command binding) owns the call.
        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.LastReport == null || _vm.LastReport.OwnedPaths.Count == 0 || !_vm.CanDeleteCopy) return;

            string paths = string.Join(Environment.NewLine, _vm.LastReport.OwnedPaths);
            string service = string.IsNullOrEmpty(_vm.LastReport.OwnedServiceName)
                ? string.Empty
                : $"\n\nThe test service '{_vm.LastReport.OwnedServiceName}' will be stopped and removed first (if it still exists).";
            var answer = MessageBox.Show(
                $"Permanently delete these folders?\n\n{paths}{service}",
                "Delete UAT copy", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            await _vm.DeleteCopyCommand.ExecuteAsync(null);
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            string text = _vm.BuildLogText();
            try
            {
                // SetDataObject(copy: true) keeps the text after the app exits and retries on a busy clipboard.
                Clipboard.SetDataObject(text, true);
                _vm.Summary = $"Log copied to the clipboard ({text.Length:N0} characters).";
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; show the text so it can be copied manually.
                MessageBox.Show($"Could not access the clipboard ({ex.Message}).\n\n{text}",
                    "Copy log", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // Closing requests cancellation and waits for the run to finish its cleanup instead of abandoning it.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm.IsRunning)
            {
                _vm.CancelCommand.Execute(null);
                MessageBox.Show(
                    "A run is in progress. It was asked to cancel; the window can be closed once cleanup finishes.",
                    "UAT", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }
    }
}
