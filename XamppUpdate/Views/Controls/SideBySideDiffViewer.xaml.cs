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
        private sealed record RowSnapshot(
            DiffRowModel Row,
            string LocalText,
            DiffLineType LocalType,
            DiffLineType IncomingType,
            int? LocalLineNumber,
            bool IsChanged,
            RowChangeType ChangeAction,
            string ChangedBadgeText,
            bool CanUndoLine,
            bool CanInsert,
            bool CanRemove
        );

        private sealed record InsertUndoState(
            List<RowSnapshot> ChangedRows,
            ConfigFileResolution Resolution,
            bool IsCustomMerged,
            DiffRowModel Position
        );

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
            if (ChkDifferencesOnly.IsChecked == true)
            {
                bool hideChanged = ChkHideChanged?.IsChecked == true;
                _rowsView.Filter = item =>
                {
                    if (item is not DiffRowModel row) return false;
                    if (row.IsChanged)
                    {
                        return !hideChanged;
                    }
                    return IsDifferent(row);
                };
            }
            else
            {
                _rowsView.Filter = null;
            }
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

        private void HideChanged_Changed(object sender, RoutedEventArgs e)
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

        private void BtnUndoLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffRowModel row) return;

            CancelLineEdit();
            SetNavigationPosition(row);
            RevertRowChange(row);
            ShowNotice("✓ Undid line change");
        }

        private static void ApplyInsertToRow(DiffRowModel row)
        {
            row.Local.Text = row.Incoming.Text;
            if (row.Local.Type == DiffLineType.EmptyPlaceholder)
            {
                row.Local.Type = DiffLineType.Inserted;
                row.ChangeAction = RowChangeType.Inserted;
            }
            else
            {
                row.Local.Type = DiffLineType.Modified;
                row.ChangeAction = RowChangeType.Modified;
            }

            row.IsChanged = true;
            row.ChangedBadgeText = "Changed";
            row.CanUndoLine = true;
            row.Incoming.CanInsert = false;
            row.Incoming.CanInsertBlock = false;
            row.Local.CanRemove = false;
            row.Local.CanRemoveBlock = false;
        }

        private static void ApplyRemoveToRow(DiffRowModel row)
        {
            row.IsChanged = true;
            row.ChangeAction = RowChangeType.Removed;
            row.ChangedBadgeText = "Removed";
            row.CanUndoLine = true;
            row.Local.CanRemove = false;
            row.Local.CanRemoveBlock = false;
            row.Incoming.CanInsert = false;
            row.Incoming.CanInsertBlock = false;
        }

        private void RevertRowChange(DiffRowModel row)
        {
            row.Local.Text = row.OriginalLocalText;
            row.Local.Type = row.OriginalLocalType;
            row.Incoming.Type = row.OriginalIncomingType;
            row.Local.LineNumber = row.OriginalLocalLineNumber;
            row.IsChanged = false;
            row.ChangeAction = RowChangeType.None;
            row.ChangedBadgeText = string.Empty;
            row.CanUndoLine = false;
            row.Incoming.CanInsert = (row.OriginalIncomingType == DiffLineType.Inserted || row.OriginalIncomingType == DiffLineType.Modified);
            row.Local.CanRemove = (row.OriginalLocalType == DiffLineType.Deleted);

            if (DiffItem?.DiffRows != null)
            {
                ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            }

            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();
        }

        private void UpdateMergedContentFromRows()
        {
            if (DiffItem?.DiffRows == null) return;

            int currentLineNumber = 1;
            var mergedLines = new List<string>();

            foreach (var row in DiffItem.DiffRows)
            {
                bool includeInMerged;
                if (row.IsChanged)
                {
                    includeInMerged = (row.ChangeAction != RowChangeType.Removed);
                }
                else
                {
                    includeInMerged = (row.Local.Type != DiffLineType.EmptyPlaceholder);
                }

                if (includeInMerged)
                {
                    row.Local.LineNumber = currentLineNumber++;
                    mergedLines.Add(row.Local.Text);
                }
                else
                {
                    row.Local.LineNumber = null;
                }
            }

            string lineEnding = DetectLineEnding(DiffItem.OriginalLocalContent ?? DiffItem.MergedContent);
            DiffItem.MergedContent = string.Join(lineEnding, mergedLines);

            bool anyChanged = DiffItem.DiffRows.Any(r => r.IsChanged);
            if (anyChanged)
            {
                DiffItem.Resolution = ConfigFileResolution.UseMerged;
                DiffItem.IsCustomMerged = true;
            }
            else
            {
                DiffItem.Resolution = ConfigFileResolution.KeepCurrent;
                DiffItem.IsCustomMerged = false;
            }

            int unresolvedAdditions = DiffItem.DiffRows.Count(r => r.Incoming.CanInsert && r.Incoming.Type == DiffLineType.Inserted);
            int unresolvedDeletions = DiffItem.DiffRows.Count(r => r.Local.CanRemove && r.Local.Type == DiffLineType.Deleted);
            int unresolvedModifications = DiffItem.DiffRows.Count(r => r.Incoming.CanInsert && r.Incoming.Type == DiffLineType.Modified);

            DiffItem.AdditionsCount = unresolvedAdditions;
            DiffItem.DeletionsCount = unresolvedDeletions;
            DiffItem.ModificationsCount = unresolvedModifications;

            TxtDirectEditor.Text = DiffItem.MergedContent;
        }

        private static RowSnapshot CreateRowSnapshot(DiffRowModel row)
        {
            return new RowSnapshot(
                Row: row,
                LocalText: row.Local.Text,
                LocalType: row.Local.Type,
                IncomingType: row.Incoming.Type,
                LocalLineNumber: row.Local.LineNumber,
                IsChanged: row.IsChanged,
                ChangeAction: row.ChangeAction,
                ChangedBadgeText: row.ChangedBadgeText,
                CanUndoLine: row.CanUndoLine,
                CanInsert: row.Incoming.CanInsert,
                CanRemove: row.Local.CanRemove
            );
        }

        private static void RestoreRowSnapshot(RowSnapshot snapshot)
        {
            var row = snapshot.Row;
            row.Local.Text = snapshot.LocalText;
            row.Local.Type = snapshot.LocalType;
            row.Incoming.Type = snapshot.IncomingType;
            row.Local.LineNumber = snapshot.LocalLineNumber;
            row.IsChanged = snapshot.IsChanged;
            row.ChangeAction = snapshot.ChangeAction;
            row.ChangedBadgeText = snapshot.ChangedBadgeText;
            row.CanUndoLine = snapshot.CanUndoLine;
            row.Incoming.CanInsert = snapshot.CanInsert;
            row.Local.CanRemove = snapshot.CanRemove;
        }

        private void BtnInsertLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffRowModel row) return;
            if (!row.Incoming.CanInsert) return;

            CancelLineEdit();
            SetNavigationPosition(row);

            var snapshot = CreateRowSnapshot(row);
            _insertUndo.Push(new InsertUndoState(new List<RowSnapshot> { snapshot }, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            ApplyInsertToRow(row);

            if (DiffItem.DiffRows != null)
            {
                ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            }

            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();

            ShowNotice("✓ Inserted incoming line into merged config");
        }

        private void BtnRemoveLine_Click(object sender, RoutedEventArgs e)
        {
            if (DiffItem == null) return;
            if (sender is not Button btn || btn.DataContext is not DiffRowModel row) return;
            if (!row.Local.CanRemove) return;

            CancelLineEdit();
            SetNavigationPosition(row);

            var snapshot = CreateRowSnapshot(row);
            _insertUndo.Push(new InsertUndoState(new List<RowSnapshot> { snapshot }, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            ApplyRemoveToRow(row);

            if (DiffItem.DiffRows != null)
            {
                ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            }

            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();

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
            if (DiffItem?.DiffRows == null || DiffItem.DiffRows.Count == 0) return;
            CancelLineEdit();

            var (startIndex, endIndex) = GetBlockRange(row);
            if (startIndex < 0 || endIndex < 0) return;

            var blockRows = DiffItem.DiffRows.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
            var rowsToInsert = blockRows.Where(r => r.Incoming.CanInsert).ToList();
            if (rowsToInsert.Count == 0) return;

            SetNavigationPosition(row);
            var snapshots = rowsToInsert.Select(CreateRowSnapshot).ToList();
            _insertUndo.Push(new InsertUndoState(snapshots, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            foreach (var r in rowsToInsert)
            {
                ApplyInsertToRow(r);
            }

            ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();

            ShowNotice($"✓ Inserted incoming block ({rowsToInsert.Count} line{(rowsToInsert.Count == 1 ? "" : "s")}) into merged config");
        }

        private void ExecuteRemoveBlock(DiffRowModel row)
        {
            if (DiffItem?.DiffRows == null || DiffItem.DiffRows.Count == 0) return;
            CancelLineEdit();

            var (startIndex, endIndex) = GetBlockRange(row);
            if (startIndex < 0 || endIndex < 0) return;

            var blockRows = DiffItem.DiffRows.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
            var rowsToRemove = blockRows.Where(r => r.Local.CanRemove).ToList();
            if (rowsToRemove.Count == 0) return;

            SetNavigationPosition(row);
            var snapshots = rowsToRemove.Select(CreateRowSnapshot).ToList();
            _insertUndo.Push(new InsertUndoState(snapshots, DiffItem.Resolution, DiffItem.IsCustomMerged, row));
            BtnUndoInsert.IsEnabled = true;

            foreach (var r in rowsToRemove)
            {
                ApplyRemoveToRow(r);
            }

            ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();

            ShowNotice($"✓ Removed deleted block ({rowsToRemove.Count} line{(rowsToRemove.Count == 1 ? "" : "s")}) from merged config");
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

            CancelLineEdit();
            _positionRow = state.Position;

            foreach (var snapshot in state.ChangedRows)
            {
                RestoreRowSnapshot(snapshot);
            }

            if (DiffItem.DiffRows != null)
            {
                ConfigDiffService.AnnotateDifferenceBlocks(DiffItem.DiffRows);
            }

            UpdateMergedContentFromRows();
            _rowsView?.Refresh();
            UpdateDifferenceNavigation();

            BtnUndoInsert.IsEnabled = _insertUndo.Count > 0;
            ShowNotice("✓ Undid last action");
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
