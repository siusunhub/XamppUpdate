namespace XamppUpdate.Models
{
    public class DiffRowModel
    {
        public int Index { get; set; }
        public DiffLineModel Local { get; set; } = new();
        public DiffLineModel Incoming { get; set; } = new();
    }
}
