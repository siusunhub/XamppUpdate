using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public class DiffLineModel : ObservableObject
    {
        private int? _lineNumber;
        public int? LineNumber
        {
            get => _lineNumber;
            set
            {
                if (SetProperty(ref _lineNumber, value))
                {
                    OnPropertyChanged(nameof(DisplayLineNumber));
                }
            }
        }

        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set
            {
                if (SetProperty(ref _text, value))
                {
                    OnPropertyChanged(nameof(HasText));
                }
            }
        }

        private DiffLineType _type = DiffLineType.Unchanged;
        public DiffLineType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    OnPropertyChanged(nameof(CanInsert));
                    OnPropertyChanged(nameof(CanRemove));
                    OnPropertyChanged(nameof(IsPlaceholder));
                }
            }
        }

        public string DisplayLineNumber => LineNumber.HasValue ? LineNumber.Value.ToString() : string.Empty;

        private bool? _canInsertOverride;
        public bool CanInsert
        {
            get => _canInsertOverride ?? (Type == DiffLineType.Inserted || Type == DiffLineType.Modified);
            set
            {
                if (_canInsertOverride != value)
                {
                    _canInsertOverride = value;
                    OnPropertyChanged(nameof(CanInsert));
                }
            }
        }

        private bool? _canRemoveOverride;
        public bool CanRemove
        {
            get => _canRemoveOverride ?? (Type == DiffLineType.Deleted);
            set
            {
                if (_canRemoveOverride != value)
                {
                    _canRemoveOverride = value;
                    OnPropertyChanged(nameof(CanRemove));
                }
            }
        }

        public bool HasText => !string.IsNullOrEmpty(Text);
        public bool IsPlaceholder => Type == DiffLineType.EmptyPlaceholder;

        private bool _canInsertBlock;
        public bool CanInsertBlock { get => _canInsertBlock; set => SetProperty(ref _canInsertBlock, value); }

        private bool _canRemoveBlock;
        public bool CanRemoveBlock { get => _canRemoveBlock; set => SetProperty(ref _canRemoveBlock, value); }

        private string _blockButtonText = string.Empty;
        public string BlockButtonText { get => _blockButtonText; set => SetProperty(ref _blockButtonText, value); }

        private string _blockButtonToolTip = string.Empty;
        public string BlockButtonToolTip { get => _blockButtonToolTip; set => SetProperty(ref _blockButtonToolTip, value); }

        private int _blockLineCount;
        public int BlockLineCount { get => _blockLineCount; set => SetProperty(ref _blockLineCount, value); }
    }
}
