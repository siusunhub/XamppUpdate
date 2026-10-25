using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public enum RowChangeType
    {
        None,
        Inserted,
        Removed,
        Modified
    }

    public class DiffRowModel : ObservableObject
    {
        private int _index;
        public int Index { get => _index; set => SetProperty(ref _index, value); }

        public DiffLineModel Local { get; set; } = new();
        public DiffLineModel Incoming { get; set; } = new();

        private bool _isChanged;
        public bool IsChanged { get => _isChanged; set => SetProperty(ref _isChanged, value); }

        private RowChangeType _changeAction = RowChangeType.None;
        public RowChangeType ChangeAction { get => _changeAction; set => SetProperty(ref _changeAction, value); }

        private string _changedBadgeText = string.Empty;
        public string ChangedBadgeText { get => _changedBadgeText; set => SetProperty(ref _changedBadgeText, value); }

        private bool _canUndoLine;
        public bool CanUndoLine { get => _canUndoLine; set => SetProperty(ref _canUndoLine, value); }

        public string OriginalLocalText { get; set; } = string.Empty;
        public DiffLineType OriginalLocalType { get; set; } = DiffLineType.Unchanged;
        public DiffLineType OriginalIncomingType { get; set; } = DiffLineType.Unchanged;
        public int? OriginalLocalLineNumber { get; set; }
    }
}
