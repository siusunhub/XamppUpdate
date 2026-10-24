using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XamppUpdate.Models;
using XamppUpdate.Services;

namespace XamppUpdate.Views.Controls
{
    public partial class SideBySideDiffViewer : UserControl
    {
        private readonly IConfigDiffService _diffService = new ConfigDiffService();
        private readonly DispatcherTimer _noticeTimer;
        private ListCollectionView? _rowsView;
        private readonly List<DiffRowModel> _differenceSections = new();
        private int _currentSection = -1;
        private DiffRowModel? _positionRow;
        private TextBox? _lineEditor;
        private DiffRowModel? _editingRow;
        private readonly Stack<InsertUndoState> _insertUndo = new();
        private sealed record InsertUndoState(string Content, ConfigFileResolution Resolution, bool IsCustomMerged, DiffRowModel Position);

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
            if (d is SideBySideDiffViewer viewer)
            {
                viewer.CancelLineEdit();
                viewer.ClearInsertUndo();
                viewer._positionRow = null;
                if (e.OldValue is ConfigDiffItem oldItem)
                    PropertyChangedEventManager.RemoveHandler(oldItem, viewer.DiffItem_PropertyChanged, nameof(ConfigDiffItem.DiffRows));

                if (e.NewValue is ConfigDiffItem item)
                {
                    PropertyChangedEventManager.AddHandler(item, viewer.DiffItem_PropertyChanged, nameof(ConfigDiffItem.DiffRows));
                    viewer.OnDiffItemAssigned(item);
                }
                else
                {
                    viewer.TxtDirectEditor.Clear();
                }
                viewer.RefreshDiffRows();
            }
        }

