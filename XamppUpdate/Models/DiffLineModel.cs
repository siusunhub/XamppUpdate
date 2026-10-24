namespace XamppUpdate.Models
{
    public class DiffLineModel
    {
        public int? LineNumber { get; set; }
        public string Text { get; set; } = string.Empty;
        public DiffLineType Type { get; set; } = DiffLineType.Unchanged;

        public string DisplayLineNumber => LineNumber.HasValue ? LineNumber.Value.ToString() : string.Empty;
        public bool CanInsert => Type == DiffLineType.Inserted || Type == DiffLineType.Modified;
        public bool CanRemove => Type == DiffLineType.Deleted;
        public bool HasText => !string.IsNullOrEmpty(Text);
        public bool IsPlaceholder => Type == DiffLineType.EmptyPlaceholder;

        public bool CanInsertBlock { get; set; }
        public bool CanRemoveBlock { get; set; }
        public string BlockButtonText { get; set; } = string.Empty;
        public string BlockButtonToolTip { get; set; } = string.Empty;
        public int BlockLineCount { get; set; }
    }
}
