using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace XamppUpdate.Views.Controls
{
    public static class AutoScrollBehavior
    {
        public static readonly DependencyProperty AutoScrollProperty =
            DependencyProperty.RegisterAttached(
                "AutoScroll",
                typeof(bool),
                typeof(AutoScrollBehavior),
                new PropertyMetadata(false, OnAutoScrollPropertyChanged));

        public static bool GetAutoScroll(DependencyObject obj) => (bool)obj.GetValue(AutoScrollProperty);
        public static void SetAutoScroll(DependencyObject obj, bool value) => obj.SetValue(AutoScrollProperty, value);

        private static readonly DependencyProperty HandlerProperty =
            DependencyProperty.RegisterAttached("Handler", typeof(NotifyCollectionChangedEventHandler), typeof(AutoScrollBehavior));

        private static void OnAutoScrollPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListBox listBox) return;

            if ((bool)e.NewValue)
            {
                listBox.Loaded += ListBox_Loaded;
                var dpd = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));
                dpd?.AddValueChanged(listBox, OnItemsSourceChanged);

                HookCollectionChanged(listBox);
            }
            else
            {
                listBox.Loaded -= ListBox_Loaded;
                var dpd = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));
                dpd?.RemoveValueChanged(listBox, OnItemsSourceChanged);

                UnhookCollectionChanged(listBox);
            }
        }

        private static void ListBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListBox listBox)
            {
                HookCollectionChanged(listBox);
                ScrollToEnd(listBox);
            }
        }

        private static void OnItemsSourceChanged(object? sender, EventArgs e)
        {
            if (sender is ListBox listBox)
            {
                HookCollectionChanged(listBox);
                ScrollToEnd(listBox);
            }
        }

        private static void HookCollectionChanged(ListBox listBox)
        {
            UnhookCollectionChanged(listBox);

            if (listBox.ItemsSource is INotifyCollectionChanged incc)
            {
                NotifyCollectionChangedEventHandler handler = (s, args) =>
                {
                    ScrollToEnd(listBox);
                };

                incc.CollectionChanged += handler;
                listBox.SetValue(HandlerProperty, handler);
            }
        }

        private static void UnhookCollectionChanged(ListBox listBox)
        {
            if (listBox.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler handler)
            {
                if (listBox.ItemsSource is INotifyCollectionChanged incc)
                {
                    incc.CollectionChanged -= handler;
                }
                listBox.ClearValue(HandlerProperty);
            }
        }

        private static void ScrollToEnd(ListBox listBox)
        {
            listBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (listBox.Items.Count > 0)
                {
                    listBox.ScrollIntoView(listBox.Items[^1]);
                }
            }));
        }
    }
}
