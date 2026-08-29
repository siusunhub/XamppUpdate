namespace XamppUpdate.Models
{
    public class DiffLineModel
    {
        public int? LineNumber { get; set; }
        public string Text { get; set; } = string.Empty;
        public DiffLineType Type { get; set; } = DiffLineType.Unchanged;

        public string DisplayLineNumber => LineNumber.HasValue ? LineNumber.Value.ToString() : string.Empty;
        public bool CanInsert => Type == DiffLineType.Inserted || Type == DiffLineType.Modified;
        public bool HasText => !string.IsNullOrEmpty(Text);
        public bool IsPlaceholder => Type == DiffLineType.EmptyPlaceholder;
    }
}
