using System.Windows;
using System.Windows.Controls;
using XamppUpdate.Models;

namespace XamppUpdate.Views.Controls
{
    public partial class StatusBadge : UserControl
    {
        public static readonly DependencyProperty StatusProperty =
            DependencyProperty.Register(nameof(Status), typeof(ServiceStatus), typeof(StatusBadge),
                new PropertyMetadata(ServiceStatus.Unknown, OnStatusChanged));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(StatusBadge),
                new PropertyMetadata("Unknown"));

        public ServiceStatus Status
        {
            get => (ServiceStatus)GetValue(StatusProperty);
            set => SetValue(StatusProperty, value);
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public StatusBadge()
        {
            InitializeComponent();
        }

        private static void OnStatusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StatusBadge badge && e.NewValue is ServiceStatus status)
            {
                if (string.IsNullOrEmpty(badge.Text) || badge.Text == "Unknown" || badge.Text == "Running" || badge.Text == "Stopped" || badge.Text == "Not Installed" || badge.Text == "Under Construction")
                {
                    badge.Text = status switch
                    {
                        ServiceStatus.Running => "Running",
                        ServiceStatus.Stopped => "Stopped",
                        ServiceStatus.NotInstalled => "Not Installed",
                        ServiceStatus.UnderConstruction => "Under Construction",
                        ServiceStatus.Updating => "Updating...",
                        ServiceStatus.Error => "Error",
                        _ => "Unknown"
                    };
                }
            }
        }
    }
}
