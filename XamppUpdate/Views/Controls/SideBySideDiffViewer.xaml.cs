using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.Views.Controls
{
    public partial class SideBySideDiffViewer : UserControl
    {
        private bool _isSyncingScroll;
        private readonly IConfigDiffService _diffService = new ConfigDiffService();
        private readonly DispatcherTimer _noticeTimer;

        public static readonly DependencyProperty DiffItemProperty =
            DependencyProperty.Register(nameof(DiffItem), typeof(ConfigDiffItem), typeof(SideBySideDiffViewer),
                new PropertyMetadata(null, OnDiffItemChangedCallback));

        public ConfigDiffItem? DiffItem
        {
            get => (ConfigDiffItem?)GetValue(DiffItemProperty);
            set => SetValue(DiffItemProperty, value);
        }

        public SideBySideDiffViewer()
        {
            InitializeComponent();

            _noticeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _noticeTimer.Tick += (s, e) =>
            {
                _noticeTimer.Stop();
                BorderActionNotice.Visibility = Visibility.Collapsed;
            };
        }

        private static void OnDiffItemChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SideBySideDiffViewer viewer && e.NewValue is ConfigDiffItem item)
            {
                viewer.OnDiffItemAssigned(item);
            }
        }

        private void OnDiffItemAssigned(ConfigDiffItem item)
        {
            if (string.IsNullOrEmpty(item.MergedContent) && !string.IsNullOrEmpty(item.OriginalLocalContent))
            {
                item.MergedContent = item.OriginalLocalContent;
            }

            TxtDirectEditor.Text = item.MergedContent;
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;

            if (RbDiffMode.IsChecked == true)
            {
                GridDiffView.Visibility = Visibility.Visible;
                GridEditView.Visibility = Visibility.Collapsed;
            }
            else if (RbEditMode.IsChecked == true)
            {
                GridDiffView.Visibility = Visibility.Collapsed;
                GridEditView.Visibility = Visibility.Visible;
                if (DiffItem != null)
                {
                    TxtDirectEditor.Text = DiffItem.MergedContent;
                }
            }
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

        private void BtnCopyAllLocal_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            string text = !string.IsNullOrEmpty(DiffItem.MergedContent)
                ? DiffItem.MergedContent
                : DiffItem.OriginalLocalContent;

            CopyToClipboard(text, "✓ Copied current local config to clipboard");
        }

        private void BtnCopyAllIncoming_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            CopyToClipboard(DiffItem.IncomingContent, "✓ Copied incoming config to clipboard");
        }

        private void BtnCopyLine_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DiffLineModel line && !string.IsNullOrEmpty(line.Text))
            {
                CopyToClipboard(line.Text, "✓ Copied line to clipboard");
            }
        }

        private void BtnInsertLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffLineModel incomingLine) return;

            int index = DiffItem.IncomingLines.IndexOf(incomingLine);
            if (index < 0) return;

            var localLines = new List<string>(NormalizeLines(DiffItem.MergedContent));

            // Check if there is a corresponding local line
            DiffLineModel? localCorresponding = index < DiffItem.LocalLines.Count ? DiffItem.LocalLines[index] : null;

            if (localCorresponding != null && localCorresponding.LineNumber.HasValue && localCorresponding.LineNumber.Value > 0)
            {
                int targetLineIndex = localCorresponding.LineNumber.Value - 1;
                if (targetLineIndex >= 0 && targetLineIndex < localLines.Count)
                {
                    // Replace existing line
                    localLines[targetLineIndex] = incomingLine.Text;
                }
                else
                {
                    localLines.Add(incomingLine.Text);
                }
            }
            else
            {
                // Find nearest previous line in local diff that has a line number
                int insertPos = 0;
                for (int i = index - 1; i >= 0; i--)
                {
                    if (i < DiffItem.LocalLines.Count && DiffItem.LocalLines[i].LineNumber.HasValue)
                    {
                        insertPos = DiffItem.LocalLines[i].LineNumber!.Value;
                        break;
                    }
                }

                if (insertPos >= 0 && insertPos <= localLines.Count)
                {
                    localLines.Insert(insertPos, incomingLine.Text);
                }
                else
                {
                    localLines.Add(incomingLine.Text);
                }
            }

            DiffItem.MergedContent = string.Join(Environment.NewLine, localLines);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;

            ShowNotice("✓ Inserted incoming line into merged config");
        }

        private void BtnApplyDirectEdit_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;

            DiffItem.MergedContent = TxtDirectEditor.Text;
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            ShowNotice("✓ Applied direct edits to merged config");
        }

        private void BtnRevertLocal_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;

            TxtDirectEditor.Text = DiffItem.OriginalLocalContent;
            DiffItem.MergedContent = DiffItem.OriginalLocalContent;
            DiffItem.Resolution = ConfigFileResolution.KeepCurrent;
            DiffItem.IsCustomMerged = false;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            ShowNotice("✓ Reverted to original local config");
        }

        private void BtnFillIncoming_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;

            TxtDirectEditor.Text = DiffItem.IncomingContent;
            DiffItem.MergedContent = DiffItem.IncomingContent;
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            ShowNotice("✓ Filled editor with incoming default config");
        }

        private void BtnCopyEditorText_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(TxtDirectEditor.Text, "✓ Copied editor text to clipboard");
        }

        private void CopyToClipboard(string text, string successMessage)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                ShowNotice(successMessage);
            }
            catch (Exception ex)
            {
                ShowNotice($"Failed to copy: {ex.Message}");
            }
        }

        private void ShowNotice(string message)
        {
            TxtActionNotice.Text = message;
            BorderActionNotice.Visibility = Visibility.Visible;
            _noticeTimer.Stop();
            _noticeTimer.Start();
        }

        private static string[] NormalizeLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}
