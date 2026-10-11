using System;
using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class VHostManagerWindow : Window
    {
        private readonly VHostManagerViewModel _viewModel;

        public VHostManagerWindow(VHostManagerViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += async (s, e) =>
            {
                await _viewModel.InitializeAsync();
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.HasUnsavedChanges)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes to the VirtualHost configurations. Do you want to close without saving?",
                    "Unsaved Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            Close();
        }
    }
}
