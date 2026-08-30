using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class SslManagerWindow : Window
    {
        private readonly SslManagerViewModel _viewModel;

        public SslManagerWindow(SslManagerViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            DataContext = _viewModel;

            Loaded += async (_, _) => await _viewModel.InitializeAsync();
        }
    }
}
