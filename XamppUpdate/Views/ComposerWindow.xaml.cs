using System.Windows;
using XamppUpdate.ViewModels;

namespace XamppUpdate.Views
{
    public partial class ComposerWindow : Window
    {
        public ComposerWindow(ComposerViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            viewModel.RequestClose += () => Close();
        }
    }
}
