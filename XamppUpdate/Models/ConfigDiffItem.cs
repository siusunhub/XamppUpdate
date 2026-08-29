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

        [ObservableProperty]
        private string _originalLocalContent = string.Empty;

        [ObservableProperty]
        private string _incomingContent = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private bool _isCustomMerged;

        [ObservableProperty]
        private List<DiffLineModel> _localLines = new();

        [ObservableProperty]
        private List<DiffLineModel> _incomingLines = new();

        [ObservableProperty]
        private List<DiffRowModel> _diffRows = new();

        public string SummaryText => IsCustomMerged
            ? "Custom Merged / Edited"
            : (HasDifferences
                ? $"+{AdditionsCount} / -{DeletionsCount} changes"
                : (LocalExists && IncomingExists ? "Identical" : (LocalExists ? "New file missing" : "Local file missing")));
    }
}