        private void DiffItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            RefreshDiffRows();
        }

        private static bool IsDifferent(DiffRowModel row)
        {
            return IsChangedLine(row.Local.Type) || IsChangedLine(row.Incoming.Type);
        }

        private static bool IsChangedLine(DiffLineType type)
        {
            return type is DiffLineType.Inserted or DiffLineType.Deleted or DiffLineType.Modified;
        }

        private void RefreshDiffRows()
        {
            CancelLineEdit();
            var position = _positionRow;
            _differenceSections.Clear();
            _currentSection = -1;
            var rows = DiffItem?.DiffRows ?? new List<DiffRowModel>();
            bool previousWasDifferent = false;
            foreach (var row in rows)
            {
                bool different = IsDifferent(row);
                if (different && !previousWasDifferent)
                    _differenceSections.Add(row);
                previousWasDifferent = different;
            }

            // A private view keeps filtering local to this viewer and leaves merge indices intact.
            _rowsView = new ListCollectionView(rows);
            ApplyDifferenceFilter();
            DiffList.ItemsSource = _rowsView;
            if (position != null && rows.Count > 0)
            {
                // Incoming line numbers stay stable when local lines are inserted or replaced.
                var restored = rows.FirstOrDefault(row => position.Incoming.LineNumber.HasValue
                    && row.Incoming.LineNumber == position.Incoming.LineNumber)
                    ?? rows.FirstOrDefault(row => position.Local.LineNumber.HasValue
                        && row.Local.LineNumber == position.Local.LineNumber)
                    ?? rows[Math.Min(position.Index, rows.Count - 1)];
                SetNavigationPosition(restored);
                if (_rowsView.Contains(restored))
                    DiffList.SelectedItem = restored;
                var visibleRow = _rowsView.Contains(restored) ? restored
                    : rows.FirstOrDefault(row => row.Index > restored.Index && IsDifferent(row))
                        ?? rows.LastOrDefault(IsDifferent);
                if (visibleRow != null) DiffList.ScrollIntoView(visibleRow);
            }
            else
            {
                _positionRow = null;
            }
            UpdateDifferenceNavigation();
        }

        private void ApplyDifferenceFilter()
        {
            if (_rowsView == null) return;
            _rowsView.Filter = ChkDifferencesOnly.IsChecked == true
                ? item => item is DiffRowModel row && IsDifferent(row)
                : null;
        }

        private void DifferencesOnly_Changed(object sender, RoutedEventArgs e)
        {
            var selectedRow = DiffList?.SelectedItem as DiffRowModel;
            ApplyDifferenceFilter();
            if (selectedRow != null && _rowsView?.Contains(selectedRow) == true)
            {
                DiffList!.SelectedItem = selectedRow;
                DiffList.ScrollIntoView(selectedRow);
            }
        }

        private void BtnPreviousDifference_Click(object sender, RoutedEventArgs e)
        {
            JumpToDifference(PreviousSectionIndex);
        }

        private void BtnNextDifference_Click(object sender, RoutedEventArgs e)
        {
            JumpToDifference(_currentSection + 1);
        }

        private void JumpToDifference(int section)
        {
            if (section < 0 || section >= _differenceSections.Count) return;
            _currentSection = section;
            var row = _differenceSections[section];
            SetNavigationPosition(row);
            DiffList.SelectedItem = row;
            DiffList.ScrollIntoView(row);
            UpdateDifferenceNavigation();
        }

        private void DiffList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DiffList.SelectedItem is DiffRowModel row)
            {
                SetNavigationPosition(row);
            }
            UpdateDifferenceNavigation();
        }

        private void SetNavigationPosition(DiffRowModel row)
        {
            _positionRow = row;
            _currentSection = _differenceSections.FindLastIndex(start => start.Index <= row.Index);
        }

        private int PreviousSectionIndex => _positionRow != null && !IsDifferent(_positionRow)
            ? _currentSection : _currentSection - 1;

        private void UpdateDifferenceNavigation()
        {
            BtnPreviousDifference.IsEnabled = PreviousSectionIndex >= 0;
            BtnNextDifference.IsEnabled = _currentSection + 1 < _differenceSections.Count;
            TxtDifferencePosition.Text = _differenceSections.Count == 0
                ? "No differences"
                : _positionRow != null && !IsDifferent(_positionRow)
                    ? $"Between differences ({_differenceSections.Count} blocks)"
                : _currentSection < 0
                    ? $"{_differenceSections.Count} difference block{(_differenceSections.Count == 1 ? "" : "s")}"
                    : $"Difference {_currentSection + 1} of {_differenceSections.Count}";

            UpdateToolbarBlockButtons();
        }

        private void UpdateToolbarBlockButtons()
        {
            if (_positionRow != null && !IsDifferent(_positionRow))
            {
                BtnToolbarInsertBlock.IsEnabled = false;
                BtnToolbarInsertBlock.Content = "⬅️ Insert Block";
                BtnToolbarInsertBlock.ToolTip = "Jump to or select a difference block to insert";

                BtnToolbarRemoveBlock.IsEnabled = false;
                BtnToolbarRemoveBlock.Content = "🗑 Remove Block";
                BtnToolbarRemoveBlock.ToolTip = "Jump to or select a difference block to remove";
                return;
            }

            var targetRow = (_currentSection >= 0 && _currentSection < _differenceSections.Count)
                ? _differenceSections[_currentSection]
                : (_positionRow != null && IsDifferent(_positionRow) ? _positionRow : null);

            if (targetRow != null)
            {
                var (start, end) = GetBlockRange(targetRow);
                var rows = DiffItem?.DiffRows;
                if (start >= 0 && end >= 0 && rows != null)
                {
                    var blockRows = rows.Skip(start).Take(end - start + 1).ToList();
                    int insertCount = blockRows.Count(r => r.Incoming.CanInsert);
                    int removeCount = blockRows.Count(r => r.Local.CanRemove);

                    BtnToolbarInsertBlock.IsEnabled = insertCount > 1;
                    BtnToolbarInsertBlock.Content = insertCount > 1 ? $"⬅️ Insert Block ({insertCount})" : "⬅️ Insert Block";
                    BtnToolbarInsertBlock.ToolTip = insertCount > 1
                        ? $"Insert or replace current incoming block ({insertCount} lines) into local merged config"
                        : (insertCount == 1 ? "Current difference is 1 line (use row Insert button or select a multi-line block)" : "No incoming lines to insert in current block");

                    BtnToolbarRemoveBlock.IsEnabled = removeCount > 1;
                    BtnToolbarRemoveBlock.Content = removeCount > 1 ? $"🗑 Remove Block ({removeCount})" : "🗑 Remove Block";
                    BtnToolbarRemoveBlock.ToolTip = removeCount > 0
                        ? (removeCount > 1 ? $"Remove current block of deleted lines ({removeCount} lines) from local merged config" : "Current difference is 1 line (use row Remove button or select a multi-line block)")
                        : "No deleted lines to remove in current block";
                    return;
                }
            }

            BtnToolbarInsertBlock.IsEnabled = false;
            BtnToolbarInsertBlock.Content = "⬅️ Insert Block";
            BtnToolbarInsertBlock.ToolTip = "Jump to or select a difference block to insert";

            BtnToolbarRemoveBlock.IsEnabled = false;
            BtnToolbarRemoveBlock.Content = "🗑 Remove Block";
            BtnToolbarRemoveBlock.ToolTip = "Jump to or select a difference block to remove";
        }

        private void BtnToolbarInsertBlock_Click(object sender, RoutedEventArgs e)
        {
            if (_positionRow != null && !IsDifferent(_positionRow)) return;

            var targetRow = (_currentSection >= 0 && _currentSection < _differenceSections.Count)
                ? _differenceSections[_currentSection]
                : (_positionRow != null && IsDifferent(_positionRow) ? _positionRow : null);

            if (targetRow != null)
            {
                ExecuteInsertBlock(targetRow);
            }
        }

        private void BtnToolbarRemoveBlock_Click(object sender, RoutedEventArgs e)
        {
            if (_positionRow != null && !IsDifferent(_positionRow)) return;

            var targetRow = (_currentSection >= 0 && _currentSection < _differenceSections.Count)
                ? _differenceSections[_currentSection]
                : (_positionRow != null && IsDifferent(_positionRow) ? _positionRow : null);

            if (targetRow != null)
            {
                ExecuteRemoveBlock(targetRow);
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



        private void LocalLine_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || DiffItem == null
                || sender is not TextBox editor || editor.DataContext is not DiffRowModel row
                || !row.Local.LineNumber.HasValue || row.Local.IsPlaceholder) return;

            CancelLineEdit();
            _lineEditor = editor;
            _editingRow = row;
            SetNavigationPosition(row);
            DiffList.SelectedItem = row;
            editor.IsReadOnly = false;
            editor.BorderThickness = new Thickness(1);
            editor.BorderBrush = Brushes.DeepSkyBlue;
            editor.Focus();
            editor.SelectAll();
            ShowNotice("Edit local line: Enter to save, Esc or click away to cancel");
            e.Handled = true;
        }

        private void LocalLine_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!ReferenceEquals(sender, _lineEditor)) return;
            if (e.Key == Key.Escape)
            {
                CancelLineEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                SaveLineEdit();
                e.Handled = true;
            }
        }

        private void LocalLine_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _lineEditor)) CancelLineEdit();
        }

        private void LocalLine_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (ReferenceEquals(sender, _lineEditor)) CancelLineEdit();
        }

        private void LocalLine_Unloaded(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(sender, _lineEditor)) CancelLineEdit();
        }

        private void CancelLineEdit()
        {
            if (_lineEditor == null) return;
            var editor = _lineEditor;
            _lineEditor = null;
            _editingRow = null;
            editor.IsReadOnly = true;
            editor.BorderThickness = new Thickness(0);
            editor.ClearValue(Control.BorderBrushProperty);
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        private void SaveLineEdit()
        {
            if (DiffItem == null || _lineEditor == null || _editingRow?.Local.LineNumber is not int lineNumber) return;
            string text = _lineEditor.Text;
            if (text.Contains('\r') || text.Contains('\n'))
            {
                ShowNotice("Use Direct Edit Local Config to edit multiple lines");
                return;
            }
            var row = _editingRow;
            // Keep the existing line separators and all other lines exactly as they were.
            var parts = Regex.Split(DiffItem.MergedContent, "(\\r\\n|\\r|\\n)");
            int textIndex = (lineNumber - 1) * 2;
            if (textIndex < 0 || textIndex >= parts.Length) { CancelLineEdit(); return; }
            bool changed = parts[textIndex] != text;
            parts[textIndex] = text;
            CancelLineEdit();
            if (!changed) return;
            ClearInsertUndo();
            SetNavigationPosition(row);
            DiffItem.MergedContent = string.Concat(parts);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;
            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;
            ShowNotice("✓ Saved local line edit");
        }

        private void BtnInsertLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffRowModel row) return;
            if (!row.Incoming.CanInsert) return;

            SetNavigationPosition(row);
            _insertUndo.Push(new InsertUndoState(DiffItem.MergedContent, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            var incomingLine = row.Incoming;
            int index = row.Index;

            var localLines = new List<string>(NormalizeLines(DiffItem.MergedContent));

            // Check if there is a corresponding local line
            DiffLineModel localCorresponding = row.Local;

            if (localCorresponding.LineNumber.HasValue && localCorresponding.LineNumber.Value > 0)
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
                // Find nearest previous line in DiffRows that has a local line number
                int insertPos = 0;
                for (int i = index - 1; i >= 0; i--)
                {
                    if (i < DiffItem.DiffRows.Count && DiffItem.DiffRows[i].Local.LineNumber.HasValue)
                    {
                        insertPos = DiffItem.DiffRows[i].Local.LineNumber!.Value;
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

            string lineEnding = DetectLineEnding(DiffItem.MergedContent);
            DiffItem.MergedContent = string.Join(lineEnding, localLines);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;

            ShowNotice("✓ Inserted incoming line into merged config");
        }

        private void BtnRemoveLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffRowModel row) return;
            if (!row.Local.CanRemove || row.Local.LineNumber is not int lineNumber) return;

            var localLines = new List<string>(NormalizeLines(DiffItem.MergedContent));
            int targetLineIndex = lineNumber - 1;
            if (targetLineIndex < 0 || targetLineIndex >= localLines.Count) return;

            SetNavigationPosition(row);
            _insertUndo.Push(new InsertUndoState(DiffItem.MergedContent, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            localLines.RemoveAt(targetLineIndex);

            string lineEnding = DetectLineEnding(DiffItem.MergedContent);
            DiffItem.MergedContent = string.Join(lineEnding, localLines);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;

            ShowNotice("✓ Removed deleted line from merged config");
        }

        private void BtnInsertBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DiffRowModel row)
            {
                ExecuteInsertBlock(row);
            }
        }

        private void BtnRemoveBlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DiffRowModel row)
            {
                ExecuteRemoveBlock(row);
            }
        }

        private (int startIndex, int endIndex) GetBlockRange(DiffRowModel row)
        {
            var rows = DiffItem?.DiffRows;
            if (rows == null || rows.Count == 0 || row.Index < 0 || row.Index >= rows.Count)
                return (-1, -1);

            int start = row.Index;
            while (start > 0 && IsDifferent(rows[start - 1]))
            {
                start--;
            }

            int end = row.Index;
            while (end < rows.Count - 1 && IsDifferent(rows[end + 1]))
            {
                end++;
            }

            return (start, end);
        }

        private void ExecuteInsertBlock(DiffRowModel row)
        {
            if (DiffItem == null) return;
            CancelLineEdit();
            var rows = DiffItem.DiffRows;
            if (rows == null || rows.Count == 0) return;

            var (startIndex, endIndex) = GetBlockRange(row);
            if (startIndex < 0 || endIndex < 0) return;

            var blockRows = rows.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
            var incomingLinesToInsert = blockRows
                .Where(r => r.Incoming.CanInsert)
                .Select(r => r.Incoming.Text)
                .ToList();

            if (incomingLinesToInsert.Count == 0) return;

            SetNavigationPosition(row);
            _insertUndo.Push(new InsertUndoState(DiffItem.MergedContent, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            var localLines = new List<string>(NormalizeLines(DiffItem.MergedContent));

            // Find all local lines in this block (lines that exist in working/merged local file)
            var localBlockLines = blockRows
                .Where(r => r.Local.LineNumber.HasValue && r.Local.Type != DiffLineType.EmptyPlaceholder)
                .Select(r => r.Local.LineNumber!.Value)
                .Distinct()
                .OrderByDescending(n => n)
                .ToList();

            if (localBlockLines.Count > 0)
            {
                int minLine = localBlockLines.Min();
                int targetIndex = minLine - 1;

                // Remove existing local lines in this block in descending order
                foreach (int lineNum in localBlockLines)
                {
                    int idx = lineNum - 1;
                    if (idx >= 0 && idx < localLines.Count)
                    {
                        localLines.RemoveAt(idx);
                    }
                }

                // Insert incoming lines at the start position of this block
                if (targetIndex >= 0 && targetIndex <= localLines.Count)
                {
                    localLines.InsertRange(targetIndex, incomingLinesToInsert);
                }
                else
                {
                    localLines.AddRange(incomingLinesToInsert);
                }
            }
            else
            {
                // No local lines in this block (pure insertion).
                // Find nearest previous line in DiffRows before startIndex that has a local line number
                int insertPos = 0;
                for (int i = startIndex - 1; i >= 0; i--)
                {
                    if (i < rows.Count && rows[i].Local.LineNumber.HasValue)
                    {
                        insertPos = rows[i].Local.LineNumber!.Value;
                        break;
                    }
                }

                if (insertPos >= 0 && insertPos <= localLines.Count)
                {
                    localLines.InsertRange(insertPos, incomingLinesToInsert);
                }
                else
                {
                    localLines.AddRange(incomingLinesToInsert);
                }
            }

            string lineEnding = DetectLineEnding(DiffItem.MergedContent);
            DiffItem.MergedContent = string.Join(lineEnding, localLines);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;

            ShowNotice($"✓ Inserted incoming block ({incomingLinesToInsert.Count} line{(incomingLinesToInsert.Count == 1 ? "" : "s")}) into merged config");
        }

        private void ExecuteRemoveBlock(DiffRowModel row)
        {
            if (DiffItem == null) return;
            CancelLineEdit();
            var rows = DiffItem.DiffRows;
            if (rows == null || rows.Count == 0) return;

            var (startIndex, endIndex) = GetBlockRange(row);
            if (startIndex < 0 || endIndex < 0) return;

            var blockRows = rows.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
            var localLinesToRemove = blockRows
                .Where(r => r.Local.CanRemove && r.Local.LineNumber.HasValue)
                .Select(r => r.Local.LineNumber!.Value)
                .Distinct()
                .OrderByDescending(n => n)
                .ToList();

            if (localLinesToRemove.Count == 0) return;

            SetNavigationPosition(row);
            _insertUndo.Push(new InsertUndoState(DiffItem.MergedContent, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            var localLines = new List<string>(NormalizeLines(DiffItem.MergedContent));

            foreach (int lineNum in localLinesToRemove)
            {
                int targetIndex = lineNum - 1;
                if (targetIndex >= 0 && targetIndex < localLines.Count)
                {
                    localLines.RemoveAt(targetIndex);
                }
            }

            string lineEnding = DetectLineEnding(DiffItem.MergedContent);
            DiffItem.MergedContent = string.Join(lineEnding, localLines);
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;

            ShowNotice($"✓ Removed deleted block ({localLinesToRemove.Count} line{(localLinesToRemove.Count == 1 ? "" : "s")}) from merged config");
        }

        private void BtnApplyDirectEdit_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            ClearInsertUndo();

            DiffItem.MergedContent = TxtDirectEditor.Text;
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            ShowNotice("✓ Applied direct edits to merged config");
        }

        private void BtnRevertLocal_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            ClearInsertUndo();

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
            ClearInsertUndo();

            TxtDirectEditor.Text = DiffItem.IncomingContent;
            DiffItem.MergedContent = DiffItem.IncomingContent;
            DiffItem.Resolution = ConfigFileResolution.UseMerged;
            DiffItem.IsCustomMerged = true;

            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            ShowNotice("✓ Filled editor with incoming default config");
        }

        private void ClearInsertUndo()
        {
            _insertUndo.Clear();
            BtnUndoInsert.IsEnabled = false;
        }

        private void BtnUndoInsert_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null || !_insertUndo.TryPop(out var state)) return;
            _positionRow = state.Position;
            DiffItem.MergedContent = state.Content;
            DiffItem.Resolution = state.Resolution;
            DiffItem.IsCustomMerged = state.IsCustomMerged;
            _diffService.RebuildDiff(DiffItem, DiffItem.MergedContent, DiffItem.IncomingContent);
            TxtDirectEditor.Text = DiffItem.MergedContent;
            BtnUndoInsert.IsEnabled = _insertUndo.Count > 0;
            ShowNotice("✓ Undid last line change");
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

        private static string DetectLineEnding(string text)
        {
            if (string.IsNullOrEmpty(text)) return Environment.NewLine;
            return text.Contains("\r\n") ? "\r\n" : (text.Contains('\n') ? "\n" : Environment.NewLine);
        }

        private static string[] NormalizeLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}
