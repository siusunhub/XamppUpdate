namespace XamppUpdate.Models
{
    public enum ServiceType
    {
        Apache,
        Php,
        MySql,
        PhpMyAdmin,
        Ssl,
        Composer
    }

    public enum ServiceStatus
    {
        Unknown,
        Running,
        Stopped,
        NotInstalled,
        UnderConstruction,
        Updating,
        Error
    }
}
