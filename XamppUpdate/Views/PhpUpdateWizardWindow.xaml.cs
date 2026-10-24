using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class PhpUpdateWizardWindow : Window
    {
        public PhpUpdateWizardWindow(PhpWizardViewModel viewModel)
        {
            InitializeComponent();
            Title = $"PHP Update Wizard - {AppInfo.AppName} v{AppInfo.Version}";
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }

        /// <summary>Opens the upload_progress context menu on a normal (left) click as well as right click.</summary>
        private void UploadProgressButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.ContextMenu == null) return;

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }
    }
}
