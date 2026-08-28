namespace XamppUpdate.Models
{
    public enum ConfigFileResolution
    {
        KeepCurrent,       // Preserve current working config file
        OverwriteWithNew,  // Overwrite with incoming new default config
        UseMerged          // Use staged merged configuration
    }

    public enum DiffLineType
    {
        Unchanged,
        Inserted,
        Deleted,
        Modified,
        EmptyPlaceholder
    }
}
