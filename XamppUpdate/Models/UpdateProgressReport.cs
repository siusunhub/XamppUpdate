using System;

namespace XamppUpdate.Models
{
    public enum UpdateStep
    {
        SourceValidation,
        DownloadAndExtract,
        ConfigInspection,
        BackupCreation,
        ServiceStop,
        FileReplacement,
        ConfigApplication,
        ServiceStart,
        HealthCheck,
        Cleanup,
        Complete,
        Failed
    }

    public class UpdateProgressReport
    {
        public UpdateStep Step { get; set; }
        public string StepTitle { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public double Percentage { get; set; }
        public bool IsIndeterminate { get; set; }
        public bool IsError { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
