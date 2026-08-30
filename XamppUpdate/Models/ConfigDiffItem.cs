using System.Collections.Generic;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XamppUpdate.Models
{
    public partial class ConfigDiffItem : ObservableObject
    {
        private static readonly SolidColorBrush BlueBg = CreateFrozenBrush("#E0F2FE");
        private static readonly SolidColorBrush BlueFg = CreateFrozenBrush("#0369A1");
        private static readonly SolidColorBrush GreenBg = CreateFrozenBrush("#DCFCE7");
        private static readonly SolidColorBrush GreenFg = CreateFrozenBrush("#15803D");
        private static readonly SolidColorBrush AmberBg = CreateFrozenBrush("#FEF3C7");
        private static readonly SolidColorBrush AmberFg = CreateFrozenBrush("#B45309");
        private static readonly SolidColorBrush SlateBg = CreateFrozenBrush("#F1F5F9");
        private static readonly SolidColorBrush SlateFg = CreateFrozenBrush("#64748B");

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

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
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        [NotifyPropertyChangedFor(nameof(BadgeBackgroundBrush))]
        [NotifyPropertyChangedFor(nameof(BadgeForegroundBrush))]
        private bool _hasDifferences;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private int _additionsCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        private int _deletionsCount;

        [ObservableProperty]
        private int _modificationsCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        [NotifyPropertyChangedFor(nameof(BadgeBackgroundBrush))]
        [NotifyPropertyChangedFor(nameof(BadgeForegroundBrush))]
        private ConfigFileResolution _resolution = ConfigFileResolution.KeepCurrent;

        [ObservableProperty]
        private string _mergedContent = string.Empty;

        [ObservableProperty]
        private string _originalLocalContent = string.Empty;

        [ObservableProperty]
        private string _incomingContent = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SummaryText))]
        [NotifyPropertyChangedFor(nameof(BadgeBackgroundBrush))]
        [NotifyPropertyChangedFor(nameof(BadgeForegroundBrush))]
        private bool _isCustomMerged;

        [ObservableProperty]
        private List<DiffLineModel> _localLines = new();

        [ObservableProperty]
        private List<DiffLineModel> _incomingLines = new();

        [ObservableProperty]
        private List<DiffRowModel> _diffRows = new();

        public string SummaryText
        {
            get
            {
                if (Resolution == ConfigFileResolution.OverwriteWithNew)
                {
                    return "Overwrite with Incoming";
                }
                if (Resolution == ConfigFileResolution.UseMerged || IsCustomMerged)
                {
                    return "Custom Merged / Edited";
                }
                if (HasDifferences)
                {
                    return $"Keep Local (+{AdditionsCount} / -{DeletionsCount})";
                }
                if (LocalExists && IncomingExists)
                {
                    return "Identical";
                }
                return LocalExists ? "New file missing" : "Local file missing";
            }
        }

        public Brush BadgeBackgroundBrush
        {
            get
            {
                if (Resolution == ConfigFileResolution.OverwriteWithNew)
                    return BlueBg;
                if (Resolution == ConfigFileResolution.UseMerged || IsCustomMerged)
                    return GreenBg;
                if (HasDifferences)
                    return AmberBg;
                return SlateBg;
            }
        }

        public Brush BadgeForegroundBrush
        {
            get
            {
                if (Resolution == ConfigFileResolution.OverwriteWithNew)
                    return BlueFg;
                if (Resolution == ConfigFileResolution.UseMerged || IsCustomMerged)
                    return GreenFg;
                if (HasDifferences)
                    return AmberFg;
                return SlateFg;
            }
        }
    }
}
