using System.Windows;
using System.Windows.Controls;
using XamppUpdate.Models;

namespace XamppUpdate.Views.Controls
{
    public partial class SideBySideDiffViewer : UserControl
    {
        private bool _isSyncingScroll;

        public static readonly DependencyProperty DiffItemProperty =
            DependencyProperty.Register(nameof(DiffItem), typeof(ConfigDiffItem), typeof(SideBySideDiffViewer),
                new PropertyMetadata(null));

        public ConfigDiffItem? DiffItem
        {
            get => (ConfigDiffItem?)GetValue(DiffItemProperty);
            set => SetValue(DiffItemProperty, value);
        }

        public SideBySideDiffViewer()
        {
            InitializeComponent();
        }

        private void LeftScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSyncingScroll) return;

            if (e.VerticalChange != 0)
            {
                _isSyncingScroll = true;
                RightScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
                _isSyncingScroll = false;
            }
        }

        private void RightScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isSyncingScroll) return;

            if (e.VerticalChange != 0)
            {
                _isSyncingScroll = true;
                LeftScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
                _isSyncingScroll = false;
            }
        }
    }
}
