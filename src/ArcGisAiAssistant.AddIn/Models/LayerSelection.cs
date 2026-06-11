using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ArcGisAiAssistant.AddIn.Models;

internal sealed class LayerSelection : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; init; } = string.Empty;
    public string GeometryType { get; init; } = string.Empty;
    public bool IsVisible { get; init; } = true;
    public long? RowCount { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string DisplayText => $"{Name} [{GeometryType}] {(RowCount.HasValue ? $"({RowCount})" : "")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
