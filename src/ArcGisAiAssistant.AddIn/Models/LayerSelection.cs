using ArcGIS.Desktop.Framework.ComponentModel;

namespace ArcGisAiAssistant.AddIn.Models;

internal sealed class LayerSelection : ObservableObject
{
    private bool _isSelected;

    public string Name { get; init; } = string.Empty;
    public string GeometryType { get; init; } = string.Empty;
    public bool IsVisible { get; init; } = true;
    public long? RowCount { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayText => $"{Name} [{GeometryType}] {(RowCount.HasValue ? $"({RowCount})" : "")}";
}
