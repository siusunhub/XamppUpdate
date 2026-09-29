using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class GenerateDefaultSslCertWindow : Window
    {
        public GenerateDefaultSslCertWindow(GenerateDefaultSslCertViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }
    }
}
