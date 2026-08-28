using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class ConfigDiffItem : ObservableObject
    {
        [ObservableProperty]
        private string _relativeFilePath = string.Empty;

        [ObservableProperty]
        private string _localFilePath = string.Empty;

        [ObservableProperty]
        private string _incomingFilePath = string.Empty;

        [ObservableProperty]
        private bool _localExists;

        [ObservableProperty]
        private bool _incomingExists;

        [ObservableProperty]
        private bool _hasDifferences;

        [ObservableProperty]
        private int _additionsCount;

        [ObservableProperty]
        private int _deletionsCount;

        [ObservableProperty]
        private int _modificationsCount;

        [ObservableProperty]
        private ConfigFileResolution _resolution = ConfigFileResolution.KeepCurrent;

        [ObservableProperty]
        private string _mergedContent = string.Empty;

        public List<DiffLineModel> LocalLines { get; set; } = new();
        public List<DiffLineModel> IncomingLines { get; set; } = new();

        public string SummaryText => HasDifferences
            ? $"+{AdditionsCount} / -{DeletionsCount} changes"
            : (LocalExists && IncomingExists ? "Identical" : (LocalExists ? "New file missing" : "Local file missing"));
    }
}
